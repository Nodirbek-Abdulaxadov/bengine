namespace Bengine.Studio.Views;

using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Bengine.Studio.ViewModels;

/// <summary>
/// The Query Tool tab: a SQL editor that runs the selection (or everything) on
/// F5 / Ctrl+Enter, and a result grid whose columns are built per result set
/// with each column's database type under its name.
/// </summary>
public partial class QueryView : UserControl
{
    private readonly DataGrid _grid;
    private readonly TextBox _editor;
    private QueryDocumentViewModel? _bound;

    /// <summary>Creates the view.</summary>
    public QueryView()
    {
        AvaloniaXamlLoader.Load(this);
        _grid = this.FindControl<DataGrid>("Grid")!;
        _editor = this.FindControl<TextBox>("Editor")!;
        _editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        _grid.LoadingRow += (_, e) => e.Row.Header = (e.Row.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        DataContextChanged += (_, _) => Bind();
    }

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.ColumnsChanged -= Rebuild;
        }

        _bound = DataContext as QueryDocumentViewModel;
        if (_bound is not null)
        {
            _bound.ColumnsChanged += Rebuild;
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 || (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            e.Handled = true;
            Run(explain: false);
        }
    }

    private void OnExecute(object? sender, RoutedEventArgs e) => Run(explain: false);

    private void OnExplain(object? sender, RoutedEventArgs e) => Run(explain: true);

    private void Run(bool explain)
    {
        if (_bound is not null)
        {
            _ = _bound.ExecuteAsync(_editor.SelectedText, explain);
        }
    }

    private void Rebuild(IReadOnlyList<string> columns, IReadOnlyList<string> types)
    {
        _grid.Columns.Clear();
        for (var i = 0; i < columns.Count; i++)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = columns[i], FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = i < types.Count ? types[i] : string.Empty, FontSize = 11, FontWeight = FontWeight.Normal, Foreground = new SolidColorBrush(Color.Parse("#5d6a7d")), HorizontalAlignment = HorizontalAlignment.Left },
                    },
                },
                Binding = new Binding($"[{i}]"),
                Width = DataGridLength.Auto,
                MaxWidth = 480,
            });
        }
    }
}
