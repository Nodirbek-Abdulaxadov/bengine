namespace Bengine.Generators.Migrations;

using System.Text;
using Bengine.Models;

/// <summary>
/// Builds migrations from a schema AST by delegating all SQL rendering to an
/// <see cref="ISqlGenerator"/>. Provider independence is inherited from the
/// injected generator; this type adds only forward/reverse orchestration.
/// </summary>
public sealed class MigrationGenerator
{
    private readonly ISqlGenerator _sql;

    /// <summary>Initializes the generator with a provider SQL generator.</summary>
    /// <param name="sql">The SQL generator that renders the DDL.</param>
    public MigrationGenerator(ISqlGenerator sql) => _sql = sql;

    /// <summary>
    /// Builds the initial migration that creates the entire schema, with a
    /// reverse script that drops every table in the opposite order.
    /// </summary>
    /// <param name="schema">The schema to migrate to.</param>
    /// <param name="name">The migration name.</param>
    /// <returns>The initial <see cref="Migration"/>.</returns>
    public Migration CreateInitial(DatabaseSchema schema, string name = "initial")
    {
        var up = _sql.Generate(schema);

        var down = new StringBuilder();
        foreach (var table in schema.Tables.Reverse())
        {
            down.AppendLine(_sql.DropTable(table));
        }

        return new Migration
        {
            Name = name,
            Up = up,
            Down = down.ToString().TrimEnd() + Environment.NewLine,
        };
    }
}
