namespace Bengine.Studio.Views;

using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Bengine.Studio.ViewModels;

/// <summary>
/// The View/Edit Data tab. A relation's shape is only known at runtime, so the
/// grid's columns are built in code — one editable text column per result
/// column, with the database type under the name and a key mark on primary-key
/// columns. Header clicks sort on the server; committed cell edits are saved.
/// </summary>
public partial class DataView : UserControl
{
    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "smallint", "integer", "bigint", "int", "int2", "int4", "int8", "tinyint", "mediumint", "numeric", "decimal",
        "real", "double precision", "double", "float", "money", "oid",
    };

    private static readonly IBrush NumberBrush = new SolidColorBrush(Color.Parse("#f7b98c"));

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
        _grid.CellEditEnded += (_, e) =>
        {
            if (_bound is not null && e.Row.DataContext is RowItem row && e.Column.Tag is string column)
            {
                _ = _bound.UpdateCellAsync(row, column);
            }
        };
        _grid.LoadingRow += (_, e) => e.Row.Header = (e.Row.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        DataContextChanged += (_, _) => Bind();
    }

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.ColumnsChanged -= Rebuild;
        }

        _bound = DataContext as DataDocumentViewModel;
        if (_bound is not null)
        {
            _bound.ColumnsChanged += Rebuild;
            if (_bound.Columns.Count > 0)
            {
                Rebuild(_bound.Columns.ToList(), [], []);
            }
        }
    }

    private void Rebuild(IReadOnlyList<string> columns, IReadOnlyList<string> types, IReadOnlyList<string> primaryKey)
    {
        _grid.Columns.Clear();
        for (var i = 0; i < columns.Count; i++)
        {
            var type = i < types.Count ? types[i] : string.Empty;
            var numeric = NumericTypes.Contains(type.Split('(')[0].Trim());
            var column = new DataGridTextColumn
            {
                Header = Header(columns[i], type, primaryKey.Contains(columns[i], StringComparer.OrdinalIgnoreCase)),
                Tag = columns[i],
                Binding = new Binding($"[{i}]") { Mode = BindingMode.TwoWay },
                Width = DataGridLength.Auto,
                MaxWidth = 420,
            };

            // Only override the color for numbers; a null brush would hide the text.
            if (numeric)
            {
                column.Foreground = NumberBrush;
            }

            _grid.Columns.Add(column);
        }
    }

    private static StackPanel Header(string name, string type, bool isKey) => new()
    {
        Spacing = 0,
        Children =
        {
            new TextBlock { Text = (isKey ? "🔑 " : string.Empty) + name, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = type, FontSize = 11, FontWeight = FontWeight.Normal, Foreground = new SolidColorBrush(Color.Parse("#5d6a7d")), HorizontalAlignment = HorizontalAlignment.Left },
        },
    };
}
