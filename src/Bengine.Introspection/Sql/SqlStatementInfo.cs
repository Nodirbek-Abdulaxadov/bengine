namespace Bengine.Introspection.Sql;

using Bengine.Enums;

/// <summary>What a statement does, coarsely.</summary>
public enum SqlStatementKind
{
    /// <summary>Reads data: <c>SELECT</c>, <c>WITH … SELECT</c>, <c>VALUES</c>, <c>SHOW</c>, <c>EXPLAIN</c>, …</summary>
    Query,

    /// <summary>Changes rows: <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>, <c>MERGE</c>, <c>COPY</c>, …</summary>
    Dml,

    /// <summary>Changes structure or privileges: <c>CREATE</c>, <c>ALTER</c>, <c>DROP</c>, <c>TRUNCATE</c>, <c>GRANT</c>, …</summary>
    Ddl,

    /// <summary>Controls a transaction: <c>BEGIN</c>, <c>COMMIT</c>, <c>ROLLBACK</c>, <c>SAVEPOINT</c>, …</summary>
    Transaction,

    /// <summary>Changes session state: <c>SET</c>, <c>USE</c>, <c>RESET</c>.</summary>
    Session,

    /// <summary>Anything else: <c>CALL</c>, <c>EXEC</c>, <c>DO</c>, <c>VACUUM</c>, …</summary>
    Other,
}

/// <summary>
/// A lexical classification of one statement, enough for a console's safety
/// checks: whether it can modify the database (refused on a read-only
/// connection), and whether it is an <c>UPDATE</c>/<c>DELETE</c> without a
/// <c>WHERE</c> clause (confirmed before running). It inspects only the
/// statement's top level, so a <c>WHERE</c> inside a subquery does not count.
/// </summary>
public sealed record SqlStatementInfo
{
    private static readonly HashSet<string> QueryWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "VALUES", "TABLE", "SHOW", "DESCRIBE", "DESC", "EXPLAIN", "PRAGMA", "HELP",
    };

    private static readonly HashSet<string> DmlWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "REPLACE", "COPY", "LOAD",
    };

    private static readonly HashSet<string> DdlWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME", "COMMENT", "GRANT", "REVOKE", "REINDEX", "CLUSTER", "REFRESH",
    };

    private static readonly HashSet<string> TransactionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "BEGIN", "START", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "END", "ABORT",
    };

    private static readonly HashSet<string> SessionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SET", "USE", "RESET", "DISCARD",
    };

    /// <summary>Gets the statement kind.</summary>
    public required SqlStatementKind Kind { get; init; }

    /// <summary>Gets the leading keyword, upper-cased (<c>SELECT</c>, <c>UPDATE</c>, …), or empty.</summary>
    public required string Verb { get; init; }

    /// <summary>Gets whether running the statement can change data, structure, or privileges.</summary>
    public required bool CanModify { get; init; }

    /// <summary>Gets whether the statement is an <c>UPDATE</c> or <c>DELETE</c> with no top-level <c>WHERE</c>.</summary>
    public required bool IsUnfilteredDml { get; init; }

    /// <summary>Classifies a statement.</summary>
    /// <param name="sql">The statement text.</param>
    /// <param name="provider">The dialect.</param>
    /// <returns>The classification.</returns>
    public static SqlStatementInfo Classify(string sql, DatabaseProvider provider)
    {
        var lexemes = SqlLexer.Significant(sql, provider);
        var top = TopLevel(lexemes);
        var first = lexemes.FirstOrDefault(l => l.Kind == SqlLexemeKind.Word);
        var verb = first.Text?.ToUpperInvariant() ?? string.Empty;

        // WITH … : the kind is decided by the first top-level verb after the CTEs.
        if (verb == "WITH")
        {
            var main = top.Skip(1).FirstOrDefault(l => l.Kind == SqlLexemeKind.Word
                && (QueryWords.Contains(l.Text) || DmlWords.Contains(l.Text)) && !l.Is("RECURSIVE"));
            verb = main.Text?.ToUpperInvariant() ?? "SELECT";
        }

        var kind = verb switch
        {
            _ when QueryWords.Contains(verb) => SqlStatementKind.Query,
            _ when DmlWords.Contains(verb) => SqlStatementKind.Dml,
            _ when DdlWords.Contains(verb) => SqlStatementKind.Ddl,
            _ when TransactionWords.Contains(verb) => SqlStatementKind.Transaction,
            _ when SessionWords.Contains(verb) => SqlStatementKind.Session,
            _ => SqlStatementKind.Other,
        };

        // A few query-shaped statements still write.
        var canModify = kind switch
        {
            SqlStatementKind.Query when verb == "SELECT" => top.Any(l => l.Is("INTO")) && provider != DatabaseProvider.MySql
                || (provider == DatabaseProvider.MySql && IntoTable(top)),
            SqlStatementKind.Query when verb == "EXPLAIN" => top.Any(l => l.Is("ANALYZE") || l.Is("ANALYSE"))
                && lexemes.Any(l => DmlWords.Contains(l.Text) && l.Kind == SqlLexemeKind.Word),
            SqlStatementKind.Query when verb == "PRAGMA" => lexemes.Any(l => l.IsSymbol("=")),
            SqlStatementKind.Query => false,
            SqlStatementKind.Transaction or SqlStatementKind.Session => false,
            _ => true,
        };

        // A joined UPDATE/DELETE (SQL Server, MySQL) is filtered by its ON clause.
        var unfiltered = verb is "UPDATE" or "DELETE"
            && !top.Any(l => l.Is("WHERE"))
            && !(top.Any(l => l.Is("JOIN")) && top.Any(l => l.Is("ON")));

        return new SqlStatementInfo { Kind = kind, Verb = verb, CanModify = canModify, IsUnfilteredDml = unfiltered };
    }

    // MySQL's SELECT … INTO @var / OUTFILE does not create tables; SELECT … INTO TABLE is not MySQL syntax.
    private static bool IntoTable(List<SqlLexeme> top)
    {
        var into = top.FindIndex(l => l.Is("INTO"));
        return into >= 0 && into + 1 < top.Count && (top[into + 1].Is("OUTFILE") || top[into + 1].Is("DUMPFILE"));
    }

    /// <summary>Returns the lexemes at parenthesis depth zero.</summary>
    /// <param name="lexemes">The significant lexemes.</param>
    /// <returns>The top-level lexemes.</returns>
    internal static List<SqlLexeme> TopLevel(List<SqlLexeme> lexemes)
    {
        var depth = 0;
        var top = new List<SqlLexeme>();
        foreach (var lexeme in lexemes)
        {
            if (lexeme.IsSymbol("("))
            {
                depth++;
            }
            else if (lexeme.IsSymbol(")"))
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0)
            {
                top.Add(lexeme);
            }
        }

        return top;
    }
}
