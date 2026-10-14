namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using Bengine.Introspection;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>
/// An ER diagram tab for a database (or one schema): its tables and views with
/// columns, and the foreign keys between them, from the cached catalog.
/// </summary>
public sealed partial class DiagramDocumentViewModel : DocumentViewModel
{
    private readonly ExplorerViewModel _explorer;
    private DatabaseCatalog? _catalog;

    [ObservableProperty] private string? _schema;
    [ObservableProperty] private bool _keysOnly;
    [ObservableProperty] private bool _includeViews;
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Creates the tab.</summary>
    /// <param name="explorer">The explorer.</param>
    /// <param name="server">The server.</param>
    /// <param name="database">The database.</param>
    /// <param name="schema">The schema to show, or <c>null</c> for all.</param>
    public DiagramDocumentViewModel(ExplorerViewModel explorer, ServerProfile server, string? database, string? schema)
    {
        _explorer = explorer;
        Server = server;
        Database = database;
        _schema = schema;
    }

    /// <summary>Gets the server.</summary>
    public ServerProfile Server { get; }

    /// <summary>Gets the database.</summary>
    public string? Database { get; }

    /// <inheritdoc />
    public override string Title => $"Diagram {Schema ?? Database}";

    /// <inheritdoc />
    public override string IconKind => "Schema";

    /// <inheritdoc />
    public override string? AccentColor => Server.Color;

    /// <inheritdoc />
    public override string ToolTip => $"ER diagram — {Server.Name} › {Database}" + (Schema is null ? string.Empty : $" › {Schema}");

    /// <summary>Gets the schema choices.</summary>
    public ObservableCollection<string> Schemas { get; } = [];

    /// <summary>Gets the relations drawn.</summary>
    public IReadOnlyList<CatalogRelation> Relations { get; private set; } = [];

    /// <summary>Gets the foreign keys drawn.</summary>
    public IReadOnlyList<CatalogForeignKey> Keys { get; private set; } = [];

    /// <summary>Raised when the drawn set changes.</summary>
    public event Action? Changed;

    /// <summary>Loads the catalog.</summary>
    /// <returns>A task that completes when loaded.</returns>
    public Task InitializeAsync() => LoadAsync(refresh: false);

    /// <summary>Opens a relation's data.</summary>
    /// <param name="relation">The relation.</param>
    public void Open(CatalogRelation relation) => _explorer.OpenData(Server, Database, relation.Ref);

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(refresh: true);

    partial void OnSchemaChanged(string? value)
    {
        OnPropertyChanged(nameof(Title));
        Apply();
    }

    partial void OnIncludeViewsChanged(bool value) => Apply();

    private async Task LoadAsync(bool refresh)
    {
        IsBusy = true;
        try
        {
            _catalog = await _explorer.GetCatalogAsync(Server, Database, refresh);
            if (_catalog is null)
            {
                Status = "Could not read the database metadata.";
                return;
            }

            Schemas.Clear();
            foreach (var name in _catalog.Relations.Select(r => r.Schema).OfType<string>().Distinct().Order())
            {
                Schemas.Add(name);
            }

            if (Schema is not null && !Schemas.Contains(Schema))
            {
                Schema = null;
            }

            Apply();
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply()
    {
        if (_catalog is null)
        {
            return;
        }

        Relations = _catalog.Relations
            .Where(r => Schema is null || string.Equals(r.Schema, Schema, StringComparison.OrdinalIgnoreCase))
            .Where(r => IncludeViews || r.Kind == Bengine.Introspection.Model.TableKind.Table)
            .ToList();
        var names = Relations.Select(r => (r.Schema ?? string.Empty) + "." + r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Keys = _catalog.ForeignKeys
            .Where(k => names.Contains((k.Schema ?? string.Empty) + "." + k.Table) && names.Contains((k.ReferencedSchema ?? string.Empty) + "." + k.ReferencedTable))
            .ToList();
        Status = $"{Relations.Count} table(s), {Keys.Count} relationship(s). Drag boxes to arrange, the background to pan, wheel to zoom; double-click opens data.";
        Changed?.Invoke();
    }
}
