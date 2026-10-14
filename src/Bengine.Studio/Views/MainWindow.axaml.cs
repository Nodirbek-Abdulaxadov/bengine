namespace Bengine.Studio.Views;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Bengine.Introspection.History;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Tree;
using Bengine.Studio.Controls;
using Bengine.Studio.ViewModels;

/// <summary>
/// The Studio main window. Hosts the Object Explorer and the workspace, builds
/// the tree's context menu, implements <see cref="IDialogService"/> so view
/// models can ask for passwords, confirmations, files, and parameters, and
/// runs the keymap's actions — from shortcuts (rebindable in the Keymap
/// dialog) or the Find Action palette (Ctrl+Shift+A).
/// </summary>
public partial class MainWindow : Window, IDialogService
{
    private static readonly HashSet<string> RelationKinds = ["Table", "View", "MaterializedView", "ForeignTable"];

    private readonly ServerStore _store;
    private readonly TreeView _tree;
    private readonly TreeView _fileTree;

    /// <summary>Creates the window (for the designer).</summary>
    public MainWindow()
        : this(new ServerStore())
    {
    }

    /// <summary>Creates the window over a server store.</summary>
    /// <param name="store">The registered-server list.</param>
    public MainWindow(ServerStore store)
    {
        _store = store;
        AvaloniaXamlLoader.Load(this);
        _tree = this.FindControl<TreeView>("Tree")!;
        _tree.ContextRequested += OnTreeContextRequested;
        _tree.DoubleTapped += OnTreeDoubleTapped;
        _tree.KeyDown += OnTreeKeyDown;
        _fileTree = this.FindControl<TreeView>("FileTree")!;
        _fileTree.DoubleTapped += (_, e) => OpenFile(e.Source);
        _fileTree.KeyDown += OnFileTreeKeyDown;
        _fileTree.ContextRequested += OnFileTreeContextRequested;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private ExplorerViewModel? ViewModel => DataContext as ExplorerViewModel;

    /// <inheritdoc />
    public async Task<PasswordAnswer?> AskPasswordAsync(ServerProfile server, string? error) =>
        await new PasswordDialog(server, error).ShowDialog<PasswordAnswer?>(this);

    /// <inheritdoc />
    public async Task<ServerProfile?> EditServerAsync(ServerProfile? server)
    {
        var form = new ServerFormViewModel(_store, server);
        await new ServerDialog { DataContext = form }.ShowDialog(this);
        return form.Saved;
    }

    /// <inheritdoc />
    public async Task<int> ImportFromPgAdminAsync() => await new ImportDialog(_store).ShowDialog<int>(this);

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message) =>
        await new ConfirmDialog(title, message).ShowDialog<bool>(this);

    /// <inheritdoc />
    public async Task<string?> SaveFileAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task<string?> OpenFileAsync(string title, IReadOnlyList<string> extensions)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = extensions.Count == 0
                ? null
                : [new FilePickerFileType(string.Join(", ", extensions)) { Patterns = extensions.Select(e => "*." + e).ToList() }, FilePickerFileTypes.All],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>?> AskParametersAsync(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> previous) =>
        await new ParametersDialog(names, previous).ShowDialog<IReadOnlyDictionary<string, string>?>(this);

    /// <inheritdoc />
    public async Task<bool> ReviewSqlAsync(string title, string sql, string? confirmText) =>
        await new ReviewSqlDialog(title, sql, confirmText, ProviderOfActiveTab()).ShowDialog<bool>(this);

    /// <inheritdoc />
    public async Task<string?> EditValueAsync(string title, string? value, bool readOnly) =>
        await new ValueDialog(title, value, readOnly).ShowDialog<string?>(this);

    /// <inheritdoc />
    public async Task<string?> PromptAsync(string title, string label, string initial) =>
        await new PromptDialog(title, label, initial).ShowDialog<string?>(this);

    /// <inheritdoc />
    public async Task<QueryHistoryEntry?> ShowHistoryAsync(QueryHistory history, string? serverId) =>
        await new HistoryDialog(history, serverId).ShowDialog<QueryHistoryEntry?>(this);

    /// <inheritdoc />
    public async Task<bool> EditTableAsync(TableEditorViewModel editor) =>
        await new TableEditorDialog { DataContext = editor }.ShowDialog<bool>(this);

    /// <inheritdoc />
    public async Task<bool> ImportDataAsync(ImportDataViewModel import) =>
        await new ImportDataDialog { DataContext = import }.ShowDialog<bool>(this);

    /// <inheritdoc />
    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task<string?> ShowLocalHistoryAsync(string title, IReadOnlyList<Bengine.Introspection.Projects.HistoryVersion> versions, string current) =>
        await new LocalHistoryDialog(title, versions, current, ProviderOfActiveTab()).ShowDialog<string?>(this);

    /// <inheritdoc />
    public async Task<Bengine.Introspection.Sql.Usage?> ShowUsagesAsync(string title, IReadOnlyList<Bengine.Introspection.Sql.Usage> usages) =>
        await new UsagesDialog(title, usages).ShowDialog<Bengine.Introspection.Sql.Usage?>(this);

    /// <inheritdoc />
    public Task ShowBackupAsync(BackupViewModel backup) => new BackupDialog { DataContext = backup }.ShowDialog(this);

    /// <inheritdoc />
    public Task SetClipboardAsync(string text) => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

    /// <inheritdoc />
    public async Task<string?> GetClipboardAsync() => Clipboard is { } clipboard ? await clipboard.GetTextAsync() : null;

    private Bengine.Enums.DatabaseProvider ProviderOfActiveTab() => ViewModel?.ActiveTab switch
    {
        QueryDocumentViewModel q => q.Provider,
        DataDocumentViewModel d => d.Provider,
        _ => ViewModel?.SelectedItem?.Server?.Provider ?? Bengine.Enums.DatabaseProvider.PostgreSql,
    };

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DocumentViewModel doc)
        {
            doc.Close?.Invoke();
        }

        e.Handled = true;
    }

    private async void OnGoTo(object? sender, RoutedEventArgs e) => await GoToAsync();

    private async Task GoToAsync()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var hit = await new GoToDialog(vm).ShowDialog<ObjectHit?>(this);
        if (hit is null)
        {
            return;
        }

        if (await vm.RevealAsync(hit) is { } item)
        {
            _tree.ScrollIntoView(item);
        }

        vm.OpenData(hit.Server, hit.Database, hit.Relation.Ref);
    }

    // Keys that work wherever the focus is; editor and tree keys are handled where they apply.
    private static readonly string[] WindowActions =
    [
        "actions.find", "keymap.edit", "goto.object", "tab.close", "history.show", "compare.schema", "compare.data",
        "file.open", "file.save", "file.saveAs", "file.newScratch", "file.localHistory", "query.new",
    ];

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        foreach (var id in WindowActions)
        {
            if (Keymap.Current.Matches(id, e))
            {
                e.Handled = RunAction(id);
                return;
            }
        }
    }

    /// <summary>Runs an action of the keymap (from a shortcut or the action palette).</summary>
    /// <param name="id">The action id.</param>
    /// <returns><c>true</c> when the action applied here.</returns>
    public bool RunAction(string id)
    {
        if (ViewModel is not { } vm)
        {
            return false;
        }

        var query = vm.ActiveTab as QueryDocumentViewModel;
        var view = query is null ? null : this.GetVisualDescendants().OfType<QueryView>().FirstOrDefault(v => v.DataContext == query);
        var item = vm.SelectedItem;
        bool Do(Action action)
        {
            action();
            return true;
        }

        switch (id)
        {
            case "actions.find": return Do(() => _ = FindActionAsync());
            case "keymap.edit": return Do(() => _ = EditKeymapAsync());
            case "goto.object": return Do(() => _ = GoToAsync());
            case "tab.close" when vm.ActiveTab is { CanClose: true } tab: return Do(() => tab.Close?.Invoke());
            case "history.show" when query is not null: return Do(() => query.ShowHistoryCommand.Execute(null));
            case "history.show": return Do(() => vm.ShowHistoryCommand.Execute(null));
            case "compare.schema": return Do(() => vm.CompareCommand.Execute(null));
            case "compare.data": return Do(() => _ = vm.OpenDataCompareAsync(item is { Kind: "Table" or "View" } ? item : null));
            case "file.open": return Do(() => vm.FilesPanel.OpenFileCommand.Execute(null));
            case "file.save" when query is not null: return Do(() => _ = query.SaveAsync());
            case "file.saveAs" when query is not null: return Do(() => _ = query.SaveAsAsync());
            case "file.newScratch": return Do(() => vm.FilesPanel.NewScratchCommand.Execute(null));
            case "file.localHistory" when query is not null: return Do(() => query.ShowLocalHistoryCommand.Execute(null));
            case "query.new": return Do(() => vm.QueryToolCommand.Execute(null));
            case "query.run" when view is not null: return Do(view.Run);
            case "query.runScript" when view is not null: return Do(view.RunScript);
            case "query.cancel" when query is not null: return Do(() => query.CancelCommand.Execute(null));
            case "query.explain" when view is not null: return Do(() => view.Explain(analyze: false));
            case "query.explainAnalyze" when view is not null: return Do(() => view.Explain(analyze: true));
            case "query.commit" when query is not null: return Do(() => query.CommitCommand.Execute(null));
            case "query.rollback" when query is not null: return Do(() => query.RollbackCommand.Execute(null));
            case "editor.complete" when view is not null: return Do(() => view.EditorControl.ShowCompletion());
            case "editor.format" when view is not null: return Do(view.EditorControl.FormatSql);
            case "editor.comment" when view is not null: return Do(view.EditorControl.ToggleLineComment);
            case "editor.duplicate" when view is not null: return Do(view.EditorControl.DuplicateLine);
            case "editor.toggleCase" when view is not null: return Do(view.EditorControl.ToggleCase);
            case "editor.replace" when view is not null: return Do(view.EditorControl.OpenReplace);
            case "tree.filter": return Do(() => this.FindControl<TextBox>("TreeFilter")?.Focus());
            case "tree.refresh": return Do(() => vm.RefreshCommand.Execute(null));
            case "object.rename" when item is not null && ExplorerViewModel.CanRename(item): return Do(() => _ = vm.RenameAsync(item));
            case "object.drop" when item is not null && ExplorerViewModel.CanDrop(item): return Do(() => _ = vm.DropAsync(item));
            case "object.findUsages" when item is not null && ExplorerViewModel.CanFindUsages(item): return Do(() => _ = vm.ShowUsagesAsync(item));
            case "object.ddl" when item is not null && RelationKinds.Contains(item.Kind): return Do(() => _ = vm.GenerateSqlAsync(item, "ddl"));
            default:
                vm.Status = $"{Keymap.Actions.FirstOrDefault(a => a.Id == id)?.Title ?? id}: not available here.";
                return false;
        }
    }

    private async Task FindActionAsync()
    {
        if (await new ActionPaletteDialog().ShowDialog<string?>(this) is { } id)
        {
            RunAction(id);
        }
    }

    private async Task EditKeymapAsync()
    {
        if (ViewModel is { } vm && await new KeymapDialog(vm.Settings.Shortcuts).ShowDialog<IReadOnlyDictionary<string, string>?>(this) is { } overrides)
        {
            vm.Settings.Shortcuts = new Dictionary<string, string>(overrides, StringComparer.Ordinal);
            Keymap.Current = new Keymap(vm.Settings.Shortcuts);
            try
            {
                vm.Settings.Save();
                vm.Status = $"Keymap saved ({overrides.Count} custom shortcut(s)).";
            }
            catch (IOException ex)
            {
                vm.Status = $"Keymap applied but not saved: {ex.Message}";
            }
        }
    }

    private static FileItem? FileFrom(object? source) =>
        (source as Control)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<FileItem>().FirstOrDefault();

    private void OpenFile(object? source)
    {
        if (FileFrom(source) is { IsDirectory: false } file && ViewModel is { } vm)
        {
            _ = vm.FilesPanel.OpenAsync(file);
        }
    }

    private void OnFileTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { FilesPanel.Selected: { } file } vm)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (file.IsDirectory)
            {
                file.IsExpanded = !file.IsExpanded;
            }
            else
            {
                _ = vm.FilesPanel.OpenAsync(file);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            _ = vm.FilesPanel.RenameAsync(file);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            _ = vm.FilesPanel.DeleteAsync(file);
            e.Handled = true;
        }
    }

    private void OnFileTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm || FileFrom(e.Source) is not { } file)
        {
            return;
        }

        vm.FilesPanel.Selected = file;
        var panel = vm.FilesPanel;
        var items = new List<Control>();
        if (file.IsDirectory)
        {
            items.Add(Menu("New SQL File…", () => panel.NewFileCommand.Execute(null)));
            items.Add(Menu("Refresh", panel.Refresh));
        }
        else
        {
            items.Add(Menu("Open", () => _ = panel.OpenAsync(file)));
            items.Add(Menu("Local History…", () => _ = panel.ShowHistoryAsync(file)));
            items.Add(new Separator());
            items.Add(new MenuItem { Header = "Rename…", InputGesture = KeyGesture.Parse("F2") });
            ((MenuItem)items[^1]).Click += (_, _) => _ = panel.RenameAsync(file);
            items.Add(new MenuItem { Header = "Delete…", InputGesture = KeyGesture.Parse("Delete") });
            ((MenuItem)items[^1]).Click += (_, _) => _ = panel.DeleteAsync(file);
            items.Add(Menu("Copy Path", () => _ = SetClipboardAsync(file.Path)));
        }

        new ContextMenu { ItemsSource = items }.Open(_fileTree);
        e.Handled = true;
    }

    private static TreeItemViewModel? ItemFrom(object? source) =>
        (source as Control)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<TreeItemViewModel>().FirstOrDefault();

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemFrom(e.Source) is { IsPlaceholder: false } item)
        {
            Activate(item);
        }
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel?.SelectedItem is not { } item)
        {
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            Activate(item);
            e.Handled = true;
            return;
        }

        foreach (var id in new[] { "tree.refresh", "object.drop", "object.rename", "object.findUsages", "tree.filter", "object.ddl" })
        {
            if (Keymap.Current.Matches(id, e))
            {
                e.Handled = RunAction(id);
                return;
            }
        }
    }

    private void Activate(TreeItemViewModel item)
    {
        if (RelationKinds.Contains(item.Kind))
        {
            ViewModel?.OpenData(item);
        }
        else if (item.Children.Count > 0)
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm || ItemFrom(e.Source) is not { IsPlaceholder: false } item)
        {
            return;
        }

        vm.SelectedItem = item;
        var menu = new ContextMenu { ItemsSource = BuildMenu(vm, item, this) };
        menu.Open(_tree);
        e.Handled = true;
    }

    private static List<Control> BuildMenu(ExplorerViewModel vm, TreeItemViewModel item, MainWindow window)
    {
        var items = new List<Control>();
        var readOnly = item.Server?.ReadOnly == true;
        void Add(string header, Action action, string? gesture = null, bool enabled = true)
        {
            var menuItem = Menu(header, action);
            menuItem.IsEnabled = enabled;
            if (gesture is not null)
            {
                menuItem.InputGesture = KeyGesture.Parse(gesture);
            }

            items.Add(menuItem);
        }

        void AddObjectActions()
        {
            if (ExplorerViewModel.CanFindUsages(item))
            {
                Add("Find Usages", () => _ = vm.ShowUsagesAsync(item), "Alt+F7");
            }

            if (ExplorerViewModel.CanRename(item))
            {
                Add("Rename…", () => _ = vm.RenameAsync(item), "F2", !readOnly);
            }

            if (ExplorerViewModel.CanDrop(item))
            {
                Add("Drop…", () => _ = vm.DropAsync(item), "Delete", !readOnly);
                if (item.Server?.Provider == Bengine.Enums.DatabaseProvider.PostgreSql && item.Kind is not ("Database" or "Column"))
                {
                    Add("Drop Cascade…", () => _ = vm.DropAsync(item, cascade: true), enabled: !readOnly);
                }
            }
        }

        switch (item.Kind)
        {
            case "ServerGroup":
                Add("Register Server…", () => vm.RegisterServerCommand.Execute(null));
                Add("Import Servers (pgAdmin / DataGrip)…", () => vm.ImportPgAdminCommand.Execute(null));
                Add("Refresh", vm.ReloadServers);
                break;
            case "Server":
                if (item.IsConnected)
                {
                    Add("Disconnect Server", () => vm.Disconnect(item));
                }
                else
                {
                    Add("Connect Server", () => item.IsExpanded = true);
                }

                Add("Query Tool", () => _ = vm.OpenQueryAsync(item.Server!, null), "Ctrl+Shift+Q");
                if (Bengine.Introspection.Activity.ServerActivity.IsSupported(item.Server!.Provider))
                {
                    Add("Server Activity", () => vm.OpenActivity(item.Server!));
                }

                Add("Refresh", () => _ = item.RefreshAsync(), "F5");
                items.Add(new Separator());
                Add("Properties…", () => _ = vm.EditServerAsync(item));
                Add("Remove Server", () => _ = vm.RemoveServerAsync(item));
                break;
            case "Database":
                Add("Query Tool", () => _ = vm.OpenQueryAsync(item.Server!, item.Node!.Ref.Database), "Ctrl+Shift+Q");
                Add("New Table…", () => _ = vm.NewTableAsync(item), enabled: !readOnly);
                Add("Import Data from File…", () => _ = vm.ImportDataAsync(item), enabled: !readOnly);
                Add("Compare With…", () => _ = vm.OpenCompareAsync(item));
                Add("ER Diagram", () => vm.OpenDiagram(item), "Ctrl+Alt+Shift+U");
                Add("Refresh", () => _ = item.RefreshAsync(), "F5");
                items.Add(new Separator());
                Add("Dump / Backup…", () => _ = vm.BackupAsync(item, restore: false));
                Add("Restore / Run SQL Script…", () => _ = vm.BackupAsync(item, restore: true), enabled: !readOnly);
                items.Add(new Separator());
                AddObjectActions();
                break;
            case var kind when RelationKinds.Contains(kind):
            {
                var r = item.Node!.Ref;
                Add("View/Edit Data", () => vm.OpenData(item), "Enter");
                Add("Query Tool", () => _ = vm.OpenQueryAsync(item.Server!, r.Database, SelectTop(item.Server!.Provider, r.Schema, r.Name!)));
                items.Add(new MenuItem
                {
                    Header = "SQL Generator",
                    InputGesture = KeyGesture.Parse("Ctrl+Alt+G"),
                    ItemsSource = new List<Control>
                    {
                        Menu("SELECT", () => _ = vm.GenerateSqlAsync(item, "select")),
                        Menu("INSERT", () => _ = vm.GenerateSqlAsync(item, "insert")),
                        Menu("UPDATE", () => _ = vm.GenerateSqlAsync(item, "update")),
                        Menu("DELETE", () => _ = vm.GenerateSqlAsync(item, "delete")),
                        Menu("DDL", () => _ = vm.GenerateSqlAsync(item, "ddl")),
                    },
                });
                Add("Copy DDL", async () =>
                {
                    var props = await ObjectExplorer.GetPropertiesAsync(item.Server!, vm.PasswordFor(item.Server!), r);
                    if (props.Sql is { } sql)
                    {
                        await window.SetClipboardAsync(sql);
                    }
                });
                Add("Refresh", () => _ = item.RefreshAsync(), "F5");
                items.Add(new Separator());
                Add("Compare Data With…", () => _ = vm.OpenDataCompareAsync(item));
                if (kind == "Table")
                {
                    Add("Modify Table…", () => _ = vm.ModifyTableAsync(item), "Ctrl+F6");
                    Add("Import Data from File…", () => _ = vm.ImportDataAsync(item), enabled: !readOnly);
                    Add("Truncate…", () => _ = vm.TruncateAsync(item), enabled: !readOnly);
                }

                AddObjectActions();
                items.Add(new Separator());
                Add("Copy Name", () => _ = window.SetClipboardAsync(r.Name ?? item.Label));
                break;
            }

            default:
                if (item.Kind is "TablesFolder" or "Schema")
                {
                    Add("New Table…", () => _ = vm.NewTableAsync(item), enabled: !readOnly);
                    Add("Import Data from File…", () => _ = vm.ImportDataAsync(item), enabled: !readOnly);
                }

                if (item.Kind == "Schema")
                {
                    Add("Compare With…", () => _ = vm.OpenCompareAsync(item));
                    Add("ER Diagram", () => vm.OpenDiagram(item), "Ctrl+Alt+Shift+U");
                }

                Add("Refresh", () => _ = item.RefreshAsync(), "F5");
                AddObjectActions();
                if (item.Node?.Ref.Name is { } name)
                {
                    Add("Copy Name", () => _ = window.SetClipboardAsync(name));
                }

                break;
        }

        return items;
    }

    // A provider-correct "first 100 rows" query for the Query Tool.
    private static string SelectTop(Bengine.Enums.DatabaseProvider provider, string? schema, string name)
    {
        var target = Bengine.Introspection.Sql.ObjectSql.Qualified(provider, schema, name);
        return provider == Bengine.Enums.DatabaseProvider.SqlServer
            ? $"SELECT TOP 100 * FROM {target};"
            : $"SELECT * FROM {target}\nLIMIT 100;";
    }

    private static MenuItem Menu(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
