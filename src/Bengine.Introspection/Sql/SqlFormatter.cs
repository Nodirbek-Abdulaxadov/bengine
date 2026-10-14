namespace Bengine.Introspection.Sql;

using System.Text;
using Bengine.Enums;

/// <summary>
/// Reformats SQL for reading: each major clause (<c>SELECT</c>, <c>FROM</c>,
/// <c>JOIN</c>, <c>WHERE</c>, <c>GROUP BY</c>, …) starts a line, list items and
/// <c>AND</c>/<c>OR</c> conditions continue on indented lines, subqueries are
/// indented one level, and keywords are upper-cased. Only whitespace and keyword
/// case change: identifiers, literals, and comments are kept verbatim, and the
/// text between statements (terminators, <c>GO</c>, <c>DELIMITER</c>) is left as
/// it was.
/// </summary>
public static class SqlFormatter
{
    private const string Indent = "    ";

    // Clause starters, longest first so "LEFT OUTER JOIN" wins over "LEFT".
    private static readonly string[][] Clauses =
    [
        ["LEFT", "OUTER", "JOIN"], ["RIGHT", "OUTER", "JOIN"], ["FULL", "OUTER", "JOIN"], ["INSERT", "INTO"],
        ["DELETE", "FROM"], ["GROUP", "BY"], ["ORDER", "BY"], ["UNION", "ALL"], ["LEFT", "JOIN"], ["RIGHT", "JOIN"],
        ["FULL", "JOIN"], ["INNER", "JOIN"], ["CROSS", "JOIN"], ["NATURAL", "JOIN"], ["CROSS", "APPLY"], ["OUTER", "APPLY"],
        ["ON", "CONFLICT"], ["PARTITION", "BY"],
        ["SELECT"], ["FROM"], ["WHERE"], ["HAVING"], ["LIMIT"], ["OFFSET"], ["FETCH"], ["UNION"], ["INTERSECT"],
        ["EXCEPT"], ["VALUES"], ["UPDATE"], ["SET"], ["RETURNING"], ["JOIN"], ["WINDOW"], ["WITH"], ["INSERT"], ["DELETE"],
        ["MERGE"], ["USING"],
    ];

    // Clauses whose top-level commas put each item on its own line.
    private static readonly HashSet<string> ListClauses = ["SELECT", "GROUP BY", "ORDER BY", "SET", "RETURNING", "VALUES", "WITH"];

    // Clauses whose top-level AND/OR start a continuation line.
    private static readonly HashSet<string> ConditionClauses = ["WHERE", "HAVING", "ON", "JOIN", "LEFT JOIN", "RIGHT JOIN",
        "FULL JOIN", "INNER JOIN", "LEFT OUTER JOIN", "RIGHT OUTER JOIN", "FULL OUTER JOIN"];

    /// <summary>Formats every statement of a script.</summary>
    /// <param name="sql">The script.</param>
    /// <param name="provider">The dialect.</param>
    /// <param name="upperCaseKeywords">Whether to upper-case keywords.</param>
    /// <returns>The formatted script.</returns>
    public static string Format(string sql, DatabaseProvider provider, bool upperCaseKeywords = true)
    {
        var statements = SqlScript.Split(sql, provider);
        var builder = new StringBuilder(sql);
        foreach (var statement in statements.Reverse())
        {
            builder.Remove(statement.Start, statement.End - statement.Start)
                .Insert(statement.Start, FormatStatement(statement.Text, provider, upperCaseKeywords));
        }

        return builder.ToString();
    }

    /// <summary>Formats a single statement.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="provider">The dialect.</param>
    /// <param name="upperCaseKeywords">Whether to upper-case keywords.</param>
    /// <returns>The formatted statement.</returns>
    public static string FormatStatement(string sql, DatabaseProvider provider, bool upperCaseKeywords = true)
    {
        var lexemes = SqlLexer.Lex(sql, provider).Where(l => l.Kind != SqlLexemeKind.Whitespace).ToList();

        // Routine bodies and other procedural code are left alone: re-flowing them
        // would change their meaning to a reader more than it helps.
        if (lexemes.Count(l => l.Is("BEGIN") || l.Is("DECLARE")) > 0 && lexemes.Any(l => l.Is("CREATE")))
        {
            return sql.Trim();
        }

        var output = new StringBuilder();
        var frames = new Stack<Frame>();
        var frame = new Frame(0, string.Empty, IsSubquery: false);
        var lineStart = true;
        var lineIndent = 0;
        SqlLexeme? previous = null;

        void NewLine(int depth)
        {
            TrimTrailingSpace(output);
            output.Append('\n').Append(string.Concat(Enumerable.Repeat(Indent, depth)));
            lineStart = true;
            lineIndent = depth;
        }

        void Write(string text, SqlLexeme lexeme)
        {
            if (!lineStart && NeedsSpace(previous, lexeme))
            {
                output.Append(' ');
            }

            output.Append(text);
            lineStart = false;
            previous = lexeme;
        }

        for (var k = 0; k < lexemes.Count; k++)
        {
            var lexeme = lexemes[k];

            if (lexeme.Kind == SqlLexemeKind.Comment)
            {
                Write(lexeme.Text.TrimEnd(), lexeme);
                if (lexeme.Text.StartsWith("--", StringComparison.Ordinal) || lexeme.Text.StartsWith('#'))
                {
                    NewLine(frame.Depth + (frame.Clause.Length > 0 ? 1 : 0));
                }

                continue;
            }

            if (lexeme.IsSymbol("("))
            {
                var opensSubquery = NextSignificant(lexemes, k) is { } next && (next.Is("SELECT") || next.Is("WITH"));
                Write("(", lexeme);
                frames.Push(frame);
                frame = opensSubquery
                    ? new Frame(lineIndent + 1, string.Empty, IsSubquery: true, OpenIndent: lineIndent)
                    : frame with { Parens = frame.Parens + 1 };
                if (opensSubquery)
                {
                    NewLine(frame.Depth);
                }

                continue;
            }

            if (lexeme.IsSymbol(")"))
            {
                if (frames.Count > 0)
                {
                    var closing = frame;
                    frame = frames.Pop();
                    if (closing.IsSubquery)
                    {
                        NewLine(closing.OpenIndent);
                    }
                }

                Write(")", lexeme);
                continue;
            }

            if (frame.Parens == 0 && lexeme.Kind == SqlLexemeKind.Word && MatchClause(lexemes, k) is { } clause)
            {
                var words = clause.Length;
                var name = string.Join(" ", clause);
                var isConditionContinuation = name is "ON" && frame.Clause.Contains("JOIN", StringComparison.Ordinal);

                if (!isConditionContinuation && !(name == "SET" && frame.Clause == "ON CONFLICT") && output.Length > 0 && !lineStart)
                {
                    NewLine(frame.Depth);
                }

                for (var w = 0; w < words; w++)
                {
                    var part = lexemes[k + w];
                    Write(upperCaseKeywords ? part.Text.ToUpperInvariant() : part.Text, part);
                }

                k += words - 1;
                frame = frame with { Clause = isConditionContinuation ? frame.Clause : name };
                if (ListClauses.Contains(name) && name != "WITH" && name != "VALUES" && !IsShortList(lexemes, k + 1))
                {
                    NewLine(frame.Depth + 1);
                }

                continue;
            }

            if (frame.Parens == 0 && lexeme.IsSymbol(",") && ListClauses.Contains(frame.Clause) && frame.Clause != "VALUES")
            {
                Write(",", lexeme);
                NewLine(frame.Depth + 1);
                continue;
            }

            if (frame.Parens == 0 && (lexeme.Is("AND") || lexeme.Is("OR")) && ConditionClauses.Contains(frame.Clause)
                && !IsBetweenAnd(lexemes, k))
            {
                NewLine(frame.Depth + 1);
                Write(upperCaseKeywords ? lexeme.Text.ToUpperInvariant() : lexeme.Text, lexeme);
                continue;
            }

            var text = upperCaseKeywords && lexeme.Kind == SqlLexemeKind.Word && SqlKeywords.IsKeyword(lexeme.Text, provider)
                && !(NextSignificant(lexemes, k) is { } n && n.IsSymbol(".")) && !(previous is { } p && p.IsSymbol("."))
                ? lexeme.Text.ToUpperInvariant()
                : lexeme.Text;
            Write(text, lexeme);
        }

        return output.ToString().Trim();
    }

    private static string[]? MatchClause(List<SqlLexeme> lexemes, int index)
    {
        foreach (var clause in Clauses)
        {
            var k = index;
            var matched = true;
            foreach (var word in clause)
            {
                while (k < lexemes.Count && lexemes[k].Kind == SqlLexemeKind.Comment)
                {
                    k++;
                }

                if (k >= lexemes.Count || !lexemes[k].Is(word))
                {
                    matched = false;
                    break;
                }

                k++;
            }

            if (matched && k - index == clause.Length)
            {
                // "SET" after UPDATE is a clause; in "SET search_path" or "CHARACTER SET" it is not one we re-flow.
                if (clause is ["SET"] && !lexemes.Take(index).Any(l => l.Is("UPDATE") || l.Is("CONFLICT")))
                {
                    return null;
                }

                if (clause is ["USING"] && !lexemes.Take(index).Any(l => l.Is("MERGE") || l.Is("DELETE")))
                {
                    return null;
                }

                return clause;
            }
        }

        return null;
    }

    // "SELECT *" / "ORDER BY id" stay on one line.
    private static bool IsShortList(List<SqlLexeme> lexemes, int from)
    {
        var depth = 0;
        for (var k = from; k < lexemes.Count; k++)
        {
            var l = lexemes[k];
            if (l.IsSymbol("("))
            {
                depth++;
            }
            else if (l.IsSymbol(")"))
            {
                if (depth == 0)
                {
                    return true;
                }

                depth--;
            }
            else if (depth == 0 && l.IsSymbol(","))
            {
                return false;
            }
            else if (depth == 0 && MatchClause(lexemes, k) is not null)
            {
                return true;
            }
        }

        return true;
    }

    private static bool IsBetweenAnd(List<SqlLexeme> lexemes, int index)
    {
        if (!lexemes[index].Is("AND"))
        {
            return false;
        }

        for (var k = index - 1; k >= 0 && k >= index - 6; k--)
        {
            if (lexemes[k].Is("BETWEEN"))
            {
                return true;
            }

            if (lexemes[k].Is("AND") || lexemes[k].Is("OR"))
            {
                return false;
            }
        }

        return false;
    }

    private static SqlLexeme? NextSignificant(List<SqlLexeme> lexemes, int index)
    {
        for (var k = index + 1; k < lexemes.Count; k++)
        {
            if (lexemes[k].IsSignificant)
            {
                return lexemes[k];
            }
        }

        return null;
    }

    private static bool NeedsSpace(SqlLexeme? previous, SqlLexeme current)
    {
        if (previous is not { } p)
        {
            return false;
        }

        if (current.IsSymbol(",") || current.IsSymbol(")") || current.IsSymbol(".") || current.IsSymbol(";")
            || current.IsSymbol("::") || current.IsSymbol("]"))
        {
            return false;
        }

        if (p.IsSymbol("(") || p.IsSymbol(".") || p.IsSymbol("::") || p.IsSymbol("@") || p.IsSymbol("["))
        {
            return false;
        }

        // A function call or a column list: name(…) — but keep "IN (", "VALUES (", "AS (" spaced.
        if (current.IsSymbol("(") && p.IsName && !(p.Kind == SqlLexemeKind.Word && IsSpacedBeforeParen(p.Text)))
        {
            return false;
        }

        return true;
    }

    private static bool IsSpacedBeforeParen(string word) =>
        word.ToUpperInvariant() is "IN" or "VALUES" or "AS" or "EXISTS" or "ON" or "AND" or "OR" or "NOT" or "FROM" or "JOIN"
            or "INTO" or "USING" or "WHERE" or "SELECT" or "OVER" or "THEN" or "ELSE" or "WHEN" or "RETURNS" or "TABLE";

    private static void TrimTrailingSpace(StringBuilder builder)
    {
        while (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }
    }

    private sealed record Frame(int Depth, string Clause, bool IsSubquery, int Parens = 0, int OpenIndent = 0);
}
