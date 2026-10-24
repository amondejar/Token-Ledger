using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TokenLedger.Tool;

/// <summary>A baseline file at one side of the comparison: its folder, scenario name and content.</summary>
internal sealed record BaselineEntry(string Folder, string Scenario, string Path, string Content);

/// <summary>
/// Reads TokenLedger folders from a git ref or from the working tree. A TokenLedger folder is any folder named
/// <c>tokenledger</c> (or the --dir name) that holds <c>baselines/</c>; its scenarios are the JSON files below it.
/// </summary>
internal sealed class Source
{
    public const string WorkingTree = "working tree";

    private readonly string _root;
    private readonly string? _reference;
    private readonly Regex _baseline;
    private readonly string[] _files;

    private Source(string root, string? reference, string folderName)
    {
        _root = root;
        _reference = reference;
        _baseline = new Regex($"^(?<folder>(?:.*/)?{Regex.Escape(folderName.Trim('/'))})/baselines/(?<scenario>.+)\\.json$", RegexOptions.CultureInvariant);
        var listing = reference is null
            ? Git.Run(root, "ls-files", "--cached", "--others", "--exclude-standard")
            : Git.Run(root, "ls-tree", "-r", "--name-only", reference);
        _files = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The ref's name, or <see cref="WorkingTree"/>.</summary>
    public string Label => _reference ?? WorkingTree;

    /// <summary>Opens a git ref, or the working tree when <paramref name="reference"/> is null.</summary>
    public static Source Open(string root, string? reference, string folderName)
    {
        if (reference is not null && !Git.TryRun(root, out _, "rev-parse", "--verify", "--quiet", reference + "^{commit}"))
            throw new ToolException($"'{reference}' is not a git ref here. In CI, fetch it first (actions/checkout with fetch-depth: 0, or git fetch origin {reference}).");
        return new Source(root, reference, folderName);
    }

    public IEnumerable<BaselineEntry> Baselines()
    {
        foreach (var path in _files)
        {
            var match = _baseline.Match(path);
            if (!match.Success || match.Groups["scenario"].Value.Contains("/baselines/", StringComparison.Ordinal)) continue;
            if (Read(path) is { } content) yield return new BaselineEntry(match.Groups["folder"].Value, match.Groups["scenario"].Value, path, content);
        }
    }

    public string? Read(string path)
    {
        if (_reference is null)
        {
            var file = System.IO.Path.Combine(_root, path);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }

        return Git.TryRun(_root, out var content, "show", $"{_reference}:{path}") ? content : null;
    }
}

internal static class Git
{
    public static string Root(string workingDirectory) => Run(workingDirectory, "rev-parse", "--show-toplevel").Trim();

    public static string Run(string directory, params string[] arguments) =>
        TryRun(directory, out var output, arguments)
            ? output
            : throw new ToolException($"git {string.Join(' ', arguments)} failed: {output.Trim()}");

    public static bool TryRun(string directory, out string output, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=off");   // paths with accents as they are
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new ToolException("could not start git.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ToolException("git was not found on PATH.");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            output = process.ExitCode == 0 ? stdout.Result : stderr;
            return process.ExitCode == 0;
        }
    }
}

internal sealed class UsageException(string message) : Exception(message);

internal sealed class ToolException(string message) : Exception(message);
