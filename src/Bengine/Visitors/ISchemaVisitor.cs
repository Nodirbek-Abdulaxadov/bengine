namespace Bengine.Visitors;

using Bengine.Models;

/// <summary>
/// Visits the nodes of a schema AST. Traversal is driven externally by
/// <see cref="SchemaWalker"/>, keeping the model types free of any dependency on
/// visitors. Implementations override only the nodes they care about.
/// </summary>
public interface ISchemaVisitor
{
    /// <summary>Visits the root database schema.</summary>
    /// <param name="schema">The schema being visited.</param>
    void VisitDatabase(DatabaseSchema schema);

    /// <summary>Visits a table.</summary>
    /// <param name="table">The table being visited.</param>
    void VisitTable(Table table);

    /// <summary>Visits a column.</summary>
    /// <param name="column">The column being visited.</param>
    void VisitColumn(Column column);

    /// <summary>Visits a primary key.</summary>
    /// <param name="primaryKey">The primary key being visited.</param>
    void VisitPrimaryKey(PrimaryKey primaryKey);

    /// <summary>Visits a foreign key.</summary>
    /// <param name="foreignKey">The foreign key being visited.</param>
    void VisitForeignKey(ForeignKey foreignKey);

    /// <summary>Visits an index.</summary>
    /// <param name="index">The index being visited.</param>
    void VisitIndex(Index index);

    /// <summary>Visits a check constraint.</summary>
    /// <param name="check">The check constraint being visited.</param>
    void VisitCheckConstraint(CheckConstraint check);

    /// <summary>Visits a sequence.</summary>
    /// <param name="sequence">The sequence being visited.</param>
    void VisitSequence(Sequence sequence);

    /// <summary>Visits a view.</summary>
    /// <param name="view">The view being visited.</param>
    void VisitView(View view);

    /// <summary>Visits a trigger.</summary>
    /// <param name="trigger">The trigger being visited.</param>
    void VisitTrigger(Trigger trigger);
}
