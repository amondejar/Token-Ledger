using System.Globalization;
using System.Reflection;

namespace TokenLedger.Tool;

/// <summary>Parses the command line and runs a command. Returns the process exit code.</summary>
internal static class Cli
{
    public const int Ok = 0;
    public const int ChangesFound = 1;
    public const int UsageError = 2;

    private const string Usage = """
        Usage:
          dotnet tokenledger diff [<base> [<head>]] [options]
          dotnet tokenledger summary [<ref>] [options]
          dotnet tokenledger pricing init [<folder>] [--currency <code>]

        diff      Compares the TokenLedger baselines of <base> and <head> and lists, per scenario, the regressions,
                  behavior changes and improvements. <base> and <head> are git refs; <base> defaults to HEAD and
                  <head> to the working tree, so a bare `diff` shows what your last test run changed.
        summary   What each scenario costs at <ref> (default: the working tree).
        pricing   init creates or completes pricing.json with an empty entry for each model your scenarios use.
                  <folder> defaults to every tokenledger folder below the current one.

        Options:
          --format <format>     text (default), markdown or json.
          --output <file>       Write the report to a file instead of the console.
          --fail-on <level>     diff exits with code 1 on: regression, change (any difference, new or removed
                                scenario), or never (default).
          --tolerance <0..1>    Relative token change to ignore. Default: 0.10.
          --pricing <file>      Prices to use instead of each folder's pricing.json.
          --dir <name>          The TokenLedger folder name. Default: tokenledger.
          --currency <code>     Currency of a new pricing.json. Default: USD.

        Examples:
          dotnet tokenledger diff                                   what the last test run changed
          dotnet tokenledger diff origin/main HEAD --fail-on regression
          dotnet tokenledger diff v0.3.0 --format markdown --output report.md
          dotnet tokenledger pricing init --currency EUR
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? UsageError : Ok;
        }

        if (args[0] is "--version")
        {
            output.WriteLine(typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
            return Ok;
        }

        try
        {
            var options = Options.Parse(args[1..]);
            return args[0] switch
            {
                "diff" => Diff(options, output, workingDirectory),
                "summary" => Summary(options, output, workingDirectory),
                "pricing" => Pricing(options, output, workingDirectory),
                _ => throw new UsageException($"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException e)
        {
            error.WriteLine(e.Message + "\n");
            error.WriteLine(Usage);
            return UsageError;
        }
        catch (Exception e) when (e is ToolException or TokenLedgerException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine("error: " + e.Message);
            return UsageError;
        }
    }

    private static int Diff(Options options, TextWriter output, string workingDirectory)
    {
        if (options.Positional.Count > 2) throw new UsageException("diff takes an optional <base> and <head>.");
        var root = Git.Root(workingDirectory);
        var baseSide = Source.Open(root, options.Positional.ElementAtOrDefault(0) ?? "HEAD", options.Dir);
        var headSide = Source.Open(root, options.Positional.ElementAtOrDefault(1), options.Dir);
        var result = DiffResult.Compare(baseSide, headSide, options.Tolerance, options.PricingFile, workingDirectory);

        Write(options, output, workingDirectory, options.Format switch
        {
            ReportFormat.Markdown => Reports.Markdown(result),
            ReportFormat.Json => Reports.Json(result),
            _ => Reports.Text(result),
        });

        var fail = options.FailOn switch
        {
            FailOn.Regression => result.Count(ScenarioStatus.Regression) > 0,
            FailOn.Change => result.Changed.Any(),
            _ => false,
        };
        return fail ? ChangesFound : Ok;
    }

    private static int Summary(Options options, TextWriter output, string workingDirectory)
    {
        if (options.Positional.Count > 1) throw new UsageException("summary takes an optional <ref>.");
        var root = Git.Root(workingDirectory);
        var side = Source.Open(root, options.Positional.ElementAtOrDefault(0), options.Dir);
        var result = DiffResult.Compare(side, side, options.Tolerance, options.PricingFile, workingDirectory);
        Write(options, output, workingDirectory, options.Format == ReportFormat.Json ? Reports.Json(result) : Reports.Summary(result, options.Format == ReportFormat.Markdown));
        return Ok;
    }

    private static int Pricing(Options options, TextWriter output, string workingDirectory)
    {
        if (options.Positional.ElementAtOrDefault(0) != "init" || options.Positional.Count > 2)
            throw new UsageException("Use: dotnet tokenledger pricing init [<folder>].");

        IReadOnlyList<string> folders = options.Positional.Count == 2
            ? [Path.GetFullPath(options.Positional[1], workingDirectory)]
            : PricingInit.Find(workingDirectory, options.Dir);
        if (folders.Count == 0)
            throw new ToolException($"No '{options.Dir}' folder with baselines or recordings below {workingDirectory}. Run your TokenLedger tests first, or pass the folder.");
        if (folders.Count == 1 && !Directory.Exists(folders[0])) throw new ToolException($"{folders[0]} does not exist.");

        return PricingInit.Run(folders, options.Currency, output, workingDirectory);
    }

    private static void Write(Options options, TextWriter output, string workingDirectory, string report)
    {
        if (options.Output is { } file) File.WriteAllText(Path.GetFullPath(file, workingDirectory), report);
        else output.Write(report);
    }
}

internal enum ReportFormat { Text, Markdown, Json }

internal enum FailOn { Never, Regression, Change }

internal sealed class Options
{
    public List<string> Positional { get; } = [];
    public ReportFormat Format { get; private set; }
    public string? Output { get; private set; }
    public FailOn FailOn { get; private set; }
    public double Tolerance { get; private set; } = 0.10;
    public string? PricingFile { get; private set; }
    public string Dir { get; private set; } = "tokenledger";
    public string? Currency { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Value() => i + 1 < args.Length ? args[++i] : throw new UsageException($"{arg} needs a value.");

            switch (arg)
            {
                case "--format":
                    options.Format = Value() switch
                    {
                        "text" => ReportFormat.Text,
                        "markdown" or "md" => ReportFormat.Markdown,
                        "json" => ReportFormat.Json,
                        var other => throw new UsageException($"Unknown format '{other}': use text, markdown or json."),
                    };
                    break;
                case "--output" or "-o":
                    options.Output = Value();
                    break;
                case "--fail-on":
                    options.FailOn = Value() switch
                    {
                        "never" or "none" => FailOn.Never,
                        "regression" => FailOn.Regression,
                        "change" or "any" => FailOn.Change,
                        var other => throw new UsageException($"Unknown --fail-on level '{other}': use regression, change or never."),
                    };
                    break;
                case "--tolerance":
                    var tolerance = Value();
                    options.Tolerance = double.TryParse(tolerance, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t is >= 0 and <= 1
                        ? t
                        : throw new UsageException($"--tolerance '{tolerance}' must be a number between 0 and 1, such as 0.1.");
                    break;
                case "--pricing":
                    options.PricingFile = Value();
                    break;
                case "--dir":
                    options.Dir = Value();
                    break;
                case "--currency":
                    options.Currency = Value().Trim().ToUpperInvariant();
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"Unknown option '{arg}'.");
                    options.Positional.Add(arg);
                    break;
            }
        }

        return options;
    }
}
