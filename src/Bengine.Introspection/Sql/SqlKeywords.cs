namespace Bengine.Introspection.Sql;

using Bengine.Enums;

/// <summary>
/// The SQL vocabulary editor features need per dialect: keywords (for
/// completion and highlighting), reserved words (identifiers that must be
/// quoted), and the common built-in functions offered by completion.
/// </summary>
public static class SqlKeywords
{
    private static readonly string[] Common =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "BETWEEN", "EXISTS", "AS", "ON",
        "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "NATURAL", "USING", "GROUP", "BY", "HAVING", "ORDER",
        "ASC", "DESC", "NULLS", "FIRST", "LAST", "UNION", "ALL", "INTERSECT", "EXCEPT", "DISTINCT", "CASE", "WHEN", "THEN",
        "ELSE", "END", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "MERGE", "MATCHED", "CREATE", "ALTER", "DROP",
        "TRUNCATE", "TABLE", "VIEW", "INDEX", "UNIQUE", "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "CONSTRAINT", "CHECK",
        "DEFAULT", "COLUMN", "ADD", "RENAME", "TO", "IF", "CASCADE", "RESTRICT", "WITH", "RECURSIVE", "BEGIN", "COMMIT",
        "ROLLBACK", "TRANSACTION", "SAVEPOINT", "GRANT", "REVOKE", "SCHEMA", "DATABASE", "SEQUENCE", "TRIGGER",
        "FUNCTION", "PROCEDURE", "RETURNS", "CAST", "TRUE", "FALSE", "OVER", "PARTITION", "WINDOW", "ROWS", "RANGE",
        "PRECEDING", "FOLLOWING", "UNBOUNDED", "CURRENT", "ROW", "FETCH", "NEXT", "ONLY", "OFFSET", "ANY", "SOME",
        "EXPLAIN", "ANALYZE", "COLLATE", "ESCAPE", "LATERAL", "FILTER", "WITHIN",
    ];

    private static readonly Dictionary<DatabaseProvider, string[]> Extra = new()
    {
        [DatabaseProvider.PostgreSql] =
        [
            "LIMIT", "ILIKE", "SIMILAR", "RETURNING", "CONFLICT", "DO", "NOTHING", "MATERIALIZED", "REFRESH", "CONCURRENTLY",
            "EXTENSION", "TYPE", "ENUM", "DOMAIN", "OWNER", "TABLESPACE", "VACUUM", "LANGUAGE", "PLPGSQL", "SECURITY",
            "DEFINER", "INVOKER", "IMMUTABLE", "STABLE", "VOLATILE", "GENERATED", "ALWAYS", "IDENTITY", "STORED", "INHERITS",
            "UNLOGGED", "TEMPORARY", "TEMP", "COPY", "LISTEN", "NOTIFY", "LOCK", "SHARE", "NOWAIT", "SKIP", "LOCKED",
        ],
        [DatabaseProvider.MySql] =
        [
            "LIMIT", "AUTO_INCREMENT", "ENGINE", "CHARSET", "SHOW", "TABLES", "DATABASES", "COLUMNS", "DESCRIBE", "USE",
            "REPLACE", "IGNORE", "DUPLICATE", "STRAIGHT_JOIN", "REGEXP", "RLIKE", "UNSIGNED", "ZEROFILL", "DELIMITER",
            "PROCESSLIST", "STATUS", "VARIABLES", "DEFINER", "EVENT", "SCHEDULE", "LOCK", "UNLOCK", "PARTITION",
        ],
        [DatabaseProvider.SqlServer] =
        [
            "TOP", "PERCENT", "TIES", "OUTPUT", "IDENTITY", "NOLOCK", "CLUSTERED", "NONCLUSTERED", "EXEC", "EXECUTE",
            "DECLARE", "PRINT", "GO", "TRY", "CATCH", "THROW", "RAISERROR", "APPLY", "PIVOT", "UNPIVOT", "INCLUDE", "OPTION",
            "NVARCHAR", "TRAN", "USE", "INSTEAD", "OF", "RETURN", "WHILE", "BREAK", "CONTINUE", "SYNONYM",
        ],
        [DatabaseProvider.Sqlite] =
        [
            "LIMIT", "GLOB", "AUTOINCREMENT", "WITHOUT", "ROWID", "STRICT", "PRAGMA", "VACUUM", "ATTACH", "DETACH",
            "REINDEX", "INDEXED", "TEMP", "TEMPORARY", "CONFLICT", "ABORT", "FAIL", "IGNORE", "REPLACE", "RETURNING",
            "UPSERT", "DO", "NOTHING", "VIRTUAL",
        ],
        [DatabaseProvider.Oracle] =
        [
            "MINUS", "CONNECT", "PRIOR", "START", "NOCYCLE", "LEVEL", "ROWNUM", "ROWID", "DUAL", "SYSDATE", "SYSTIMESTAMP",
            "DECLARE", "EXCEPTION", "RAISE", "LOOP", "EXIT", "WHILE", "FOR", "RETURN", "IS", "PACKAGE", "BODY", "TYPE", "RECORD",
            "CURSOR", "OPEN", "CLOSE", "PRAGMA", "AUTONOMOUS_TRANSACTION", "BULK", "COLLECT", "FORALL", "IMMEDIATE", "EXECUTE",
            "SYNONYM", "PUBLIC", "TABLESPACE", "MATERIALIZED", "REFRESH", "PURGE", "FLASHBACK", "GENERATED", "ALWAYS", "IDENTITY",
            "VIRTUAL", "INVISIBLE", "MODIFY", "ENABLE", "DISABLE", "NOVALIDATE", "PIVOT", "UNPIVOT", "SAMPLE", "SIBLINGS",
            "RETURNING", "LOG", "ERRORS", "COMMENT", "NOCACHE", "NOORDER", "EDITIONABLE", "NONEDITIONABLE",
        ],
    };

    private static readonly string[] CommonFunctions =
    [
        "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "NULLIF", "LOWER", "UPPER", "TRIM", "LTRIM", "RTRIM", "LENGTH",
        "SUBSTRING", "REPLACE", "ABS", "ROUND", "FLOOR", "CEILING", "ROW_NUMBER", "RANK", "DENSE_RANK", "LAG", "LEAD",
        "FIRST_VALUE", "LAST_VALUE", "CURRENT_DATE", "CURRENT_TIMESTAMP",
    ];

    private static readonly Dictionary<DatabaseProvider, string[]> ExtraFunctions = new()
    {
        [DatabaseProvider.PostgreSql] =
        [
            "NOW", "STRING_AGG", "ARRAY_AGG", "JSON_AGG", "JSONB_AGG", "JSONB_BUILD_OBJECT", "JSON_BUILD_OBJECT", "TO_CHAR",
            "TO_DATE", "TO_TIMESTAMP", "DATE_TRUNC", "EXTRACT", "AGE", "GENERATE_SERIES", "UNNEST", "CONCAT", "CONCAT_WS",
            "SPLIT_PART", "REGEXP_REPLACE", "POSITION", "LEFT", "RIGHT", "GEN_RANDOM_UUID", "NEXTVAL", "CURRVAL",
            "PG_SIZE_PRETTY", "PG_TOTAL_RELATION_SIZE", "GREATEST", "LEAST", "CEIL",
        ],
        [DatabaseProvider.MySql] =
        [
            "NOW", "CONCAT", "CONCAT_WS", "GROUP_CONCAT", "IFNULL", "IF", "DATE_FORMAT", "STR_TO_DATE", "DATE_ADD", "DATE_SUB",
            "DATEDIFF", "CURDATE", "CURTIME", "UNIX_TIMESTAMP", "FROM_UNIXTIME", "JSON_EXTRACT", "JSON_OBJECT", "JSON_ARRAYAGG",
            "UUID", "LAST_INSERT_ID", "FOUND_ROWS", "GREATEST", "LEAST", "LEFT", "RIGHT", "LOCATE", "CEIL",
        ],
        [DatabaseProvider.SqlServer] =
        [
            "GETDATE", "GETUTCDATE", "SYSDATETIME", "ISNULL", "IIF", "CONVERT", "TRY_CONVERT", "FORMAT", "DATEADD", "DATEDIFF",
            "DATEPART", "DATENAME", "EOMONTH", "LEN", "CHARINDEX", "STRING_AGG", "STRING_SPLIT", "NEWID", "SCOPE_IDENTITY",
            "OBJECT_ID", "JSON_VALUE", "JSON_QUERY", "OPENJSON", "LEFT", "RIGHT", "STUFF", "CONCAT",
        ],
        [DatabaseProvider.Sqlite] =
        [
            "DATE", "TIME", "DATETIME", "JULIANDAY", "STRFTIME", "IFNULL", "IIF", "INSTR", "PRINTF", "GROUP_CONCAT",
            "JSON_EXTRACT", "JSON_OBJECT", "JSON_GROUP_ARRAY", "RANDOM", "TYPEOF", "LAST_INSERT_ROWID", "CHANGES", "SUBSTR",
        ],
        [DatabaseProvider.Oracle] =
        [
            "NVL", "NVL2", "DECODE", "TO_CHAR", "TO_DATE", "TO_TIMESTAMP", "TO_NUMBER", "TRUNC", "ADD_MONTHS", "MONTHS_BETWEEN",
            "LAST_DAY", "NEXT_DAY", "EXTRACT", "SUBSTR", "INSTR", "LPAD", "RPAD", "INITCAP", "LISTAGG", "REGEXP_LIKE",
            "REGEXP_SUBSTR", "REGEXP_REPLACE", "REGEXP_INSTR", "SYS_GUID", "SYS_CONTEXT", "USERENV", "GREATEST", "LEAST",
            "CEIL", "MOD", "POWER", "SQRT", "CONCAT", "RAWTOHEX", "HEXTORAW", "CAST", "JSON_VALUE", "JSON_QUERY", "JSON_OBJECT",
            "JSON_TABLE", "XMLAGG", "XMLELEMENT", "NUMTODSINTERVAL", "NUMTOYMINTERVAL", "LNNVL", "NULLIF", "COALESCE",
        ],
    };

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALL", "AND", "ANY", "AS", "ASC", "BETWEEN", "BY", "CASE", "CHECK", "COLUMN", "CONSTRAINT", "CREATE", "CROSS",
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "DEFAULT", "DELETE", "DESC", "DISTINCT",
        "DROP", "ELSE", "END", "EXCEPT", "EXISTS", "FALSE", "FETCH", "FOR", "FOREIGN", "FROM", "FULL", "GRANT", "GROUP",
        "HAVING", "IN", "INNER", "INSERT", "INTERSECT", "INTO", "IS", "JOIN", "KEY", "LEFT", "LIKE", "LIMIT", "NATURAL",
        "NOT", "NULL", "OFFSET", "ON", "OR", "ORDER", "OUTER", "PRIMARY", "REFERENCES", "RIGHT", "SELECT", "SET", "TABLE",
        "THEN", "TO", "TRUE", "UNION", "UNIQUE", "UPDATE", "USER", "USING", "VALUES", "WHEN", "WHERE", "WITH", "WINDOW",
        "OVER", "PARTITION", "ROWS", "RANGE", "ANALYZE", "TOP", "INDEX", "VIEW", "TRIGGER", "PROCEDURE", "FUNCTION",
    };

    private static readonly HashSet<string> OracleReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCESS", "AUDIT", "CLUSTER", "COMMENT", "COMPRESS", "CONNECT", "DATE", "DECIMAL", "EXCLUSIVE", "FILE", "FLOAT",
        "IDENTIFIED", "IMMEDIATE", "INCREMENT", "INITIAL", "INTEGER", "LEVEL", "LOCK", "LONG", "MAXEXTENTS", "MINUS",
        "MLSLABEL", "MODE", "MODIFY", "NOAUDIT", "NOCOMPRESS", "NOWAIT", "NUMBER", "OF", "OFFLINE", "ONLINE", "OPTION",
        "PCTFREE", "PRIOR", "PUBLIC", "RAW", "RENAME", "RESOURCE", "REVOKE", "ROW", "ROWID", "ROWNUM", "SESSION", "SHARE",
        "SIZE", "SMALLINT", "START", "SUCCESSFUL", "SYNONYM", "SYSDATE", "UID", "VALIDATE", "VARCHAR", "VARCHAR2", "WHENEVER",
    };

    /// <summary>Gets the keywords of a dialect, upper-case.</summary>
    /// <param name="provider">The dialect.</param>
    /// <returns>The keywords.</returns>
    public static IReadOnlyList<string> For(DatabaseProvider provider) =>
        [.. Common, .. Extra.GetValueOrDefault(provider, [])];

    /// <summary>Gets the common built-in functions of a dialect, upper-case.</summary>
    /// <param name="provider">The dialect.</param>
    /// <returns>The function names.</returns>
    public static IReadOnlyList<string> Functions(DatabaseProvider provider) =>
        [.. CommonFunctions, .. ExtraFunctions.GetValueOrDefault(provider, [])];

    /// <summary>Gets whether a word is reserved and so must be quoted as an identifier.</summary>
    /// <param name="word">The word.</param>
    /// <returns><c>true</c> if reserved.</returns>
    public static bool IsReserved(string word) => Reserved.Contains(word);

    /// <summary>Gets whether a word is a keyword of the dialect.</summary>
    /// <param name="word">The word.</param>
    /// <param name="provider">The dialect.</param>
    /// <returns><c>true</c> if it is a keyword.</returns>
    public static bool IsKeyword(string word, DatabaseProvider provider) =>
        Common.Contains(word, StringComparer.OrdinalIgnoreCase)
        || Extra.GetValueOrDefault(provider, []).Contains(word, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Renders an identifier for SQL text, quoting it only when needed: when it
    /// is reserved, is not a plain lower-case (PostgreSQL) or plain (others) name,
    /// or would otherwise not survive unquoted.
    /// </summary>
    /// <param name="name">The identifier.</param>
    /// <param name="provider">The dialect.</param>
    /// <returns>The identifier, quoted if necessary.</returns>
    public static string QuoteIfNeeded(string name, DatabaseProvider provider)
    {
        // Oracle folds bare names to upper case, so only a plain upper-case name may go bare.
        if (provider == DatabaseProvider.Oracle)
        {
            var bare = name.Length > 0 && name[0] is >= 'A' and <= 'Z'
                && name.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '$' or '#')
                && !IsReserved(name) && !OracleReserved.Contains(name);
            return bare ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        var plain = name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_')
            && name.All(c => char.IsLetterOrDigit(c) || c == '_')
            && (provider != DatabaseProvider.PostgreSql || !name.Any(char.IsUpper))
            && !IsReserved(name);
        if (plain)
        {
            return name;
        }

        return provider switch
        {
            DatabaseProvider.SqlServer => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]",
            DatabaseProvider.MySql => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`",
            _ => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
        };
    }
}
