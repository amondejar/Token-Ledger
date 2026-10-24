using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace TokenLedger;

/// <summary>
/// A chat client middleware that replays or records model calls and measures them for the open
/// <see cref="LedgerScenario"/>. Outside a scenario it passes every call through untouched, so it can stay in
/// production code. Put it last in the pipeline, closest to the provider, so it sees each model round trip.
/// </summary>
public sealed class TokenLedgerChatClient : DelegatingChatClient
{
    private static int _created;

    /// <summary>Wraps <paramref name="innerClient"/>.</summary>
    public TokenLedgerChatClient(IChatClient innerClient) : base(innerClient) => Interlocked.Increment(ref _created);

    internal static int Created => Volatile.Read(ref _created);

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (Ledger.Current is not { } scenario) return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var defaultModel = DefaultModel();
        var fingerprint = scenario.Mode == LedgerMode.Live ? "" : Fingerprint.Compute(list, options, defaultModel, scenario.Settings);

        if (Replay(scenario, fingerprint, list, options, defaultModel) is { } recorded)
        {
            var replayed = Recorded.ToResponse(recorded.Response);
            scenario.Measure(recorded.Response.Model ?? "unknown", replayed.Usage, Recorded.Tools(replayed), replayed: true, streaming: false);
            return replayed;
        }

        var response = await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
        Observe(scenario, fingerprint, list, options, defaultModel, response, streaming: false);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Ledger.Current is not { } scenario)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false)) yield return update;
            yield break;
        }

        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var defaultModel = DefaultModel();
        var fingerprint = scenario.Mode == LedgerMode.Live ? "" : Fingerprint.Compute(list, options, defaultModel, scenario.Settings);

        if (Replay(scenario, fingerprint, list, options, defaultModel) is { } recorded)
        {
            var replayed = Recorded.ToResponse(recorded.Response);
            scenario.Measure(recorded.Response.Model ?? "unknown", replayed.Usage, Recorded.Tools(replayed), replayed: true, streaming: true);
            foreach (var update in Recorded.ToUpdates(recorded.Response))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }

            yield break;
        }

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            yield return update;
        }

        // Only a stream read to the end is recorded and measured.
        Observe(scenario, fingerprint, list, options, defaultModel, Recorded.Join(updates), streaming: true);
    }

    private string? DefaultModel() => (InnerClient.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata)?.DefaultModelId;

    /// <summary>The recorded interaction to replay, or null to call the model. Throws in replay mode when there is none.</summary>
    private static RecordedInteraction? Replay(LedgerScenario scenario, string fingerprint, IReadOnlyList<ChatMessage> messages, ChatOptions? options, string? defaultModel)
    {
        if (scenario.Mode is not (LedgerMode.Auto or LedgerMode.Replay)) return null;
        if (scenario.TryReplay(fingerprint, out var interaction)) return interaction;
        if (scenario.Mode == LedgerMode.Auto) return null;

        var request = Summary(messages, options, defaultModel, scenario.Settings);
        scenario.Miss(request);
        throw new TokenLedgerException(
            $"TokenLedger: no recorded response in scenario '{scenario.Name}' for a request to {request.Model ?? "the default model"} " +
            $"({request.Messages} message(s), last: {request.Last}). Record it with TOKENLEDGER_MODE=record or auto.");
    }

    private static void Observe(LedgerScenario scenario, string fingerprint, IReadOnlyList<ChatMessage> messages, ChatOptions? options, string? defaultModel, ChatResponse response, bool streaming)
    {
        var model = response.ModelId ?? options?.ModelId ?? defaultModel ?? "unknown";
        if (scenario.Mode != LedgerMode.Live)
        {
            scenario.Store(new RecordedInteraction
            {
                Fingerprint = fingerprint,
                Request = Summary(messages, options, defaultModel, scenario.Settings),
                Response = Recorded.From(response, model, scenario.Settings),
            });
        }

        scenario.Measure(model, response.Usage, Recorded.Tools(response), replayed: false, streaming);
    }

    private static RecordedRequest Summary(IReadOnlyList<ChatMessage> messages, ChatOptions? options, string? defaultModel, LedgerOptions settings)
    {
        string? last = null;
        if (messages.Count > 0)
        {
            var message = messages[^1];
            var text = message.Text;
            if (string.IsNullOrEmpty(text))
            {
                text = message.Contents.OfType<FunctionResultContent>().Any() ? "(tool results)"
                    : message.Contents.OfType<FunctionCallContent>().Select(c => c.Name).FirstOrDefault() is { } name ? $"(call {name})"
                    : "(no text)";
            }
            else
            {
                text = settings.ApplyRedactors(text.ReplaceLineEndings(" "));
                if (text.Length > 120) text = text[..117] + "...";
            }

            last = $"{message.Role.Value}: {text}";
        }

        return new RecordedRequest { Model = options?.ModelId ?? defaultModel, Messages = messages.Count, Last = last };
    }
}

/// <summary>Adds TokenLedger to a chat client pipeline.</summary>
public static class TokenLedgerChatClientBuilderExtensions
{
    /// <summary>
    /// Adds the TokenLedger middleware. Call it last, after <c>UseFunctionInvocation()</c> and other middleware,
    /// so it sits closest to the provider and sees each model round trip. Outside a scenario it does nothing.
    /// </summary>
    public static ChatClientBuilder UseTokenLedger(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(inner => new TokenLedgerChatClient(inner));
    }
}
