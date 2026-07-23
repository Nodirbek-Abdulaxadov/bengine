namespace Bengine.Visitors;

using Bengine.Models;

/// <summary>
/// Drives a depth-first traversal of a schema AST, dispatching each node to an
/// <see cref="ISchemaVisitor"/>. Centralizing traversal here keeps the ordering
/// consistent for every visitor and the model types visitor-agnostic.
/// </summary>
public sealed class SchemaWalker
{
    /// <summary>Walks the schema, dispatching every node to the visitor.</summary>
    /// <param name="schema">The schema to traverse.</param>
    /// <param name="visitor">The visitor to dispatch to.</param>
    public void Walk(DatabaseSchema schema, ISchemaVisitor visitor)
    {
        visitor.VisitDatabase(schema);

        foreach (var sequence in schema.Sequences)
        {
            visitor.VisitSequence(sequence);
        }

        foreach (var table in schema.Tables)
        {
            WalkTable(table, visitor);
        }

        foreach (var view in schema.Views)
        {
            visitor.VisitView(view);
        }

        foreach (var trigger in schema.Triggers)
        {
            visitor.VisitTrigger(trigger);
        }
    }

    private static void WalkTable(Table table, ISchemaVisitor visitor)
    {
        visitor.VisitTable(table);

        foreach (var column in table.Columns)
        {
            visitor.VisitColumn(column);
        }

        if (table.PrimaryKey is { } primaryKey)
        {
            visitor.VisitPrimaryKey(primaryKey);
        }

        foreach (var foreignKey in table.ForeignKeys)
        {
            visitor.VisitForeignKey(foreignKey);
        }

        foreach (var index in table.Indexes)
        {
            visitor.VisitIndex(index);
        }

        foreach (var check in table.CheckConstraints)
        {
            visitor.VisitCheckConstraint(check);
        }
    }
}
