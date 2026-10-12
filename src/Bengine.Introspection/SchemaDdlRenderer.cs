namespace Bengine.Introspection;

using Bengine.Enums;
using Bengine.Generators;
using Bengine.Generators.Sql;
using Bengine.Models;

/// <summary>
/// Renders an introspected <see cref="DatabaseSchema"/> or a single
/// <see cref="Table"/> back to provider DDL by reusing the framework's own SQL
/// generators. Because introspection reconstructs the same AST the generators
/// consume, the "DDL" tab of the browser is just a round-trip: live catalog →
/// AST → <c>CREATE TABLE</c>.
/// </summary>
public static class SchemaDdlRenderer
{
    /// <summary>Renders a whole schema as the provider's DDL.</summary>
    /// <param name="provider">The dialect to render in.</param>
    /// <param name="schema">The schema to render.</param>
    /// <returns>The DDL script.</returns>
    public static string Render(DatabaseProvider provider, DatabaseSchema schema) =>
        GeneratorFor(provider).Generate(schema);

    /// <summary>Renders a single table as the provider's DDL.</summary>
    /// <param name="provider">The dialect to render in.</param>
    /// <param name="table">The table to render.</param>
    /// <returns>The DDL for the one table.</returns>
    public static string RenderTable(DatabaseProvider provider, Table table) =>
        Render(provider, new DatabaseSchema { Name = table.Schema ?? "schema", Tables = [table] });

    private static ISqlGenerator GeneratorFor(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.PostgreSql => new PostgreSqlGenerator(),
        DatabaseProvider.SqlServer => new SqlServerGenerator(),
        DatabaseProvider.MySql => new MySqlGenerator(),
        DatabaseProvider.Sqlite => new SqliteGenerator(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider."),
    };
}
