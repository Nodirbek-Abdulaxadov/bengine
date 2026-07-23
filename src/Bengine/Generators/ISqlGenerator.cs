namespace Bengine.Generators;

using Bengine.Enums;
using Bengine.Models;

/// <summary>
/// Emits provider-specific DDL for a schema AST. Implementations resolve every
/// type through metadata and mapping and never hardcode database type names.
/// </summary>
public interface ISqlGenerator
{
    /// <summary>Gets the provider this generator targets.</summary>
    DatabaseProvider Provider { get; }

    /// <summary>Generates the full DDL script for a schema.</summary>
    /// <param name="schema">The schema to render.</param>
    /// <returns>A newline-delimited DDL script.</returns>
    string Generate(DatabaseSchema schema);

    /// <summary>Generates the <c>CREATE TABLE</c> statement for a single table.</summary>
    /// <param name="table">The table to render.</param>
    /// <returns>The table's DDL.</returns>
    string CreateTable(Table table);

    /// <summary>Generates the <c>DROP TABLE</c> statement for a single table.</summary>
    /// <param name="table">The table to drop.</param>
    /// <returns>The drop DDL.</returns>
    string DropTable(Table table);
}
