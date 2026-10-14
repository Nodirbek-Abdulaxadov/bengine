namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Design;
using Bengine.Introspection.Import;
using Bengine.Introspection.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>A delimiter choice.</summary>
/// <param name="Label">The label.</param>
/// <param name="Value">The delimiter, or <c>null</c> to detect it.</param>
public sealed record DelimiterOption(string Label, char? Value)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>Where one file column goes.</summary>
public sealed partial class MappingRow : ObservableObject
{
    /// <summary>The choice that skips a column.</summary>
    public const string Skip = "(skip)";

    [ObservableProperty] private string _target;
    [ObservableProperty] private string _type;

    /// <summary>Creates the row.</summary>
    /// <param name="index">The file column position.</param>
    /// <param name="header">The file column name.</param>
    /// <param name="sample">A sample value.</param>
    /// <param name="target">The target column (or the new column's name).</param>
    /// <param name="type">The inferred type (new tables).</param>
    public MappingRow(int index, string header, string sample, string target, string type)
    {
        Index = index;
        Header = header;
        Sample = sample;
        _target = target;
        _type = type;
    }

    /// <summary>Gets the file column position.</summary>
    public int Index { get; }

    /// <summary>Gets the file column name.</summary>
    public string Header { get; }

    /// <summary>Gets a sample value.</summary>
    public string Sample { get; }
}

/// <summary>
/// "Import Data from File": reads a CSV/TSV file, previews it, maps its columns
/// to an existing table's columns or designs a new table from it (inferred
/// types, editable), and imports every row in one transaction.
/// </summary>
public sealed partial class ImportDataViewModel : ObservableObject
{
    private readonly Func<Task<DatabaseSession>> _open;
    private readonly IDialogService? _dialogs;
    private CancellationTokenSource? _cancel;

    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private DelimiterOption _delimiter;
    [ObservableProperty] private bool _hasHeader = true;
    [ObservableProperty] private bool _emptyIsNull = true;
    [ObservableProperty] private bool _createTable;
    [ObservableProperty] private string _newTableName = string.Empty;
    [ObservableProperty] private CatalogRelation? _existingTable;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _previewInfo = "Choose a file.";

    /// <summary>Creates the dialog model.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="schema">The schema new tables go in.</param>
    /// <param name="tables">The tables of the database.</param>
    /// <param name="target">The table to import into, or <c>null</c> to create one.</param>
    /// <param name="open">Opens a session.</param>
    /// <param name="dialogs">The dialogs.</param>
    public ImportDataViewModel(DatabaseProvider provider, string? schema, IReadOnlyList<CatalogRelation> tables, CatalogRelation? target, Func<Task<DatabaseSession>> open, IDialogService? dialogs)
    {
        Provider = provider;
        Schema = schema;
        Tables = tables.Where(t => t.Kind == TableKind.Table).ToList();
        _open = open;
        _dialogs = dialogs;
        _delimiter = Delimiters[0];
        _existingTable = target;
        _createTable = target is null;
    }

    /// <summary>Gets the provider.</summary>
    public DatabaseProvider Provider { get; }

    /// <summary>Gets the schema for a new table.</summary>
    public string? Schema { get; }

    /// <summary>Gets the tables that can be imported into.</summary>
    public IReadOnlyList<CatalogRelation> Tables { get; }

    /// <summary>Gets the delimiter choices.</summary>
    public IReadOnlyList<DelimiterOption> Delimiters { get; } =
        [new("Detect", null), new("Comma ,", ','), new("Semicolon ;", ';'), new("Tab", '\t'), new("Pipe |", '|')];

    /// <summary>Gets the column mappings.</summary>
    public ObservableCollection<MappingRow> Mappings { get; } = [];

    /// <summary>Gets the target column choices.</summary>
    public ObservableCollection<string> TargetColumns { get; } = [];

    /// <summary>Gets the preview column names.</summary>
    public IReadOnlyList<string> PreviewColumns { get; private set; } = [];

    /// <summary>Gets the preview rows.</summary>
    public ObservableCollection<RowItem> PreviewRows { get; } = [];

    /// <summary>Raised when the preview columns change.</summary>
    public event Action? PreviewChanged;

    /// <summary>Raised when the dialog should close; the argument tells whether rows were imported.</summary>
    public event Action<bool>? Closed;

    /// <summary>Gets the imported table after a successful import.</summary>
    public TableRef? Imported { get; private set; }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (_dialogs is not null && await _dialogs.OpenFileAsync("Import data", ["csv", "tsv", "txt"]) is { } path)
        {
            FilePath = path;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cancel is { } cancel)
        {
            cancel.Cancel();
        }
        else
        {
            Closed?.Invoke(false);
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (!File.Exists(FilePath))
        {
            Show("Choose a file to import.", error: true);
            return;
        }

        IsBusy = true;
        _cancel = new CancellationTokenSource();
        try
        {
            await using var session = await _open();
            var options = Options();
            TableRef table;
            IReadOnlyList<ColumnMapping> mapping;
            Dictionary<string, string> types;
            if (CreateTable)
            {
                var design = new TableDesign(Provider, Schema) { Name = NewTableName.Trim() };
                foreach (var row in Mappings.Where(m => m.Target != MappingRow.Skip && m.Target.Trim().Length > 0))
                {
                    design.Columns.Add(new ColumnDesign { Name = row.Target.Trim(), Type = row.Type.Trim(), Nullable = true });
                }

                if (design.Validate() is { Count: > 0 } problems)
                {
                    Show(string.Join(" ", problems), error: true);
                    return;
                }

                await SchemaApplier.ApplyAsync(session, design.Script(), cancellationToken: _cancel.Token);
                table = new TableRef(design.Name, Schema);
                mapping = Mappings.Where(m => m.Target != MappingRow.Skip && m.Target.Trim().Length > 0).Select(m => new ColumnMapping(m.Index, m.Target.Trim())).ToList();
                types = Mappings.ToDictionary(m => m.Target.Trim(), m => m.Type, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                if (ExistingTable is not { } target)
                {
                    Show("Choose the table to import into.", error: true);
                    return;
                }

                table = target.Ref;
                mapping = Mappings.Select(m => new ColumnMapping(m.Index, m.Target == MappingRow.Skip ? null : m.Target)).ToList();
                types = target.Columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.OrdinalIgnoreCase);
            }

            var progress = new Progress<long>(n => Show($"Imported {n:N0} rows…", error: false));
            var count = await DataImporter.ImportAsync(session, table, FilePath, options, mapping, types, progress, _cancel.Token);
            Imported = table;
            Show($"Imported {count:N0} rows into {table.QualifiedName}.", error: false);
            Closed?.Invoke(true);
        }
        catch (OperationCanceledException)
        {
            Show("Import cancelled; nothing was imported.", error: true);
        }
        catch (IntrospectionException ex)
        {
            Show($"{ex.Message} Nothing was imported.", error: true);
        }
        catch (IOException ex)
        {
            Show(ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    partial void OnFilePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(NewTableName) && value.Length > 0)
        {
            NewTableName = DataImporter.ColumnName(Path.GetFileNameWithoutExtension(value));
        }

        Reload();
    }

    partial void OnDelimiterChanged(DelimiterOption value) => Reload();

    partial void OnHasHeaderChanged(bool value) => Reload();

    partial void OnCreateTableChanged(bool value) => Reload();

    partial void OnExistingTableChanged(CatalogRelation? value) => Reload();

    private ImportOptions Options() => new() { Delimiter = Delimiter.Value, HasHeader = HasHeader, EmptyIsNull = EmptyIsNull };

    private void Reload()
    {
        Mappings.Clear();
        PreviewRows.Clear();
        TargetColumns.Clear();
        if (!File.Exists(FilePath))
        {
            return;
        }

        try
        {
            var preview = DataImporter.Preview(FilePath, Options(), Provider);
            PreviewColumns = preview.Headers;
            foreach (var row in preview.Rows.Take(50))
            {
                PreviewRows.Add(new RowItem(preview.Headers, preview.Headers.Select((_, i) => (object?)(i < row.Count ? row[i] : null)).ToList()));
            }

            TargetColumns.Add(MappingRow.Skip);
            if (!CreateTable && ExistingTable is { } table)
            {
                foreach (var column in table.Columns)
                {
                    TargetColumns.Add(column.Name);
                }
            }

            for (var i = 0; i < preview.Headers.Count; i++)
            {
                var header = preview.Headers[i];
                var sample = preview.Rows.Select(r => i < r.Count ? r[i] : string.Empty).FirstOrDefault(v => v.Length > 0) ?? string.Empty;
                var target = CreateTable
                    ? DataImporter.ColumnName(header)
                    : ExistingTable?.Columns.FirstOrDefault(c => c.Name.Equals(header, StringComparison.OrdinalIgnoreCase) || c.Name.Equals(DataImporter.ColumnName(header), StringComparison.OrdinalIgnoreCase))?.Name ?? MappingRow.Skip;
                Mappings.Add(new MappingRow(i, header, sample, target, preview.Types[i]));
            }

            PreviewInfo = $"{preview.Headers.Count} column(s) · delimiter {(preview.Delimiter == '\t' ? "tab" : preview.Delimiter.ToString())} · showing {PreviewRows.Count} row(s)";
            IsError = false;
            PreviewChanged?.Invoke();
        }
        catch (IOException ex)
        {
            Show(ex.Message, error: true);
        }
    }

    private void Show(string message, bool error)
    {
        Status = message;
        IsError = error;
    }
}
