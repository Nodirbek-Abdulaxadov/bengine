namespace Bengine.Introspection.Mongo;

using System.Text.RegularExpressions;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Model;
using Bengine.Introspection.Sql;

/// <summary>
/// Completion for the MongoDB console: collections and database methods after
/// <c>db.</c>, collection methods after <c>db.users.</c>, cursor methods after
/// <c>find(…).</c>, the collection's fields (dotted paths included) as document
/// keys, and the query, update, and pipeline-stage operators after <c>$</c>.
/// </summary>
public static partial class MongoCompletion
{
    private static readonly string[] DatabaseMethods =
    [
        "getCollection", "getCollectionNames", "getCollectionInfos", "createCollection", "createView", "runCommand", "adminCommand",
        "aggregate", "stats", "version", "serverStatus", "hostInfo", "buildInfo", "currentOp", "killOp", "getUsers", "getRoles",
        "createUser", "dropUser", "dropDatabase", "getName", "getSiblingDB", "getProfilingStatus", "hello",
    ];

    private static readonly string[] CollectionMethods =
    [
        "find", "findOne", "aggregate", "countDocuments", "estimatedDocumentCount", "distinct", "insertOne", "insertMany",
        "updateOne", "updateMany", "replaceOne", "deleteOne", "deleteMany", "findOneAndUpdate", "findOneAndReplace",
        "findOneAndDelete", "createIndex", "createIndexes", "dropIndex", "dropIndexes", "getIndexes", "hideIndex", "unhideIndex",
        "drop", "renameCollection", "stats", "validate", "explain", "latencyStats",
    ];

    private static readonly string[] FindCursorMethods =
    [
        "sort", "limit", "skip", "projection", "count", "size", "hint", "collation", "maxTimeMS", "comment", "allowDiskUse",
        "batchSize", "toArray", "pretty", "explain",
    ];

    private static readonly string[] QueryOperators =
    [
        "$eq", "$ne", "$gt", "$gte", "$lt", "$lte", "$in", "$nin", "$exists", "$type", "$regex", "$options", "$and", "$or",
        "$nor", "$not", "$elemMatch", "$size", "$all", "$expr", "$text", "$search", "$mod", "$where", "$jsonSchema",
    ];

    private static readonly string[] UpdateOperators =
    [
        "$set", "$unset", "$inc", "$mul", "$rename", "$min", "$max", "$currentDate", "$setOnInsert", "$push", "$pull",
        "$pullAll", "$addToSet", "$pop", "$each", "$position", "$slice", "$sort",
    ];

    private static readonly string[] Stages =
    [
        "$match", "$project", "$group", "$sort", "$limit", "$skip", "$unwind", "$lookup", "$addFields", "$set", "$unset",
        "$count", "$facet", "$bucket", "$bucketAuto", "$sample", "$replaceRoot", "$replaceWith", "$sortByCount",
        "$graphLookup", "$unionWith", "$out", "$merge", "$densify", "$fill", "$setWindowFields", "$documents", "$redact",
    ];

    private static readonly string[] Expressions =
    [
        "$sum", "$avg", "$min", "$max", "$first", "$last", "$push", "$addToSet", "$count", "$cond", "$ifNull", "$concat",
        "$toUpper", "$toLower", "$substr", "$dateToString", "$year", "$month", "$dayOfMonth", "$size", "$arrayElemAt",
        "$filter", "$map", "$reduce", "$eq", "$ne", "$gt", "$gte", "$lt", "$lte", "$and", "$or", "$not", "$in", "$add",
        "$subtract", "$multiply", "$divide", "$round", "$toString", "$toInt", "$toDate", "$mergeObjects", "$literal",
    ];

    /// <summary>Computes the proposals at a caret.</summary>
    /// <param name="text">The editor text.</param>
    /// <param name="caret">The caret offset.</param>
    /// <param name="catalog">The database catalog.</param>
    /// <returns>The proposals.</returns>
    public static CompletionResult Complete(string text, int caret, DatabaseCatalog catalog)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var statement = MongoShell.Split(text).LastOrDefault(s => s.Start <= caret && caret <= s.End + 1);
        var start = statement?.Start ?? (text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1);
        if (start > caret)
        {
            return CompletionResult.None(caret);
        }

        var head = text[start..caret];
        if (Scan(head) is not { } frames)
        {
            // Inside a string or a comment.
            return CompletionResult.None(caret);
        }

        var inDocument = frames.Count > 0 && frames[^1].Open == '{';
        var from = caret;
        while (from > start && (char.IsLetterOrDigit(text[from - 1]) || text[from - 1] is '_' or '$' || (inDocument && text[from - 1] == '.')))
        {
            from--;
        }

        var prefix = text[from..caret];
        var before = Previous(text, from, start);
        var items = new List<CompletionItem>();
        var collection = Target().Match(text[start..]) is { Success: true } target
            ? catalog.Find(null, target.Groups[1].Success ? target.Groups[1].Value : target.Groups[2].Value)
            : null;

        if (frames.Count == 0)
        {
            if (before == '.')
            {
                items.AddRange(Members(text, from - 1, start, catalog));
            }
            else if (Regex.IsMatch(head[..(from - start)], @"^\s*show\s+$"))
            {
                items.AddRange(new[] { "dbs", "collections", "users", "roles" }.Select(w => new CompletionItem(w, w, CompletionKind.Keyword)));
            }
            else if (head[..(from - start)].Trim().Length == 0)
            {
                items.Add(new CompletionItem("db", "db", CompletionKind.Keyword, "the current database"));
                items.AddRange(new[] { "show dbs", "show collections", "show users", "show roles" }.Select(w => new CompletionItem(w, w, CompletionKind.Keyword)));
            }
        }
        else if (inDocument && before is '{' or ',')
        {
            items.AddRange(Keys(frames, prefix, collection));
        }

        var ranked = items
            .Select(i => i with { Score = SqlCompletion.MatchScore(i.Label, prefix) })
            .Where(i => i.Score > 0)
            .GroupBy(i => i.Label, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.Kind == CompletionKind.Column ? 0 : 1)
            .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new CompletionResult(from, caret - from, prefix, ranked);
    }

    // What may follow "expr." — collections and db methods after db, collection methods after a collection, cursor methods after a call.
    private static IEnumerable<CompletionItem> Members(string text, int dot, int start, DatabaseCatalog catalog)
    {
        var end = dot;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        if (end > start && text[end - 1] == ')')
        {
            var call = CallName(text, end - 1, start);
            return call switch
            {
                "getCollection" => Methods(CollectionMethods, "method"),
                "getSiblingDB" => Methods(DatabaseMethods, "database method"),
                "find" or "sort" or "limit" or "skip" or "projection" or "hint" or "collation" or "maxTimeMS" or "comment" or "allowDiskUse" or "batchSize" => Methods(FindCursorMethods, "cursor"),
                "aggregate" => Methods(["toArray", "pretty", "explain"], "cursor"),
                "explain" => Methods(["find", "aggregate", "count", "distinct", "updateOne", "updateMany", "deleteOne", "deleteMany"], "explain"),
                _ => [],
            };
        }

        var word = end;
        while (word > start && (char.IsLetterOrDigit(text[word - 1]) || text[word - 1] is '_' or '$'))
        {
            word--;
        }

        var name = text[word..end];
        var qualifier = Previous(text, word, start);
        if (name == "db" && qualifier != '.')
        {
            return catalog.Relations
                .Select(r => new CompletionItem(r.Name, Accessor(r.Name), r.Kind == TableKind.View ? CompletionKind.View : CompletionKind.Table, r.Kind == TableKind.View ? "view" : "collection"))
                .Concat(Methods(DatabaseMethods, "database method"));
        }

        return name.Length > 0 && qualifier == '.' ? Methods(CollectionMethods, "method") : [];
    }

    // Keys inside a document: fields, or operators after '$' (which ones depends on where the document sits).
    private static IEnumerable<CompletionItem> Keys(IReadOnlyList<Frame> frames, string prefix, CatalogRelation? collection)
    {
        var call = frames.LastOrDefault(f => f.Open == '(');
        var method = call?.Method ?? string.Empty;
        var argument = call?.Commas ?? 0;
        var inPipeline = method == "aggregate" || frames.Any(f => f.Open == '[' && f.Key is "pipeline");
        var stageLevel = frames.Count >= 2 && frames[^2].Open == '[' && inPipeline;
        if (prefix.StartsWith('$'))
        {
            if (stageLevel)
            {
                return Operators(Stages, "stage");
            }

            if (inPipeline)
            {
                return Operators(Expressions, "expression").Concat(Operators(QueryOperators, "query"));
            }

            var update = method is "updateOne" or "updateMany" or "update" or "findOneAndUpdate" && argument == 1 && frames.Count(f => f.Open == '{') == 1;
            return update ? Operators(UpdateOperators, "update") : Operators(QueryOperators, "query");
        }

        if (stageLevel || collection is null)
        {
            return [];
        }

        return collection.Columns.Select(c => new CompletionItem(c.Name, MongoValues.Key(c.Name), CompletionKind.Column, c.Type));
    }

    private static IEnumerable<CompletionItem> Methods(IEnumerable<string> names, string detail) =>
        names.Select(n => new CompletionItem(n, n, CompletionKind.Function, detail));

    private static IEnumerable<CompletionItem> Operators(IEnumerable<string> names, string detail) =>
        names.Select(n => new CompletionItem(n, n, CompletionKind.Keyword, detail));

    private static string Accessor(string name) =>
        Regex.IsMatch(name, @"^[A-Za-z_$][A-Za-z0-9_$]*$") ? name : $"getCollection({MongoValues.Quote(name)})";

    private static char Previous(string text, int before, int start)
    {
        for (var i = before - 1; i >= start; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return text[i];
            }
        }

        return '\0';
    }

    // The method name of the call whose ')' is at close.
    private static string? CallName(string text, int close, int start)
    {
        var depth = 0;
        for (var i = close; i >= start; i--)
        {
            depth += text[i] switch { ')' or ']' or '}' => 1, '(' or '[' or '{' => -1, _ => 0 };
            if (depth == 0)
            {
                var end = i;
                while (end > start && char.IsWhiteSpace(text[end - 1]))
                {
                    end--;
                }

                var word = end;
                while (word > start && (char.IsLetterOrDigit(text[word - 1]) || text[word - 1] is '_' or '$'))
                {
                    word--;
                }

                return text[word..end];
            }
        }

        return null;
    }

    // The open brackets at the end of the text, or null inside a string or comment.
    private static List<Frame>? Scan(string text)
    {
        var frames = new List<Frame>();
        var last = '\0';
        var word = string.Empty;
        string? key = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                var close = i + 1;
                while (close < text.Length && text[close] != c)
                {
                    close += text[close] == '\\' ? 2 : 1;
                }

                if (close >= text.Length)
                {
                    return null;
                }

                key = text[(i + 1)..close];
                i = close;
                last = c;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*')
            {
                var end = text[i + 1] == '/' ? text.IndexOf('\n', i) : text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return null;
                }

                i = text[i + 1] == '/' ? end : end + 1;
                continue;
            }

            if (char.IsLetterOrDigit(c) || c is '_' or '$')
            {
                word = char.IsLetterOrDigit(last) || last is '_' or '$' ? word + c : c.ToString();
                key = word;
            }

            switch (c)
            {
                case '(':
                    frames.Add(new Frame('(', word, 0));
                    break;
                case '{' or '[':
                    frames.Add(new Frame(c, null, 0) { Key = c == '[' ? key : null });
                    break;
                case ')' or '}' or ']' when frames.Count > 0:
                    frames.RemoveAt(frames.Count - 1);
                    break;
                case ',' when frames.Count > 0 && frames[^1].Open == '(':
                    frames[^1] = frames[^1] with { Commas = frames[^1].Commas + 1 };
                    break;
            }

            if (!char.IsWhiteSpace(c))
            {
                last = c;
            }
        }

        return frames;
    }

    [GeneratedRegex(@"^\s*db\s*\.\s*(?:getCollection\(\s*[""']([^""']+)[""']\s*\)|([A-Za-z_$][A-Za-z0-9_$]*))")]
    private static partial Regex Target();

    // An open bracket: '(' of a call (its method and how many arguments precede the caret), '{', or '[' (the key it is the value of).
    private sealed record Frame(char Open, string? Method, int Commas)
    {
        public string? Key { get; init; }
    }
}
