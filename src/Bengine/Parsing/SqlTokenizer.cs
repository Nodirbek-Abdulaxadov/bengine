namespace Bengine.Parsing;

using System.Collections.Generic;

/// <summary>
/// Converts raw SQL text into a flat list of <see cref="SqlToken"/>. Whitespace
/// and comments (<c>--</c>, <c>#</c>, and <c>/* ... */</c>) are discarded. The
/// tokenizer understands the delimited-identifier styles of all supported
/// providers — double quotes, backticks, and square brackets — so a single pass
/// serves PostgreSQL, SQL Server, MySQL, and SQLite.
/// </summary>
public static class SqlTokenizer
{
    /// <summary>Tokenizes a SQL document.</summary>
    /// <param name="sql">The SQL text to tokenize.</param>
    /// <returns>The ordered tokens, excluding whitespace and comments.</returns>
    public static IReadOnlyList<SqlToken> Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        var i = 0;
        var length = sql.Length;

        while (i < length)
        {
            var c = sql[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // Line comments: -- ... and MySQL # ...
            if ((c == '-' && i + 1 < length && sql[i + 1] == '-') || c == '#')
            {
                i = SkipToLineEnd(sql, i);
                continue;
            }

            // Block comments: /* ... */
            if (c == '/' && i + 1 < length && sql[i + 1] == '*')
            {
                i = SkipBlockComment(sql, i + 2);
                continue;
            }

            if (c is '"' or '`' or '[')
            {
                tokens.Add(ReadQuotedIdentifier(sql, ref i, c));
                continue;
            }

            if (c == '\'')
            {
                tokens.Add(ReadString(sql, ref i));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                tokens.Add(ReadWord(sql, ref i));
                continue;
            }

            if (char.IsDigit(c))
            {
                tokens.Add(ReadNumber(sql, ref i));
                continue;
            }

            tokens.Add(new SqlToken(SqlTokenKind.Punct, sql[i].ToString(), i, i + 1, '\0'));
            i++;
        }

        return tokens;
    }

    private static int SkipToLineEnd(string sql, int i)
    {
        while (i < sql.Length && sql[i] != '\n')
        {
            i++;
        }

        return i;
    }

    private static int SkipBlockComment(string sql, int i)
    {
        while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
        {
            i++;
        }

        return i + 1 < sql.Length ? i + 2 : sql.Length;
    }

    private static SqlToken ReadQuotedIdentifier(string sql, ref int i, char open)
    {
        var close = open == '[' ? ']' : open;
        var start = i;
        i++; // consume opening delimiter
        var value = new System.Text.StringBuilder();

        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == close)
            {
                // A doubled closing delimiter is an escaped literal delimiter.
                if (i + 1 < sql.Length && sql[i + 1] == close)
                {
                    value.Append(close);
                    i += 2;
                    continue;
                }

                i++; // consume closing delimiter
                break;
            }

            value.Append(c);
            i++;
        }

        return new SqlToken(SqlTokenKind.Quoted, value.ToString(), start, i, open);
    }

    private static SqlToken ReadString(string sql, ref int i)
    {
        var start = i;
        i++; // consume opening quote

        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\'')
            {
                if (i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    i += 2; // escaped quote
                    continue;
                }

                i++; // consume closing quote
                break;
            }

            // Backslash escapes (MySQL) — skip the escaped character.
            if (c == '\\' && i + 1 < sql.Length)
            {
                i += 2;
                continue;
            }

            i++;
        }

        return new SqlToken(SqlTokenKind.String, sql[start..i], start, i, '\0');
    }

    private static SqlToken ReadWord(string sql, ref int i)
    {
        var start = i;
        while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$'))
        {
            i++;
        }

        return new SqlToken(SqlTokenKind.Word, sql[start..i], start, i, '\0');
    }

    private static SqlToken ReadNumber(string sql, ref int i)
    {
        var start = i;
        while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.'))
        {
            i++;
        }

        // Optional exponent.
        if (i < sql.Length && (sql[i] == 'e' || sql[i] == 'E'))
        {
            i++;
            if (i < sql.Length && (sql[i] == '+' || sql[i] == '-'))
            {
                i++;
            }

            while (i < sql.Length && char.IsDigit(sql[i]))
            {
                i++;
            }
        }

        return new SqlToken(SqlTokenKind.Number, sql[start..i], start, i, '\0');
    }
}
