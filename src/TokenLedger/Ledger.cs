using System.Runtime.CompilerServices;

namespace TokenLedger;

/// <summary>
/// The entry point for tests: open a scenario around the code that calls the model, and dispose it to compare
/// the calls with the scenario's baseline.
/// </summary>
/// <example>
/// <code>
/// await using var ledger = Ledger.Scenario("support-chat/order-status");
/// var answer = await chat.GetResponseAsync("Where is my order 42?");
/// ledger.Budget(maxCalls: 3, maxTokens: 4_000);
/// </code>
/// </example>
public static class Ledger
{
    private static readonly AsyncLocal<LedgerScenario?> Active = new();
    private static readonly AsyncLocal<Func<string?>?> ActiveEndpoint = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, LedgerScenario> Open = new(StringComparer.Ordinal);

    /// <summary>The process-wide settings. Change them before the first scenario, for example in a module initializer.</summary>
    public static LedgerOptions Options { get; } = new();

    /// <summary>Changes <see cref="Options"/>.</summary>
    public static void Configure(Action<LedgerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Options);
    }

    /// <summary>The scenario open in the current async flow, or null.</summary>
    public static LedgerScenario? Current => Active.Value;

    /// <summary>
    /// Opens a scenario: until it is disposed, every model call in this async flow that goes through a
    /// TokenLedger client is replayed or recorded and measured. Disposing it verifies the calls.
    /// </summary>
    /// <param name="name">
    /// The scenario's name and the path of its files under the TokenLedger folder: lower-case letters, digits,
    /// <c>.</c>, <c>_</c> and <c>-</c>, with <c>/</c> between folders, for example <c>support-chat/order-status</c>.
    /// </param>
    /// <param name="callerFilePath">Filled in by the compiler; locates the test project.</param>
    public static LedgerScenario Scenario(string name, [CallerFilePath] string callerFilePath = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Patterns.ScenarioName().IsMatch(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a valid scenario name. Use lower-case letters, digits, '.', '_' and '-', with '/' between folders, for example 'support-chat/order-status'.",
                nameof(name));
        }

        var project = LedgerPaths.ProjectDirectory(callerFilePath);
        var root = LedgerPaths.Root(project, Options.Directory);
        var scenario = new LedgerScenario(name, Options, LedgerEnvironment.Mode(Options), LedgerEnvironment.Update(Options), project, root, Active.Value);
        Active.Value = scenario;
        Open[scenario.Id] = scenario;
        return scenario;
    }

    internal static void Restore(LedgerScenario closing, LedgerScenario? previous)
    {
        Open.TryRemove(closing.Id, out _);
        if (ReferenceEquals(Active.Value, closing)) Active.Value = previous;
    }

    /// <summary>An open scenario by its id, for requests that name it (TokenLedger.AspNetCore).</summary>
    internal static LedgerScenario? Find(string id) => Open.GetValueOrDefault(id);

    /// <summary>Makes <paramref name="scenario"/> current in this async flow until disposed.</summary>
    internal static IDisposable Enter(LedgerScenario scenario)
    {
        var previous = Active.Value;
        Active.Value = scenario;
        return new Undo(() => Active.Value = previous);
    }

    /// <summary>The endpoint the calls in this async flow belong to, resolved when a call is measured.</summary>
    internal static IDisposable Endpoint(Func<string?> endpoint)
    {
        var previous = ActiveEndpoint.Value;
        ActiveEndpoint.Value = endpoint;
        return new Undo(() => ActiveEndpoint.Value = previous);
    }

    internal static string? CurrentEndpoint => ActiveEndpoint.Value?.Invoke();

    private sealed class Undo(Action undo) : IDisposable
    {
        private Action? _undo = undo;

        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }
}

internal static class LedgerEnvironment
{
    /// <summary>Tests set this to pretend to run in CI, or not.</summary>
    internal static bool? CiOverride { get; set; }

    public static bool IsCi => CiOverride ?? (
        Flag("CI") || Flag("GITHUB_ACTIONS") || Flag("TF_BUILD") || Environment.GetEnvironmentVariable("GITLAB_CI") is not null
        || Environment.GetEnvironmentVariable("JENKINS_URL") is not null || Environment.GetEnvironmentVariable("TEAMCITY_VERSION") is not null);

    public static LedgerMode Mode(LedgerOptions options)
    {
        var value = Environment.GetEnvironmentVariable("TOKENLEDGER_MODE");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return Enum.TryParse<LedgerMode>(value.Trim(), ignoreCase: true, out var mode) && Enum.IsDefined(mode)
                ? mode
                : throw new TokenLedgerException($"TOKENLEDGER_MODE='{value}' is not a mode. Use auto, replay, record or live.");
        }

        return options.Mode ?? (IsCi ? LedgerMode.Replay : LedgerMode.Auto);
    }

    public static bool Update(LedgerOptions options) => options.UpdateBaselines || Flag("TOKENLEDGER_UPDATE");

    private static bool Flag(string name) => Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant() is "1" or "true" or "yes";
}

internal static class LedgerPaths
{
    /// <summary>
    /// The test project's folder: the closest folder with a project file above the calling source file, or, when
    /// that path does not exist on this machine (deterministic CI builds map it), above the test assembly.
    /// </summary>
    public static string ProjectDirectory(string callerFilePath)
    {
        var fromSource = string.IsNullOrEmpty(callerFilePath) ? null : Path.GetDirectoryName(callerFilePath);
        if (fromSource is not null && Directory.Exists(fromSource) && Find(fromSource) is { } project) return project;
        return Find(AppContext.BaseDirectory) ?? Directory.GetCurrentDirectory();

        static string? Find(string start)
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (directory.EnumerateFiles("*.*proj").Any(f => f.Extension is ".csproj" or ".fsproj" or ".vbproj")) return directory.FullName;
            }

            return null;
        }
    }

    public static string Root(string project, string? configured)
    {
        var directory = Environment.GetEnvironmentVariable("TOKENLEDGER_DIR") is { Length: > 0 } fromEnvironment ? fromEnvironment : configured ?? "tokenledger";
        return Path.GetFullPath(Path.Combine(project, directory));
    }

    public static string Display(string project, string path)
    {
        var relative = Path.GetRelativePath(project, path);
        return (relative.StartsWith("..", StringComparison.Ordinal) ? path : relative).Replace('\\', '/');
    }
}
