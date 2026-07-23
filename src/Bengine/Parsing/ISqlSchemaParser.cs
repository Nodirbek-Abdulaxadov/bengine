namespace Bengine.Parsing;

using Bengine.Enums;
using Bengine.Models;

/// <summary>
/// Parses SQL DDL text into a provider-independent <see cref="DatabaseSchema"/>
/// AST. Implementations are the inverse of the SQL generators: they recognize the
/// <c>CREATE TABLE</c> / <c>CREATE INDEX</c> / <c>ALTER TABLE</c> shapes the
/// generators produce, across every supported <see cref="DatabaseProvider"/>.
/// </summary>
public interface ISqlSchemaParser
{
    /// <summary>Parses SQL DDL into a schema, interpreting dialect-specific spellings for the given provider.</summary>
    /// <param name="sql">The SQL DDL text.</param>
    /// <param name="provider">The provider whose dialect the SQL is written in.</param>
    /// <returns>The reconstructed schema AST.</returns>
    /// <exception cref="SqlParseException">The SQL could not be parsed.</exception>
    DatabaseSchema Parse(string sql, DatabaseProvider provider);
}
