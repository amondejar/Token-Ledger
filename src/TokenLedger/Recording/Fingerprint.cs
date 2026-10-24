using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace TokenLedger;

/// <summary>
/// The identity of a request for replay: a hash of everything that changes what the model answers
/// (model, instructions, sampling options, tools, messages), after the configured normalizers.
/// Provider-generated ids such as tool call ids are left out, so they never prevent a replay.
/// </summary>
internal static class Fingerprint
{
    public static string Compute(IEnumerable<ChatMessage> messages, ChatOptions? options, string? defaultModel, LedgerOptions settings)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            var text = settings.ApplyNormalizers;
            w.WriteStartObject();
            w.WriteString("model", options?.ModelId ?? defaultModel);
            if (options is not null)
            {
                if (options.Instructions is { } instructions) w.WriteString("instructions", text(instructions));
                Number(w, "temperature", options.Temperature);
                Number(w, "topP", options.TopP);
                Number(w, "frequencyPenalty", options.FrequencyPenalty);
                Number(w, "presencePenalty", options.PresencePenalty);
                if (options.TopK is { } topK) w.WriteNumber("topK", topK);
                if (options.MaxOutputTokens is { } max) w.WriteNumber("maxOutputTokens", max);
                if (options.Seed is { } seed) w.WriteNumber("seed", seed);
                if (options.StopSequences is { Count: > 0 } stops)
                {
                    w.WriteStartArray("stop");
                    foreach (var stop in stops) w.WriteStringValue(stop);
                    w.WriteEndArray();
                }

                switch (options.ResponseFormat)
                {
                    case ChatResponseFormatJson json:
                        w.WriteStartObject("responseFormat");
                        w.WriteString("type", "json");
                        if (json.SchemaName is { } schemaName) w.WriteString("name", schemaName);
                        if (json.Schema is { } schema) { w.WritePropertyName("schema"); Value(w, schema, text); }
                        w.WriteEndObject();
                        break;
                    case ChatResponseFormatText:
                        w.WriteString("responseFormat", "text");
                        break;
                }

                switch (options.ToolMode)
                {
                    case RequiredChatToolMode required: w.WriteString("toolMode", "required:" + required.RequiredFunctionName); break;
                    case NoneChatToolMode: w.WriteString("toolMode", "none"); break;
                }

                if (options.Tools is { Count: > 0 } tools)
                {
                    w.WriteStartArray("tools");
                    foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
                    {
                        w.WriteStartObject();
                        w.WriteString("name", tool.Name);
                        if (!string.IsNullOrEmpty(tool.Description)) w.WriteString("description", tool.Description);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                }
            }

            w.WriteStartArray("messages");
            foreach (var message in messages)
            {
                w.WriteStartObject();
                w.WriteString("role", message.Role.Value);
                if (message.AuthorName is { } author) w.WriteString("author", author);
                w.WriteStartArray("contents");
                foreach (var content in message.Contents)
                {
                    switch (content)
                    {
                        case TextContent t:
                            w.WriteStartObject(); w.WriteString("text", text(t.Text)); w.WriteEndObject();
                            break;
                        case FunctionCallContent call:
                            w.WriteStartObject();
                            w.WriteString("call", call.Name);
                            w.WritePropertyName("arguments");
                            Value(w, call.Arguments, text);
                            w.WriteEndObject();
                            break;
                        case FunctionResultContent result:
                            w.WriteStartObject();
                            w.WritePropertyName("result");
                            Value(w, result.Result, text);
                            w.WriteEndObject();
                            break;
                        case TextReasoningContent:
                        case UsageContent:
                            break;   // reasoning is the model's own output; usage is not part of the request
                        default:
                            w.WriteStartObject(); w.WriteString("content", content.GetType().Name); w.WriteEndObject();
                            break;
                    }
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan))[..32];
    }

    private static void Number(Utf8JsonWriter w, string name, float? value)
    {
        if (value is { } v) w.WriteNumber(name, Math.Round((double)v, 6));
    }

    /// <summary>
    /// Writes a tool argument or result canonically: object keys sorted, strings normalized.
    /// Unknown objects are written as their <see cref="object.ToString"/>, so no reflection-based serialization is needed.
    /// </summary>
    internal static void Value(Utf8JsonWriter w, object? value, Func<string, string> text)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(text(s)); break;
            case bool b: w.WriteBooleanValue(b); break;
            case JsonElement e: Element(w, e, text); break;
            case JsonNode node: Element(w, JsonSerializer.SerializeToElement(node, JsonNodeContext.Default.JsonNode), text); break;
            case JsonDocument d: Element(w, d.RootElement, text); break;
            case int or long or short or byte or sbyte or ushort or uint or ulong:
                w.WriteNumberValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture)); break;
            case float or double or decimal:
                w.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture)); break;
            case IEnumerable<KeyValuePair<string, object?>> map:
                w.WriteStartObject();
                foreach (var (key, item) in map.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(key);
                    Value(w, item, text);
                }

                w.WriteEndObject();
                break;
            case IEnumerable list and not IDictionary:
                w.WriteStartArray();
                foreach (var item in list) Value(w, item, text);
                w.WriteEndArray();
                break;
            default:
                w.WriteStringValue(text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""));
                break;
        }
    }

    private static void Element(Utf8JsonWriter w, JsonElement e, Func<string, string> text)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    Element(w, p.Value, text);
                }

                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray()) Element(w, item, text);
                w.WriteEndArray();
                break;
            case JsonValueKind.String:
                w.WriteStringValue(text(e.GetString()!));
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonNode))]
internal sealed partial class JsonNodeContext : System.Text.Json.Serialization.JsonSerializerContext;
