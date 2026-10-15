namespace Bengine.Studio.Views;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private readonly Avalonia.Threading.DispatcherTimer _speedTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private (TreeItemViewModel Item, bool Expanded)? _pressed;
    private string _speed = string.Empty;

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
        _tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _tree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        _tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        _tree.AddHandler(TextInputEvent, OnTreeTextInput, RoutingStrategies.Tunnel);
        _speedTimer.Tick += (_, _) => EndSpeedSearch();
        _fileTree = this.FindControl<TreeView>("FileTree")!;
        _fileTree.DoubleTapped += (_, e) => OpenFile(e.Source);
        _fileTree.KeyDown += OnFileTreeKeyDown;
        _fileTree.ContextRequested += OnFileTreeContextRequested;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        PositionChanged += OnPositionChanged;
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
            case "object.open" when item is { IsPlaceholder: false }: return Do(() => Open(item));
            case "object.ddl" when item is { IsPlaceholder: false, IsFolder: false, Node: not null }: return Do(() => _ = vm.OpenDdlAsync(item));
            case "object.quickDoc" when item is { IsPlaceholder: false }: return Do(() => _ = ShowQuickDocAsync(item));
            case "object.console" when item?.Server is { } server: return Do(() => _ = vm.OpenQueryAsync(server, item.Node?.Ref.Database));
            case "object.copyReference" when item is { IsPlaceholder: false }: return Do(() => _ = SetClipboardAsync(ExplorerViewModel.ReferenceOf(item)));
            case "object.rename" when item is not null && ExplorerViewModel.CanRename(item): return Do(() => _ = vm.RenameAsync(item));
            case "object.drop" when item is not null && ExplorerViewModel.CanDrop(item): return Do(() => _ = vm.DropAsync(item));
            case "object.findUsages" when item is not null && ExplorerViewModel.CanFindUsages(item): return Do(() => _ = vm.ShowUsagesAsync(item));
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

    // The row's expanded state before a double-click, which TreeViewItem toggles on its own.
    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2 && ItemFrom(e.Source) is { } item)
        {
            _pressed = (item, item.IsExpanded);
        }

        EndSpeedSearch();
    }

    // DataGrip: a double-click opens tables and views (data), columns (their table), and other objects (DDL);
    // data sources, databases, schemas, and folders just expand.
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } vm && ItemFrom(e.Source) is { IsPlaceholder: false } item && vm.OpenItem(item))
        {
            if (_pressed is { } pressed && pressed.Item == item)
            {
                item.IsExpanded = pressed.Expanded;
            }

            e.Handled = true;
        }

        _pressed = null;
    }

    private void Open(TreeItemViewModel item)
    {
        if (ViewModel?.OpenItem(item) != true)
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (_speed.Length > 0 && e.Key is Key.Escape or Key.Back)
        {
            if (e.Key == Key.Back && _speed.Length > 1)
            {
                SpeedSearch(_speed[..^1]);
            }
            else
            {
                EndSpeedSearch();
            }

            e.Handled = true;
            return;
        }

        if (ViewModel?.SelectedItem is not { } item)
        {
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            EndSpeedSearch();
            Open(item);
            e.Handled = true;
            return;
        }

        foreach (var id in new[] { "object.open", "object.ddl", "object.quickDoc", "object.console", "object.copyReference", "tree.refresh", "object.drop", "object.rename", "object.findUsages", "tree.filter" })
        {
            if (Keymap.Current.Matches(id, e))
            {
                e.Handled = RunAction(id);
                return;
            }
        }
    }

    // Speed search: typing with the tree focused selects the first visible row that matches.
    private void OnTreeTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl) || (_speed.Length == 0 && string.IsNullOrWhiteSpace(e.Text)))
        {
            return;
        }

        SpeedSearch(_speed + e.Text);
        e.Handled = true;
    }

    private void SpeedSearch(string text)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        _speed = text;
        var rows = VisibleRows(vm.Roots).ToList();
        var match = rows.FirstOrDefault(r => r.Label.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(r => r.Label.Contains(text, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(r => Bengine.Introspection.Sql.SqlCompletion.MatchScore(r.Label, text) > 0);
        var box = this.FindControl<Border>("SpeedSearchBox")!;
        var label = this.FindControl<TextBlock>("SpeedSearchText")!;
        label.Text = "⌕ " + text + (match is null ? "   no match" : string.Empty);
        if (match is null)
        {
            label.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ff8f8f"));
        }
        else
        {
            label.ClearValue(TextBlock.ForegroundProperty);
        }
        box.IsVisible = true;
        if (match is not null)
        {
            vm.SelectedItem = match;
            _tree.ScrollIntoView(match);
            (_tree.TreeContainerFromItem(match) as Control)?.Focus();
        }

        _speedTimer.Stop();
        _speedTimer.Start();
    }

    private void EndSpeedSearch()
    {
        _speedTimer.Stop();
        _speed = string.Empty;
        if (this.FindControl<Border>("SpeedSearchBox") is { } box)
        {
            box.IsVisible = false;
        }
    }

    private static IEnumerable<TreeItemViewModel> VisibleRows(IEnumerable<TreeItemViewModel> rows)
    {
        foreach (var row in rows.Where(r => r.IsVisible && !r.IsPlaceholder))
        {
            yield return row;
            if (row.IsExpanded)
            {
                foreach (var child in VisibleRows(row.Children))
                {
                    yield return child;
                }
            }
        }
    }

    private void OnCollapseAll(object? sender, RoutedEventArgs e)
    {
        static void Collapse(IEnumerable<TreeItemViewModel> rows)
        {
            foreach (var row in rows)
            {
                Collapse(row.Children);
                if (row.Kind != "ServerGroup")
                {
                    row.IsExpanded = false;
                }
            }
        }

        if (ViewModel is { } vm)
        {
            Collapse(vm.Roots);
        }
    }

    /// <summary>Shows an object's Quick Documentation (Ctrl+Q) in a popup beside its row.</summary>
    /// <param name="item">The row.</param>
    /// <returns>The popup, once the documentation is loaded (closed by clicking elsewhere or Escape).</returns>
    public async Task<Flyout?> ShowQuickDocAsync(TreeItemViewModel item)
    {
        if (ViewModel is not { } vm)
        {
            return null;
        }

        var doc = vm.QuickDoc;
        var rows = new ItemsControl
        {
            ItemsSource = doc.Rows,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PropertyRow>((row, _) => row is null ? new TextBlock() : new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("170,*"),
                Margin = new Thickness(0, 1),
                Children =
                {
                    new TextBlock { Text = row.Name, Classes = { "dim" }, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) },
                    new TextBlock { [Grid.ColumnProperty] = 1, Text = row.Value ?? string.Empty, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                },
            }),
        };
        var message = new TextBlock { Classes = { "dim" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = "Loading…" };
        var ddl = new SqlEditor { IsReadOnly = true, HighlightStatement = false, ShowLineNumbers = false, WordWrap = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Height = 170, IsVisible = false };
        var header = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                new IconView { Kind = item.IconKind, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                new TextBlock { Text = string.Join("  ›  ", item.Breadcrumb()), FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 500 },
            },
        };
        var body = new StackPanel
        {
            Width = 560,
            Spacing = 8,
            Children =
            {
                header,
                message,
                new ScrollViewer { MaxHeight = 240, Content = rows },
                new Border { BorderBrush = (Avalonia.Media.IBrush?)Application.Current?.FindResource("Line"), BorderThickness = new Thickness(1), Child = ddl, IsVisible = false },
            },
        };
        var flyout = new Flyout { Content = body, Placement = PlacementMode.RightEdgeAlignedTop, ShowMode = FlyoutShowMode.Standard };
        flyout.FlyoutPresenterClasses.Add("quickdoc");
        flyout.ShowAt((_tree.TreeContainerFromItem(item) as Control) ?? _tree);
        await vm.LoadQuickDocAsync(item);
        message.Text = doc.Message;
        message.IsVisible = doc.Message is not null;
        if (doc.Sql is { } sql)
        {
            ddl.Provider = doc.Provider;
            ddl.Text = sql;
            ddl.IsVisible = true;
            ((Border)body.Children[^1]).IsVisible = true;
        }

        return flyout;
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
        var keys = Keymap.Current;
        var readOnly = item.Server?.ReadOnly == true;
        var kind = item.Kind;
        var r = item.Node?.Ref;
        var relation = RelationKinds.Contains(kind);
        MenuItem Item(string header, Action action, string? actionId = null, bool enabled = true, string? gesture = null)
        {
            var menuItem = Menu(header, action);
            menuItem.IsEnabled = enabled;
            menuItem.InputGesture = actionId is not null ? keys.MenuGesture(actionId) : gesture is null ? null : KeyGesture.Parse(gesture);
            return menuItem;
        }

        void Add(string header, Action action, string? actionId = null, bool enabled = true, string? gesture = null) =>
            items.Add(Item(header, action, actionId, enabled, gesture));
        void Separate()
        {
            if (items.Count > 0 && items[^1] is not Separator)
            {
                items.Add(new Separator());
            }
        }

        void Sub(string header, IEnumerable<Control> children)
        {
            var list = children.ToList();
            if (list.Count > 0)
            {
                items.Add(new MenuItem { Header = header, ItemsSource = list });
            }
        }

        if (kind == "ServerGroup")
        {
            Add("New Data Source…", () => vm.RegisterServerCommand.Execute(null));
            Add("Import Data Sources (pgAdmin / DataGrip)…", () => vm.ImportPgAdminCommand.Execute(null));
            Add("Refresh", vm.ReloadServers);
            return items;
        }

        var server = item.Server!;
        if (kind == "Server")
        {
            Sub("New", [Item("Query Console", () => _ = vm.OpenQueryAsync(server, null), "query.new"), Item("Data Source…", () => vm.RegisterServerCommand.Execute(null))]);
            Add(item.IsConnected ? "Disconnect" : "Connect", () =>
            {
                if (item.IsConnected)
                {
                    vm.Disconnect(item);
                }
                else
                {
                    item.IsExpanded = true;
                }
            });
            Add("Jump to Query Console", () => _ = vm.OpenQueryAsync(server, null), "object.console");
            if (Bengine.Introspection.Activity.ServerActivity.IsSupported(server.Provider))
            {
                Add("Server Activity", () => vm.OpenActivity(server));
            }

            Add("Refresh", () => _ = item.RefreshAsync(), "tree.refresh");
            Separate();
            Add("Data Source Properties…", () => _ = vm.EditServerAsync(item));
            Add("Remove Data Source…", () => _ = vm.RemoveServerAsync(item));
            return items;
        }

        if (kind == "Folder")
        {
            Add("Refresh", () => _ = item.RefreshAsync(), "tree.refresh");
            return items;
        }

        if (server.Provider == Bengine.Enums.DatabaseProvider.MongoDb)
        {
            BuildMongoMenu();
            return items;
        }

        // Open, navigate, document.
        if (relation)
        {
            Add("Edit Data", () => vm.OpenData(item), "object.open");
        }
        else if (kind == "Column")
        {
            Add("Edit Table Data", () => vm.OpenItem(item), "object.open");
        }

        if (!item.IsFolder && kind is not ("Database" or "Schema" or "Catalog"))
        {
            Add("Go to DDL", () => _ = vm.OpenDdlAsync(item), "object.ddl");
        }

        if (!item.IsFolder)
        {
            Add("Quick Documentation", () => _ = window.ShowQuickDocAsync(item), "object.quickDoc");
        }

        if (ExplorerViewModel.CanFindUsages(item))
        {
            Add("Find Usages", () => _ = vm.ShowUsagesAsync(item), "object.findUsages");
        }

        // Create and change.
        Separate();
        var news = new List<Control>();
        if (kind is "Database" or "Schema" or "TablesFolder")
        {
            news.Add(Item("Table…", () => _ = vm.NewTableAsync(item), enabled: !readOnly));
        }

        news.Add(Item("Query Console", () => _ = vm.OpenQueryAsync(server, r?.Database), "query.new"));
        Sub("New", news);
        if (kind == "Table")
        {
            Add("Modify Table…", () => _ = vm.ModifyTableAsync(item), gesture: "Ctrl+F6", enabled: !readOnly);
        }

        if (ExplorerViewModel.CanRename(item))
        {
            Add("Rename…", () => _ = vm.RenameAsync(item), "object.rename", !readOnly);
        }

        if (ExplorerViewModel.CanDrop(item))
        {
            Add("Drop…", () => _ = vm.DropAsync(item), "object.drop", !readOnly);
            if ((server.Provider == Bengine.Enums.DatabaseProvider.PostgreSql && kind is not ("Database" or "Column"))
                || (server.Provider == Bengine.Enums.DatabaseProvider.Oracle && kind is "Table" or "View" or "Type" or "Schema"))
            {
                Add("Drop Cascade…", () => _ = vm.DropAsync(item, cascade: true), enabled: !readOnly);
            }
        }

        if (kind == "Table")
        {
            Add("Truncate…", () => _ = vm.TruncateAsync(item), enabled: !readOnly);
        }

        // SQL and consoles.
        Separate();
        if (relation)
        {
            Sub("SQL Scripts",
            [
                Item("SELECT", () => _ = vm.GenerateSqlAsync(item, "select")),
                Item("INSERT", () => _ = vm.GenerateSqlAsync(item, "insert")),
                Item("UPDATE", () => _ = vm.GenerateSqlAsync(item, "update")),
                Item("DELETE", () => _ = vm.GenerateSqlAsync(item, "delete")),
                Item("DDL to Console", () => _ = vm.GenerateSqlAsync(item, "ddl")),
                Item("Copy DDL", async () =>
                {
                    var props = await ObjectExplorer.GetPropertiesAsync(server, vm.PasswordFor(server), r!);
                    if (props.Sql is { } sql)
                    {
                        await window.SetClipboardAsync(sql);
                    }
                }),
            ]);
        }

        Add("Jump to Query Console", () => _ = vm.OpenQueryAsync(server, r?.Database, relation ? SelectTop(server.Provider, r!.Schema, r.Name!) : null), "object.console");

        // Data and tools.
        var tools = new List<Control>();
        if (kind is "Database" or "Schema" or "TablesFolder" or "Table")
        {
            tools.Add(Item("Import Data from File…", () => _ = vm.ImportDataAsync(item), enabled: !readOnly));
        }

        if (kind == "Database")
        {
            tools.Add(Item("Dump / Backup…", () => _ = vm.BackupAsync(item, restore: false)));
            tools.Add(Item("Restore / Run SQL Script…", () => _ = vm.BackupAsync(item, restore: true), enabled: !readOnly));
        }

        if (kind is "Database" or "Schema")
        {
            tools.Add(Item("Compare Schemas With…", () => _ = vm.OpenCompareAsync(item)));
            tools.Add(Item("Show Diagram", () => vm.OpenDiagram(item), gesture: "Ctrl+Alt+Shift+U"));
        }

        if (relation)
        {
            tools.Add(Item("Compare Data With…", () => _ = vm.OpenDataCompareAsync(item)));
        }

        if (tools.Count > 0)
        {
            Separate();
            items.AddRange(tools);
        }

        Separate();
        Add("Copy Reference", () => _ = window.SetClipboardAsync(ExplorerViewModel.ReferenceOf(item)), "object.copyReference");
        Add("Refresh", () => _ = item.RefreshAsync(), "tree.refresh");
        return items;

        // MongoDB: collections, views, fields, and indexes; no SQL tools.
        void BuildMongoMenu()
        {
            if (relation)
            {
                Add("Edit Data", () => vm.OpenData(item), "object.open");
            }
            else if (kind == "Column")
            {
                Add("Edit Collection Data", () => vm.OpenItem(item), "object.open");
            }

            if (kind is "Table" or "View" or "Index")
            {
                Add("Go to DDL", () => _ = vm.OpenDdlAsync(item), "object.ddl");
            }

            if (!item.IsFolder)
            {
                Add("Quick Documentation", () => _ = window.ShowQuickDocAsync(item), "object.quickDoc");
            }

            Separate();
            var created = new List<Control>();
            if (kind is "Database" or "TablesFolder")
            {
                created.Add(Item("Collection…", () => _ = vm.NewCollectionAsync(item), enabled: !readOnly));
            }

            created.Add(Item("Query Console", () => _ = vm.OpenQueryAsync(server, r?.Database), "query.new"));
            Sub("New", created);
            if (ExplorerViewModel.CanRename(item))
            {
                Add("Rename…", () => _ = vm.RenameAsync(item), "object.rename", !readOnly);
            }

            if (ExplorerViewModel.CanDrop(item))
            {
                Add("Drop…", () => _ = vm.DropAsync(item), "object.drop", !readOnly);
            }

            if (kind == "Table")
            {
                Add("Delete All Documents…", () => _ = vm.TruncateAsync(item), enabled: !readOnly);
            }

            Separate();
            if (relation)
            {
                Sub("Shell Scripts",
                [
                    Item("find", () => _ = vm.GenerateSqlAsync(item, "select")),
                    Item("insertOne", () => _ = vm.GenerateSqlAsync(item, "insert")),
                    Item("updateOne", () => _ = vm.GenerateSqlAsync(item, "update")),
                    Item("deleteOne", () => _ = vm.GenerateSqlAsync(item, "delete")),
                    Item("DDL to Console", () => _ = vm.GenerateSqlAsync(item, "ddl")),
                ]);
            }

            Add("Jump to Query Console", () => _ = vm.OpenQueryAsync(server, r?.Database, relation ? SelectTopMongo(r!.Name!) : null), "object.console");
            Separate();
            Add("Copy Reference", () => _ = window.SetClipboardAsync(ExplorerViewModel.ReferenceOf(item)), "object.copyReference");
            Add("Refresh", () => _ = item.RefreshAsync(), "tree.refresh");
        }
    }

    // A provider-correct "first 100 rows" query for the Query Tool.
    private static string SelectTopMongo(string collection) =>
        $"{Bengine.Introspection.Mongo.MongoShell.Collection(collection)}.find({{}}).limit(100)";

    // A provider-correct "first 100 rows" query for the Query Tool.
    private static string SelectTop(Bengine.Enums.DatabaseProvider provider, string? schema, string name)
    {
        var target = Bengine.Introspection.Sql.ObjectSql.Qualified(provider, schema, name);
        return provider switch
        {
            Bengine.Enums.DatabaseProvider.SqlServer => $"SELECT TOP 100 * FROM {target};",
            Bengine.Enums.DatabaseProvider.Oracle => $"SELECT * FROM {target}\nFETCH FIRST 100 ROWS ONLY;",
            _ => $"SELECT * FROM {target}\nLIMIT 100;",
        };
    }

    private static MenuItem Menu(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
