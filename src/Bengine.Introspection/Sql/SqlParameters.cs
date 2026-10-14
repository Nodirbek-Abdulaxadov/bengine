namespace Bengine.Introspection.Sql;

using System.Globalization;
using System.Text;
using Bengine.Enums;

/// <summary>One occurrence of a query parameter in SQL text.</summary>
/// <param name="Name">The parameter key: <c>name</c> for <c>:name</c> and <c>${name}</c>, <c>?1</c>, <c>?2</c>… for positional markers.</param>
/// <param name="Start">The offset of the marker.</param>
/// <param name="Length">The marker length.</param>
public sealed record SqlParameterOccurrence(string Name, int Start, int Length);

/// <summary>
/// Finds and substitutes the user parameters of a console query, as a database
/// IDE's "Parameters" panel does: <c>:name</c> and <c>${name}</c> markers, and
/// positional <c>?</c> markers. Markers inside strings, comments, and quoted
/// identifiers are ignored, as are <c>::</c> casts, <c>:=</c> assignments, and
/// PostgreSQL's <c>?</c> jsonb operator. Substitution is textual — the value is
/// inserted exactly as typed (<c>42</c>, <c>'abc'</c>, <c>now()</c>) — because
/// the console runs the user's own SQL; the data editor binds real parameters.
/// </summary>
public static class SqlParameters
{
    /// <summary>Finds the parameter markers of a statement.</summary>
    /// <param name="sql">The SQL text.</param>
    /// <param name="provider">The dialect.</param>
    /// <returns>The occurrences in text order.</returns>
    public static IReadOnlyList<SqlParameterOccurrence> Find(string sql, DatabaseProvider provider)
    {
        var result = new List<SqlParameterOccurrence>();
        var positional = 0;
        foreach (var lexeme in SqlLexer.Lex(sql, provider))
        {
            if (lexeme.Kind != SqlLexemeKind.Parameter)
            {
                continue;
            }

            var name = lexeme.Text switch
            {
                "?" => "?" + (++positional).ToString(CultureInfo.InvariantCulture),
                var t when t.StartsWith("${", StringComparison.Ordinal) => t[2..^(lexeme.IsClosed ? 1 : 0)].Trim(),
                var t => t[1..],
            };

            if (name.Length > 0)
            {
                result.Add(new SqlParameterOccurrence(name, lexeme.Start, lexeme.Length));
            }
        }

        return result;
    }

    /// <summary>Lists the distinct parameter names of a statement, in first-use order.</summary>
    /// <param name="sql">The SQL text.</param>
    /// <param name="provider">The dialect.</param>
    /// <returns>The names.</returns>
    public static IReadOnlyList<string> Names(string sql, DatabaseProvider provider) =>
        Find(sql, provider).Select(p => p.Name).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Replaces each parameter marker with its value text.</summary>
    /// <param name="sql">The SQL text.</param>
    /// <param name="values">The value text per parameter name; markers without a value are left as they are.</param>
    /// <param name="provider">The dialect.</param>
    /// <returns>The SQL with values substituted.</returns>
    public static string Substitute(string sql, IReadOnlyDictionary<string, string> values, DatabaseProvider provider)
    {
        var builder = new StringBuilder(sql);
        foreach (var occurrence in Find(sql, provider).Reverse())
        {
            if (values.TryGetValue(occurrence.Name, out var value))
            {
                builder.Remove(occurrence.Start, occurrence.Length).Insert(occurrence.Start, value);
            }
        }

        return builder.ToString();
    }
}
