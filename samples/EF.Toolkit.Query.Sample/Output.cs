using System.Diagnostics;

namespace EFToolkit.Query.Sample;

/// <summary>Console formatting, kept out of the numbered sections so they read as the point.</summary>
internal static class Output
{
    /// <summary>
    ///     The totals the seed cycles through. Four values against a three-value status cycle, so the
    ///     two never line up and a filter on both still matches something.
    /// </summary>
    internal static decimal[] SeedTotals { get; } = [10.25m, 99.99m, 55.50m, 10.25m];

    internal static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} ".PadRight(78, '─'));
    }

    internal static async Task<TimeSpan> Time(Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        await action();
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    internal static string Abbreviate(string? token)
        => token is null ? "(none)" : token.Length <= 24 ? token : $"{token[..24]}…";

    /// <summary>
    ///     The first sentence of a refusal message. The full text names the way out, which is the
    ///     point of it — but it is several lines long, and these sections are showing that the refusal
    ///     happens rather than reproducing what it says.
    /// </summary>
    internal static string FirstSentence(string message)
    {
        var stop = message.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? message : message[..(stop + 1)];
    }
}
