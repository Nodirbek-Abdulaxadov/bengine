namespace Bengine.Diff;

using Bengine.Models;

/// <summary>What differs about a column.</summary>
[Flags]
public enum ColumnChange
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The type (or its length, precision, scale).</summary>
    Type = 1,

    /// <summary>Whether it accepts <c>NULL</c>.</summary>
    Nullability = 2,

    /// <summary>The default value.</summary>
    Default = 4,

    /// <summary>The identity / auto-increment configuration.</summary>
    Identity = 8,

    /// <summary>The generation expression.</summary>
    Generated = 16,

    /// <summary>The comment.</summary>
    Comment = 32,
}

/// <summary>
/// One structural difference between two schema ASTs, as an operation that
/// moves the source schema toward the target. Each change carries the AST
/// nodes it concerns, so a provider renderer can emit exact DDL.
/// </summary>
public abstract record SchemaChange
{
    /// <summary>Gets a short human-readable description (for diff lists).</summary>
    public abstract string Description { get; }

    /// <summary>Gets the qualified name of the table (or view, sequence) the change concerns.</summary>
    public abstract string Subject { get; }

    /// <summary>Gets whether the change can lose data (drops, narrowing type changes).</summary>
    public virtual bool IsDestructive => false;

    /// <summary>Formats a schema-qualified name.</summary>
    /// <param name="schema">The schema, or <c>null</c>.</param>
    /// <param name="name">The name.</param>
    /// <returns><c>schema.name</c> or <c>name</c>.</returns>
    protected static string Qualified(string? schema, string name) => schema is null ? name : $"{schema}.{name}";
}

/// <summary>Creates a table.</summary>
/// <param name="Table">The table to create.</param>
public sealed record CreateTableChange(Table Table) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Create table {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);
}

/// <summary>Drops a table.</summary>
/// <param name="Table">The table to drop.</param>
public sealed record DropTableChange(Table Table) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Drop table {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);

    /// <inheritdoc />
    public override bool IsDestructive => true;
}

/// <summary>Renames a table (only from an editor that knows the rename; comparison sees a drop and a create).</summary>
/// <param name="Table">The table under its old name.</param>
/// <param name="NewName">The new name.</param>
public sealed record RenameTableChange(Table Table, string NewName) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Rename table {Subject} to {NewName}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);
}

/// <summary>Adds a column.</summary>
/// <param name="Table">The (target) table.</param>
/// <param name="Column">The column.</param>
public sealed record AddColumnChange(Table Table, Column Column) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Add column {Column.Name} to {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);
}

/// <summary>Drops a column.</summary>
/// <param name="Table">The (source) table.</param>
/// <param name="Column">The column.</param>
public sealed record DropColumnChange(Table Table, Column Column) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Drop column {Column.Name} from {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);

    /// <inheritdoc />
    public override bool IsDestructive => true;
}

/// <summary>Renames a column (only from an editor that knows the rename).</summary>
/// <param name="Table">The table.</param>
/// <param name="OldName">The old name.</param>
/// <param name="NewName">The new name.</param>
public sealed record RenameColumnChange(Table Table, string OldName, string NewName) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Rename column {OldName} to {NewName} in {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);
}

/// <summary>Changes a column's type, nullability, default, identity, generation, or comment.</summary>
/// <param name="Table">The target table.</param>
/// <param name="From">The column as it is.</param>
/// <param name="To">The column as it should be.</param>
/// <param name="Changes">What differs.</param>
public sealed record AlterColumnChange(Table Table, Column From, Column To, ColumnChange Changes) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Alter column {To.Name} of {Subject} ({Changes})";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);

    /// <inheritdoc />
    public override bool IsDestructive => Changes.HasFlag(ColumnChange.Type)
        || (Changes.HasFlag(ColumnChange.Nullability) && !To.IsNullable);
}

/// <summary>Adds a primary key.</summary>
/// <param name="Table">The target table.</param>
/// <param name="Key">The key.</param>
public sealed record AddPrimaryKeyChange(Table Table, PrimaryKey Key) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Add primary key ({string.Join(", ", Key.ColumnNames)}) to {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);
}

/// <summary>Drops a primary key.</summary>
/// <param name="Table">The source table.</param>
/// <param name="Key">The key.</param>
public sealed record DropPrimaryKeyChange(Table Table, PrimaryKey Key) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Drop primary key of {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);
}

/// <summary>Adds a foreign key.</summary>
/// <param name="Table">The target table.</param>
/// <param name="Key">The key.</param>
public sealed record AddForeignKeyChange(Table Table, ForeignKey Key) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Add foreign key {Key.Name} to {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);
}

/// <summary>Drops a foreign key.</summary>
/// <param name="Table">The source table.</param>
/// <param name="Key">The key.</param>
public sealed record DropForeignKeyChange(Table Table, ForeignKey Key) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Drop foreign key {Key.Name} of {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);
}

/// <summary>Creates an index.</summary>
/// <param name="Table">The target table.</param>
/// <param name="Index">The index.</param>
public sealed record AddIndexChange(Table Table, Index Index) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Create index {Index.Name} on {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);
}

/// <summary>Drops an index.</summary>
/// <param name="Table">The source table.</param>
/// <param name="Index">The index.</param>
public sealed record DropIndexChange(Table Table, Index Index) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Drop index {Index.Name} on {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);
}

/// <summary>Adds a check constraint.</summary>
/// <param name="Table">The target table.</param>
/// <param name="Check">The constraint.</param>
public sealed record AddCheckChange(Table Table, CheckConstraint Check) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Add check {Check.Name} to {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);
}

/// <summary>Drops a check constraint.</summary>
/// <param name="Table">The source table.</param>
/// <param name="Check">The constraint.</param>
public sealed record DropCheckChange(Table Table, CheckConstraint Check) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Drop check {Check.Name} of {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);
}

/// <summary>Changes a table's comment.</summary>
/// <param name="Table">The target table.</param>
public sealed record TableCommentChange(Table Table) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Set comment of {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Table.Schema, Table.Name);
}

/// <summary>Creates (or replaces) a view.</summary>
/// <param name="View">The view.</param>
/// <param name="Replaces">Whether a view of that name exists and is replaced.</param>
public sealed record CreateViewChange(View View, bool Replaces = false) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"{(Replaces ? "Replace" : "Create")} view {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(View.Schema, View.Name);
}

/// <summary>Drops a view.</summary>
/// <param name="View">The view.</param>
public sealed record DropViewChange(View View) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Drop view {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(View.Schema, View.Name);
}

/// <summary>Creates a sequence.</summary>
/// <param name="Sequence">The sequence.</param>
public sealed record CreateSequenceChange(Sequence Sequence) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Create sequence {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Sequence.Schema, Sequence.Name);
}

/// <summary>Drops a sequence.</summary>
/// <param name="Sequence">The sequence.</param>
public sealed record DropSequenceChange(Sequence Sequence) : SchemaChange
{
    /// <inheritdoc />
    public override string Description => $"Drop sequence {Subject}";

    /// <inheritdoc />
    public override string Subject => Qualified(Sequence.Schema, Sequence.Name);

    /// <inheritdoc />
    public override bool IsDestructive => true;
}
