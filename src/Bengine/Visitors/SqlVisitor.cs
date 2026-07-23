namespace Bengine.Visitors;

using System.Text;
using Bengine.Generators;
using Bengine.Models;

/// <summary>
/// A visitor that accumulates provider DDL by delegating each visited object to
/// an <see cref="ISqlGenerator"/>. Column and constraint nodes are skipped
/// because the generator renders them as part of their owning table.
/// </summary>
public sealed class SqlVisitor : SchemaVisitorBase
{
    private readonly ISqlGenerator _generator;
    private readonly StringBuilder _builder = new();

    /// <summary>Initializes the visitor with a provider SQL generator.</summary>
    /// <param name="generator">The generator that renders each object.</param>
    public SqlVisitor(ISqlGenerator generator) => _generator = generator;

    /// <summary>Gets the DDL accumulated so far.</summary>
    public string Sql => _builder.ToString().TrimEnd() + Environment.NewLine;

    /// <inheritdoc />
    public override void VisitTable(Table table) =>
        _builder.AppendLine(_generator.CreateTable(table)).AppendLine();
}
