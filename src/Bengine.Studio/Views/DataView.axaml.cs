namespace Bengine.Studio.Views;

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Bengine.Studio.Controls;
using Bengine.Studio.ViewModels;

/// <summary>
/// The View/Edit Data tab. A relation's shape is only known at runtime, so the
/// grid's columns are built in code. Edits stay pending (tinted cells, green
/// new rows, red deleted rows) until Submit. Keyboard: Ctrl+Enter submit, F5
/// reload, Alt+Insert add, Ctrl+D clone, Ctrl+Delete delete, Ctrl+Alt+N
/// <c>NULL</c>, Ctrl+Alt+D <c>DEFAULT</c>, Shift+Enter value editor, Ctrl+C /
/// Ctrl+V copy and paste blocks, Ctrl+B follow the foreign key.
/// </summary>
public partial class DataView : UserControl
{
    private readonly DataGrid _grid;
    private DataDocumentViewModel? _bound;

    /// <summary>Creates the view.</summary>
    public DataView()
    {
        AvaloniaXamlLoader.Load(this);
        _grid = this.FindControl<DataGrid>("Grid")!;
        _grid.Sorting += (_, e) =>
        {
            e.Handled = true;
            if (e.Column.Tag is string column && _bound is not null)
            {
                _ = _bound.SortByAsync(column);
            }
        };
        _grid.CellEditEnded += (_, _) => _ = _bound?.CellEditedAsync();
        _grid.LoadingRow += OnLoadingRow;
        _grid.UnloadingRow += (_, e) =>
        {
            if (e.Row.DataContext is RowItem row)
            {
                row.PropertyChanged -= OnRowStateChanged;
            }
        };
        _grid.SelectionChanged += (_, _) => PushSelection();
        _grid.CurrentCellChanged += (_, _) => PushSelection();
        _grid.DoubleTapped += (_, e) =>
        {
            if (_bound is { CanEdit: false })
            {
                _ = EditValueAsync();
            }
        };
        _grid.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _grid.ContextRequested += OnContextRequested;
        DataContextChanged += (_, _) => Bind();
    }

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.ColumnsChanged -= Rebuild;
        }

        _bound = DataContext as DataDocumentViewModel;
        if (_bound is null)
        {
            return;
        }

        _bound.ColumnsChanged += Rebuild;
        _bound.Rows.CollectionChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Renumber, Avalonia.Threading.DispatcherPriority.Background);
        if (_bound.Columns.Count > 0)
        {
            Rebuild(_bound.Columns.ToList(), _bound.ColumnTypes, _bound.PrimaryKey, []);
        }
    }

    private void Rebuild(IReadOnlyList<string> columns, IReadOnlyList<string> types, IReadOnlyList<string> primaryKey, IReadOnlyList<string> foreignKeys) =>
        GridColumns.Build(_grid, columns, types, primaryKey, foreignKeys, editable: _bound?.CanEdit == true);

    // New rows show "+"; loaded rows their position among the loaded ones.
    private void Renumber()
    {
        foreach (var visual in _grid.GetVisualDescendants().OfType<DataGridRow>())
        {
            Number(visual);
        }
    }

    private void Number(DataGridRow visual)
    {
        if (visual.DataContext is RowItem { IsNew: true })
        {
            visual.Header = "+";
        }
        else if (_bound is not null && visual.DataContext is RowItem row)
        {
            var index = _bound.Rows.Where(r => !r.IsNew).ToList().IndexOf(row);
            visual.Header = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        Number(e.Row);
        if (e.Row.DataContext is RowItem row)
        {
            row.PropertyChanged -= OnRowStateChanged;
            row.PropertyChanged += OnRowStateChanged;
            Style(e.Row, row);
        }
    }

    private void OnRowStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RowItem.State) || sender is not RowItem row)
        {
            return;
        }

        foreach (var visual in _grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.DataContext == row))
        {
            Style(visual, row);
        }
    }

    private static void Style(DataGridRow visual, RowItem row)
    {
        visual.Classes.Set("inserted", row.State == RowState.Inserted);
        visual.Classes.Set("deleted", row.State == RowState.Deleted);
    }

    private int CurrentIndex() =>
        _bound is null || _grid.CurrentColumn?.Tag is not string name ? -1 : _bound.Columns.IndexOf(name);

    private void PushSelection() =>
        _bound?.SetSelection(_grid.SelectedItems.OfType<RowItem>().ToList(), CurrentIndex());

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_bound is null)
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var editing = e.Source is TextBox;

        switch (e.Key)
        {
            case Key.Enter when ctrl:
                _grid.CommitEdit();
                e.Handled = true;
                _bound.SubmitCommand.Execute(null);
                break;
            case Key.F5:
                e.Handled = true;
                _bound.RefreshCommand.Execute(null);
                break;
            case Key.Insert when alt:
                e.Handled = true;
                _bound.AddRowCommand.Execute(null);
                break;
            case Key.D when ctrl && !alt && !editing:
                e.Handled = true;
                _bound.CloneRowCommand.Execute(null);
                break;
            case Key.Delete when ctrl && !editing:
                e.Handled = true;
                _bound.DeleteRowsCommand.Execute(null);
                break;
            case Key.N when ctrl && alt:
                e.Handled = true;
                _grid.CancelEdit();
                _bound.SetNullCommand.Execute(null);
                break;
            case Key.D when ctrl && alt:
                e.Handled = true;
                _grid.CancelEdit();
                _bound.SetDefaultCommand.Execute(null);
                break;
            case Key.Z when ctrl && alt:
                e.Handled = true;
                _bound.RevertSelectedCommand.Execute(null);
                break;
            case Key.Enter when shift && !editing:
                e.Handled = true;
                await EditValueAsync();
                break;
            case Key.C when ctrl && !editing:
                e.Handled = true;
                await CopyAsync(shift ? "tsv" : "cell");
                break;
            case Key.V when ctrl && !editing:
                e.Handled = true;
                await PasteAsync();
                break;
            case Key.B when ctrl && !editing:
                e.Handled = true;
                if (_bound.Links.FirstOrDefault(l => l.Outgoing) is { } link && _grid.SelectedItem is RowItem row)
                {
                    _bound.Navigate(link, row);
                }

                break;
        }
    }

    private async Task EditValueAsync()
    {
        if (_bound is not null && _grid.SelectedItem is RowItem row && CurrentIndex() is var index and >= 0)
        {
            await _bound.EditValueAsync(row, index);
        }
    }

    private async Task CopyAsync(string format)
    {
        if (_bound is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        var rows = _grid.SelectedItems.OfType<RowItem>().ToList();
        if (format == "cell")
        {
            if (rows.Count == 1 && CurrentIndex() is var index and >= 0)
            {
                await clipboard.SetTextAsync(RowItem.Format(rows[0].CurrentValue(index)));
                return;
            }

            format = "tsv";
        }

        await clipboard.SetTextAsync(_bound.Format(format, rows.Count > 0 ? rows : null));
    }

    private async Task PasteAsync()
    {
        if (_bound is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || await clipboard.GetTextAsync() is not { Length: > 0 } text)
        {
            return;
        }

        var start = _grid.SelectedItem as RowItem ?? _bound.Rows.FirstOrDefault();
        if (start is not null)
        {
            _bound.Paste(text, start, Math.Max(0, CurrentIndex()));
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_bound is not { } vm)
        {
            return;
        }

        PushSelection();
        var row = _grid.SelectedItem as RowItem;
        var index = CurrentIndex();
        var items = new List<Control>();
        MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, InputGesture = gesture is null ? null : KeyGesture.Parse(gesture) };
            item.Click += (_, _) => action();
            return item;
        }

        MenuItem Group(string header, IEnumerable<Control> children, bool enabled = true) =>
            new() { Header = header, ItemsSource = children.ToList(), IsEnabled = enabled };

        items.Add(Item("Copy", () => _ = CopyAsync("cell"), "Ctrl+C"));
        items.Add(Group("Copy As", new[]
        {
            Item("TSV", () => _ = CopyAsync("tsv"), "Ctrl+Shift+C"),
            Item("CSV", () => _ = CopyAsync("csv")),
            Item("JSON", () => _ = CopyAsync("json")),
            Item("Markdown", () => _ = CopyAsync("markdown")),
            Item(vm.InsertLabel, () => _ = CopyAsync("sql-insert")),
            Item(vm.UpdateLabel, () => _ = CopyAsync("sql-update")),
            Item(vm.ConditionLabel, () => _ = CopyAsync("where")),
        }));
        items.Add(Item("Paste", () => _ = PasteAsync(), "Ctrl+V", vm.CanEdit));
        items.Add(new Separator());
        items.Add(Item("Edit Value…", () => _ = EditValueAsync(), "Shift+Enter", row is not null && index >= 0));
        items.Add(Item("Set NULL", () => vm.SetNullCommand.Execute(null), "Ctrl+Alt+N", vm.CanEdit && index >= 0));
        items.Add(Item(vm.DefaultLabel, () => vm.SetDefaultCommand.Execute(null), "Ctrl+Alt+D", vm.CanEdit && index >= 0));
        if (row is not null && index >= 0)
        {
            items.Add(Group("Filter by Value", new[]
            {
                Item($"= this value", () => _ = vm.FilterByValueAsync(row, index, "=")),
                Item($"<> this value", () => _ = vm.FilterByValueAsync(row, index, "<>")),
                Item($"< this value", () => _ = vm.FilterByValueAsync(row, index, "<")),
                Item($"> this value", () => _ = vm.FilterByValueAsync(row, index, ">")),
                Item(vm.IsMongo ? "contains (/value/i)" : "LIKE %value%", () => _ = vm.FilterByValueAsync(row, index, "LIKE")),
            }));
        }

        if (vm.Links.Count > 0 && row is not null)
        {
            items.Add(Group("Navigate", vm.Links.Select(link => Item(link.Label, () => vm.Navigate(link, row), link.Outgoing ? "Ctrl+B" : null))));
        }

        items.Add(new Separator());
        items.Add(Item("Add Row", () => vm.AddRowCommand.Execute(null), "Alt+Insert", vm.CanEdit));
        items.Add(Item("Clone Row", () => vm.CloneRowCommand.Execute(null), "Ctrl+D", vm.CanEdit && row is not null));
        items.Add(Item("Delete Row(s)", () => vm.DeleteRowsCommand.Execute(null), "Ctrl+Delete", vm.CanEdit && row is not null));
        items.Add(Item("Revert Selected", () => vm.RevertSelectedCommand.Execute(null), "Ctrl+Alt+Z", vm.HasPending));
        items.Add(new Separator());
        if (_grid.CurrentColumn is { } column)
        {
            items.Add(Item($"Hide Column \"{column.Tag}\"", () => column.IsVisible = false));
        }

        items.Add(Item("Show All Columns", () =>
        {
            foreach (var c in _grid.Columns)
            {
                c.IsVisible = true;
            }
        }, enabled: _grid.Columns.Any(c => !c.IsVisible)));

        new ContextMenu { ItemsSource = items }.Open(_grid);
        e.Handled = true;
    }
}
