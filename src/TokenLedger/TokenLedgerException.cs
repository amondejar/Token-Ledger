namespace TokenLedger;

/// <summary>
/// A scenario failed: a request had no recording in replay mode, a budget was exceeded, or the calls differ from
/// the baseline. Test frameworks report it like any failed assertion.
/// </summary>
public sealed class TokenLedgerException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TokenLedgerException(string message) : base(message) { }

    /// <summary>Creates the exception with the error that caused it.</summary>
    public TokenLedgerException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>How a difference from the baseline is classified.</summary>
public enum LedgerChangeKind
{
    /// <summary>More expensive: more model calls, or more tokens beyond the tolerance.</summary>
    Regression,

    /// <summary>Different behavior: other tools, other models, or calls without usage data.</summary>
    Change,

    /// <summary>Cheaper: fewer model calls, or fewer tokens beyond the tolerance.</summary>
    Improvement,
}

/// <summary>One difference between a scenario run and its baseline.</summary>
/// <param name="Kind">The classification.</param>
/// <param name="Description">What changed, for example <c>model calls 2 → 5</c>.</param>
public sealed record LedgerDifference(LedgerChangeKind Kind, string Description)
{
    /// <inheritdoc />
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant(),-11} {Description}";
}
