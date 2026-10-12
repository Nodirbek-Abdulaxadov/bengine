namespace Bengine.Introspection.Abstractions;

using Bengine.Introspection.Model;
using Bengine.Models;

/// <summary>
/// Reconstructs the provider-independent <see cref="DatabaseSchema"/> AST from a
/// live database's catalog. This is the live-connection counterpart of
/// <c>Bengine.Parsing.ISqlSchemaParser</c>, which reconstructs the same AST from
/// DDL text: both yield the exact model the generators and migration layer
/// consume, so a schema read from a running server round-trips straight back out
/// as DDL, code, or a migration.
/// </summary>
public interface IDatabaseIntrospector
{
    /// <summary>
    /// Reads every user table in the connected database (optionally limited to
    /// one schema) into a full schema AST, including columns, primary keys,
    /// foreign keys, indexes, defaults, and identity configuration.
    /// </summary>
    /// <param name="schema">The schema to read, or <c>null</c> for every user schema.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The reconstructed schema AST.</returns>
    Task<DatabaseSchema> IntrospectAsync(string? schema = null, CancellationToken cancellationToken = default);

    /// <summary>Reads a single relation into a <see cref="Table"/> AST.</summary>
    /// <param name="table">The relation to introspect.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The reconstructed table AST.</returns>
    Task<Table> IntrospectTableAsync(TableRef table, CancellationToken cancellationToken = default);
}
