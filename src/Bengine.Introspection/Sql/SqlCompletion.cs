namespace Bengine.Introspection.Sql;

using Bengine.Enums;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Model;

/// <summary>What a completion item inserts.</summary>
public enum CompletionKind
{
    /// <summary>A SQL keyword.</summary>
    Keyword,

    /// <summary>A schema.</summary>
    Schema,

    /// <summary>A table.</summary>
    Table,

    /// <summary>A view.</summary>
    View,

    /// <summary>A column.</summary>
    Column,

    /// <summary>A built-in function.</summary>
    Function,

    /// <summary>A user-defined function or procedure.</summary>
    Routine,

    /// <summary>A table to join, with its alias and <c>ON</c> condition derived from a foreign key.</summary>
    Join,

    /// <summary>A join condition derived from a foreign key.</summary>
    Condition,

    /// <summary>A table alias in scope.</summary>
    Alias,
}

/// <summary>One completion proposal.</summary>
/// <param name="Label">The text shown in the list.</param>
/// <param name="InsertText">The text inserted in place of the typed prefix.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Detail">Secondary text: a column's type and table, a table's schema.</param>
public sealed record CompletionItem(string Label, string InsertText, CompletionKind Kind, string? Detail = null)
{
    /// <summary>Gets the ranking score; higher sorts first.</summary>
    public int Score { get; init; }
}

/// <summary>The proposals for a caret position and the text range they replace.</summary>
/// <param name="ReplaceStart">The offset of the typed prefix.</param>
/// <param name="ReplaceLength">The prefix length.</param>
/// <param name="Prefix">The typed prefix.</param>
/// <param name="Items">The proposals, best first.</param>
public sealed record CompletionResult(int ReplaceStart, int ReplaceLength, string Prefix, IReadOnlyList<CompletionItem> Items)
{
    /// <summary>Gets an empty result at a caret.</summary>
    /// <param name="caret">The caret.</param>
    /// <returns>An empty result.</returns>
    public static CompletionResult None(int caret) => new(caret, 0, string.Empty, []);
}

/// <summary>A table referenced by a statement, with its alias.</summary>
/// <param name="Schema">The schema qualifier as written, or <c>null</c>.</param>
/// <param name="Name">The table name as written.</param>
/// <param name="Alias">The alias, or <c>null</c>.</param>
/// <param name="Relation">The catalog relation it resolves to, or <c>null</c> when unknown (a CTE, a derived table, a missing table).</param>
/// <param name="Start">The offset of the reference in the statement text.</param>
/// <param name="IsDerived">Whether the reference is a CTE or derived table rather than a stored relation.</param>
public sealed record TableReference(string? Schema, string Name, string? Alias, CatalogRelation? Relation, int Start, bool IsDerived = false)
{
    /// <summary>Gets the name other clauses use for the reference: the alias, else the table name.</summary>
    public string Exposed => Alias ?? Name;
}

/// <summary>
/// Context-aware SQL completion over a <see cref="DatabaseCatalog"/>, modeled on
/// a database IDE: after <c>FROM</c>/<c>JOIN</c>/<c>UPDATE</c>/<c>INTO</c> it
/// proposes tables (and, after <c>JOIN</c>, whole join clauses derived from
/// foreign keys, such as <c>orders o ON o.user_id = u.id</c>); after <c>ON</c> it
/// proposes foreign-key conditions; after <c>alias.</c> the alias's columns;
/// after <c>schema.</c> that schema's tables; in expressions the columns of the
/// tables in scope (qualified when ambiguous), functions, and keywords. Aliases
/// are resolved over the whole statement, so columns complete in a
/// <c>SELECT</c> list before its <c>FROM</c> is written out further on.
/// Matching is case-insensitive: prefix matches first, then word-start
/// matches (<c>ui</c> → <c>user_id</c>), then substring matches.
/// </summary>
public static class SqlCompletion
{
    private static readonly HashSet<string> TableIntroducers = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "JOIN", "UPDATE", "INTO", "TABLE", "TRUNCATE", "DESCRIBE", "DESC",
    };

    private static readonly HashSet<string> ExpressionIntroducers = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WHERE", "AND", "OR", "ON", "BY", "HAVING", "SET", "NOT", "CASE", "WHEN", "THEN", "ELSE", "DISTINCT",
        "RETURNING", "IN", "LIKE", "ILIKE", "BETWEEN", "IS", "TOP", "USING", "VALUES", "EXISTS", "ALL", "ANY",
    };

    private static readonly HashSet<string> ClauseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "JOIN", "ON", "SET", "VALUES", "INTO",
        "UPDATE", "DELETE", "INSERT", "RETURNING", "UNION", "EXCEPT", "INTERSECT", "WINDOW", "USING", "WITH",
    };

    /// <summary>Computes the proposals at a caret.</summary>
    /// <param name="text">The editor text.</param>
    /// <param name="caret">The caret offset.</param>
    /// <param name="catalog">The database catalog.</param>
    /// <returns>The proposals.</returns>
    public static CompletionResult Complete(string text, int caret, DatabaseCatalog catalog)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var provider = catalog.Provider;
        if (provider == DatabaseProvider.MongoDb)
        {
            return Mongo.MongoCompletion.Complete(text, caret, catalog);
        }

        var lexemes = SqlLexer.Lex(text, provider);

        // No completion inside a comment, a string, or a closed quoted name.
        var at = lexemes.FirstOrDefault(l => l.Start < caret && caret < l.End || (l.End == caret && !l.IsClosed));
        if (at.Length > 0 && (at.Kind is SqlLexemeKind.String or SqlLexemeKind.Comment
            || (at.Kind == SqlLexemeKind.QuotedIdentifier && at.IsClosed)))
        {
            return CompletionResult.None(caret);
        }

        if (lexemes.FirstOrDefault(l => l.End == caret && l.Kind == SqlLexemeKind.Comment && l.Text.StartsWith("--", StringComparison.Ordinal)).Length > 0)
        {
            return CompletionResult.None(caret);
        }

        // The statement around the caret: from the previous ';' to the next one.
        var significant = lexemes.Where(l => l.IsSignificant).ToList();
        var first = significant.FindLastIndex(l => l.IsSymbol(";") && l.End <= caret) + 1;
        var last = significant.FindIndex(first, l => l.IsSymbol(";") && l.Start >= caret);
        var tokens = significant.GetRange(first, (last < 0 ? significant.Count : last) - first);

        // The identifier being typed.
        var prefixToken = tokens.LastOrDefault(l => l.End == caret && l.IsName);
        var prefix = prefixToken.Length > 0 ? prefixToken.Identifier : string.Empty;
        var replaceStart = prefixToken.Length > 0 ? prefixToken.Start : caret;
        var before = tokens.Where(l => l.End <= replaceStart).ToList();

        var scope = TableReferences(tokens, catalog);
        var items = new List<CompletionItem>();

        // alias. / schema. / schema.table.
        if (before.Count > 0 && before[^1].IsSymbol("."))
        {
            var chain = QualifierChain(before);
            items.AddRange(Qualified(chain, scope, catalog));
            return Rank(replaceStart, caret - replaceStart, prefix, items);
        }

        var previous = before.Count > 0 ? before[^1] : default;
        var context = Classify(before, scope);
        switch (context)
        {
            case Context.TableName:
                items.AddRange(Tables(catalog, provider));
                items.AddRange(catalog.Schemas.Select(s => new CompletionItem(s, SqlKeywords.QuoteIfNeeded(s, provider), CompletionKind.Schema)));
                if (previous.Is("JOIN"))
                {
                    items.AddRange(Joins(scope.Where(r => r.Start < replaceStart).ToList(), catalog, provider));
                }

                break;

            case Context.AfterTable:
                items.AddRange(Keywords(AfterTableKeywords(before), provider));
                break;

            case Context.InsertColumns:
                var target = scope.FirstOrDefault(r => r.Start < replaceStart && r.Relation is not null);
                if (target?.Relation is { } relation)
                {
                    items.AddRange(relation.Columns.Select(c => Column(c, relation, null, provider)));
                }

                break;

            case Context.Expression:
                var visible = scope.Where(r => !r.IsDerived || r.Alias is not null).ToList();
                if (previous.Is("ON") || ((previous.Is("AND") || previous.Is("OR")) && ClauseOf(before)?.Is("ON") == true))
                {
                    items.AddRange(Conditions(scope.Where(r => r.Start < replaceStart).ToList(), catalog, provider));
                }

                items.AddRange(Columns(visible, provider));
                items.AddRange(visible.Select(r => new CompletionItem(r.Exposed, SqlKeywords.QuoteIfNeeded(r.Exposed, provider), CompletionKind.Alias, r.Relation?.Ref.QualifiedName)));
                items.AddRange(SqlKeywords.Functions(provider).Select(f => new CompletionItem(f.ToLowerInvariant(), f.ToLowerInvariant(), CompletionKind.Function)));
                items.AddRange(catalog.Routines.Where(r => !r.IsProcedure).Select(r => new CompletionItem(r.Name, SqlKeywords.QuoteIfNeeded(r.Name, provider), CompletionKind.Routine, r.Schema)));
                items.AddRange(Keywords(ExpressionKeywords, provider));
                if (previous.Is("SELECT") && scope.Count == 0)
                {
                    items.AddRange(Tables(catalog, provider).Select(t => t with { Score = -1 }));
                }

                break;

            case Context.AfterExpression:
                items.AddRange(Keywords(AfterExpressionKeywords(before), provider));
                break;

            default:
                items.AddRange(Keywords(before.Count == 0 ? StatementKeywords : SqlKeywords.For(provider), provider));
                break;
        }

        return Rank(replaceStart, caret - replaceStart, prefix, items);
    }

    /// <summary>Finds the table references of a statement: <c>FROM</c>/<c>JOIN</c> items, <c>UPDATE</c>/<c>INTO</c>/<c>DELETE FROM</c> targets, and CTE names.</summary>
    /// <param name="statementTokens">The statement's significant lexemes.</param>
    /// <param name="catalog">The catalog to resolve names against.</param>
    /// <returns>The references in text order.</returns>
    public static IReadOnlyList<TableReference> TableReferences(IReadOnlyList<SqlLexeme> statementTokens, DatabaseCatalog catalog)
    {
        var tokens = statementTokens as List<SqlLexeme> ?? statementTokens.ToList();
        var result = new List<TableReference>();
        var ctes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // WITH name AS ( … ), name2 AS ( … )
        for (var k = 0; k + 2 < tokens.Count; k++)
        {
            if (tokens[k].IsName && tokens[k + 1].Is("AS") && tokens[k + 2].IsSymbol("(")
                && k > 0 && (tokens[k - 1].Is("WITH") || tokens[k - 1].IsSymbol(",") || tokens[k - 1].Is("RECURSIVE")))
            {
                ctes.Add(tokens[k].Identifier);
            }
        }

        for (var k = 0; k < tokens.Count; k++)
        {
            var token = tokens[k];
            var isFromList = token.Is("FROM") || token.Is("JOIN") || token.Is("USING") && k > 0 && HasDeleteBefore(tokens, k)
                || token.Is("APPLY");
            var isTarget = token.Is("UPDATE") || token.Is("INTO") || (token.Is("TABLE") && k > 0 && (tokens[k - 1].Is("ALTER") || tokens[k - 1].Is("TRUNCATE")));
            if (!isFromList && !isTarget)
            {
                continue;
            }

            var j = k + 1;
            while (j < tokens.Count)
            {
                // A derived table: ( SELECT … ) alias
                if (tokens[j].IsSymbol("("))
                {
                    var close = MatchingParen(tokens, j);
                    j = close + 1;
                    var derivedAlias = ReadAlias(tokens, ref j);
                    if (derivedAlias is not null)
                    {
                        result.Add(new TableReference(null, derivedAlias, derivedAlias, null, tokens[Math.Min(close, tokens.Count - 1)].Start, IsDerived: true));
                    }
                }
                else if (tokens[j].IsName && !IsClauseOrJoinWord(tokens[j]) && !tokens[j].Is("ONLY") && !tokens[j].Is("LATERAL"))
                {
                    var start = tokens[j].Start;
                    var parts = new List<string> { tokens[j].Identifier };
                    j++;
                    while (j + 1 < tokens.Count && tokens[j].IsSymbol(".") && tokens[j + 1].IsName)
                    {
                        parts.Add(tokens[j + 1].Identifier);
                        j += 2;
                    }

                    // A function in FROM (generate_series(…)) is not a table; INSERT INTO t (cols) is.
                    if (j < tokens.Count && tokens[j].IsSymbol("(") && !(isTarget && token.Is("INTO")))
                    {
                        j = MatchingParen(tokens, j) + 1;
                        var fnAlias = ReadAlias(tokens, ref j);
                        if (fnAlias is not null)
                        {
                            result.Add(new TableReference(null, fnAlias, fnAlias, null, start, IsDerived: true));
                        }
                    }
                    else
                    {
                        var name = parts[^1];
                        var schema = parts.Count >= 2 ? parts[^2] : null;
                        var alias = isTarget && token.Is("INTO") ? null : ReadAlias(tokens, ref j);
                        var isCte = schema is null && ctes.Contains(name);
                        var relation = isCte ? null : catalog.Find(schema, name);
                        result.Add(new TableReference(schema, name, alias, relation, start, IsDerived: isCte));
                    }
                }
                else
                {
                    break;
                }

                // FROM a, b — a comma continues the list; anything else ends it.
                if (isFromList && j < tokens.Count && tokens[j].IsSymbol(","))
                {
                    j++;
                    continue;
                }

                break;
            }
        }

        return result;
    }

    private enum Context
    {
        Keyword,
        TableName,
        AfterTable,
        InsertColumns,
        Expression,
        AfterExpression,
    }

    private static readonly string[] StatementKeywords =
    [
        "SELECT", "INSERT INTO", "UPDATE", "DELETE FROM", "WITH", "CREATE TABLE", "CREATE VIEW", "CREATE INDEX", "ALTER TABLE",
        "DROP TABLE", "TRUNCATE TABLE", "EXPLAIN", "BEGIN", "COMMIT", "ROLLBACK", "GRANT", "REVOKE",
    ];

    private static readonly string[] ExpressionKeywords =
    [
        "DISTINCT", "CASE", "WHEN", "THEN", "ELSE", "END", "NOT", "NULL", "TRUE", "FALSE", "EXISTS", "CAST", "AS",
    ];

    private static Context Classify(List<SqlLexeme> before, IReadOnlyList<TableReference> scope)
    {
        if (before.Count == 0)
        {
            return Context.Keyword;
        }

        var previous = before[^1];
        if (previous.Kind == SqlLexemeKind.Word && TableIntroducers.Contains(previous.Text)
            && !(previous.Is("DESC") && before.Count > 1)
            && !(previous.Is("INTO") && before.Count > 1 && !before.Any(l => l.Is("INSERT") || l.Is("MERGE") || l.Is("REPLACE"))))
        {
            return Context.TableName;
        }

        // FROM a, |
        if (previous.IsSymbol(",") && ClauseOf(before) is { } clause && clause.Is("FROM"))
        {
            return Context.TableName;
        }

        // INSERT INTO t ( |  or  INSERT INTO t (a, |
        if ((previous.IsSymbol("(") || previous.IsSymbol(",")) && InInsertColumnList(before))
        {
            return Context.InsertColumns;
        }

        // FROM t |  /  FROM t x |  /  JOIN s.t AS x |
        if (IsAfterTableReference(before))
        {
            return Context.AfterTable;
        }

        if ((previous.Kind == SqlLexemeKind.Word && ExpressionIntroducers.Contains(previous.Text))
            || previous.IsSymbol(",") || previous.IsSymbol("(") || IsOperator(previous))
        {
            return Context.Expression;
        }

        if (previous.IsName || previous.Kind is SqlLexemeKind.Number or SqlLexemeKind.String or SqlLexemeKind.Parameter
            || previous.IsSymbol(")") || previous.IsSymbol("*"))
        {
            return ClauseOf(before) is null ? Context.Keyword : Context.AfterExpression;
        }

        return Context.Keyword;
    }

    private static bool IsOperator(SqlLexeme lexeme) =>
        lexeme.Kind == SqlLexemeKind.Symbol && lexeme.Text is "=" or "<" or ">" or "<=" or ">=" or "<>" or "!=" or "+" or "-"
            or "/" or "%" or "||" or "::" or "->" or "->>";

    private static SqlLexeme? ClauseOf(List<SqlLexeme> before)
    {
        var depth = 0;
        for (var k = before.Count - 1; k >= 0; k--)
        {
            var l = before[k];
            if (l.IsSymbol(")"))
            {
                depth++;
            }
            else if (l.IsSymbol("("))
            {
                if (depth == 0)
                {
                    return null;
                }

                depth--;
            }
            else if (depth == 0 && l.Kind == SqlLexemeKind.Word && ClauseWords.Contains(l.Text))
            {
                return l;
            }
        }

        return null;
    }

    private static bool InInsertColumnList(List<SqlLexeme> before)
    {
        var depth = 0;
        for (var k = before.Count - 1; k >= 0; k--)
        {
            var l = before[k];
            if (l.IsSymbol(")"))
            {
                depth++;
            }
            else if (l.IsSymbol("("))
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }

                // ( must follow INTO name[.name]
                var j = k - 1;
                if (j < 0 || !before[j].IsName)
                {
                    return false;
                }

                while (j - 2 >= 0 && before[j - 1].IsSymbol(".") && before[j - 2].IsName)
                {
                    j -= 2;
                }

                return j - 1 >= 0 && before[j - 1].Is("INTO");
            }
        }

        return false;
    }

    private static bool IsAfterTableReference(List<SqlLexeme> before)
    {
        // Walk back over: [AS] alias, then name(.name)*, and expect a table introducer or a FROM-list comma.
        var k = before.Count - 1;
        if (k < 0 || !before[k].IsName || IsClauseOrJoinWord(before[k]))
        {
            return false;
        }

        bool StartsReference(int index) =>
            index >= 0 && ((before[index].Kind == SqlLexemeKind.Word && (TableIntroducers.Contains(before[index].Text) || before[index].Is("APPLY")))
                || (before[index].IsSymbol(",") && ClauseOf(before.Take(index).ToList()) is { } c && c.Is("FROM")));

        // name(.name)* directly after the introducer.
        var j = k;
        while (j - 2 >= 0 && before[j - 1].IsSymbol(".") && before[j - 2].IsName)
        {
            j -= 2;
        }

        if (StartsReference(j - 1))
        {
            return true;
        }

        // alias after the name: introducer name [AS] alias
        j = k - 1;
        if (j >= 0 && before[j].Is("AS"))
        {
            j--;
        }

        if (j < 0 || !before[j].IsName)
        {
            return false;
        }

        while (j - 2 >= 0 && before[j - 1].IsSymbol(".") && before[j - 2].IsName)
        {
            j -= 2;
        }

        return StartsReference(j - 1);
    }

    private static IEnumerable<string> AfterTableKeywords(List<SqlLexeme> before)
    {
        var clause = ClauseOf(before);
        var verb = before.FirstOrDefault(l => l.Kind == SqlLexemeKind.Word);
        if (verb.Is("UPDATE") && clause?.Is("UPDATE") == true)
        {
            return ["SET", "AS"];
        }

        if (clause?.Is("JOIN") == true)
        {
            return ["ON", "USING", "AS", "WHERE"];
        }

        if (clause?.Is("INTO") == true)
        {
            return ["VALUES", "SELECT", "DEFAULT VALUES"];
        }

        return ["WHERE", "JOIN", "LEFT JOIN", "INNER JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "ORDER BY", "GROUP BY",
            "LIMIT", "AS", "UNION", "HAVING"];
    }

    private static IEnumerable<string> AfterExpressionKeywords(List<SqlLexeme> before)
    {
        var clause = ClauseOf(before);
        return clause?.Text.ToUpperInvariant() switch
        {
            "SELECT" => ["FROM", "AS", "INTO"],
            "WHERE" or "ON" or "HAVING" => ["AND", "OR", "IS NULL", "IS NOT NULL", "IN", "LIKE", "BETWEEN", "ORDER BY", "GROUP BY", "LIMIT", "NOT"],
            "ORDER" or "GROUP" => ["ASC", "DESC", "NULLS FIRST", "NULLS LAST", "LIMIT", "HAVING"],
            "SET" => ["WHERE", "FROM"],
            _ => ["AND", "OR", "AS", "FROM", "WHERE", "ORDER BY", "GROUP BY", "LIMIT"],
        };
    }

    private static List<string> QualifierChain(List<SqlLexeme> before)
    {
        var chain = new List<string>();
        var k = before.Count - 1;
        while (k >= 1 && before[k].IsSymbol(".") && before[k - 1].IsName)
        {
            chain.Insert(0, before[k - 1].Identifier);
            k -= 2;
        }

        return chain;
    }

    private static IEnumerable<CompletionItem> Qualified(List<string> chain, IReadOnlyList<TableReference> scope, DatabaseCatalog catalog)
    {
        var provider = catalog.Provider;
        if (chain.Count == 0)
        {
            yield break;
        }

        if (chain.Count == 1)
        {
            var name = chain[0];
            var reference = scope.FirstOrDefault(r => string.Equals(r.Alias, name, StringComparison.OrdinalIgnoreCase))
                ?? scope.FirstOrDefault(r => r.Alias is null && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            var relation = reference?.Relation ?? (reference is null ? catalog.Find(null, name) : null);
            if (relation is not null)
            {
                foreach (var column in relation.Columns)
                {
                    yield return Column(column, relation, null, provider);
                }
            }

            if (catalog.IsSchema(name))
            {
                foreach (var item in catalog.InSchema(name).Select(r => Table(r, provider, qualify: false)))
                {
                    yield return item;
                }

                foreach (var routine in catalog.Routines.Where(r => string.Equals(r.Schema, name, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return new CompletionItem(routine.Name, SqlKeywords.QuoteIfNeeded(routine.Name, provider), CompletionKind.Routine, routine.Schema);
                }
            }

            yield break;
        }

        // schema.table.
        if (catalog.Find(chain[^2], chain[^1]) is { } qualified)
        {
            foreach (var column in qualified.Columns)
            {
                yield return Column(column, qualified, null, provider);
            }
        }
    }

    private static IEnumerable<CompletionItem> Tables(DatabaseCatalog catalog, DatabaseProvider provider) =>
        catalog.Relations.Select(r => Table(r, provider, qualify: r.Schema is not null && !string.Equals(r.Schema, catalog.DefaultSchema, StringComparison.OrdinalIgnoreCase)));

    private static CompletionItem Table(CatalogRelation relation, DatabaseProvider provider, bool qualify)
    {
        var name = SqlKeywords.QuoteIfNeeded(relation.Name, provider);
        var insert = qualify && relation.Schema is { } schema ? $"{SqlKeywords.QuoteIfNeeded(schema, provider)}.{name}" : name;
        return new CompletionItem(relation.Name, insert, relation.Kind == TableKind.View ? CompletionKind.View : CompletionKind.Table, relation.Schema)
        {
            Score = qualify ? -1 : 0,
        };
    }

    private static IEnumerable<CompletionItem> Columns(IReadOnlyList<TableReference> scope, DatabaseProvider provider)
    {
        var resolved = scope.Where(r => r.Relation is not null).ToList();
        var counts = resolved
            .SelectMany(r => r.Relation!.Columns.Select(c => c.Name))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var reference in resolved)
        {
            foreach (var column in reference.Relation!.Columns)
            {
                var ambiguous = counts[column.Name] > 1;
                yield return Column(column, reference.Relation, ambiguous ? reference.Exposed : null, provider);
            }
        }
    }

    private static CompletionItem Column(CatalogColumn column, CatalogRelation relation, string? qualifier, DatabaseProvider provider)
    {
        var name = SqlKeywords.QuoteIfNeeded(column.Name, provider);
        var insert = qualifier is null ? name : $"{SqlKeywords.QuoteIfNeeded(qualifier, provider)}.{name}";
        var label = qualifier is null ? column.Name : $"{qualifier}.{column.Name}";
        return new CompletionItem(label, insert, CompletionKind.Column, $"{column.Type} · {relation.Name}") { Score = 2 };
    }

    // JOIN |  →  "orders o ON o.user_id = u.id"
    private static IEnumerable<CompletionItem> Joins(List<TableReference> scope, DatabaseCatalog catalog, DatabaseProvider provider)
    {
        var taken = new HashSet<string>(scope.Select(r => r.Exposed), StringComparer.OrdinalIgnoreCase);
        foreach (var reference in scope.Where(r => r.Relation is not null))
        {
            var mine = reference.Relation!;
            var keys = catalog.Outgoing(mine).Select(k => (Key: k, Other: catalog.Find(k.ReferencedSchema, k.ReferencedTable), Outgoing: true))
                .Concat(catalog.Incoming(mine).Select(k => (Key: k, Other: catalog.Find(k.Schema, k.Table), Outgoing: false)));
            foreach (var (key, other, outgoing) in keys)
            {
                if (other is null)
                {
                    continue;
                }

                var alias = AliasFor(other.Name, taken);
                var mineCols = outgoing ? key.Columns : key.ReferencedColumns;
                var otherCols = outgoing ? key.ReferencedColumns : key.Columns;
                var condition = string.Join(" AND ", mineCols.Zip(otherCols, (m, o) =>
                    $"{SqlKeywords.QuoteIfNeeded(alias, provider)}.{SqlKeywords.QuoteIfNeeded(o, provider)} = {SqlKeywords.QuoteIfNeeded(reference.Exposed, provider)}.{SqlKeywords.QuoteIfNeeded(m, provider)}"));
                var table = other.Schema is { } s && !string.Equals(s, catalog.DefaultSchema, StringComparison.OrdinalIgnoreCase)
                    ? $"{SqlKeywords.QuoteIfNeeded(s, provider)}.{SqlKeywords.QuoteIfNeeded(other.Name, provider)}"
                    : SqlKeywords.QuoteIfNeeded(other.Name, provider);
                var text = $"{table} {alias} ON {condition}";
                yield return new CompletionItem(text, text, CompletionKind.Join, key.Name) { Score = 3 };
            }
        }
    }

    // ON |  →  "o.user_id = u.id"
    private static IEnumerable<CompletionItem> Conditions(List<TableReference> scope, DatabaseCatalog catalog, DatabaseProvider provider)
    {
        if (scope.Count < 2 || scope[^1].Relation is not { } last)
        {
            yield break;
        }

        var joined = scope[^1];
        foreach (var other in scope.Take(scope.Count - 1).Where(r => r.Relation is not null))
        {
            var pairs = catalog.Outgoing(last).Where(k => Matches(k.ReferencedSchema, k.ReferencedTable, other.Relation!))
                .Select(k => (Left: k.Columns, Right: k.ReferencedColumns, k.Name))
                .Concat(catalog.Incoming(last).Where(k => Matches(k.Schema, k.Table, other.Relation!))
                    .Select(k => (Left: k.ReferencedColumns, Right: k.Columns, k.Name)));
            foreach (var (left, right, name) in pairs)
            {
                var text = string.Join(" AND ", left.Zip(right, (l, r) =>
                    $"{SqlKeywords.QuoteIfNeeded(joined.Exposed, provider)}.{SqlKeywords.QuoteIfNeeded(l, provider)} = {SqlKeywords.QuoteIfNeeded(other.Exposed, provider)}.{SqlKeywords.QuoteIfNeeded(r, provider)}"));
                yield return new CompletionItem(text, text, CompletionKind.Condition, name) { Score = 4 };
            }
        }
    }

    private static bool Matches(string? schema, string table, CatalogRelation relation) =>
        string.Equals(table, relation.Name, StringComparison.OrdinalIgnoreCase)
        && (schema is null || relation.Schema is null || string.Equals(schema, relation.Schema, StringComparison.OrdinalIgnoreCase));

    /// <summary>Proposes a short alias for a table name: the initials of its words (<c>order_items</c> → <c>oi</c>), made unique.</summary>
    /// <param name="table">The table name.</param>
    /// <param name="taken">Aliases already in use; the result is added.</param>
    /// <returns>The alias.</returns>
    public static string AliasFor(string table, HashSet<string> taken)
    {
        var words = System.Text.RegularExpressions.Regex.Split(table, "[_\\s]+|(?<=[a-z])(?=[A-Z])")
            .Where(w => w.Length > 0)
            .ToList();
        var alias = words.Count == 0 ? "t" : string.Concat(words.Select(w => char.ToLowerInvariant(w[0])));
        if (!char.IsLetter(alias[0]))
        {
            alias = "t" + alias;
        }

        if (SqlKeywords.IsReserved(alias) || alias is "as" or "on" or "or" or "do" or "is" or "in" or "to" or "by" or "if")
        {
            alias += "1";
        }

        var candidate = alias;
        for (var n = 1; taken.Contains(candidate); n++)
        {
            candidate = alias + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        taken.Add(candidate);
        return candidate;
    }

    private static IEnumerable<CompletionItem> Keywords(IEnumerable<string> keywords, DatabaseProvider provider) =>
        keywords.Distinct(StringComparer.OrdinalIgnoreCase).Select(k => new CompletionItem(k, k, CompletionKind.Keyword) { Score = 1 });

    private static CompletionResult Rank(int start, int length, string prefix, List<CompletionItem> items)
    {
        var ranked = items
            .Select(item => (Item: item, Match: MatchScore(item.Label, prefix)))
            .Where(x => x.Match > 0)
            .GroupBy(x => (x.Item.Label, x.Item.InsertText, x.Item.Kind))
            .Select(g => g.First())
            .OrderByDescending(x => x.Match)
            .ThenByDescending(x => x.Item.Score)
            .ThenBy(x => x.Item.Label.Length)
            .ThenBy(x => x.Item.Label, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Item)
            .Take(400)
            .ToList();
        return new CompletionResult(start, length, prefix, ranked);
    }

    /// <summary>Scores how well a label matches a typed prefix: 0 = no match.</summary>
    /// <param name="label">The candidate label.</param>
    /// <param name="prefix">The typed text.</param>
    /// <returns>3 for a prefix match, 2 for a word-start match, 1 for a substring match, 0 otherwise.</returns>
    public static int MatchScore(string label, string prefix)
    {
        if (prefix.Length == 0)
        {
            return 1;
        }

        // Match against the last segment of a qualified label too (u.email ← em).
        var name = label.Contains('.', StringComparison.Ordinal) && !prefix.Contains('.', StringComparison.Ordinal) ? label[(label.LastIndexOf('.') + 1)..] : label;
        if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (WordStarts(name, prefix))
        {
            return 2;
        }

        return name.Contains(prefix, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    // "ui" matches "user_id"; "oI" matches "orderItems".
    private static bool WordStarts(string name, string prefix)
    {
        var starts = new List<int> { 0 };
        for (var i = 1; i < name.Length; i++)
        {
            if (name[i - 1] == '_' || (char.IsUpper(name[i]) && char.IsLower(name[i - 1])))
            {
                starts.Add(i);
            }
        }

        var p = 0;
        foreach (var s in starts)
        {
            var i = s;
            while (p < prefix.Length && i < name.Length && name[i] != '_' && char.ToLowerInvariant(name[i]) == char.ToLowerInvariant(prefix[p]))
            {
                p++;
                i++;
            }

            if (p == prefix.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadAlias(List<SqlLexeme> tokens, ref int j)
    {
        if (j < tokens.Count && tokens[j].Is("AS"))
        {
            j++;
        }

        if (j < tokens.Count && tokens[j].IsName && !IsClauseOrJoinWord(tokens[j]))
        {
            var alias = tokens[j].Identifier;
            j++;

            // AS t(a, b) column aliases
            if (j < tokens.Count && tokens[j].IsSymbol("("))
            {
                j = MatchingParen(tokens, j) + 1;
            }

            return alias;
        }

        return null;
    }

    private static int MatchingParen(IReadOnlyList<SqlLexeme> tokens, int open)
    {
        var depth = 0;
        for (var k = open; k < tokens.Count; k++)
        {
            if (tokens[k].IsSymbol("("))
            {
                depth++;
            }
            else if (tokens[k].IsSymbol(")"))
            {
                depth--;
                if (depth == 0)
                {
                    return k;
                }
            }
        }

        return tokens.Count - 1;
    }

    private static bool HasDeleteBefore(IReadOnlyList<SqlLexeme> tokens, int index) =>
        tokens.Take(index).Any(t => t.Is("DELETE") || t.Is("MERGE"));

    private static bool IsClauseOrJoinWord(SqlLexeme lexeme) =>
        lexeme.Kind == SqlLexemeKind.Word && (ClauseWords.Contains(lexeme.Text) || lexeme.Text.ToUpperInvariant() is "LEFT" or "RIGHT"
            or "INNER" or "OUTER" or "FULL" or "CROSS" or "NATURAL" or "ON" or "AS" or "WHERE" or "LIMIT" or "SET" or "VALUES"
            or "ORDER" or "GROUP" or "HAVING" or "UNION" or "DEFAULT" or "OUTPUT" or "LATERAL" or "TABLESAMPLE" or "WITH" or "FOR"
            or "OFFSET" or "FETCH" or "WINDOW" or "RETURNING" or "SELECT" or "APPLY" or "PARTITION" or "STRAIGHT_JOIN" or "USE"
            or "FORCE" or "IGNORE" or "IN" or "INDEXED" or "NOT");
}
