namespace Bengine.Introspection.Sql;

using Bengine.Enums;

/// <summary>One statement of a script, with its span in the original text.</summary>
/// <param name="Text">The statement text, trimmed, without its terminator.</param>
/// <param name="Start">The offset of the statement's first character.</param>
/// <param name="End">The exclusive offset just past its last character (before the terminator).</param>
public sealed record SqlStatement(string Text, int Start, int End)
{
    /// <summary>Gets whether an offset lies within the statement or on its terminator.</summary>
    /// <param name="offset">The offset.</param>
    /// <returns><c>true</c> if the offset belongs to the statement.</returns>
    public bool Contains(int offset) => offset >= Start && offset <= End;
}

/// <summary>
/// Splits a SQL script into statements the way a database console does: on
/// <c>;</c> outside strings, comments, and quoted identifiers; on a line holding
/// only <c>GO</c> for SQL Server; honoring the MySQL client's
/// <c>DELIMITER</c> directive; and keeping routine bodies whole — a
/// <c>CREATE FUNCTION/PROCEDURE/TRIGGER</c> whose body is a
/// <c>BEGIN ... END</c> block (MySQL, SQL Server, SQLite triggers) or a
/// PostgreSQL dollar-quoted string is one statement however many semicolons
/// it contains.
/// </summary>
public static class SqlScript
{
    /// <summary>Splits a script into statements.</summary>
    /// <param name="sql">The script.</param>
    /// <param name="provider">The dialect.</param>
    /// <returns>The non-empty statements in order.</returns>
    public static IReadOnlyList<SqlStatement> Split(string sql, DatabaseProvider provider)
    {
        var lexemes = SqlLexer.Lex(sql, provider);
        var statements = new List<SqlStatement>();
        var delimiter = ";";
        var start = 0;
        var depth = 0;
        var inRoutine = false;
        SqlLexeme? previousWord = null;
        var wordsInStatement = 0;

        void Flush(int end)
        {
            var (s, e) = Trim(sql, start, end);
            if (e > s && HasCode(lexemes, s, e))
            {
                statements.Add(new SqlStatement(sql[s..e], s, e));
            }

            depth = 0;
            inRoutine = false;
            previousWord = null;
            wordsInStatement = 0;
        }

        for (var k = 0; k < lexemes.Count; k++)
        {
            var lexeme = lexemes[k];

            // MySQL client directive: DELIMITER <token> on its own line.
            if (provider == DatabaseProvider.MySql && lexeme.Is("DELIMITER") && AtLineStart(sql, lexeme.Start))
            {
                Flush(lexeme.Start);
                var lineEnd = sql.IndexOf('\n', lexeme.End);
                lineEnd = lineEnd < 0 ? sql.Length : lineEnd;
                var value = sql[lexeme.End..lineEnd].Trim();
                if (value.Length > 0)
                {
                    delimiter = value;
                }

                start = lineEnd;
                while (k + 1 < lexemes.Count && lexemes[k + 1].Start < lineEnd)
                {
                    k++;
                }

                continue;
            }

            // SQL Server batch separator: GO alone on its line.
            if (provider == DatabaseProvider.SqlServer && lexeme.Is("GO") && AtLineStart(sql, lexeme.Start) && RestOfLineBlank(sql, lexeme.End))
            {
                Flush(lexeme.Start);
                start = lexeme.End;
                continue;
            }

            if (lexeme.Kind == SqlLexemeKind.Word)
            {
                wordsInStatement++;
                if (wordsInStatement <= 6 && IsRoutineKeyword(lexeme) && StartsWithCreate(lexemes, start, lexeme.Start))
                {
                    inRoutine = true;
                }

                if (inRoutine)
                {
                    if (lexeme.Is("BEGIN") && !NextWordIs(lexemes, k, "TRANSACTION", "TRAN", "WORK"))
                    {
                        depth++;
                    }
                    else if (lexeme.Is("CASE") && depth > 0 && !(previousWord?.Is("END") ?? false))
                    {
                        depth++;
                    }
                    else if (lexeme.Is("END") && depth > 0 && !NextWordIs(lexemes, k, "IF", "LOOP", "WHILE", "REPEAT"))
                    {
                        depth--;
                    }
                }

                previousWord = lexeme;
            }

            // A custom DELIMITER exists precisely so routine bodies need no BEGIN/END
            // tracking: only the delimiter ends a statement, even glued to a word (END$$).
            if (delimiter != ";")
            {
                var at = DelimiterOffset(sql, lexeme, delimiter);
                if (at >= 0)
                {
                    Flush(at);
                    start = at + delimiter.Length;
                    while (k + 1 < lexemes.Count && lexemes[k + 1].Start < start)
                    {
                        k++;
                    }
                }

                continue;
            }

            // A T-SQL routine body runs to the end of its batch (GO), BEGIN/END or not.
            if (depth > 0 || (inRoutine && provider == DatabaseProvider.SqlServer))
            {
                continue;
            }

            if (lexeme.IsSymbol(";"))
            {
                Flush(lexeme.Start);
                start = lexeme.End;
            }
        }

        Flush(sql.Length);
        return statements;
    }

    /// <summary>
    /// Finds the statement a caret belongs to: the one containing it, else the
    /// nearest statement ending before it on the same or a previous line (the
    /// caret just after a <c>;</c>), else the next one.
    /// </summary>
    /// <param name="sql">The script.</param>
    /// <param name="caret">The caret offset.</param>
    /// <param name="provider">The dialect.</param>
    /// <returns>The statement, or <c>null</c> when the script is empty.</returns>
    public static SqlStatement? StatementAt(string sql, int caret, DatabaseProvider provider)
    {
        var statements = Split(sql, provider);
        if (statements.Count == 0)
        {
            return null;
        }

        var containing = statements.FirstOrDefault(s => s.Contains(caret));
        if (containing is not null)
        {
            return containing;
        }

        var before = statements.LastOrDefault(s => s.End <= caret);
        if (before is not null && !sql[before.End..Math.Min(caret, sql.Length)].Contains("\n\n", StringComparison.Ordinal))
        {
            return before;
        }

        return statements.FirstOrDefault(s => s.Start >= caret) ?? before;
    }

    private static bool IsRoutineKeyword(SqlLexeme lexeme) =>
        lexeme.Is("FUNCTION") || lexeme.Is("PROCEDURE") || lexeme.Is("PROC") || lexeme.Is("TRIGGER") || lexeme.Is("EVENT");

    private static bool StartsWithCreate(List<SqlLexeme> lexemes, int statementStart, int before)
    {
        foreach (var lexeme in lexemes)
        {
            if (lexeme.Start < statementStart || !lexeme.IsSignificant)
            {
                continue;
            }

            if (lexeme.Start >= before)
            {
                return false;
            }

            return lexeme.Is("CREATE") || lexeme.Is("ALTER");
        }

        return false;
    }

    private static bool NextWordIs(List<SqlLexeme> lexemes, int index, params string[] words)
    {
        for (var k = index + 1; k < lexemes.Count; k++)
        {
            if (!lexemes[k].IsSignificant)
            {
                continue;
            }

            return words.Any(lexemes[k].Is);
        }

        return false;
    }

    // The offset of a custom delimiter that starts in this lexeme, or -1.
    private static int DelimiterOffset(string sql, SqlLexeme lexeme, string delimiter)
    {
        if (lexeme.Kind is SqlLexemeKind.String or SqlLexemeKind.Comment or SqlLexemeKind.QuotedIdentifier or SqlLexemeKind.Whitespace)
        {
            return -1;
        }

        var end = Math.Min(sql.Length, lexeme.End + delimiter.Length - 1);
        var index = sql.IndexOf(delimiter, lexeme.Start, end - lexeme.Start, StringComparison.Ordinal);
        return index >= 0 && index < lexeme.End ? index : -1;
    }

    private static bool AtLineStart(string sql, int offset)
    {
        for (var i = offset - 1; i >= 0; i--)
        {
            if (sql[i] == '\n')
            {
                return true;
            }

            if (!char.IsWhiteSpace(sql[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RestOfLineBlank(string sql, int offset)
    {
        for (var i = offset; i < sql.Length && sql[i] != '\n'; i++)
        {
            if (!char.IsWhiteSpace(sql[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static (int Start, int End) Trim(string sql, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(sql[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(sql[end - 1]))
        {
            end--;
        }

        return (start, end);
    }

    private static bool HasCode(List<SqlLexeme> lexemes, int start, int end) =>
        lexemes.Any(l => l.Start >= start && l.End <= end && l.IsSignificant);
}
