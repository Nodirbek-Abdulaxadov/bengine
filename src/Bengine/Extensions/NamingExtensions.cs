namespace Bengine.Extensions;

using System.Text;

/// <summary>
/// Case-conversion helpers used by code generators to render identifiers in the
/// idiom of each target language. Splitting is performed on underscores, spaces,
/// hyphens, and camel-case boundaries.
/// </summary>
public static class NamingExtensions
{
    /// <summary>Converts an identifier to <c>PascalCase</c>.</summary>
    /// <param name="value">The source identifier.</param>
    /// <returns>The Pascal-cased identifier.</returns>
    public static string ToPascalCase(this string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var word in SplitWords(value))
        {
            builder.Append(char.ToUpperInvariant(word[0]));
            if (word.Length > 1)
            {
                builder.Append(word[1..].ToLowerInvariant());
            }
        }

        return builder.ToString();
    }

    /// <summary>Converts an identifier to <c>camelCase</c>.</summary>
    /// <param name="value">The source identifier.</param>
    /// <returns>The camel-cased identifier.</returns>
    public static string ToCamelCase(this string value)
    {
        var pascal = value.ToPascalCase();
        return pascal.Length == 0
            ? pascal
            : char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    /// <summary>Converts an identifier to <c>snake_case</c>.</summary>
    /// <param name="value">The source identifier.</param>
    /// <returns>The snake-cased identifier.</returns>
    public static string ToSnakeCase(this string value) =>
        string.Join('_', SplitWords(value).Select(w => w.ToLowerInvariant()));

    private static IEnumerable<string> SplitWords(string value)
    {
        var builder = new StringBuilder();
        char? previous = null;

        foreach (var ch in value)
        {
            if (ch is '_' or ' ' or '-')
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }
            }
            else
            {
                if (char.IsUpper(ch) && previous is { } p && !char.IsUpper(p) && builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                builder.Append(ch);
            }

            previous = ch;
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }
}
