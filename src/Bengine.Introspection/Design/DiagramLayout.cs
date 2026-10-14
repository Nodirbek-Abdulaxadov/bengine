namespace Bengine.Introspection.Design;

using Bengine.Introspection.Catalog;

/// <summary>A positioned table box of an ER diagram.</summary>
/// <param name="Relation">The relation.</param>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public sealed record DiagramNode(CatalogRelation Relation, double X, double Y, double Width, double Height)
{
    /// <summary>Gets the node key (<c>schema.name</c>).</summary>
    public string Key => DiagramLayout.KeyOf(Relation.Schema, Relation.Name);
}

/// <summary>
/// Lays out an ER diagram: referenced tables to the left of the tables that
/// reference them (layers by longest foreign-key path, cycles broken), each
/// layer ordered by the average position of its neighbours to cut crossings,
/// and unrelated tables in a grid after the connected ones.
/// </summary>
public static class DiagramLayout
{
    /// <summary>Builds a node key.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="name">The relation name.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(string? schema, string name) => (schema is null ? name : $"{schema}.{name}").ToLowerInvariant();

    /// <summary>Positions the relations.</summary>
    /// <param name="relations">The relations to draw.</param>
    /// <param name="keys">The foreign keys among them.</param>
    /// <param name="size">Measures a relation's box.</param>
    /// <param name="gapX">The horizontal gap between layers.</param>
    /// <param name="gapY">The vertical gap between boxes.</param>
    /// <returns>The positioned nodes.</returns>
    public static IReadOnlyList<DiagramNode> Layout(
        IReadOnlyList<CatalogRelation> relations,
        IReadOnlyList<CatalogForeignKey> keys,
        Func<CatalogRelation, (double Width, double Height)> size,
        double gapX = 110,
        double gapY = 36)
    {
        var byKey = relations.ToDictionary(r => KeyOf(r.Schema, r.Name));
        var parents = relations.ToDictionary(r => KeyOf(r.Schema, r.Name), _ => new HashSet<string>());
        var children = relations.ToDictionary(r => KeyOf(r.Schema, r.Name), _ => new HashSet<string>());
        foreach (var key in keys)
        {
            var child = KeyOf(key.Schema, key.Table);
            var parent = KeyOf(key.ReferencedSchema, key.ReferencedTable);
            if (child != parent && byKey.ContainsKey(child) && byKey.ContainsKey(parent))
            {
                parents[child].Add(parent);
                children[parent].Add(child);
            }
        }

        // Layer = longest path to a table with no parents (memoized, cycles cut).
        var layer = new Dictionary<string, int>();
        var visiting = new HashSet<string>();
        int LayerOf(string node)
        {
            if (layer.TryGetValue(node, out var known))
            {
                return known;
            }

            if (!visiting.Add(node))
            {
                return 0;
            }

            var value = parents[node].Count == 0 ? 0 : parents[node].Max(p => LayerOf(p) + 1);
            visiting.Remove(node);
            layer[node] = value;
            return value;
        }

        var connected = byKey.Keys.Where(k => parents[k].Count > 0 || children[k].Count > 0).ToList();
        foreach (var node in connected)
        {
            LayerOf(node);
        }

        var layers = connected.GroupBy(k => layer[k]).OrderBy(g => g.Key).Select(g => g.OrderBy(k => k).ToList()).ToList();

        // Two barycenter sweeps to reduce crossings.
        for (var sweep = 0; sweep < 2; sweep++)
        {
            for (var i = 1; i < layers.Count; i++)
            {
                var previous = layers[i - 1].Select((k, index) => (k, index)).ToDictionary(x => x.k, x => (double)x.index);
                layers[i] = layers[i]
                    .OrderBy(k => parents[k].Concat(children[k]).Where(previous.ContainsKey).Select(n => previous[n]).DefaultIfEmpty(double.MaxValue).Average())
                    .ThenBy(k => k)
                    .ToList();
            }
        }

        var nodes = new List<DiagramNode>();
        double x = 0;
        foreach (var column in layers)
        {
            var sizes = column.Select(k => (Key: k, Size: size(byKey[k]))).ToList();
            var width = sizes.Max(s => s.Size.Width);
            double y = 0;
            foreach (var (key, (w, h)) in sizes)
            {
                nodes.Add(new DiagramNode(byKey[key], x, y, w, h));
                y += h + gapY;
            }

            x += width + gapX;
        }

        // Unrelated tables in a grid to the right.
        var isolated = byKey.Keys.Except(connected).OrderBy(k => k).ToList();
        if (isolated.Count > 0)
        {
            var perColumn = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(isolated.Count)));
            for (var start = 0; start < isolated.Count; start += perColumn)
            {
                var column = isolated.Skip(start).Take(perColumn).Select(k => (Key: k, Size: size(byKey[k]))).ToList();
                double y = 0;
                foreach (var (key, (w, h)) in column)
                {
                    nodes.Add(new DiagramNode(byKey[key], x, y, w, h));
                    y += h + gapY;
                }

                x += column.Max(c => c.Size.Width) + gapX / 2;
            }
        }

        return nodes;
    }
}
