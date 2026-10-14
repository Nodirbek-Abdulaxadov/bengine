namespace Bengine.Diff;

using System.Text.RegularExpressions;
using Bengine.Enums;
using Bengine.Generators.Migrations;
using Bengine.Models;

/// <summary>How two schemas are compared.</summary>
public sealed record SchemaDiffOptions
{
    /// <summary>Gets the default options.</summary>
    public static SchemaDiffOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether tables match by name alone, ignoring their
    /// schema — for comparing two differently named schemas. Changes are emitted
    /// against the source table's schema.
    /// </summary>
    public bool IgnoreSchemaNames { get; init; }

    /// <summary>
    /// Gets a value indicating whether foreign keys, check constraints, and
    /// indexes whose names differ but whose definitions match are treated as
    /// equal (generated names often differ between environments).
    /// </summary>
    public bool IgnoreConstraintNames { get; init; } = true;

    /// <summary>Gets a value indicating whether comments are ignored.</summary>
    public bool IgnoreComments { get; init; }

    /// <summary>
    /// Gets the provider whose native type spellings are compared when both
    /// columns carry one (introspected columns); otherwise logical types are.
    /// </summary>
    public DatabaseProvider? Provider { get; init; }
}

/// <summary>
/// Compares two schema ASTs and lists the operations that turn the source into
/// the target: tables, columns (type, nullability, default, identity,
/// generation, comment), primary keys, foreign keys, indexes, checks, views, and
/// sequences. The list is ordered so it can be applied top to bottom: foreign
/// keys and views are dropped first, new tables are created before the foreign
/// keys that reference them, and views are recreated last. It powers schema
/// comparison between databases and the table editor's <c>ALTER</c> preview.
/// </summary>
public static partial class SchemaDiffer
{
    /// <summary>Compares two schemas.</summary>
    /// <param name="source">The schema as it is.</param>
    /// <param name="target">The schema as it should be.</param>
    /// <param name="options">The comparison options.</param>
    /// <returns>The ordered changes; empty when the schemas match.</returns>
    public static IReadOnlyList<SchemaChange> Compare(DatabaseSchema source, DatabaseSchema target, SchemaDiffOptions? options = null)
    {
        options ??= SchemaDiffOptions.Default;
        var changes = new List<SchemaChange>();
        var sourceTables = source.Tables.ToDictionary(t => Key(t.Schema, t.Name, options), StringComparer.OrdinalIgnoreCase);
        var targetTables = target.Tables.ToDictionary(t => Key(t.Schema, t.Name, options), StringComparer.OrdinalIgnoreCase);

        foreach (var (key, table) in sourceTables)
        {
            if (!targetTables.ContainsKey(key))
            {
                changes.AddRange(table.ForeignKeys.Select(fk => new DropForeignKeyChange(table, fk)));
                changes.Add(new DropTableChange(table));
            }
        }

        foreach (var (key, table) in targetTables)
        {
            if (sourceTables.TryGetValue(key, out var existing))
            {
                // Emit against the source's schema when schema names are ignored.
                var anchored = options.IgnoreSchemaNames ? WithSchema(table, existing.Schema) : table;
                changes.AddRange(CompareTables(existing, anchored, options));
            }
            else
            {
                changes.Add(new CreateTableChange(WithoutForeignKeys(table)));
                changes.AddRange(table.Indexes.Select(i => new AddIndexChange(table, i)));
                changes.AddRange(table.ForeignKeys.Select(fk => new AddForeignKeyChange(table, fk)));
            }
        }

        // Views.
        var sourceViews = source.Views.ToDictionary(v => Key(v.Schema, v.Name, options), StringComparer.OrdinalIgnoreCase);
        var targetViews = target.Views.ToDictionary(v => Key(v.Schema, v.Name, options), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, view) in sourceViews)
        {
            if (!targetViews.TryGetValue(key, out var desired))
            {
                changes.Add(new DropViewChange(view));
            }
            else if (Normalize(view.Definition) != Normalize(desired.Definition) || view.IsMaterialized != desired.IsMaterialized)
            {
                changes.Add(new DropViewChange(view));
                changes.Add(new CreateViewChange(options.IgnoreSchemaNames ? WithSchema(desired, view.Schema) : desired, Replaces: true));
            }
        }

        changes.AddRange(targetViews.Where(v => !sourceViews.ContainsKey(v.Key)).Select(v => new CreateViewChange(v.Value)));

        // Sequences.
        var sourceSequences = source.Sequences.ToDictionary(s => Key(s.Schema, s.Name, options), StringComparer.OrdinalIgnoreCase);
        var targetSequences = target.Sequences.ToDictionary(s => Key(s.Schema, s.Name, options), StringComparer.OrdinalIgnoreCase);
        changes.AddRange(sourceSequences.Where(s => !targetSequences.ContainsKey(s.Key)).Select(s => new DropSequenceChange(s.Value)));
        changes.AddRange(targetSequences.Where(s => !sourceSequences.ContainsKey(s.Key)).Select(s => new CreateSequenceChange(s.Value)));

        return Order(changes);
    }

    /// <summary>Compares two versions of one table.</summary>
    /// <param name="source">The table as it is.</param>
    /// <param name="target">The table as it should be.</param>
    /// <param name="options">The comparison options.</param>
    /// <returns>The ordered changes.</returns>
    public static IReadOnlyList<SchemaChange> CompareTables(Table source, Table target, SchemaDiffOptions? options = null)
    {
        options ??= SchemaDiffOptions.Default;
        var changes = new List<SchemaChange>();

        // Columns.
        foreach (var column in source.Columns)
        {
            if (target.Columns.FirstOrDefault(c => Same(c.Name, column.Name)) is not { } desired)
            {
                changes.Add(new DropColumnChange(source, column));
            }
            else if (CompareColumns(column, desired, options) is var what and not ColumnChange.None)
            {
                changes.Add(new AlterColumnChange(target, column, desired, what));
            }
        }

        changes.AddRange(target.Columns.Where(c => !source.Columns.Any(s => Same(s.Name, c.Name))).Select(c => new AddColumnChange(target, c)));

        // Primary key.
        var sourceKey = source.PrimaryKey?.ColumnNames ?? [];
        var targetKey = target.PrimaryKey?.ColumnNames ?? [];
        if (!sourceKey.SequenceEqual(targetKey, StringComparer.OrdinalIgnoreCase))
        {
            if (source.PrimaryKey is { } oldKey)
            {
                changes.Add(new DropPrimaryKeyChange(source, oldKey));
            }

            if (target.PrimaryKey is { } newKey)
            {
                changes.Add(new AddPrimaryKeyChange(target, newKey));
            }
        }

        // Foreign keys, indexes, checks: matched by name, else by definition.
        Pair(source.ForeignKeys, target.ForeignKeys, fk => fk.Name, fk => ForeignKeyShape(fk, options.Provider), options,
            fk => changes.Add(new DropForeignKeyChange(source, fk)),
            fk => changes.Add(new AddForeignKeyChange(target, fk)));
        Pair(source.Indexes, target.Indexes, i => i.Name, IndexShape, options,
            i => changes.Add(new DropIndexChange(source, i)),
            i => changes.Add(new AddIndexChange(target, i)));
        Pair(source.CheckConstraints, target.CheckConstraints, c => c.Name, c => Normalize(c.Expression), options,
            c => changes.Add(new DropCheckChange(source, c)),
            c => changes.Add(new AddCheckChange(target, c)));

        if (!options.IgnoreComments && (source.Comment ?? string.Empty) != (target.Comment ?? string.Empty))
        {
            changes.Add(new TableCommentChange(target));
        }

        return Order(changes);
    }

    /// <summary>Compares two versions of a column.</summary>
    /// <param name="from">The column as it is.</param>
    /// <param name="to">The column as it should be.</param>
    /// <param name="options">The comparison options.</param>
    /// <returns>What differs.</returns>
    public static ColumnChange CompareColumns(Column from, Column to, SchemaDiffOptions? options = null)
    {
        options ??= SchemaDiffOptions.Default;
        var changes = ColumnChange.None;
        if (!SameType(from, to, options.Provider))
        {
            changes |= ColumnChange.Type;
        }

        if (from.Nullability != to.Nullability)
        {
            changes |= ColumnChange.Nullability;
        }

        var bothIdentity = from.Identity.Strategy != IdentityStrategy.None && to.Identity.Strategy != IdentityStrategy.None;
        if (!bothIdentity && (from.Default.Kind != to.Default.Kind || Normalize(from.Default.Value) != Normalize(to.Default.Value))
            && !SameRenderedDefault(from, to, options.Provider))
        {
            changes |= ColumnChange.Default;
        }

        if (from.Identity.Strategy != to.Identity.Strategy)
        {
            changes |= ColumnChange.Identity;
        }

        // Oracle computes every generated column on read (VIRTUAL) and keeps its expression with quoted names.
        var oracle = options.Provider == DatabaseProvider.Oracle;
        string Expression(Column c) => oracle
            ? Whitespace().Replace(Normalize(c.Generated?.Expression), string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal)
            : Normalize(c.Generated?.Expression);
        if (Expression(from) != Expression(to) || (!oracle && from.Generated?.Kind != to.Generated?.Kind) || (from.Generated is null) != (to.Generated is null))
        {
            changes |= ColumnChange.Generated;
        }

        if (!options.IgnoreComments && (from.Comment ?? string.Empty) != (to.Comment ?? string.Empty))
        {
            changes |= ColumnChange.Comment;
        }

        return changes;
    }

    /// <summary>Copies a table without its foreign keys.</summary>
    /// <param name="table">The table.</param>
    /// <returns>The copy.</returns>
    public static Table WithoutForeignKeys(Table table) => Copy(table, foreignKeys: []);

    /// <summary>Copies a table with every element, optionally replacing parts.</summary>
    /// <param name="table">The table.</param>
    /// <param name="name">A new name.</param>
    /// <param name="schema">A new schema (pass the same value to keep it).</param>
    /// <param name="columns">New columns.</param>
    /// <param name="foreignKeys">New foreign keys.</param>
    /// <param name="indexes">New indexes.</param>
    /// <returns>The copy.</returns>
    public static Table Copy(
        Table table,
        string? name = null,
        Optional<string?> schema = default,
        IReadOnlyList<Column>? columns = null,
        IReadOnlyList<ForeignKey>? foreignKeys = null,
        IReadOnlyList<Index>? indexes = null) => new()
    {
        Name = name ?? table.Name,
        Schema = schema.HasValue ? schema.Value : table.Schema,
        Columns = columns ?? table.Columns,
        PrimaryKey = table.PrimaryKey,
        ForeignKeys = foreignKeys ?? table.ForeignKeys,
        Indexes = indexes ?? table.Indexes,
        CheckConstraints = table.CheckConstraints,
        Comment = table.Comment,
        ProviderOptions = table.ProviderOptions,
    };

    private static Table WithSchema(Table table, string? schema) => Copy(table, schema: new Optional<string?>(schema));

    private static View WithSchema(View view, string? schema) => new()
    {
        Name = view.Name,
        Schema = schema,
        Definition = view.Definition,
        Columns = view.Columns,
        IsMaterialized = view.IsMaterialized,
        Comment = view.Comment,
    };

    private static bool SameType(Column from, Column to, DatabaseProvider? provider)
    {
        string? Native(Column c, DatabaseProvider p) =>
            c.ProviderOptions.TryGet(p, out var o) && o?.NativeTypeName is { Length: > 0 } n ? NormalizeType(n) : null;

        if (provider is { } p && Native(from, p) is { } a && Native(to, p) is { } b)
        {
            return a == b;
        }

        if (from.Type == to.Type)
        {
            return true;
        }

        // A live column carries its native type, a design only its logical one: compare the spelling the design renders to.
        if (provider is { } q && (Native(from, q) ?? Rendered(from, q)) is { } x && (Native(to, q) ?? Rendered(to, q)) is { } y)
        {
            return x == y;
        }

        return false;
    }

    private static string? Rendered(Column column, DatabaseProvider provider)
    {
        try
        {
            return NormalizeType(AlterScriptGenerator.For(provider).Sql.ColumnType(column));
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    // Defaults written differently that render the same (Oracle: true and 1).
    private static bool SameRenderedDefault(Column from, Column to, DatabaseProvider? provider)
    {
        if (provider is not { } p)
        {
            return false;
        }

        var dialect = AlterScriptGenerator.For(p).Sql.Dialect;
        return Normalize(dialect.RenderDefault(from.Default)) == Normalize(dialect.RenderDefault(to.Default));
    }

    private static void Pair<T>(
        IReadOnlyList<T> source,
        IReadOnlyList<T> target,
        Func<T, string> name,
        Func<T, string> shape,
        SchemaDiffOptions options,
        Action<T> drop,
        Action<T> add)
    {
        var unmatchedTarget = target.ToList();
        foreach (var item in source)
        {
            var byName = unmatchedTarget.FirstOrDefault(t => Same(name(t), name(item)) && name(item).Length > 0);
            if (byName is not null)
            {
                unmatchedTarget.Remove(byName);
                if (shape(byName) != shape(item))
                {
                    drop(item);
                    add(byName);
                }

                continue;
            }

            var byShape = options.IgnoreConstraintNames ? unmatchedTarget.FirstOrDefault(t => shape(t) == shape(item)) : default;
            if (byShape is not null)
            {
                unmatchedTarget.Remove(byShape);
                continue;
            }

            drop(item);
        }

        foreach (var item in unmatchedTarget)
        {
            add(item);
        }
    }

    private static string ForeignKeyShape(ForeignKey fk, DatabaseProvider? provider)
    {
        var (onDelete, onUpdate) = (fk.OnDelete, fk.OnUpdate);
        if (provider == DatabaseProvider.Oracle)
        {
            // Oracle has ON DELETE CASCADE / SET NULL and nothing else; any other action is NO ACTION.
            onDelete = onDelete is ReferentialAction.Cascade or ReferentialAction.SetNull ? onDelete : ReferentialAction.NoAction;
            onUpdate = ReferentialAction.NoAction;
        }

        return $"{string.Join(",", fk.Columns).ToLowerInvariant()}>{fk.ReferencedTable.ToLowerInvariant()}({string.Join(",", fk.ReferencedColumns).ToLowerInvariant()}) {onDelete}/{onUpdate}";
    }

    private static string IndexShape(Index index) =>
        $"{index.Type}:{index.Method}:{string.Join(",", index.Columns.Select(c => c.ColumnName.ToLowerInvariant() + c.Direction))}:{Normalize(index.Filter)}";

    // Change order: what must go first comes first.
    private static IReadOnlyList<SchemaChange> Order(List<SchemaChange> changes) => changes
        .Select((change, index) => (change, index))
        .OrderBy(x => x.change switch
        {
            DropForeignKeyChange => 0,
            DropViewChange => 1,
            DropIndexChange or DropCheckChange => 2,
            DropPrimaryKeyChange => 3,
            DropTableChange => 4,
            DropSequenceChange => 5,
            CreateSequenceChange => 6,
            CreateTableChange => 7,
            RenameTableChange => 8,
            RenameColumnChange => 9,
            AddColumnChange => 10,
            AlterColumnChange => 11,
            DropColumnChange => 12,
            AddPrimaryKeyChange => 13,
            AddCheckChange => 14,
            AddIndexChange => 15,
            AddForeignKeyChange => 16,
            CreateViewChange => 17,
            _ => 18,
        })
        .ThenBy(x => x.index)
        .Select(x => x.change)
        .ToList();

    private static string Key(string? schema, string name, SchemaDiffOptions options) =>
        options.IgnoreSchemaNames || schema is null ? name : $"{schema}.{name}";

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? sql) =>
        sql is null ? string.Empty : Whitespace().Replace(sql.Trim().TrimEnd(';'), " ").ToLowerInvariant();

    private static string NormalizeType(string type)
    {
        var text = Whitespace().Replace(type.Trim().ToLowerInvariant(), " ").Replace(" (", "(", StringComparison.Ordinal).Replace(", ", ",", StringComparison.Ordinal);

        // Six fractional digits is the default precision of a timestamp.
        return text.StartsWith("timestamp(6)", StringComparison.Ordinal) ? "timestamp" + text["timestamp(6)".Length..] : text;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>An optional argument that can explicitly be <c>null</c>.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="value">The value.</param>
public readonly struct Optional<T>(T value)
{
    /// <summary>Gets whether a value was given.</summary>
    public bool HasValue { get; } = true;

    /// <summary>Gets the value.</summary>
    public T Value { get; } = value;
}
