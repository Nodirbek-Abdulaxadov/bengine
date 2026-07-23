namespace Bengine.Visitors;

using Bengine.Models;

/// <summary>
/// Convenience base with no-op implementations of every visit method, so derived
/// visitors override only the nodes relevant to them.
/// </summary>
public abstract class SchemaVisitorBase : ISchemaVisitor
{
    /// <inheritdoc />
    public virtual void VisitDatabase(DatabaseSchema schema)
    {
    }

    /// <inheritdoc />
    public virtual void VisitTable(Table table)
    {
    }

    /// <inheritdoc />
    public virtual void VisitColumn(Column column)
    {
    }

    /// <inheritdoc />
    public virtual void VisitPrimaryKey(PrimaryKey primaryKey)
    {
    }

    /// <inheritdoc />
    public virtual void VisitForeignKey(ForeignKey foreignKey)
    {
    }

    /// <inheritdoc />
    public virtual void VisitIndex(Index index)
    {
    }

    /// <inheritdoc />
    public virtual void VisitCheckConstraint(CheckConstraint check)
    {
    }

    /// <inheritdoc />
    public virtual void VisitSequence(Sequence sequence)
    {
    }

    /// <inheritdoc />
    public virtual void VisitView(View view)
    {
    }

    /// <inheritdoc />
    public virtual void VisitTrigger(Trigger trigger)
    {
    }
}
