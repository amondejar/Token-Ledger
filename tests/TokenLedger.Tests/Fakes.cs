using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace TokenLedger.Tests;

/// <summary>
/// A provider that answers from a script and counts how many times it was really called.
/// Each answer is built from the request, so tests can check what the model saw.
/// </summary>
internal sealed class FakeModel(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] script) : IChatClient
{
    private int _next;

    public int Calls { get; private set; }

    public string? DefaultModelId { get; init; } = "fake-model";

    public bool ReportUsage { get; init; } = true;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        var list = messages.ToList();
        var answer = script[Math.Min(_next++, script.Length - 1)](list);
        if (ReportUsage && answer.Usage is null)
        {
            // A made-up but stable count: 10 tokens per input message, 1 per output character.
            answer.Usage = new UsageDetails { InputTokenCount = 10 * list.Count, OutputTokenCount = answer.Text.Length, TotalTokenCount = 10 * list.Count + answer.Text.Length };
        }

        return Task.FromResult(answer);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is TextContent text)
                {
                    // Split text into small chunks, as providers do.
                    for (var i = 0; i < text.Text.Length; i += 4)
                        yield return new ChatResponseUpdate(message.Role, text.Text.Substring(i, Math.Min(4, text.Text.Length - i))) { ModelId = response.ModelId };
                }
                else
                {
                    yield return new ChatResponseUpdate(message.Role, [content]) { ModelId = response.ModelId };
                }
            }
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Usage is { } usage ? [new UsageContent(usage)] : null) { FinishReason = response.FinishReason };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("fake", null, DefaultModelId)
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose() { }

    public static ChatResponse Text(string text, string? model = null) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = model, FinishReason = ChatFinishReason.Stop };

    public static ChatResponse Call(string tool, string callId, Dictionary<string, object?> arguments, string? model = null) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, tool, arguments)])) { ModelId = model, FinishReason = ChatFinishReason.ToolCalls };
}

/// <summary>A clean TokenLedger setup in a temporary folder for each test.</summary>
internal sealed class Sandbox : IDisposable
{
    public Sandbox(LedgerMode mode = LedgerMode.Auto, bool ci = false)
    {
        Directory = Path.Combine(Path.GetTempPath(), "tokenledger-tests", Guid.NewGuid().ToString("N"));
        Ledger.Options.Reset();
        Ledger.Options.Directory = Directory;
        Ledger.Options.Mode = mode;
        LedgerEnvironment.CiOverride = ci;
    }

    public string Directory { get; }

    public string Recording(string scenario) => Path.Combine(Directory, "recordings", scenario.Replace('/', Path.DirectorySeparatorChar) + ".json");

    public string Baseline(string scenario) => Path.Combine(Directory, "baselines", scenario.Replace('/', Path.DirectorySeparatorChar) + ".json");

    public static LedgerMode Mode { set => Ledger.Options.Mode = value; }

    public static bool Ci { set => LedgerEnvironment.CiOverride = value; }

    public void Dispose()
    {
        Ledger.Options.Reset();
        LedgerEnvironment.CiOverride = null;
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}
