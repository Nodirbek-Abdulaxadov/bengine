namespace Bengine.Introspection.Design;

using System.Collections.Frozen;
using Bengine.Diff;
using Bengine.Enums;
using Bengine.Generators.Migrations;
using Bengine.Models;
using Bengine.Models.Options;
using Bengine.Parsing;

/// <summary>One column as edited in the table designer.</summary>
public sealed class ColumnDesign
{
    /// <summary>Gets or sets the column's name in the database, or <c>null</c> for a new column.</summary>
    public string? OriginalName { get; set; }

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the type as the provider spells it (<c>varchar(255)</c>, <c>bigint</c>, <c>text[]</c>).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the column accepts <c>NULL</c>.</summary>
    public bool Nullable { get; set; } = true;

    /// <summary>Gets or sets the default as SQL (<c>0</c>, <c>'x'</c>, <c>now()</c>), or empty for none.</summary>
    public string Default { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the column is in the primary key.</summary>
    public bool PrimaryKey { get; set; }

    /// <summary>Gets or sets whether values are generated (identity / auto-increment).</summary>
    public bool Identity { get; set; }

    /// <summary>Gets or sets the comment.</summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>Gets the column this design started from, to keep what the designer does not show.</summary>
    internal Column? Source { get; init; }
}

/// <summary>An index as edited in the table designer.</summary>
public sealed class IndexDesign
{
    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the column names, comma-separated (append <c>DESC</c> to a column for descending).</summary>
    public string Columns { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the index is unique.</summary>
    public bool Unique { get; set; }

    /// <summary>Gets or sets a partial-index predicate, or empty.</summary>
    public string Filter { get; set; } = string.Empty;

    /// <summary>Gets the index this design started from.</summary>
    internal Index? Source { get; init; }
}

/// <summary>A foreign key as edited in the table designer.</summary>
public sealed class ForeignKeyDesign
{
    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the local columns, comma-separated.</summary>
    public string Columns { get; set; } = string.Empty;

    /// <summary>Gets or sets the referenced table (<c>schema.table</c> or <c>table</c>).</summary>
    public string ReferencedTable { get; set; } = string.Empty;

    /// <summary>Gets or sets the referenced columns, comma-separated.</summary>
    public string ReferencedColumns { get; set; } = string.Empty;

    /// <summary>Gets or sets the action on delete.</summary>
    public ReferentialAction OnDelete { get; set; } = ReferentialAction.NoAction;

    /// <summary>Gets or sets the action on update.</summary>
    public ReferentialAction OnUpdate { get; set; } = ReferentialAction.NoAction;
}

/// <summary>A check constraint as edited in the table designer.</summary>
public sealed class CheckDesign
{
    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the boolean expression.</summary>
    public string Expression { get; set; } = string.Empty;
}

/// <summary>
/// The editable model behind "Modify Table" / "New Table": columns with their
/// provider type spelling, the primary key, indexes, foreign keys, checks, and
/// comments. It turns the edits back into a table AST, compares it with the
/// table as introspected (renames are tracked through each column's original
/// name), and renders the <c>ALTER</c> (or <c>CREATE</c>) script for the
/// provider.
/// </summary>
public sealed class TableDesign
{
    private Table? _original;

    /// <summary>Creates an empty design for a new table.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="schema">The schema to create the table in.</param>
    public TableDesign(DatabaseProvider provider, string? schema)
    {
        Provider = provider;
        Schema = schema;
    }

    /// <summary>Gets the provider.</summary>
    public DatabaseProvider Provider { get; }

    /// <summary>Gets the schema.</summary>
    public string? Schema { get; }

    /// <summary>Gets or sets the table name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the table comment.</summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>Gets the columns.</summary>
    public List<ColumnDesign> Columns { get; } = [];

    /// <summary>Gets the indexes.</summary>
    public List<IndexDesign> Indexes { get; } = [];

    /// <summary>Gets the foreign keys.</summary>
    public List<ForeignKeyDesign> ForeignKeys { get; } = [];

    /// <summary>Gets the check constraints.</summary>
    public List<CheckDesign> Checks { get; } = [];

    /// <summary>Gets whether the design creates a new table.</summary>
    public bool IsNew => _original is null;

    /// <summary>Gets the table as it was loaded, or <c>null</c> for a new table.</summary>
    public Table? Original => _original;

    /// <summary>Creates a design from an introspected table.</summary>
    /// <param name="table">The table.</param>
    /// <param name="provider">The provider.</param>
    /// <returns>The design.</returns>
    public static TableDesign From(Table table, DatabaseProvider provider)
    {
        var design = new TableDesign(provider, table.Schema) { Name = table.Name, Comment = table.Comment ?? string.Empty, _original = table };
        var keys = table.PrimaryKey?.ColumnNames ?? [];
        foreach (var column in table.Columns)
        {
            design.Columns.Add(new ColumnDesign
            {
                OriginalName = column.Name,
                Name = column.Name,
                Type = TypeText(column, provider),
                Nullable = column.IsNullable,
                Default = column.Identity.Strategy == IdentityStrategy.None ? DefaultText(column.Default) : string.Empty,
                PrimaryKey = keys.Contains(column.Name, StringComparer.OrdinalIgnoreCase),
                Identity = column.Identity.Strategy != IdentityStrategy.None,
                Comment = column.Comment ?? string.Empty,
                Source = column,
            });
        }

        design.Indexes.AddRange(table.Indexes.Select(i => new IndexDesign
        {
            Name = i.Name,
            Columns = string.Join(", ", i.Columns.Select(c => c.Direction == SortDirection.Descending ? c.ColumnName + " DESC" : c.ColumnName)),
            Unique = i.IsUnique,
            Filter = i.Filter ?? string.Empty,
            Source = i,
        }));
        design.ForeignKeys.AddRange(table.ForeignKeys.Select(fk => new ForeignKeyDesign
        {
            Name = fk.Name,
            Columns = string.Join(", ", fk.Columns),
            ReferencedTable = fk.ReferencedSchema is { } s && s != table.Schema ? $"{s}.{fk.ReferencedTable}" : fk.ReferencedTable,
            ReferencedColumns = string.Join(", ", fk.ReferencedColumns),
            OnDelete = fk.OnDelete,
            OnUpdate = fk.OnUpdate,
        }));
        design.Checks.AddRange(table.CheckConstraints.Select(c => new CheckDesign { Name = c.Name, Expression = c.Expression }));
        return design;
    }

    /// <summary>Lists what is wrong with the design, if anything.</summary>
    /// <returns>The problems; empty when the design can be applied.</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("The table needs a name.");
        }

        if (Columns.Count == 0)
        {
            problems.Add("The table needs at least one column.");
        }

        foreach (var column in Columns.Where(c => string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Type)))
        {
            problems.Add($"Column {(string.IsNullOrWhiteSpace(column.Name) ? "#" + (Columns.IndexOf(column) + 1) : column.Name)} needs a name and a type.");
        }

        foreach (var duplicate in Columns.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            problems.Add($"Column {duplicate.Key} appears more than once.");
        }

        var names = Columns.Select(c => c.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var index in Indexes)
        {
            foreach (var column in SplitColumns(index.Columns).Select(c => c.Name).Where(c => !names.Contains(c)))
            {
                problems.Add($"Index {index.Name} uses unknown column {column}.");
            }
        }

        foreach (var key in ForeignKeys)
        {
            var local = Split(key.Columns);
            if (local.Count == 0 || local.Count != Split(key.ReferencedColumns).Count || string.IsNullOrWhiteSpace(key.ReferencedTable))
            {
                problems.Add($"Foreign key {key.Name} needs columns, a referenced table, and as many referenced columns.");
            }

            problems.AddRange(local.Where(c => !names.Contains(c)).Select(c => $"Foreign key {key.Name} uses unknown column {c}."));
        }

        return problems;
    }

    /// <summary>Builds the table AST the design describes.</summary>
    /// <returns>The table.</returns>
    public Table ToTable()
    {
        var columns = Columns.Select(BuildColumn).ToList();
        var keys = Columns.Where(c => c.PrimaryKey).Select(c => c.Name.Trim()).ToList();
        return new Table
        {
            Name = Name.Trim(),
            Schema = Schema,
            Columns = columns,
            PrimaryKey = keys.Count == 0 ? null : new PrimaryKey { Name = _original?.PrimaryKey?.Name ?? string.Empty, ColumnNames = keys },
            Indexes = Indexes.Where(i => !string.IsNullOrWhiteSpace(i.Name)).Select(i => new Index
            {
                Name = i.Name.Trim(),
                Type = i.Unique ? IndexType.Unique : IndexType.NonUnique,
                Method = i.Source?.Method ?? IndexMethod.BTree,
                Columns = SplitColumns(i.Columns).Select(c => new IndexColumn { ColumnName = c.Name, Direction = c.Descending ? SortDirection.Descending : SortDirection.Ascending }).ToList(),
                Filter = string.IsNullOrWhiteSpace(i.Filter) ? null : i.Filter.Trim(),
                Comment = i.Source?.Comment,
            }).ToList(),
            ForeignKeys = ForeignKeys.Where(k => !string.IsNullOrWhiteSpace(k.ReferencedTable)).Select(k =>
            {
                var target = k.ReferencedTable.Trim();
                var dot = target.LastIndexOf('.');
                return new ForeignKey
                {
                    Name = string.IsNullOrWhiteSpace(k.Name) ? $"fk_{Name.Trim()}_{string.Join("_", Split(k.Columns))}" : k.Name.Trim(),
                    Columns = Split(k.Columns),
                    ReferencedSchema = dot > 0 ? target[..dot] : Schema,
                    ReferencedTable = dot > 0 ? target[(dot + 1)..] : target,
                    ReferencedColumns = Split(k.ReferencedColumns),
                    OnDelete = k.OnDelete,
                    OnUpdate = k.OnUpdate,
                };
            }).ToList(),
            CheckConstraints = Checks.Where(c => !string.IsNullOrWhiteSpace(c.Expression)).Select((c, i) => new CheckConstraint
            {
                Name = string.IsNullOrWhiteSpace(c.Name) ? $"ck_{Name.Trim()}_{i + 1}" : c.Name.Trim(),
                Expression = c.Expression.Trim(),
            }).ToList(),
            Comment = string.IsNullOrWhiteSpace(Comment) ? null : Comment,
            ProviderOptions = _original?.ProviderOptions ?? ProviderOptions.Empty,
        };
    }

    /// <summary>Computes the changes from the loaded table to the design.</summary>
    /// <returns>The ordered changes; empty when nothing changed.</returns>
    public IReadOnlyList<SchemaChange> Changes()
    {
        var target = ToTable();
        if (_original is null)
        {
            return SchemaDiffer.Compare(new DatabaseSchema { Name = "a" }, new DatabaseSchema { Name = "b", Tables = [target] }, Options);
        }

        // Renamed columns are compared under their new names; the renames come first.
        var renames = Columns.Where(c => c.OriginalName is not null && !c.OriginalName.Equals(c.Name.Trim(), StringComparison.Ordinal))
            .ToDictionary(c => c.OriginalName!, c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        var source = Renamed(_original, renames);
        var changes = new List<SchemaChange>();
        if (!_original.Name.Equals(target.Name, StringComparison.Ordinal))
        {
            changes.Add(new RenameTableChange(_original, target.Name));
        }

        changes.AddRange(renames.Select(r => new RenameColumnChange(target, r.Key, r.Value)));
        changes.AddRange(SchemaDiffer.CompareTables(source, target, Options));
        return changes;
    }

    /// <summary>Renders the design's changes as statements for the provider.</summary>
    /// <returns>The statements.</returns>
    public IReadOnlyList<string> Script()
    {
        var changes = Changes();
        var target = new DatabaseSchema { Name = "target", Tables = [ToTable()] };
        var source = _original is null ? null : new DatabaseSchema { Name = "source", Tables = [_original] };
        return AlterScriptGenerator.For(Provider).Render(changes, source, target);
    }

    private SchemaDiffOptions Options => new() { Provider = Provider, IgnoreConstraintNames = false };

    private Column BuildColumn(ColumnDesign design)
    {
        var type = design.Type.Trim();
        var native = type;
        ColumnTypeOptions logical;
        var source = design.Source;
        if (source is not null && TypeText(source, Provider).Equals(type, StringComparison.OrdinalIgnoreCase))
        {
            logical = source.Type;
        }
        else
        {
            logical = Resolve(type);
        }

        var identity = design.Identity
            ? source?.Identity is { Strategy: not IdentityStrategy.None } kept ? kept : new IdentityOptions { Strategy = IdentityStrategy.Identity }
            : IdentityOptions.None;
        var defaultText = design.Default.Trim();
        var sameDefault = source is not null && DefaultText(source.Default) == defaultText;
        return new Column
        {
            Name = design.Name.Trim(),
            Type = logical,
            // An unchanged flag keeps the loaded nullability (SQLite reports INTEGER PRIMARY KEY as nullable).
            Nullability = source is not null && design.Nullable == source.IsNullable ? source.Nullability
                : design.Nullable ? Nullability.Nullable : Nullability.NotNull,
            Default = design.Identity ? source?.Default ?? DefaultValue.None
                : sameDefault ? source!.Default
                : defaultText.Length == 0 ? DefaultValue.None
                : DefaultValue.Expression(defaultText),
            Identity = identity,
            Generated = source?.Generated,
            Validation = source?.Validation ?? Bengine.Models.Options.Validation.ColumnValidation.None,
            Comment = string.IsNullOrWhiteSpace(design.Comment) ? null : design.Comment,
            ProviderOptions = new ProviderOptions
            {
                Overrides = new Dictionary<DatabaseProvider, ProviderOverride> { [Provider] = new() { NativeTypeName = native } }.ToFrozenDictionary(),
            },
        };
    }

    // The logical type of a provider spelling such as varchar(255) or numeric(10,2).
    private ColumnTypeOptions Resolve(string type)
    {
        var open = type.IndexOf('(');
        var name = open > 0 ? type[..open].Trim() : type;
        var arguments = open > 0 ? type[(open + 1)..].TrimEnd(')').Split(',').Select(a => int.TryParse(a.Trim(), out var n) ? n : (int?)null).ToList() : [];
        try
        {
            var resolved = SqlTypeResolver.Resolve(Provider, name, arguments.FirstOrDefault());
            return new ColumnTypeOptions
            {
                DataType = resolved.DataType,
                Length = resolved.DataType is DbDataType.Char or DbDataType.VarChar or DbDataType.Binary or DbDataType.VarBinary ? arguments.FirstOrDefault() : null,
                Precision = resolved.DataType == DbDataType.Decimal ? arguments.FirstOrDefault() : null,
                Scale = resolved.DataType == DbDataType.Decimal && arguments.Count > 1 ? arguments[1] : null,
            };
        }
        catch (SqlParseException)
        {
            return ColumnTypeOptions.Of(DbDataType.Text);
        }
    }

    private static Table Renamed(Table table, IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0)
        {
            return table;
        }

        string Map(string name) => renames.TryGetValue(name, out var renamed) ? renamed : name;
        Column Rename(Column c) => !renames.ContainsKey(c.Name) ? c : new Column
        {
            Name = Map(c.Name), Type = c.Type, Nullability = c.Nullability, Default = c.Default, Identity = c.Identity,
            Generated = c.Generated, Validation = c.Validation, Comment = c.Comment, ProviderOptions = c.ProviderOptions,
        };

        return new Table
        {
            Name = table.Name,
            Schema = table.Schema,
            Columns = table.Columns.Select(Rename).ToList(),
            PrimaryKey = table.PrimaryKey is { } pk ? new PrimaryKey { Name = pk.Name, ColumnNames = pk.ColumnNames.Select(Map).ToList() } : null,
            ForeignKeys = table.ForeignKeys.Select(fk => new ForeignKey
            {
                Name = fk.Name, Columns = fk.Columns.Select(Map).ToList(), ReferencedTable = fk.ReferencedTable, ReferencedSchema = fk.ReferencedSchema,
                ReferencedColumns = fk.ReferencedColumns, OnDelete = fk.OnDelete, OnUpdate = fk.OnUpdate, IsDeferrable = fk.IsDeferrable,
            }).ToList(),
            Indexes = table.Indexes.Select(i => new Index
            {
                Name = i.Name, Type = i.Type, Method = i.Method, Filter = i.Filter, Comment = i.Comment,
                Columns = i.Columns.Select(c => c with { ColumnName = Map(c.ColumnName) }).ToList(),
            }).ToList(),
            CheckConstraints = table.CheckConstraints,
            Comment = table.Comment,
            ProviderOptions = table.ProviderOptions,
        };
    }

    /// <summary>Gets a column's type as the provider spells it.</summary>
    /// <param name="column">The column.</param>
    /// <param name="provider">The provider.</param>
    /// <returns>The type text.</returns>
    public static string TypeText(Column column, DatabaseProvider provider) =>
        column.ProviderOptions.TryGet(provider, out var native) && native?.NativeTypeName is { Length: > 0 } name
            ? name
            : Bengine.Mapping.TypeMapper.Instance.RenderProviderType(provider, column.Type);

    private static string DefaultText(DefaultValue value) => value.Kind switch
    {
        DefaultValueKind.None => string.Empty,
        DefaultValueKind.CurrentTimestamp => "CURRENT_TIMESTAMP",
        DefaultValueKind.CurrentDate => "CURRENT_DATE",
        DefaultValueKind.CurrentTime => "CURRENT_TIME",
        DefaultValueKind.Null => "NULL",
        _ => value.Value ?? string.Empty,
    };

    private static List<string> Split(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static IEnumerable<(string Name, bool Descending)> SplitColumns(string text) =>
        Split(text).Select(c => c.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase) ? (c[..^5].Trim(), true)
            : c.EndsWith(" ASC", StringComparison.OrdinalIgnoreCase) ? (c[..^4].Trim(), false) : (c, false));
}
