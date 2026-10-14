namespace Bengine.Studio.Views;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Bengine.Introspection.Export;
using Bengine.Introspection.Model;
using Bengine.Studio.Controls;
using Bengine.Studio.ViewModels;

/// <summary>
/// One Query Tool result tab: a read-only grid with the result columns, a
/// footer with the row count and timing, and copy/export of the selection or
/// the whole result in several formats. Shift+Enter or a double-click opens
/// the cell value in a large viewer.
/// </summary>
public sealed class ResultView : UserControl
{
    private readonly DataGrid _grid;
    private readonly TextBlock _summary;
    private ResultSetViewModel? _bound;

    /// <summary>Creates the view.</summary>
    public ResultView()
    {
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Extended,
            HeadersVisibility = DataGridHeadersVisibility.All,
        };
        _grid.LoadingRow += (_, e) => e.Row.Header = (e.Row.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _grid.DoubleTapped += (_, _) => _ = ShowValueAsync();
        _grid.KeyDown += OnKeyDown;
        _grid.ContextMenu = BuildMenu();

        _summary = new TextBlock { Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center };
        var footer = new Border
        {
            Background = (IBrush?)Application.Current?.FindResource("Panel"),
            Padding = new Thickness(10, 3),
            Child = new DockPanel
            {
                Children =
                {
                    new Button { Classes = { "ghost" }, Content = "Export ▾", Flyout = ExportFlyout(), [DockPanel.DockProperty] = Dock.Right },
                    new ToggleButton
                    {
                        Classes = { "tool" }, Content = "📌", Margin = new Thickness(0, 0, 6, 0),
                        [ToolTip.TipProperty] = "Pin this result (keep it on the next run)",
                        [!ToggleButton.IsCheckedProperty] = new Avalonia.Data.Binding(nameof(ResultSetViewModel.IsPinned)) { Mode = Avalonia.Data.BindingMode.TwoWay },
                        [DockPanel.DockProperty] = Dock.Right,
                    },
                    _summary,
                },
            },
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel { Children = { footer, _grid } };
        DataContextChanged += (_, _) => Bind();
    }

    private void Bind()
    {
        _bound = DataContext as ResultSetViewModel;
        if (_bound is null)
        {
            return;
        }

        GridColumns.Build(_grid, _bound.Columns, _bound.ColumnTypes);
        _grid.ItemsSource = _bound.Rows;
        _summary.Text = _bound.Summary;
    }

    private IReadOnlyList<RowItem> Selected() =>
        _grid.SelectedItems.OfType<RowItem>().ToList() is { Count: > 0 } rows ? rows : _bound?.Rows.ToList() ?? [];

    private string Format(string format)
    {
        if (_bound is null)
        {
            return string.Empty;
        }

        var rows = Selected();
        var values = rows.Select(r => r.CurrentValues()).ToList();
        return format switch
        {
            "csv" => DataExporter.ToCsv(_bound.Columns, values),
            "json" => DataExporter.ToJson(_bound.Columns, values),
            "markdown" => DataExporter.ToMarkdown(_bound.Columns, values),
            "sql" => DataExporter.ToSqlInserts(Dialects.Provider(this), new TableRef("result"), _bound.Columns, values),
            _ => DataExporter.ToTsv(_bound.Columns, values),
        };
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && e.Key == Key.C)
        {
            e.Handled = true;
            await CopyAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "tsv" : "cell");
        }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            await ShowValueAsync();
        }
    }

    private async Task CopyAsync(string format)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || _bound is null)
        {
            return;
        }

        if (format == "cell")
        {
            var rows = _grid.SelectedItems.OfType<RowItem>().ToList();
            var column = _grid.CurrentColumn?.DisplayIndex ?? -1;
            if (rows.Count == 1 && column >= 0)
            {
                await clipboard.SetTextAsync(RowItem.Format(rows[0].CurrentValue(IndexOf(column))));
                return;
            }

            format = "tsv";
        }

        await clipboard.SetTextAsync(Format(format));
    }

    // DisplayIndex follows column reordering; the column's tag is its name.
    private int IndexOf(int displayIndex)
    {
        var column = _grid.Columns.FirstOrDefault(c => c.DisplayIndex == displayIndex);
        return column is null || _bound is null ? displayIndex : _bound.Columns.ToList().IndexOf((string)column.Tag!);
    }

    private async Task ShowValueAsync()
    {
        if (_grid.SelectedItem is not RowItem row || _grid.CurrentColumn is not { } column || _bound is null
            || TopLevel.GetTopLevel(this) is not IDialogService dialogs)
        {
            return;
        }

        var index = _bound.Columns.ToList().IndexOf((string)column.Tag!);
        await dialogs.EditValueAsync(_bound.Columns[index], row[index], readOnly: true);
    }

    private ContextMenu BuildMenu()
    {
        MenuItem Item(string header, Action action, string? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGesture = gesture is null ? null : KeyGesture.Parse(gesture) };
            item.Click += (_, _) => action();
            return item;
        }

        return new ContextMenu
        {
            ItemsSource = new Control[]
            {
                Item("Copy", () => _ = CopyAsync("cell"), "Ctrl+C"),
                new MenuItem
                {
                    Header = "Copy As",
                    ItemsSource = new Control[]
                    {
                        Item("TSV (with header)", () => _ = CopyAsync("tsv"), "Ctrl+Shift+C"),
                        Item("CSV", () => _ = CopyAsync("csv")),
                        Item("JSON", () => _ = CopyAsync("json")),
                        Item("Markdown", () => _ = CopyAsync("markdown")),
                        Item("SQL INSERT", () => _ = CopyAsync("sql")),
                    },
                },
                Item("View Value…", () => _ = ShowValueAsync(), "Shift+Enter"),
                new Separator(),
                Item("Select All", () => _grid.SelectAll(), "Ctrl+A"),
            },
        };
    }

    private MenuFlyout ExportFlyout()
    {
        var flyout = new MenuFlyout();
        foreach (var (label, format, extension) in new[] { ("CSV…", "csv", "csv"), ("TSV…", "tsv", "tsv"), ("JSON…", "json", "json"), ("Markdown…", "markdown", "md"), ("SQL INSERTs…", "sql", "sql"), ("Excel (.xlsx)…", "xlsx", "xlsx") })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) =>
            {
                if (_bound is null || TopLevel.GetTopLevel(this) is not IDialogService dialogs || await dialogs.SaveFileAsync($"result.{extension}") is not { } path)
                {
                    return;
                }

                var values = _bound.Values();
                if (format == "xlsx")
                {
                    await File.WriteAllBytesAsync(path, XlsxWriter.Write("Result", _bound.Columns, values));
                }
                else
                {
                    _grid.SelectedItems.Clear();
                    await File.WriteAllTextAsync(path, Format(format));
                }
            };
            flyout.Items.Add(item);
        }

        return flyout;
    }
}

/// <summary>Finds the provider of the Query Tool hosting a control.</summary>
internal static class Dialects
{
    public static Bengine.Enums.DatabaseProvider Provider(Control control)
    {
        for (Visual? v = control; v is not null; v = v.GetVisualParent())
        {
            if (v is Control { DataContext: QueryDocumentViewModel query })
            {
                return query.Provider;
            }
        }

        return Bengine.Enums.DatabaseProvider.PostgreSql;
    }

    private static Visual? GetVisualParent(this Visual visual) => Avalonia.VisualTree.VisualExtensions.GetVisualParent(visual);
}
