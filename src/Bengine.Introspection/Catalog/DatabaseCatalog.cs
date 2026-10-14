namespace Bengine.Introspection.Catalog;

using Bengine.Enums;
using Bengine.Introspection.Model;

/// <summary>A column as the editor sees it.</summary>
/// <param name="Name">The column name.</param>
/// <param name="Type">The database type as the server spells it (<c>varchar(255)</c>, <c>bigint</c>).</param>
/// <param name="IsNullable">Whether the column accepts <c>NULL</c>.</param>
/// <param name="IsPrimaryKey">Whether the column is part of the primary key.</param>
/// <param name="HasDefault">Whether the column has a default or is generated/identity.</param>
/// <param name="IsGenerated">Whether the column is computed (and so cannot be written).</param>
public sealed record CatalogColumn(string Name, string Type, bool IsNullable, bool IsPrimaryKey, bool HasDefault = false, bool IsGenerated = false);

/// <summary>A table, view, or materialized view with its columns.</summary>
/// <param name="Schema">The schema, or <c>null</c> for providers without schemas.</param>
/// <param name="Name">The relation name.</param>
/// <param name="Kind">The relation kind.</param>
/// <param name="Columns">The columns in ordinal order.</param>
public sealed record CatalogRelation(string? Schema, string Name, TableKind Kind, IReadOnlyList<CatalogColumn> Columns)
{
    /// <summary>Gets the relation as a <see cref="TableRef"/>.</summary>
    public TableRef Ref => new(Name, Schema, Kind);

    /// <summary>Gets a column by name, case-insensitively.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The column, or <c>null</c>.</returns>
    public CatalogColumn? Column(string name) =>
        Columns.FirstOrDefault(c => c.Name == name) ?? Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A foreign key between two relations.</summary>
/// <param name="Name">The constraint name.</param>
/// <param name="Schema">The referencing relation's schema.</param>
/// <param name="Table">The referencing relation.</param>
/// <param name="Columns">The referencing columns.</param>
/// <param name="ReferencedSchema">The referenced relation's schema.</param>
/// <param name="ReferencedTable">The referenced relation.</param>
/// <param name="ReferencedColumns">The referenced columns, aligned with <paramref name="Columns"/>.</param>
public sealed record CatalogForeignKey(
    string Name,
    string? Schema,
    string Table,
    IReadOnlyList<string> Columns,
    string? ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns);

/// <summary>A user-defined function or procedure.</summary>
/// <param name="Schema">The schema.</param>
/// <param name="Name">The routine name.</param>
/// <param name="IsProcedure">Whether it is a procedure rather than a function.</param>
public sealed record CatalogRoutine(string? Schema, string Name, bool IsProcedure);

/// <summary>
/// A snapshot of one database's user objects — schemas, relations with their
/// columns, foreign keys, and routines — read in a few bulk catalog queries. It
/// powers the SQL editor (completion, join suggestions, reference checks), the
/// data editor's foreign-key navigation, and "Go to object". Name lookups follow
/// the server's default schema first, then any schema.
/// </summary>
public sealed class DatabaseCatalog
{
    private readonly Dictionary<string, List<CatalogRelation>> _byName;

    /// <summary>Creates a catalog.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="database">The database name.</param>
    /// <param name="defaultSchema">The session's default schema, or <c>null</c>.</param>
    /// <param name="schemas">The user schemas.</param>
    /// <param name="relations">The relations.</param>
    /// <param name="foreignKeys">The foreign keys.</param>
    /// <param name="routines">The routines.</param>
    public DatabaseCatalog(
        DatabaseProvider provider,
        string database,
        string? defaultSchema,
        IReadOnlyList<string> schemas,
        IReadOnlyList<CatalogRelation> relations,
        IReadOnlyList<CatalogForeignKey> foreignKeys,
        IReadOnlyList<CatalogRoutine> routines)
    {
        Provider = provider;
        Database = database;
        DefaultSchema = defaultSchema;
        Schemas = schemas;
        Relations = relations;
        ForeignKeys = foreignKeys;
        Routines = routines;
        _byName = relations
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets the provider.</summary>
    public DatabaseProvider Provider { get; }

    /// <summary>Gets the database name.</summary>
    public string Database { get; }

    /// <summary>Gets the default schema of the session that read the catalog.</summary>
    public string? DefaultSchema { get; }

    /// <summary>Gets the user schemas.</summary>
    public IReadOnlyList<string> Schemas { get; }

    /// <summary>Gets every relation.</summary>
    public IReadOnlyList<CatalogRelation> Relations { get; }

    /// <summary>Gets every foreign key.</summary>
    public IReadOnlyList<CatalogForeignKey> ForeignKeys { get; }

    /// <summary>Gets the user routines.</summary>
    public IReadOnlyList<CatalogRoutine> Routines { get; }

    /// <summary>Gets an empty catalog, used before metadata has loaded.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>An empty catalog.</returns>
    public static DatabaseCatalog Empty(DatabaseProvider provider) => new(provider, string.Empty, null, [], [], [], []);

    /// <summary>
    /// Finds a relation by name: an exact schema match when a schema is given;
    /// otherwise the default schema first, then the only (or first) other match.
    /// Exact-case matches win over case-insensitive ones.
    /// </summary>
    /// <param name="schema">The schema, or <c>null</c> when unqualified.</param>
    /// <param name="name">The relation name.</param>
    /// <returns>The relation, or <c>null</c>.</returns>
    public CatalogRelation? Find(string? schema, string name)
    {
        if (!_byName.TryGetValue(name, out var candidates))
        {
            return null;
        }

        IEnumerable<CatalogRelation> scoped = schema is null
            ? candidates
            : candidates.Where(r => string.Equals(r.Schema, schema, StringComparison.OrdinalIgnoreCase));

        return scoped
            .OrderByDescending(r => r.Name == name)
            .ThenByDescending(r => schema is null && string.Equals(r.Schema, DefaultSchema, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    /// <summary>Gets the relations of a schema.</summary>
    /// <param name="schema">The schema.</param>
    /// <returns>The relations.</returns>
    public IEnumerable<CatalogRelation> InSchema(string? schema) =>
        Relations.Where(r => string.Equals(r.Schema, schema, StringComparison.OrdinalIgnoreCase));

    /// <summary>Gets whether a name is one of the catalog's schemas.</summary>
    /// <param name="name">The name.</param>
    /// <returns><c>true</c> if it names a schema.</returns>
    public bool IsSchema(string name) => Schemas.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the foreign keys declared on a relation (it references others).</summary>
    /// <param name="relation">The relation.</param>
    /// <returns>The outgoing keys.</returns>
    public IEnumerable<CatalogForeignKey> Outgoing(CatalogRelation relation) =>
        ForeignKeys.Where(k => Same(k.Schema, k.Table, relation));

    /// <summary>Gets the foreign keys that reference a relation.</summary>
    /// <param name="relation">The relation.</param>
    /// <returns>The incoming keys.</returns>
    public IEnumerable<CatalogForeignKey> Incoming(CatalogRelation relation) =>
        ForeignKeys.Where(k => Same(k.ReferencedSchema, k.ReferencedTable, relation));

    private static bool Same(string? schema, string table, CatalogRelation relation) =>
        string.Equals(table, relation.Name, StringComparison.OrdinalIgnoreCase)
        && (schema is null || relation.Schema is null || string.Equals(schema, relation.Schema, StringComparison.OrdinalIgnoreCase));
}
