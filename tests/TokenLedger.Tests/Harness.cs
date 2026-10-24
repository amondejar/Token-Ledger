using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TokenLedger.Tests;

/// <summary>A minimal test runner: each test is a named async delegate; a thrown exception fails it.</summary>
internal static class Harness
{
    private static readonly List<(string Name, Func<Task> Body)> Tests = [];

    public static void Test(string name, Func<Task> body) => Tests.Add((name, body));

    public static void Test(string name, Action body) => Tests.Add((name, () => { body(); return Task.CompletedTask; }));

    public static async Task<int> RunAsync()
    {
        var failed = 0;
        var clock = Stopwatch.StartNew();
        foreach (var (name, body) in Tests)
        {
            try
            {
                await body();
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}\n        {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"\n{Tests.Count - failed} passed, {failed} failed ({clock.ElapsedMilliseconds} ms)");
        return failed == 0 ? 0 : 1;
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual, [CallerArgumentExpression(nameof(actual))] string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{what}: expected <{expected}>, got <{actual}>");
    }

    public static void True(bool condition, [CallerArgumentExpression(nameof(condition))] string? what = null)
    {
        if (!condition) throw new AssertionException($"expected true: {what}");
    }

    public static void Contains(string expected, string? actual)
    {
        if (actual is null || !actual.Contains(expected, StringComparison.Ordinal))
            throw new AssertionException($"expected to contain <{expected}>, got <{actual}>");
    }

    public static void DoesNotContain(string unexpected, string? actual)
    {
        if (actual is not null && actual.Contains(unexpected, StringComparison.Ordinal))
            throw new AssertionException($"expected not to contain <{unexpected}>, got <{actual}>");
    }

    public static TException Throws<TException>(Action body) where TException : Exception
    {
        try { body(); }
        catch (TException ex) { return ex; }
        throw new AssertionException($"expected {typeof(TException).Name}, nothing was thrown");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> body) where TException : Exception
    {
        try { await body(); }
        catch (TException ex) { return ex; }
        throw new AssertionException($"expected {typeof(TException).Name}, nothing was thrown");
    }
}

internal sealed class AssertionException(string message) : Exception(message);
