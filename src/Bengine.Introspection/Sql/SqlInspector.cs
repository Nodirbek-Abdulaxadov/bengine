namespace Bengine.Introspection.Sql;

using Bengine.Enums;
using Bengine.Introspection.Catalog;

/// <summary>How serious an inspection finding is.</summary>
public enum InspectionSeverity
{
    /// <summary>The statement will fail.</summary>
    Error,

    /// <summary>The statement is suspicious.</summary>
    Warning,
}

/// <summary>One inspection finding, with the text range it concerns.</summary>
/// <param name="Start">The start offset in the text.</param>
/// <param name="Length">The length.</param>
/// <param name="Message">The message.</param>
/// <param name="Severity">The severity.</param>
/// <param name="Code">A stable inspection id (<c>unresolved-table</c>, <c>missing-where</c>, …).</param>
public sealed record InspectionFinding(int Start, int Length, string Message, InspectionSeverity Severity, string Code);

/// <summary>
/// Inspects SQL as it is typed, without running it — the checks a database
/// IDE underlines: unresolved tables and qualified columns (against the
/// catalog), ambiguous unqualified columns, <c>UPDATE</c>/<c>DELETE</c>
/// without <c>WHERE</c>, comparisons with <c>NULL</c>, <c>INSERT</c> value
/// counts that do not match the column list, duplicate select aliases, unused
/// CTEs, a <c>JOIN</c> without <c>ON</c> in a <c>DELETE</c>, constant
/// conditions, unbalanced parentheses, unterminated strings and comments, and
/// modifying statements on a read-only connection. Checks that need the
/// catalog are skipped when it is empty, and names it cannot be sure of (CTEs,
/// derived tables, functions) are never reported.
/// </summary>
public static class SqlInspector
{
    /// <summary>Inspects every statement of a text.</summary>
    /// <param name="text">The editor text.</param>
    /// <param name="catalog">The catalog, or an empty one.</param>
    /// <param name="readOnly">Whether the connection is read-only.</param>
    /// <returns>The findings in text order.</returns>
    public static IReadOnlyList<InspectionFinding> Inspect(string text, DatabaseCatalog catalog, bool readOnly = false)
    {
        var provider = catalog.Provider;
        var findings = new List<InspectionFinding>();
        var lexemes = SqlLexer.Lex(text, provider);

        foreach (var open in lexemes.Where(l => !l.IsClosed))
        {
            var what = open.Kind switch
            {
                SqlLexemeKind.String => "string literal",
                SqlLexemeKind.Comment => "comment",
                SqlLexemeKind.QuotedIdentifier => "quoted identifier",
                _ => "token",
            };
            findings.Add(new(open.Start, Math.Min(open.Length, 1), $"Unterminated {what}.", InspectionSeverity.Error, "unterminated"));
        }

        foreach (var statement in SqlScript.Split(text, provider))
        {
            var tokens = lexemes.Where(l => l.IsSignificant && l.Start >= statement.Start && l.End <= statement.End).ToList();
            if (tokens.Count == 0)
            {
                continue;
            }

            Parentheses(tokens, findings);
            var info = SqlStatementInfo.Classify(statement.Text, provider);
            if (readOnly && info.CanModify)
            {
                findings.Add(new(tokens[0].Start, tokens[0].Length, $"The connection is read-only: this {info.Verb} will be refused.", InspectionSeverity.Error, "read-only"));
            }

            if (info.IsUnfilteredDml)
            {
                findings.Add(new(tokens[0].Start, tokens[0].Length, $"{info.Verb} without WHERE affects every row of the table.", InspectionSeverity.Warning, "missing-where"));
            }

            NullComparisons(tokens, findings);
            ConstantConditions(tokens, findings);
            DuplicateAliases(tokens, findings);
            UnusedCtes(tokens, findings);
            if (info.Verb == "DELETE")
            {
                JoinWithoutOn(tokens, findings);
            }

            if (catalog.Relations.Count > 0)
            {
                References(tokens, catalog, findings);
                InsertValues(tokens, catalog, findings);
            }
        }

        return findings.OrderBy(f => f.Start).ToList();
    }

    private static void Parentheses(List<SqlLexeme> tokens, List<InspectionFinding> findings)
    {
        var open = new Stack<SqlLexeme>();
        foreach (var token in tokens)
        {
            if (token.IsSymbol("("))
            {
                open.Push(token);
            }
            else if (token.IsSymbol(")"))
            {
                if (open.Count == 0)
                {
                    findings.Add(new(token.Start, 1, "Closing parenthesis without an opening one.", InspectionSeverity.Error, "parentheses"));
                }
                else
                {
                    open.Pop();
                }
            }
        }

        foreach (var token in open)
        {
            findings.Add(new(token.Start, 1, "Parenthesis is never closed.", InspectionSeverity.Error, "parentheses"));
        }
    }

    // x = NULL / x <> NULL is never true.
    private static void NullComparisons(List<SqlLexeme> tokens, List<InspectionFinding> findings)
    {
        for (var k = 1; k < tokens.Count; k++)
        {
            if (tokens[k].Is("NULL") && tokens[k - 1].Kind == SqlLexemeKind.Symbol && tokens[k - 1].Text is "=" or "<>" or "!=")
            {
                // SET col = NULL in an UPDATE is an assignment, not a comparison.
                if (tokens[k - 1].Text == "=" && IsAssignment(tokens, k - 1))
                {
                    continue;
                }

                var fix = tokens[k - 1].Text == "=" ? "IS NULL" : "IS NOT NULL";
                findings.Add(new(tokens[k - 1].Start, tokens[k].End - tokens[k - 1].Start, $"Comparison with NULL is never true; use {fix}.", InspectionSeverity.Warning, "null-comparison"));
            }
        }
    }

    // 1 = 1, 'a' = 'a': conditions that are always true.
    private static void ConstantConditions(List<SqlLexeme> tokens, List<InspectionFinding> findings)
    {
        for (var k = 1; k + 1 < tokens.Count; k++)
        {
            var (left, op, right) = (tokens[k - 1], tokens[k], tokens[k + 1]);
            if (op.IsSymbol("=") && left.Kind is SqlLexemeKind.Number or SqlLexemeKind.String && left.Kind == right.Kind && left.Text == right.Text
                && InCondition(tokens, k))
            {
                findings.Add(new(left.Start, right.End - left.Start, "The condition is always true.", InspectionSeverity.Warning, "constant-condition"));
            }
        }
    }

    private static void DuplicateAliases(List<SqlLexeme> tokens, List<InspectionFinding> findings)
    {
        var select = tokens.FindIndex(t => t.Is("SELECT"));
        if (select < 0)
        {
            return;
        }

        var aliases = new Dictionary<string, SqlLexeme>(StringComparer.OrdinalIgnoreCase);
        var depth = 0;
        for (var k = select + 1; k < tokens.Count; k++)
        {
            var t = tokens[k];
            if (t.IsSymbol("("))
            {
                depth++;
            }
            else if (t.IsSymbol(")"))
            {
                depth--;
            }
            else if (depth == 0 && t.Is("FROM"))
            {
                break;
            }
            else if (depth == 0 && t.Is("AS") && k + 1 < tokens.Count && tokens[k + 1].IsName)
            {
                var alias = tokens[k + 1];
                if (!aliases.TryAdd(alias.Identifier, alias))
                {
                    findings.Add(new(alias.Start, alias.Length, $"The alias {alias.Identifier} is used twice in the select list.", InspectionSeverity.Warning, "duplicate-alias"));
                }
            }
        }
    }

    private static void UnusedCtes(List<SqlLexeme> tokens, List<InspectionFinding> findings)
    {
        if (tokens.Count == 0 || !tokens[0].Is("WITH"))
        {
            return;
        }

        for (var k = 1; k + 2 < tokens.Count; k++)
        {
            if (tokens[k].IsName && tokens[k + 1].Is("AS") && tokens[k + 2].IsSymbol("(") && (tokens[k - 1].Is("WITH") || tokens[k - 1].IsSymbol(",") || tokens[k - 1].Is("RECURSIVE")))
            {
                var name = tokens[k].Identifier;
                var used = tokens.Where((t, i) => i != k && t.IsName && t.Identifier.Equals(name, StringComparison.OrdinalIgnoreCase)).Any();
                if (!used)
                {
                    findings.Add(new(tokens[k].Start, tokens[k].Length, $"The common table expression {name} is never used.", InspectionSeverity.Warning, "unused-cte"));
                }
            }
        }
    }

    private static void JoinWithoutOn(List<SqlLexeme> tokens, List<InspectionFinding> findings)
    {
        for (var k = 0; k < tokens.Count; k++)
        {
            if (!tokens[k].Is("JOIN") || (k > 0 && tokens[k - 1].Is("CROSS")))
            {
                continue;
            }

            var hasCondition = tokens.Skip(k + 1).TakeWhile(t => !t.Is("JOIN")).Any(t => t.Is("ON") || t.Is("USING"));
            if (!hasCondition && !tokens.Any(t => t.Is("WHERE")))
            {
                findings.Add(new(tokens[k].Start, tokens[k].Length, "JOIN without ON in a DELETE deletes from every combination of rows.", InspectionSeverity.Warning, "join-without-on"));
            }
        }
    }

    private static void References(List<SqlLexeme> tokens, DatabaseCatalog catalog, List<InspectionFinding> findings)
    {
        var scope = SqlCompletion.TableReferences(tokens, catalog);
        var ctes = scope.Where(r => r.IsDerived).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Unknown tables.
        foreach (var reference in scope.Where(r => r.Relation is null && !r.IsDerived))
        {
            if (reference.Schema is { } schema && !catalog.IsSchema(schema) && catalog.Provider != DatabaseProvider.MySql)
            {
                // Another database or a linked server: not something the catalog can judge.
                continue;
            }

            if (IsCreateTarget(tokens, reference.Start) || Parts(tokens, reference.Start) > 2)
            {
                // A table being created, or one in another database (db.schema.table).
                continue;
            }

            var token = tokens.First(t => t.Start == reference.Start);
            findings.Add(new(token.Start, NameLength(tokens, reference.Start), $"Unknown table {(reference.Schema is null ? string.Empty : reference.Schema + ".")}{reference.Name}.", InspectionSeverity.Error, "unresolved-table"));
        }

        // alias.column / table.column.
        var resolved = scope.Where(r => r.Relation is not null).ToList();
        for (var k = 0; k + 2 < tokens.Count; k++)
        {
            if (!tokens[k].IsName || !tokens[k + 1].IsSymbol(".") || !tokens[k + 2].IsName || (k > 0 && tokens[k - 1].IsSymbol(".")))
            {
                continue;
            }

            // schema.table.column — skip the schema part.
            if (k + 4 < tokens.Count && tokens[k + 3].IsSymbol(".") && tokens[k + 4].IsName)
            {
                continue;
            }

            var qualifier = tokens[k].Identifier;
            var reference = resolved.FirstOrDefault(r => string.Equals(r.Alias, qualifier, StringComparison.OrdinalIgnoreCase))
                ?? resolved.FirstOrDefault(r => r.Alias is null && r.Name.Equals(qualifier, StringComparison.OrdinalIgnoreCase));
            if (reference?.Relation is not { } relation || scope.Any(r => r.Start == tokens[k].Start))
            {
                continue;
            }

            var column = tokens[k + 2];
            if (column.IsSymbol("*") || relation.Column(column.Identifier) is not null || (k + 3 < tokens.Count && tokens[k + 3].IsSymbol("(")))
            {
                continue;
            }

            findings.Add(new(column.Start, column.Length, $"Unknown column {column.Identifier} in {relation.Name}.", InspectionSeverity.Error, "unresolved-column"));
        }

        // Unqualified column present in several tables of the scope (single-level
        // statements only: a subquery has a scope of its own).
        if (resolved.Count > 1 && ctes.Count == 0 && tokens.Count(t => t.Is("SELECT")) <= 1)
        {
            var clause = string.Empty;
            for (var k = 0; k < tokens.Count; k++)
            {
                var t = tokens[k];
                if (t.Kind == SqlLexemeKind.Word && t.Text.ToUpperInvariant() is "SELECT" or "WHERE" or "ON" or "BY" or "HAVING" or "FROM" or "JOIN" or "SET")
                {
                    clause = t.Text.ToUpperInvariant();
                    continue;
                }

                if (clause is "FROM" or "JOIN" or "SET" || !t.IsName || SqlKeywords.IsKeyword(t.Text, catalog.Provider)
                    || (k > 0 && tokens[k - 1].IsSymbol(".")) || (k + 1 < tokens.Count && (tokens[k + 1].IsSymbol(".") || tokens[k + 1].IsSymbol("(")))
                    || (k > 0 && tokens[k - 1].Is("AS")))
                {
                    continue;
                }

                var owners = resolved.Where(r => r.Relation!.Column(t.Identifier) is not null).Select(r => r.Exposed).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (owners.Count > 1)
                {
                    findings.Add(new(t.Start, t.Length, $"Column {t.Identifier} is ambiguous: it is in {string.Join(" and ", owners)}. Qualify it.", InspectionSeverity.Error, "ambiguous-column"));
                }
            }
        }
    }

    private static void InsertValues(List<SqlLexeme> tokens, DatabaseCatalog catalog, List<InspectionFinding> findings)
    {
        var into = tokens.FindIndex(t => t.Is("INTO"));
        if (into < 0 || into + 1 >= tokens.Count || !tokens[0].Is("INSERT") && !tokens[0].Is("REPLACE"))
        {
            return;
        }

        var k = into + 1;
        var parts = new List<string> { tokens[k].Identifier };
        while (k + 2 < tokens.Count && tokens[k + 1].IsSymbol(".") && tokens[k + 2].IsName)
        {
            parts.Add(tokens[k + 2].Identifier);
            k += 2;
        }

        var relation = catalog.Find(parts.Count > 1 ? parts[^2] : null, parts[^1]);
        if (relation is null)
        {
            return;
        }

        k++;
        int expected;
        if (k < tokens.Count && tokens[k].IsSymbol("("))
        {
            var list = Group(tokens, k);
            var columns = list.Where(t => t.IsName).ToList();
            foreach (var column in columns.Where(c => relation.Column(c.Identifier) is null))
            {
                findings.Add(new(column.Start, column.Length, $"Unknown column {column.Identifier} in {relation.Name}.", InspectionSeverity.Error, "unresolved-column"));
            }

            foreach (var column in columns.Where(c => relation.Column(c.Identifier) is { IsGenerated: true }))
            {
                findings.Add(new(column.Start, column.Length, $"{column.Identifier} is a generated column and cannot be written.", InspectionSeverity.Error, "generated-column"));
            }

            expected = columns.Count;
            k += list.Count + 2;
        }
        else
        {
            expected = relation.Columns.Count(c => !c.IsGenerated);
        }

        if (k >= tokens.Count || !tokens[k].Is("VALUES"))
        {
            return;
        }

        for (k++; k < tokens.Count && tokens[k].IsSymbol("("); )
        {
            var tuple = Group(tokens, k);
            var count = tuple.Count == 0 ? 0 : TopLevelCommas(tuple) + 1;
            if (count != expected)
            {
                findings.Add(new(tokens[k].Start, 1, $"{count} value(s) for {expected} column(s).", InspectionSeverity.Error, "insert-values"));
            }

            k += tuple.Count + 2;
            if (k < tokens.Count && tokens[k].IsSymbol(","))
            {
                k++;
            }
        }
    }

    // The tokens inside the parenthesis group opening at index.
    private static List<SqlLexeme> Group(List<SqlLexeme> tokens, int open)
    {
        var depth = 0;
        for (var k = open; k < tokens.Count; k++)
        {
            if (tokens[k].IsSymbol("("))
            {
                depth++;
            }
            else if (tokens[k].IsSymbol(")") && --depth == 0)
            {
                return tokens.GetRange(open + 1, k - open - 1);
            }
        }

        return tokens.GetRange(open + 1, tokens.Count - open - 1);
    }

    private static int TopLevelCommas(List<SqlLexeme> tokens)
    {
        var depth = 0;
        var commas = 0;
        foreach (var t in tokens)
        {
            depth += t.IsSymbol("(") ? 1 : t.IsSymbol(")") ? -1 : 0;
            if (depth == 0 && t.IsSymbol(","))
            {
                commas++;
            }
        }

        return commas;
    }

    private static bool IsAssignment(List<SqlLexeme> tokens, int index)
    {
        for (var k = index - 1; k >= 0; k--)
        {
            if (tokens[k].Is("WHERE") || tokens[k].Is("ON") || tokens[k].Is("HAVING") || tokens[k].Is("AND") || tokens[k].Is("OR"))
            {
                return false;
            }

            if (tokens[k].Is("SET"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InCondition(List<SqlLexeme> tokens, int index)
    {
        for (var k = index - 1; k >= 0; k--)
        {
            if (tokens[k].Is("WHERE") || tokens[k].Is("ON") || tokens[k].Is("HAVING"))
            {
                return true;
            }

            if (tokens[k].Is("SELECT") || tokens[k].Is("SET") || tokens[k].Is("FROM"))
            {
                return false;
            }
        }

        return false;
    }

    // CREATE TABLE x / ALTER TABLE x RENAME / DROP TABLE IF EXISTS x name tables that may not exist yet.
    private static bool IsCreateTarget(List<SqlLexeme> tokens, int start) =>
        tokens.Count > 0 && (tokens[0].Is("CREATE") || tokens[0].Is("DROP") || (tokens[0].Is("ALTER") && tokens.Any(t => t.Is("RENAME"))))
        && tokens.Take(6).Any(t => t.Start <= start);

    private static int Parts(List<SqlLexeme> tokens, int start)
    {
        var k = tokens.FindIndex(t => t.Start == start);
        var parts = 1;
        while (k + 2 < tokens.Count && tokens[k + 1].IsSymbol(".") && tokens[k + 2].IsName)
        {
            parts++;
            k += 2;
        }

        return parts;
    }

    private static int NameLength(List<SqlLexeme> tokens, int start)
    {
        var k = tokens.FindIndex(t => t.Start == start);
        var end = tokens[k].End;
        while (k + 2 < tokens.Count && tokens[k + 1].IsSymbol(".") && tokens[k + 2].IsName)
        {
            end = tokens[k + 2].End;
            k += 2;
        }

        return end - start;
    }
}
