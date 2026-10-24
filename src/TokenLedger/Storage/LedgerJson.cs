using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenLedger;

/// <summary>The files TokenLedger writes: recordings, baselines and the pricing file.</summary>
[JsonSerializable(typeof(RecordingFile))]
[JsonSerializable(typeof(BaselineFile))]
[JsonSerializable(typeof(BaselineCall))]
[JsonSerializable(typeof(BaselineTotals))]
[JsonSerializable(typeof(PricingFile))]
[JsonSerializable(typeof(string))]
internal sealed partial class LedgerJson : JsonSerializerContext
{
    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = indented,
        IndentSize = 2,
        NewLine = "\n",
        // Files for people to read in a diff: keep accents and quotes as they are.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Indented, for recordings.</summary>
    public static LedgerJson Indented { get; } = new(CreateOptions(indented: true));

    /// <summary>One line per object, for the calls in a baseline.</summary>
    public static LedgerJson Compact { get; } = new(CreateOptions(indented: false));

    /// <summary>For reading files people edit: comments and trailing commas allowed.</summary>
    public static LedgerJson Lenient => Indented;
}

internal sealed class RecordingFile
{
    public int Format { get; set; } = 1;
    public string Scenario { get; set; } = "";
    public List<RecordedInteraction> Interactions { get; set; } = [];
}

internal sealed class RecordedInteraction
{
    /// <summary>Hash of the normalized request; what a replay matches on.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>A summary of the request, for people reading the file.</summary>
    public RecordedRequest? Request { get; set; }

    public RecordedResponse Response { get; set; } = new();
}

internal sealed class RecordedRequest
{
    public string? Model { get; set; }
    public int Messages { get; set; }
    public string? Last { get; set; }
}

internal sealed class RecordedResponse
{
    public string? Model { get; set; }
    public string? ResponseId { get; set; }
    public string? FinishReason { get; set; }
    public List<RecordedMessage> Messages { get; set; } = [];
    public RecordedUsage? Usage { get; set; }
}

internal sealed class RecordedMessage
{
    public string Role { get; set; } = "assistant";
    public string? AuthorName { get; set; }
    public List<RecordedContent> Contents { get; set; } = [];
}

internal sealed class RecordedContent
{
    /// <summary>text, reasoning, functionCall or functionResult.</summary>
    public string Type { get; set; } = "text";
    public string? Text { get; set; }
    public string? CallId { get; set; }
    public string? Name { get; set; }
    public JsonElement? Arguments { get; set; }
    public JsonElement? Result { get; set; }
}

internal sealed class RecordedUsage
{
    public long? Input { get; set; }
    public long? Output { get; set; }
    public long? Total { get; set; }
    public long? CachedInput { get; set; }
    public long? Reasoning { get; set; }
}

internal sealed class BaselineFile
{
    public int Format { get; set; } = 1;
    public string Scenario { get; set; } = "";
    public List<BaselineCall> Calls { get; set; } = [];
    public BaselineTotals Totals { get; set; } = new();
}

internal sealed class BaselineCall
{
    public string Model { get; set; } = "";
    public long? Input { get; set; }
    public long? Output { get; set; }
    public long? CachedInput { get; set; }
    public long? Reasoning { get; set; }
    public List<string> Tools { get; set; } = [];
}

internal sealed class BaselineTotals
{
    public int Calls { get; set; }
    public long Input { get; set; }
    public long Output { get; set; }
    public long CachedInput { get; set; }
    public long Reasoning { get; set; }

    /// <summary>Calls without usage data; omitted when zero.</summary>
    public int? Unreported { get; set; }
}

internal sealed class PricingFile
{
    public string? Currency { get; set; }
    public Dictionary<string, PricingEntry>? Models { get; set; }
}

internal sealed class PricingEntry
{
    public decimal? Input { get; set; }
    public decimal? Output { get; set; }
    public decimal? CachedInput { get; set; }
}
