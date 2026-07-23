namespace Bengine.Generators.Sql;

using System.Text;
using Bengine.Enums;
using Bengine.Mapping;
using Bengine.Models;

/// <summary>
/// Shared, provider-independent DDL composition. All structural logic lives here
/// and is parameterized by a <see cref="SqlDialect"/> for provider-specific
/// tokens and an <see cref="ITypeMapper"/> for type rendering, so no database
/// type names are hardcoded.
/// </summary>
public abstract class SqlGeneratorBase : ISqlGenerator
{
    private readonly SqlDialect _dialect;
    private readonly ITypeMapper _mapper;

    /// <summary>Initializes the generator with a dialect and type mapper.</summary>
    /// <param name="dialect">The provider dialect.</param>
    /// <param name="mapper">The type mapper.</param>
    protected SqlGeneratorBase(SqlDialect dialect, ITypeMapper mapper)
    {
        _dialect = dialect;
        _mapper = mapper;
    }

    /// <inheritdoc />
    public DatabaseProvider Provider => _dialect.Provider;

    /// <inheritdoc />
    public string Generate(DatabaseSchema schema)
    {
        var builder = new StringBuilder();

        if (_dialect.Supports(SqlDialectFeature.Sequences))
        {
            foreach (var sequence in schema.Sequences)
            {
                builder.AppendLine(CreateSequence(sequence)).AppendLine();
            }
        }

        foreach (var table in schema.Tables)
        {
            builder.AppendLine(CreateTable(table)).AppendLine();

            foreach (var index in table.Indexes)
            {
                builder.AppendLine(CreateIndex(table, index));
            }

            if (table.Indexes.Count > 0)
            {
                builder.AppendLine();
            }
        }

        foreach (var view in schema.Views)
        {
            builder.AppendLine(CreateView(view)).AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <inheritdoc />
    public string CreateTable(Table table)
    {
        var lines = new List<string>();
        lines.AddRange(table.Columns.Select(RenderColumn));

        if (table.PrimaryKey is { } pk)
        {
            lines.Add($"PRIMARY KEY ({JoinQuoted(pk.ColumnNames)})");
        }

        lines.AddRange(table.ForeignKeys.Select(RenderForeignKey));
        lines.AddRange(table.CheckConstraints.Select(RenderCheck));

        var ifNotExists = _dialect.Supports(SqlDialectFeature.IfNotExists) ? "IF NOT EXISTS " : string.Empty;
        var body = string.Join("," + Environment.NewLine, lines.Select(l => "    " + l));

        return $"CREATE TABLE {ifNotExists}{QualifiedName(table.Schema, table.Name)} ("
               + Environment.NewLine + body + Environment.NewLine + ");";
    }

    /// <inheritdoc />
    public string DropTable(Table table)
    {
        var ifExists = _dialect.Supports(SqlDialectFeature.IfNotExists) ? "IF EXISTS " : string.Empty;
        return $"DROP TABLE {ifExists}{QualifiedName(table.Schema, table.Name)};";
    }

    /// <summary>Generates a <c>CREATE INDEX</c> statement.</summary>
    /// <param name="table">The table the index belongs to.</param>
    /// <param name="index">The index to render.</param>
    /// <returns>The index DDL.</returns>
    public string CreateIndex(Table table, Index index)
    {
        var unique = index.IsUnique ? "UNIQUE " : string.Empty;
        var columns = string.Join(", ", index.Columns.Select(RenderIndexColumn));
        var filter = index.IsPartial ? $" WHERE {index.Filter}" : string.Empty;

        return $"CREATE {unique}INDEX {_dialect.QuoteIdentifier(index.Name)} "
               + $"ON {QualifiedName(table.Schema, table.Name)} ({columns}){filter};";
    }

    /// <summary>Generates a <c>CREATE SEQUENCE</c> statement.</summary>
    /// <param name="sequence">The sequence to render.</param>
    /// <returns>The sequence DDL.</returns>
    public string CreateSequence(Sequence sequence)
    {
        var builder = new StringBuilder($"CREATE SEQUENCE {QualifiedName(sequence.Schema, sequence.Name)}");
        builder.Append($" START WITH {sequence.StartValue} INCREMENT BY {sequence.Increment}");
        if (sequence.MinValue is { } min)
        {
            builder.Append($" MINVALUE {min}");
        }

        if (sequence.MaxValue is { } max)
        {
            builder.Append($" MAXVALUE {max}");
        }

        builder.Append(sequence.Cycle ? " CYCLE" : " NO CYCLE").Append(';');
        return builder.ToString();
    }

    /// <summary>Generates a <c>CREATE VIEW</c> statement.</summary>
    /// <param name="view">The view to render.</param>
    /// <returns>The view DDL.</returns>
    public string CreateView(View view)
    {
        var materialized = view.IsMaterialized ? "MATERIALIZED " : string.Empty;
        return $"CREATE {materialized}VIEW {QualifiedName(view.Schema, view.Name)} AS"
               + Environment.NewLine + view.Definition.TrimEnd().TrimEnd(';') + ";";
    }

    private string RenderColumn(Column column)
    {
        var builder = new StringBuilder(_dialect.QuoteIdentifier(column.Name));
        builder.Append(' ').Append(_mapper.RenderProviderType(Provider, column.Type));

        if (column.Generated is { } generated
            && _dialect.Supports(SqlDialectFeature.GeneratedColumns))
        {
            var kind = generated.Kind == GeneratedKind.Stored ? "STORED" : "VIRTUAL";
            builder.Append($" GENERATED ALWAYS AS ({generated.Expression}) {kind}");
            return builder.ToString();
        }

        builder.Append(column.IsNullable ? " NULL" : " NOT NULL");

        if (_dialect.RenderDefault(column.Default) is { } @default)
        {
            builder.Append(" DEFAULT ").Append(@default);
        }

        builder.Append(_dialect.RenderIdentity(column.Identity));
        return builder.ToString();
    }

    private string RenderForeignKey(ForeignKey fk)
    {
        var builder = new StringBuilder(
            $"CONSTRAINT {_dialect.QuoteIdentifier(fk.Name)} FOREIGN KEY ({JoinQuoted(fk.Columns)}) ");
        builder.Append(
            $"REFERENCES {QualifiedName(fk.ReferencedSchema, fk.ReferencedTable)} "
            + $"({JoinQuoted(fk.ReferencedColumns)})");
        builder.Append($" ON DELETE {_dialect.RenderReferentialAction(fk.OnDelete)}");
        builder.Append($" ON UPDATE {_dialect.RenderReferentialAction(fk.OnUpdate)}");
        return builder.ToString();
    }

    private string RenderCheck(CheckConstraint check) =>
        $"CONSTRAINT {_dialect.QuoteIdentifier(check.Name)} CHECK ({check.Expression})";

    private string RenderIndexColumn(IndexColumn column)
    {
        var direction = column.Direction == SortDirection.Descending ? " DESC" : " ASC";
        var nulls = column.Nulls switch
        {
            NullsOrder.First => " NULLS FIRST",
            NullsOrder.Last => " NULLS LAST",
            _ => string.Empty,
        };

        return _dialect.QuoteIdentifier(column.ColumnName) + direction + nulls;
    }

    private string JoinQuoted(IEnumerable<string> names) =>
        string.Join(", ", names.Select(_dialect.QuoteIdentifier));

    private string QualifiedName(string? schema, string name) =>
        schema is not null && _dialect.Supports(SqlDialectFeature.Schemas)
            ? $"{_dialect.QuoteIdentifier(schema)}.{_dialect.QuoteIdentifier(name)}"
            : _dialect.QuoteIdentifier(name);
}
