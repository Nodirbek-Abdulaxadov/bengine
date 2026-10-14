namespace Bengine.Introspection.Sql;

using Bengine.Enums;

/// <summary>The lexical category of a <see cref="SqlLexeme"/>.</summary>
public enum SqlLexemeKind
{
    /// <summary>An unquoted identifier or keyword.</summary>
    Word,

    /// <summary>A delimited identifier: <c>"x"</c>, <c>`x`</c>, or <c>[x]</c>.</summary>
    QuotedIdentifier,

    /// <summary>A string literal, including PostgreSQL <c>E'..'</c> and dollar-quoted bodies.</summary>
    String,

    /// <summary>A numeric literal.</summary>
    Number,

    /// <summary>A line or block comment.</summary>
    Comment,

    /// <summary>A named or positional query parameter (<c>:name</c>, <c>${name}</c>, <c>?</c>).</summary>
    Parameter,

    /// <summary>An operator or punctuation (<c>::</c>, <c>&lt;&gt;</c>, <c>(</c>, <c>;</c>, …).</summary>
    Symbol,

    /// <summary>A run of whitespace.</summary>
    Whitespace,
}

/// <summary>One lexeme of SQL text with its source span.</summary>
/// <param name="Kind">The category.</param>
/// <param name="Start">The inclusive start offset.</param>
/// <param name="Length">The length in characters.</param>
/// <param name="Text">The exact source text.</param>
/// <param name="IsClosed">For strings, quoted identifiers and block comments: whether the closing delimiter was found.</param>
public readonly record struct SqlLexeme(SqlLexemeKind Kind, int Start, int Length, string Text, bool IsClosed = true)
{
    /// <summary>Gets the exclusive end offset.</summary>
    public int End => Start + Length;

    /// <summary>Gets whether the lexeme is neither whitespace nor a comment.</summary>
    public bool IsSignificant => Kind is not (SqlLexemeKind.Whitespace or SqlLexemeKind.Comment);

    /// <summary>Gets the identifier value: the undelimited name of a quoted identifier, otherwise the text.</summary>
    public string Identifier
    {
        get
        {
            if (Kind != SqlLexemeKind.QuotedIdentifier || Text.Length < 2)
            {
                return Text;
            }

            var close = Text[0] == '[' ? ']' : Text[0];
            var body = Text[1..(IsClosed ? ^1 : ^0)];
            return body.Replace(new string(close, 2), close.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>Gets whether the lexeme is the given keyword (an unquoted word, case-insensitive).</summary>
    /// <param name="keyword">The keyword.</param>
    /// <returns><c>true</c> on a match.</returns>
    public bool Is(string keyword) => Kind == SqlLexemeKind.Word && Text is not null && Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets whether the lexeme is the given symbol.</summary>
    /// <param name="symbol">The symbol text.</param>
    /// <returns><c>true</c> on a match.</returns>
    public bool IsSymbol(string symbol) => Kind == SqlLexemeKind.Symbol && Text is not null && Text == symbol;

    /// <summary>Gets whether the lexeme names something: a word or a quoted identifier.</summary>
    public bool IsName => Text is not null && Kind is SqlLexemeKind.Word or SqlLexemeKind.QuotedIdentifier;
}

/// <summary>
/// A lossless SQL lexer for editor features: unlike the DDL tokenizer in
/// <c>Bengine.Parsing</c> it keeps comments and whitespace, records exact spans,
/// and understands every supported dialect's quoting at once — <c>"..."</c>,
/// <c>`...`</c>, <c>[...]</c>, <c>'...'</c> with doubled-quote and (MySQL)
/// backslash escapes, PostgreSQL <c>E'..'</c> and <c>$tag$...$tag$</c> bodies —
/// plus query parameters. Unterminated tokens run to the end of the text and are
/// flagged, so a caret inside an open string or comment can be detected.
/// </summary>
public static class SqlLexer
{
    // The end of an Oracle q-quoted string starting at i (-1 when unterminated), or null when there is none.
    private static int? TryOracleQuote(string sql, int i)
    {
        var at = sql[i] is 'n' or 'N' ? i + 1 : i;
        if (at + 2 >= sql.Length || sql[at] is not ('q' or 'Q') || sql[at + 1] != '\'' || (at > 0 && at == i && (char.IsLetterOrDigit(sql[at - 1]) || sql[at - 1] == '_')))
        {
            return null;
        }

        var open = sql[at + 2];
        var close = open switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => open };
        var end = sql.IndexOf($"{close}'", at + 3, StringComparison.Ordinal);
        return end < 0 ? -1 : end + 2;
    }

    private static readonly string[] Operators =
        ["::", "<>", "!=", "<=", ">=", "||", "->>", "->", "#>>", "#>", ":=", "=>", "@>", "<@", "&&", "**", "<<", ">>"];

    /// <summary>
    /// Splits SQL text into lexemes covering every character, with the quoting
    /// rules of a dialect: MySQL lets a backslash escape inside <c>'...'</c>, and
    /// only PostgreSQL has <c>$tag$...$tag$</c> strings (elsewhere <c>$$</c> is,
    /// for example, a MySQL client delimiter). With no dialect, PostgreSQL's
    /// rules apply. <c>E'...'</c> strings always honor backslashes.
    /// </summary>
    /// <param name="sql">The SQL text.</param>
    /// <param name="provider">The dialect, or <c>null</c> for PostgreSQL-like defaults.</param>
    /// <returns>The lexemes in order.</returns>
    public static List<SqlLexeme> Lex(string sql, DatabaseProvider? provider = null)
    {
        var backslashEscapes = provider == DatabaseProvider.MySql;
        var oracle = provider == DatabaseProvider.Oracle;
        var dollarQuotes = provider is null or DatabaseProvider.PostgreSql;
        var result = new List<SqlLexeme>();
        var i = 0;
        while (i < sql.Length)
        {
            var start = i;
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (char.IsWhiteSpace(c))
            {
                while (i < sql.Length && char.IsWhiteSpace(sql[i]))
                {
                    i++;
                }

                result.Add(Make(SqlLexemeKind.Whitespace, sql, start, i));
            }
            else if ((c == '-' && next == '-') || (c == '#' && !IsPgJsonOperator(sql, i)))
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                result.Add(Make(SqlLexemeKind.Comment, sql, start, i));
            }
            else if (c == '/' && next == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 2;
                result.Add(Make(SqlLexemeKind.Comment, sql, start, i, close >= 0));
            }
            else if (oracle && TryOracleQuote(sql, i) is { } quoteEnd)
            {
                // Oracle alternative quoting: q'[...]', nq'{...}', q'!...!'.
                i = quoteEnd < 0 ? sql.Length : quoteEnd;
                result.Add(Make(SqlLexemeKind.String, sql, start, i, quoteEnd >= 0));
            }
            else if (c == '\'' || ((c is 'E' or 'e' or 'N' or 'n' or 'B' or 'b' or 'X' or 'x') && next == '\''))
            {
                i = c == '\'' ? i : i + 1;
                var closed = ReadQuoted(sql, ref i, '\'', allowBackslash: backslashEscapes || c is 'E' or 'e');
                result.Add(Make(SqlLexemeKind.String, sql, start, i, closed));
            }
            else if (c == '$' && dollarQuotes && TryDollarTag(sql, i, out var tag))
            {
                var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + tag.Length;
                result.Add(Make(SqlLexemeKind.String, sql, start, i, close >= 0));
            }
            else if (c == '$' && next == '{')
            {
                var close = sql.IndexOf('}', i + 2);
                i = close < 0 ? sql.Length : close + 1;
                result.Add(Make(SqlLexemeKind.Parameter, sql, start, i, close >= 0));
            }
            else if (c is '"' or '`')
            {
                var closed = ReadQuoted(sql, ref i, c, allowBackslash: false);
                result.Add(Make(SqlLexemeKind.QuotedIdentifier, sql, start, i, closed));
            }
            else if (c == '[' && LooksLikeBracketIdentifier(result))
            {
                var closed = ReadQuoted(sql, ref i, ']', allowBackslash: false);
                result.Add(Make(SqlLexemeKind.QuotedIdentifier, sql, start, i, closed));
            }
            else if (char.IsLetter(c) || c == '_' || (c == '@' && (char.IsLetter(next) || next == '@' || next == '_')))
            {
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' or '@' or '#'))
                {
                    i++;
                }

                result.Add(Make(SqlLexemeKind.Word, sql, start, i));
            }
            else if (char.IsDigit(c) || (c == '.' && char.IsDigit(next)))
            {
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.'
                       || ((sql[i] is '+' or '-') && sql[i - 1] is 'e' or 'E')))
                {
                    i++;
                }

                result.Add(Make(SqlLexemeKind.Number, sql, start, i));
            }
            else if (c == ':' && (char.IsLetter(next) || next == '_') && (i == 0 || sql[i - 1] != ':'))
            {
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                {
                    i++;
                }

                result.Add(Make(SqlLexemeKind.Parameter, sql, start, i));
            }
            else if (c == '?' && !IsPgJsonbQuestionOperator(result))
            {
                i++;
                result.Add(Make(SqlLexemeKind.Parameter, sql, start, i));
            }
            else
            {
                var op = Operators.FirstOrDefault(o => string.CompareOrdinal(sql, i, o, 0, o.Length) == 0);
                i += op?.Length ?? 1;
                result.Add(Make(SqlLexemeKind.Symbol, sql, start, i));
            }
        }

        return result;
    }

    /// <summary>Lists only the significant lexemes (no whitespace or comments).</summary>
    /// <param name="sql">The SQL text.</param>
    /// <param name="provider">The dialect, or <c>null</c> for PostgreSQL-like defaults.</param>
    /// <returns>The significant lexemes.</returns>
    public static List<SqlLexeme> Significant(string sql, DatabaseProvider? provider = null) =>
        Lex(sql, provider).Where(l => l.IsSignificant).ToList();

    private static SqlLexeme Make(SqlLexemeKind kind, string sql, int start, int end, bool closed = true) =>
        new(kind, start, end - start, sql[start..end], closed);

    private static bool ReadQuoted(string sql, ref int i, char close, bool allowBackslash)
    {
        i++; // opening delimiter
        while (i < sql.Length)
        {
            var c = sql[i];
            if (allowBackslash && c == '\\' && i + 1 < sql.Length)
            {
                i += 2;
                continue;
            }

            if (c == close)
            {
                // A doubled closing delimiter is an escaped delimiter.
                if (i + 1 < sql.Length && sql[i + 1] == close)
                {
                    i += 2;
                    continue;
                }

                i++;
                return true;
            }

            i++;
        }

        return false;
    }

    // $$ or $tag$ opening a PostgreSQL dollar-quoted string.
    private static bool TryDollarTag(string sql, int i, out string tag)
    {
        tag = string.Empty;
        var j = i + 1;
        while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_'))
        {
            j++;
        }

        if (j < sql.Length && sql[j] == '$' && (j == i + 1 || !char.IsDigit(sql[i + 1])))
        {
            tag = sql[i..(j + 1)];
            return true;
        }

        return false;
    }

    // `[` starts a SQL Server identifier unless it follows an expression (array subscripts in PostgreSQL).
    private static bool LooksLikeBracketIdentifier(List<SqlLexeme> previous)
    {
        for (var k = previous.Count - 1; k >= 0; k--)
        {
            var p = previous[k];
            if (!p.IsSignificant)
            {
                continue;
            }

            return !(p.IsName && !IsKeywordLike(p.Text) || p.Kind == SqlLexemeKind.Number || p.IsSymbol(")") || p.IsSymbol("]"));
        }

        return true;
    }

    private static bool IsKeywordLike(string word) =>
        word.ToUpperInvariant() is "SELECT" or "FROM" or "JOIN" or "ON" or "WHERE" or "AND" or "OR" or "BY" or "AS" or "SET"
            or "INTO" or "UPDATE" or "TABLE" or "VALUES" or "CREATE" or "ALTER" or "DROP" or "INDEX" or "EXEC" or "EXECUTE"
            or "DELETE" or "INSERT" or "TOP" or "DISTINCT" or "WHEN" or "THEN" or "ELSE" or "RETURN" or "PROCEDURE"
            or "FUNCTION" or "VIEW" or "TRUNCATE" or "IN" or "NOT" or "ADD" or "COLUMN" or "CONSTRAINT" or "REFERENCES";

    // PostgreSQL's jsonb #> / #>> operators look like MySQL # comments.
    private static bool IsPgJsonOperator(string sql, int i) => i + 1 < sql.Length && sql[i + 1] == '>';

    // `?` after an expression is PostgreSQL's jsonb key-exists operator, not a parameter.
    private static bool IsPgJsonbQuestionOperator(List<SqlLexeme> previous)
    {
        for (var k = previous.Count - 1; k >= 0; k--)
        {
            var p = previous[k];
            if (!p.IsSignificant)
            {
                continue;
            }

            return (p.IsName && !IsKeywordLike(p.Text)) || p.IsSymbol(")");
        }

        return false;
    }
}
