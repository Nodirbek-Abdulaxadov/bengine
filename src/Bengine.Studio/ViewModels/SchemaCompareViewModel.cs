namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using Bengine.Diff;
using Bengine.Enums;
using Bengine.Generators.Migrations;
using Bengine.Introspection;
using Bengine.Introspection.Design;
using Bengine.Introspection.Servers;
using Bengine.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>One side of a schema comparison: a live database (optionally one schema) or a schema file.</summary>
public sealed partial class CompareEndpoint : ObservableObject
{
    private readonly ExplorerViewModel _explorer;

    [ObservableProperty] private ServerProfile? _server;
    [ObservableProperty] private string? _database;
    [ObservableProperty] private string? _schema;
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private bool _useFile;

    /// <summary>Creates an endpoint.</summary>
    /// <param name="explorer">The explorer (servers, sessions).</param>
    /// <param name="servers">The registered servers.</param>
    public CompareEndpoint(ExplorerViewModel explorer, IReadOnlyList<ServerProfile> servers)
    {
        _explorer = explorer;
        Servers = servers;
    }

    /// <summary>Gets the registered servers.</summary>
    public IReadOnlyList<ServerProfile> Servers { get; }

    /// <summary>Gets the databases of the chosen server.</summary>
    public ObservableCollection<string> Databases { get; } = [];

    /// <summary>Gets the schemas of the chosen database ("(all)" first).</summary>
    public ObservableCollection<string> Schemas { get; } = [];

    /// <summary>Gets the provider the endpoint's SQL is in.</summary>
    public DatabaseProvider Provider { get; private set; } = DatabaseProvider.PostgreSql;

    /// <summary>Gets a short description.</summary>
    public string Label => UseFile ? Path.GetFileName(FilePath ?? "(file)") : $"{Server?.Name} › {Database}{(Schema is { } s && s != All ? $" › {s}" : string.Empty)}";

    private const string All = "(all schemas)";

    /// <summary>Selects a server, database, and schema (from an explorer node).</summary>
    /// <param name="server">The server.</param>
    /// <param name="database">The database.</param>
    /// <param name="schema">The schema, or <c>null</c>.</param>
    /// <returns>A task that completes when the lists are loaded.</returns>
    public async Task SelectAsync(ServerProfile server, string? database, string? schema)
    {
        Server = server;
        await LoadDatabasesAsync();
        Database = database ?? Databases.FirstOrDefault();
        await LoadSchemasAsync();
        Schema = schema ?? Schemas.FirstOrDefault();
    }

    /// <summary>Reads the endpoint's schema AST.</summary>
    /// <returns>The schema.</returns>
    /// <exception cref="IntrospectionException">The database could not be read.</exception>
    public async Task<DatabaseSchema> LoadAsync()
    {
        if (UseFile)
        {
            if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
            {
                throw new IntrospectionException("Choose a schema.json or .sql file.");
            }

            var text = await File.ReadAllTextAsync(FilePath);
            if (FilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                Provider = Server?.Provider ?? DatabaseProvider.PostgreSql;
                return Bengine.Serialization.SchemaSerializer.FromJson(text);
            }

            Provider = Bengine.Parsing.SqlDialectDetector.Detect(text);
            try
            {
                return Bengine.Parsing.SqlSchemaParser.ParseAs(text, Provider);
            }
            catch (Bengine.Parsing.SqlParseException ex)
            {
                throw new IntrospectionException($"Could not parse {Path.GetFileName(FilePath)}: {ex.Message}", ex);
            }
        }

        if (Server is null)
        {
            throw new IntrospectionException("Choose a server and database.");
        }

        Provider = Server.Provider;
        await using var session = await _explorer.OpenSessionAsync(Server, Database);
        var schema = await session.Introspector.IntrospectAsync(Schema is { } s && s != All ? s : DefaultSchema(Server.Provider, Database));
        return schema;
    }

    /// <summary>Opens a session on the endpoint's database.</summary>
    /// <returns>The session.</returns>
    public Task<DatabaseSession> OpenAsync() => _explorer.OpenSessionAsync(Server!, Database);

    partial void OnServerChanged(ServerProfile? value)
    {
        OnPropertyChanged(nameof(Label));
        _ = LoadDatabasesAsync();
    }

    partial void OnDatabaseChanged(string? value)
    {
        OnPropertyChanged(nameof(Label));
        _ = LoadSchemasAsync();
    }

    partial void OnSchemaChanged(string? value) => OnPropertyChanged(nameof(Label));

    partial void OnUseFileChanged(bool value) => OnPropertyChanged(nameof(Label));

    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(Label));

    private static string? DefaultSchema(DatabaseProvider provider, string? database) => provider switch
    {
        DatabaseProvider.MySql => database,
        _ => null,
    };

    private async Task LoadDatabasesAsync()
    {
        Databases.Clear();
        if (Server is null)
        {
            return;
        }

        try
        {
            foreach (var name in await _explorer.ListDatabasesAsync(Server))
            {
                Databases.Add(name);
            }
        }
        catch (IntrospectionException)
        {
            // Left empty; the user can still pick the file side.
        }
    }

    private async Task LoadSchemasAsync()
    {
        Schemas.Clear();
        Schemas.Add(All);
        if (Server is null || Database is null || Server.Provider is DatabaseProvider.Sqlite or DatabaseProvider.MySql)
        {
            return;
        }

        try
        {
            await using var session = await _explorer.OpenSessionAsync(Server, Database);
            foreach (var schema in await session.Explorer.GetSchemasAsync())
            {
                Schemas.Add(schema);
            }
        }
        catch (IntrospectionException)
        {
            // Compare all schemas.
        }
    }
}

/// <summary>One difference in the comparison result, which can be left out of the script.</summary>
public sealed partial class ChangeItem(SchemaChange change) : ObservableObject
{
    [ObservableProperty] private bool _include = true;

    /// <summary>Gets the change.</summary>
    public SchemaChange Change => change;

    /// <summary>Gets the description.</summary>
    public string Description => change.Description;

    /// <summary>Gets the object the change concerns.</summary>
    public string Subject => change.Subject;

    /// <summary>Gets a marker: + created, − dropped, ~ altered.</summary>
    public string Marker => change switch
    {
        CreateTableChange or AddColumnChange or AddIndexChange or AddForeignKeyChange or AddCheckChange or AddPrimaryKeyChange or CreateViewChange { Replaces: false } or CreateSequenceChange => "+",
        DropTableChange or DropColumnChange or DropIndexChange or DropForeignKeyChange or DropCheckChange or DropPrimaryKeyChange or DropViewChange or DropSequenceChange => "−",
        _ => "~",
    };

    /// <summary>Gets whether the change can lose data.</summary>
    public bool IsDestructive => change.IsDestructive;
}

/// <summary>
/// Schema comparison ("Compare Structure"): reads two schemas — live databases
/// on any registered servers, or <c>schema.json</c>/<c>.sql</c> files — lists
/// every difference, and generates the migration that makes the target match
/// the source, in the target's dialect. Differences can be left out one by
/// one; the script can be reviewed, copied, saved, opened in a Query Tool, or
/// applied to the target in one transaction.
/// </summary>
public sealed partial class SchemaCompareViewModel : DocumentViewModel
{
    private readonly ExplorerViewModel _explorer;
    private DatabaseSchema? _sourceSchema;
    private DatabaseSchema? _targetSchema;

    [ObservableProperty] private string _script = string.Empty;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ChangeItem? _selectedChange;
    [ObservableProperty] private bool _ignoreComments;
    [ObservableProperty] private bool _ignoreConstraintNames = true;

    /// <summary>Creates the tab.</summary>
    /// <param name="explorer">The explorer.</param>
    /// <param name="servers">The registered servers.</param>
    public SchemaCompareViewModel(ExplorerViewModel explorer, IReadOnlyList<ServerProfile> servers)
    {
        _explorer = explorer;
        Source = new CompareEndpoint(explorer, servers);
        Target = new CompareEndpoint(explorer, servers);
        Changes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Gets the source (desired state).</summary>
    public CompareEndpoint Source { get; }

    /// <summary>Gets the target (the database to migrate).</summary>
    public CompareEndpoint Target { get; }

    /// <summary>Gets the differences.</summary>
    public ObservableCollection<ChangeItem> Changes { get; } = [];

    /// <inheritdoc />
    public override string Title => "Compare";

    /// <inheritdoc />
    public override string IconKind => "Schema";

    /// <inheritdoc />
    public override string ToolTip => $"Compare {Source.Label} → {Target.Label}";

    /// <summary>Gets a one-line summary of the differences.</summary>
    public string Summary => Changes.Count == 0
        ? "No differences."
        : $"{Changes.Count} difference(s): {Changes.Count(c => c.Marker == "+")} added, {Changes.Count(c => c.Marker == "−")} removed, {Changes.Count(c => c.Marker == "~")} changed" +
          (Changes.Any(c => c.IsDestructive) ? $" · {Changes.Count(c => c.IsDestructive)} can lose data" : string.Empty);

    [RelayCommand]
    private async Task CompareAsync()
    {
        IsBusy = true;
        IsError = false;
        Status = "Reading schemas…";
        try
        {
            _sourceSchema = await Source.LoadAsync();
            _targetSchema = await Target.LoadAsync();
            var options = new SchemaDiffOptions
            {
                IgnoreSchemaNames = !string.Equals(Source.Schema, Target.Schema, StringComparison.OrdinalIgnoreCase) || Source.UseFile || Target.UseFile
                    || Source.Provider != Target.Provider,
                IgnoreComments = IgnoreComments,
                IgnoreConstraintNames = IgnoreConstraintNames,
                Provider = Source.Provider == Target.Provider ? Target.Provider : null,
            };
            var changes = SchemaDiffer.Compare(_targetSchema, _sourceSchema, options);
            foreach (var item in Changes)
            {
                item.PropertyChanged -= OnItemChanged;
            }

            Changes.Clear();
            foreach (var change in changes)
            {
                var item = new ChangeItem(change);
                item.PropertyChanged += OnItemChanged;
                Changes.Add(item);
            }

            Render();
            Status = Summary;
            OnPropertyChanged(nameof(ToolTip));
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
            IsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SwapAsync()
    {
        var (server, database, schema, file, useFile) = (Source.Server, Source.Database, Source.Schema, Source.FilePath, Source.UseFile);
        Source.UseFile = Target.UseFile;
        Source.FilePath = Target.FilePath;
        if (Target.Server is { } targetServer)
        {
            await Source.SelectAsync(targetServer, Target.Database, Target.Schema);
        }

        Target.UseFile = useFile;
        Target.FilePath = file;
        if (server is not null)
        {
            await Target.SelectAsync(server, database, schema);
        }

        if (Changes.Count > 0)
        {
            await CompareAsync();
        }
    }

    [RelayCommand]
    private async Task BrowseSourceAsync() => Source.FilePath = await BrowseAsync() ?? Source.FilePath;

    [RelayCommand]
    private async Task BrowseTargetAsync() => Target.FilePath = await BrowseAsync() ?? Target.FilePath;

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (_explorer.Dialogs is { } dialogs)
        {
            await dialogs.SetClipboardAsync(Script);
            Status = "Script copied.";
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_explorer.Dialogs is { } dialogs && await dialogs.SaveFileAsync("migration.sql") is { } path)
        {
            await File.WriteAllTextAsync(path, Script);
            Status = $"Saved {path}.";
        }
    }

    [RelayCommand]
    private void OpenInConsole()
    {
        if (!Target.UseFile && Target.Server is { } server)
        {
            _ = _explorer.OpenQueryAsync(server, Target.Database, Script);
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (Target.UseFile || Target.Server is not { } server || _explorer.Dialogs is not { } dialogs)
        {
            Status = "Choose a live database as the target to apply the script.";
            IsError = true;
            return;
        }

        if (server.ReadOnly)
        {
            Status = $"\"{server.Name}\" is read-only.";
            IsError = true;
            return;
        }

        var included = Changes.Where(c => c.Include).Select(c => c.Change).ToList();
        if (included.Count == 0 || !await dialogs.ReviewSqlAsync($"Apply to {Target.Label}", Script, "Apply"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await using var session = await Target.OpenAsync();
            var statements = await SchemaApplier.RenderAsync(session, included, _targetSchema, _sourceSchema);
            await SchemaApplier.ApplyAsync(session, statements);
            Status = $"Applied {included.Count} change(s) to {Target.Label}.";
            IsError = false;
            _ = _explorer.GetCatalogAsync(server, Target.Database, refresh: true);
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
            IsError = true;
        }
        finally
        {
            IsBusy = false;
        }

        await CompareAsync();
    }

    partial void OnSelectedChangeChanged(ChangeItem? value)
    {
        // Selecting a difference does not change the script; the view scrolls to it.
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChangeItem.Include))
        {
            Render();
        }
    }

    private void Render()
    {
        var included = Changes.Where(c => c.Include).Select(c => c.Change).ToList();
        if (included.Count == 0)
        {
            Script = Changes.Count == 0 ? "-- The schemas match." : "-- No differences selected.";
            return;
        }

        var provider = Target.UseFile ? Source.Provider : Target.Provider;
        var header = $"-- Make {Target.Label} match {Source.Label}\n-- {Summary}\n\n";
        Script = header + AlterScriptGenerator.For(provider).Script(included, _targetSchema, _sourceSchema);
    }

    private async Task<string?> BrowseAsync() =>
        _explorer.Dialogs is { } dialogs ? await dialogs.OpenFileAsync("Schema file", ["json", "sql"]) : null;
}
