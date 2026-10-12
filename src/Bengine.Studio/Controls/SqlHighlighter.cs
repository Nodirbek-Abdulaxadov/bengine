namespace Bengine.Studio.Controls;

using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.Media;

/// <summary>
/// Splits SQL into colored <see cref="Run"/>s — keywords, types, strings,
/// numbers, quoted identifiers, and comments — for display in a
/// <c>SelectableTextBlock</c>. Mirrors the web explorer's highlighter.
/// </summary>
public static partial class SqlHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "AFTER", "ALL", "ALTER", "ALWAYS", "AND", "AS", "ASC", "AUTHORIZATION", "BEFORE", "BEGIN", "BY", "CACHE",
        "CASCADE", "CASE", "CAST", "CHECK", "COLLATE", "COLLATION", "COLUMN", "COMMENT", "CONNECTION", "CONSTRAINT",
        "CREATE", "CYCLE", "DATA", "DATABASE", "DECLARE", "DEFAULT", "DEFERRABLE", "DELETE", "DESC", "DOMAIN", "DROP",
        "EACH", "ELSE", "END", "ENUM", "EVENT", "EXECUTE", "EXISTS", "EXTENSION", "FALSE", "FOR", "FOREIGN", "FROM",
        "FUNCTION", "GENERATED", "GRANT", "GROUP", "HANDLER", "IDENTITY", "IF", "IMMUTABLE", "IN", "INCREMENT", "INDEX",
        "INLINE", "INSERT", "INSTEAD", "INTO", "IS", "IS_TEMPLATE", "JOIN", "KEY", "LANGUAGE", "LC_COLLATE", "LC_CTYPE",
        "LEFT", "LIMIT", "LOCATION", "LOGIN", "MATERIALIZED", "MAXVALUE", "MINVALUE", "MODIFY", "NO", "NOT", "NULL",
        "OF", "OFFSET", "ON", "OPTIONS", "OR", "ORDER", "OWNER", "PARALLEL", "PRIMARY", "PROCEDURAL", "PROCEDURE",
        "PROVIDER", "PUBLICATION", "RANGE", "REFERENCES", "REPLACE", "RESTRICT", "RETURN", "RETURNS", "ROLE", "ROW",
        "SCHEMA", "SECURITY", "SELECT", "SEQUENCE", "SERVER", "SET", "SFUNC", "STABLE", "START", "STORED", "STYPE",
        "SUBSCRIPTION", "SUBTYPE", "TABLE", "TABLES", "TABLESPACE", "TAG", "THEN", "TO", "TRIGGER", "TRUE", "TRUSTED",
        "TYPE", "UNIQUE", "UPDATE", "USING", "VALIDATOR", "VALUES", "VERSION", "VIEW", "VOLATILE", "WHEN", "WHERE",
        "WITH", "WITHOUT", "WRAPPER", "ENCODING", "ACTION", "INHERIT", "NOINHERIT", "SUPERUSER", "NOSUPERUSER",
        "CREATEDB", "NOCREATEDB", "CREATEROLE", "NOCREATEROLE", "REPLICATION", "NOREPLICATION", "BYPASSRLS",
        "NOBYPASSRLS", "NOLOGIN", "VALID", "UNTIL", "EXPLAIN", "ANALYZE",
    };

    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        "bigint", "bigserial", "bit", "bool", "boolean", "bytea", "char", "character", "date", "datetime", "decimal",
        "double", "float", "int", "integer", "interval", "json", "jsonb", "money", "nchar", "numeric", "nvarchar",
        "precision", "real", "serial", "smallint", "text", "time", "timestamp", "timestamptz", "tinyint", "uuid",
        "varchar", "varying", "xml", "blob",
    };

    private static readonly IBrush Keyword = new SolidColorBrush(Color.Parse("#c792ea"));
    private static readonly IBrush Type = new SolidColorBrush(Color.Parse("#82aaff"));
    private static readonly IBrush String = new SolidColorBrush(Color.Parse("#c3e88d"));
    private static readonly IBrush Number = new SolidColorBrush(Color.Parse("#f78c6c"));
    private static readonly IBrush Comment = new SolidColorBrush(Color.Parse("#637087"));
    private static readonly IBrush Identifier = new SolidColorBrush(Color.Parse("#ffcb6b"));

    /// <summary>Builds the colored runs for a SQL text.</summary>
    /// <param name="sql">The SQL.</param>
    /// <returns>The inlines to show.</returns>
    public static InlineCollection Highlight(string sql)
    {
        var inlines = new InlineCollection();
        var last = 0;
        foreach (Match match in Token().Matches(sql))
        {
            if (match.Index > last)
            {
                inlines.Add(new Run(sql[last..match.Index]));
            }

            var run = new Run(match.Value);
            if (match.Groups[1].Success)
            {
                run.Foreground = Comment;
                run.FontStyle = FontStyle.Italic;
            }
            else if (match.Groups[2].Success)
            {
                run.Foreground = String;
            }
            else if (match.Groups[3].Success)
            {
                run.Foreground = Identifier;
            }
            else if (match.Groups[4].Success)
            {
                run.Foreground = Number;
            }
            else if (Keywords.Contains(match.Value))
            {
                run.Foreground = Keyword;
                run.FontWeight = FontWeight.SemiBold;
            }
            else if (Types.Contains(match.Value))
            {
                run.Foreground = Type;
            }

            inlines.Add(run);
            last = match.Index + match.Length;
        }

        if (last < sql.Length)
        {
            inlines.Add(new Run(sql[last..]));
        }

        return inlines;
    }

    [GeneratedRegex("""(--[^\n]*|/\*[\s\S]*?\*/)|('(?:''|[^'])*')|("(?:""|[^"])*"|`[^`]*`|\[[^\]\n]*\])|(\b\d+(?:\.\d+)?\b)|[A-Za-z_][A-Za-z0-9_$]*""")]
    private static partial Regex Token();
}
