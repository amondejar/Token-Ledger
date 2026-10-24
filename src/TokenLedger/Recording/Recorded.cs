using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace TokenLedger;

/// <summary>Converts responses to and from their recorded form.</summary>
internal static class Recorded
{
    public static RecordedResponse From(ChatResponse response, string model, LedgerOptions settings)
    {
        var recorded = new RecordedResponse
        {
            Model = model,
            ResponseId = response.ResponseId,
            FinishReason = response.FinishReason?.Value,
            Usage = Usage(response.Usage),
        };
        foreach (var message in response.Messages)
        {
            var m = new RecordedMessage { Role = message.Role.Value, AuthorName = message.AuthorName };
            foreach (var content in message.Contents)
            {
                if (Content(content, settings) is { } c) m.Contents.Add(c);
            }

            recorded.Messages.Add(m);
        }

        return recorded;
    }

    /// <summary>Joins streamed updates into one response, as a non-streaming call would have returned it.</summary>
    public static ChatResponse Join(IReadOnlyList<ChatResponseUpdate> updates)
    {
        var response = new ChatResponse();
        ChatMessage? current = null;
        foreach (var update in updates)
        {
            response.ResponseId ??= update.ResponseId;
            response.ModelId ??= update.ModelId;
            response.FinishReason = update.FinishReason ?? response.FinishReason;

            var role = update.Role ?? current?.Role ?? ChatRole.Assistant;
            if (current is null || current.Role != role || (update.MessageId is { } id && current.MessageId is { } currentId && id != currentId))
            {
                current = new ChatMessage(role, (IList<AIContent>?)null) { MessageId = update.MessageId, AuthorName = update.AuthorName };
                response.Messages.Add(current);
            }

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case UsageContent usage:
                        response.Usage = Add(response.Usage, usage.Details);
                        break;
                    case TextContent text when current.Contents.Count > 0 && current.Contents[^1] is TextContent previous:
                        current.Contents[^1] = new TextContent(previous.Text + text.Text);
                        break;
                    case TextReasoningContent reasoning when current.Contents.Count > 0 && current.Contents[^1] is TextReasoningContent previous:
                        current.Contents[^1] = new TextReasoningContent(previous.Text + reasoning.Text);
                        break;
                    default:
                        current.Contents.Add(content);
                        break;
                }
            }
        }

        // Updates that carried only usage leave an empty message behind.
        for (var i = response.Messages.Count - 1; i >= 0; i--)
        {
            if (response.Messages[i].Contents.Count == 0 && response.Messages.Count > 1) response.Messages.RemoveAt(i);
        }

        return response;
    }

    public static ChatResponse ToResponse(RecordedResponse recorded)
    {
        var response = new ChatResponse
        {
            ModelId = recorded.Model,
            ResponseId = recorded.ResponseId,
            FinishReason = recorded.FinishReason is { } reason ? new ChatFinishReason(reason) : null,
            Usage = recorded.Usage is { } u
                ? new UsageDetails { InputTokenCount = u.Input, OutputTokenCount = u.Output, TotalTokenCount = u.Total, CachedInputTokenCount = u.CachedInput, ReasoningTokenCount = u.Reasoning }
                : null,
        };
        foreach (var m in recorded.Messages)
        {
            response.Messages.Add(new ChatMessage(new ChatRole(m.Role), [.. m.Contents.Select(ToContent)]) { AuthorName = m.AuthorName });
        }

        return response;
    }

    /// <summary>Replays a recorded response as a stream: one update per content, usage and finish reason last.</summary>
    public static IEnumerable<ChatResponseUpdate> ToUpdates(RecordedResponse recorded)
    {
        var response = ToResponse(recorded);
        foreach (var message in response.Messages)
        {
            var messageId = Guid.NewGuid().ToString("N");
            foreach (var content in message.Contents)
            {
                yield return new ChatResponseUpdate(message.Role, [content])
                {
                    AuthorName = message.AuthorName,
                    MessageId = messageId,
                    ResponseId = response.ResponseId,
                    ModelId = response.ModelId,
                };
            }
        }

        var last = new ChatResponseUpdate(ChatRole.Assistant, response.Usage is { } usage ? [new UsageContent(usage)] : null)
        {
            ResponseId = response.ResponseId,
            ModelId = response.ModelId,
            FinishReason = response.FinishReason,
        };
        yield return last;
    }

    public static IReadOnlyList<string> Tools(ChatResponse response) =>
        [.. response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.Name)];

    private static RecordedContent? Content(AIContent content, LedgerOptions settings) => content switch
    {
        TextContent t => new RecordedContent { Type = "text", Text = settings.ApplyRedactors(t.Text) },
        TextReasoningContent r => new RecordedContent { Type = "reasoning", Text = settings.ApplyRedactors(r.Text) },
        FunctionCallContent c => new RecordedContent
        {
            Type = "functionCall",
            CallId = c.CallId,
            Name = c.Name,
            Arguments = c.Arguments is null ? null : ToElement(c.Arguments, settings.ApplyRedactors),
        },
        FunctionResultContent r => new RecordedContent { Type = "functionResult", CallId = r.CallId, Result = ToElement(r.Result, settings.ApplyRedactors) },
        _ => null,   // usage is recorded separately; other content (images, audio) is not replayed in 0.1
    };

    private static AIContent ToContent(RecordedContent c) => c.Type switch
    {
        "reasoning" => new TextReasoningContent(c.Text),
        "functionCall" => new FunctionCallContent(c.CallId ?? "", c.Name ?? "", Arguments(c.Arguments)),
        "functionResult" => new FunctionResultContent(c.CallId ?? "", c.Result),
        _ => new TextContent(c.Text),
    };

    private static Dictionary<string, object?>? Arguments(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } element) return null;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject()) result[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.Clone();
        return result;
    }

    private static JsonElement ToElement(object? value, Func<string, string> text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) Fingerprint.Value(w, value, text);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static RecordedUsage? Usage(UsageDetails? usage) => usage is null ? null : new RecordedUsage
    {
        Input = usage.InputTokenCount,
        Output = usage.OutputTokenCount,
        Total = usage.TotalTokenCount,
        CachedInput = usage.CachedInputTokenCount,
        Reasoning = usage.ReasoningTokenCount,
    };

    private static UsageDetails Add(UsageDetails? total, UsageDetails more)
    {
        total ??= new UsageDetails();
        total.InputTokenCount = Sum(total.InputTokenCount, more.InputTokenCount);
        total.OutputTokenCount = Sum(total.OutputTokenCount, more.OutputTokenCount);
        total.TotalTokenCount = Sum(total.TotalTokenCount, more.TotalTokenCount);
        total.CachedInputTokenCount = Sum(total.CachedInputTokenCount, more.CachedInputTokenCount);
        total.ReasoningTokenCount = Sum(total.ReasoningTokenCount, more.ReasoningTokenCount);
        return total;

        static long? Sum(long? a, long? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);
    }
}
