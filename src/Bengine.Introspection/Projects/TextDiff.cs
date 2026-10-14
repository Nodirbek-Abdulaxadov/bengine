namespace Bengine.Introspection.Projects;

/// <summary>How a line differs between two texts.</summary>
public enum DiffKind
{
    /// <summary>The line is in both.</summary>
    Same,

    /// <summary>The line is only in the new text.</summary>
    Added,

    /// <summary>The line is only in the old text.</summary>
    Removed,
}

/// <summary>One line of a line diff.</summary>
/// <param name="Kind">How the line differs.</param>
/// <param name="Text">The line.</param>
public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>A line diff of two texts (longest common subsequence), for local history and data compare previews.</summary>
public static class TextDiff
{
    /// <summary>Diffs two texts line by line.</summary>
    /// <param name="oldText">The old text.</param>
    /// <param name="newText">The new text.</param>
    /// <returns>The lines of both, in order, marked same, added, or removed.</returns>
    public static IReadOnlyList<DiffLine> Lines(string oldText, string newText)
    {
        var a = Split(oldText);
        var b = Split(newText);

        // Common head and tail are cheap to strip and keep the table small.
        var head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head])
        {
            head++;
        }

        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[^(tail + 1)] == b[^(tail + 1)])
        {
            tail++;
        }

        var result = new List<DiffLine>(a.Length + b.Length);
        result.AddRange(a[..head].Select(l => new DiffLine(DiffKind.Same, l)));
        var x = a[head..^tail];
        var y = b[head..^tail];
        if ((long)x.Length * y.Length > 4_000_000)
        {
            // Too large for the table: show it as replaced.
            result.AddRange(x.Select(l => new DiffLine(DiffKind.Removed, l)));
            result.AddRange(y.Select(l => new DiffLine(DiffKind.Added, l)));
        }
        else
        {
            var lcs = new int[x.Length + 1, y.Length + 1];
            for (var i = x.Length - 1; i >= 0; i--)
            {
                for (var j = y.Length - 1; j >= 0; j--)
                {
                    lcs[i, j] = x[i] == y[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            int p = 0, q = 0;
            while (p < x.Length || q < y.Length)
            {
                if (p < x.Length && q < y.Length && x[p] == y[q])
                {
                    result.Add(new DiffLine(DiffKind.Same, x[p++]));
                    q++;
                }
                else if (q < y.Length && (p == x.Length || lcs[p, q + 1] >= lcs[p + 1, q]))
                {
                    result.Add(new DiffLine(DiffKind.Added, y[q++]));
                }
                else
                {
                    result.Add(new DiffLine(DiffKind.Removed, x[p++]));
                }
            }
        }

        result.AddRange(a[^tail..].Select(l => new DiffLine(DiffKind.Same, l)));
        return result;
    }

    /// <summary>Renders a diff as unified text: <c>+</c> added, <c>-</c> removed, two spaces for unchanged.</summary>
    /// <param name="lines">The diff.</param>
    /// <returns>The text.</returns>
    public static string Unified(IEnumerable<DiffLine> lines) =>
        string.Join('\n', lines.Select(l => (l.Kind switch { DiffKind.Added => "+ ", DiffKind.Removed => "- ", _ => "  " }) + l.Text));

    private static string[] Split(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
