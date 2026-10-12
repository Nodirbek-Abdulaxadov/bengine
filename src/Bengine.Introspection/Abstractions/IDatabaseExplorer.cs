namespace Bengine.Introspection.Abstractions;

using Bengine.Introspection.Model;

/// <summary>
/// Cheap, top-down discovery of a live database's structure: the catalogs it
/// hosts, the schemas within the current catalog, and the relations within a
/// schema. Discovery deliberately returns lightweight <see cref="TableRef"/>
/// pointers rather than full AST so a UI can populate a navigation tree without
/// introspecting every object. Full structure comes from
/// <see cref="IDatabaseIntrospector"/>.
/// </summary>
public interface IDatabaseExplorer
{
    /// <summary>
    /// Lists the databases (catalogs) reachable on the connected server. For
    /// SQLite this is the single attached file.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The catalog names.</returns>
    Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the schemas (namespaces) in the connected database, excluding the
    /// provider's internal system schemas. Empty for providers without schemas.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The schema names.</returns>
    Task<IReadOnlyList<string>> GetSchemasAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the tables and views in the given schema, or across all user schemas
    /// when <paramref name="schema"/> is <c>null</c>.
    /// </summary>
    /// <param name="schema">The schema to list, or <c>null</c> for every user schema.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Pointers to the discovered relations.</returns>
    Task<IReadOnlyList<TableRef>> GetTablesAsync(string? schema = null, CancellationToken cancellationToken = default);
}
