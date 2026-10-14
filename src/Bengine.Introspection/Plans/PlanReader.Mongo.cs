namespace Bengine.Introspection.Plans;

using System.Globalization;
using Bengine.Introspection.Mongo;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

/// <summary>MongoDB: the <c>explain</c> command's winning plan (or, analyzed, its execution stages).</summary>
public static partial class PlanReader
{
    private static readonly HashSet<string> MongoDetailFields = new(StringComparer.Ordinal)
    {
        "indexName", "keyPattern", "direction", "filter", "indexBounds", "isMultiKey", "isUnique", "sortPattern", "memLimit",
        "limitAmount", "skipAmount", "transformBy", "nss", "keysExamined", "docsExamined", "seeks", "dupsTested", "dupsDropped",
        "usedDisk", "spills", "totalDataSizeSorted", "works", "needTime", "isEOF", "planNodeId", "nMatched", "nWouldModify", "nWouldDelete",
    };

    /// <summary>Parses the reply of MongoDB's <c>explain</c> command.</summary>
    /// <param name="explain">The reply.</param>
    /// <param name="analyzed">Whether it was run with <c>executionStats</c>.</param>
    /// <returns>The plan.</returns>
    public static QueryPlan ParseMongo(BsonDocument explain, bool analyzed)
    {
        var plan = new QueryPlan
        {
            Analyzed = analyzed,
            RawFormat = "json",
            Raw = explain.ToJson(new JsonWriterSettings { Indent = true, OutputMode = JsonOutputMode.RelaxedExtendedJson }),
        };

        // An aggregation that is not pushed down explains stage by stage; the first stage holds the query plan.
        if (explain.GetValue("stages", BsonNull.Value) is BsonArray stages && stages.Count > 0)
        {
            PlanNode? below = null;
            foreach (var stage in stages.OfType<BsonDocument>())
            {
                var name = stage.Names.FirstOrDefault(n => n.StartsWith('$')) ?? "stage";
                var node = new PlanNode { Operation = name };
                if (name == "$cursor" && stage[name] is BsonDocument cursor)
                {
                    if (Tree(cursor, analyzed) is { } tree)
                    {
                        node.Children.Add(tree);
                    }
                }
                else
                {
                    node.Details.Add(new("Stage", MongoValues.Shell(stage[name])));
                }

                if (stage.TryGetValue("nReturned", out var returned) && returned.IsNumeric)
                {
                    node.ActualRows = returned.ToDouble();
                }

                if (stage.TryGetValue("executionTimeMillisEstimate", out var time) && time.IsNumeric)
                {
                    node.ActualTimeMs = time.ToDouble();
                }

                if (below is not null)
                {
                    node.Children.Add(below);
                }

                below = node;
            }

            if (below is not null)
            {
                plan.Roots.Add(below);
            }
        }
        else if (Tree(explain, analyzed) is { } tree)
        {
            plan.Roots.Add(tree);
        }

        if (explain.GetValue("executionStats", BsonNull.Value) is BsonDocument stats && stats.TryGetValue("executionTimeMillis", out var total) && total.IsNumeric)
        {
            plan.ExecutionTimeMs = total.ToDouble();
        }

        return plan;
    }

    // The plan tree of a find-shaped explain: the execution stages when analyzed, else the winning plan.
    private static PlanNode? Tree(BsonDocument explain, bool analyzed)
    {
        if (analyzed && explain.GetValue("executionStats", BsonNull.Value) is BsonDocument stats && stats.GetValue("executionStages", BsonNull.Value) is BsonDocument stages)
        {
            var root = Stage(stages, analyzed: true);
            root.Details.Insert(0, new("Total keys examined", Text(stats.GetValue("totalKeysExamined", BsonNull.Value)) ?? "?"));
            root.Details.Insert(1, new("Total documents examined", Text(stats.GetValue("totalDocsExamined", BsonNull.Value)) ?? "?"));
            return root;
        }

        if (explain.GetValue("queryPlanner", BsonNull.Value) is not BsonDocument planner || planner.GetValue("winningPlan", BsonNull.Value) is not BsonDocument winning)
        {
            return null;
        }

        // The slot-based engine nests the familiar plan under queryPlan.
        var node = Stage(winning.GetValue("queryPlan", BsonNull.Value) as BsonDocument ?? winning, analyzed: false);
        if (planner.GetValue("namespace", BsonNull.Value) is BsonString { Value: var ns })
        {
            foreach (var (scan, _) in node.Flatten().Where(n => n.Node.Operation == "COLLSCAN"))
            {
                scan.Object ??= ns;
            }
        }

        return node;
    }

    private static PlanNode Stage(BsonDocument stage, bool analyzed)
    {
        var node = new PlanNode
        {
            Operation = stage.GetValue("stage", "?").ToString() ?? "?",
            Object = stage.GetValue("indexName", BsonNull.Value) is BsonString { Value: var index } ? index : null,
        };
        foreach (var element in stage.Where(e => MongoDetailFields.Contains(e.Name) && e.Name != "indexName"))
        {
            node.Details.Add(new(element.Name, Text(element.Value) ?? "null"));
        }

        if (analyzed)
        {
            node.ActualRows = stage.GetValue("nReturned", BsonNull.Value) is { IsNumeric: true } returned ? returned.ToDouble() : null;
            node.ActualTimeMs = stage.GetValue("executionTimeMillisEstimate", BsonNull.Value) is { IsNumeric: true } time ? time.ToDouble() : null;
        }

        if (node.Operation == "COLLSCAN")
        {
            var examined = stage.GetValue("docsExamined", BsonNull.Value) is { IsNumeric: true } docs ? docs.ToDouble() : (double?)null;
            node.Warnings.Add(examined is { } count
                ? $"Collection scan of {count:N0} documents — an index on the filtered fields may help."
                : "Collection scan — no index serves this query.");
        }
        else if (node.Operation == "SORT")
        {
            node.Warnings.Add("In-memory sort — an index matching the sort avoids it.");
        }

        foreach (var key in new[] { "inputStage", "innerStage", "outerStage", "thenStage", "elseStage" })
        {
            if (stage.GetValue(key, BsonNull.Value) is BsonDocument child)
            {
                node.Children.Add(Stage(child, analyzed));
            }
        }

        if (stage.GetValue("inputStages", BsonNull.Value) is BsonArray children)
        {
            foreach (var child in children.OfType<BsonDocument>())
            {
                node.Children.Add(Stage(child, analyzed));
            }
        }

        // A sharded plan lists each shard's plan.
        if (stage.GetValue("shards", BsonNull.Value) is BsonArray shards)
        {
            foreach (var shard in shards.OfType<BsonDocument>())
            {
                var plan = shard.GetValue("executionStages", BsonNull.Value) as BsonDocument ?? shard.GetValue("winningPlan", BsonNull.Value) as BsonDocument;
                if (plan is not null)
                {
                    var child = Stage(plan.GetValue("queryPlan", BsonNull.Value) as BsonDocument ?? plan, analyzed);
                    child.Object ??= shard.GetValue("shardName", BsonNull.Value).ToString();
                    node.Children.Add(child);
                }
            }
        }

        return node;
    }

    private static string? Text(BsonValue value) => value switch
    {
        BsonNull => null,
        BsonString text => text.Value,
        BsonInt32 or BsonInt64 => value.ToInt64().ToString("N0", CultureInfo.InvariantCulture),
        _ => MongoValues.Shell(value),
    };

    private static async Task<QueryPlan> MongoAsync(DatabaseSession session, string text, bool analyze, CancellationToken ct)
    {
        var connection = session.Mongo ?? throw new IntrospectionException("Not a MongoDB session.");
        var command = MongoShell.Parse(text);
        var target = MongoQueryExecutor.ExplainTarget(command);
        var database = command.Database is { } name ? connection.Client.GetDatabase(name) : connection.Database;
        try
        {
            // explain never applies writes, even with executionStats.
            var reply = await database.RunCommandAsync<BsonDocument>(
                new BsonDocument { { "explain", target }, { "verbosity", analyze ? "executionStats" : "queryPlanner" } },
                cancellationToken: ct).ConfigureAwait(false);
            return ParseMongo(reply, analyze);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            throw new IntrospectionException(MongoConnection.Message(ex), ex);
        }
    }
}
