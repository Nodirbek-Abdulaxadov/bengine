namespace Bengine.Studio.Controls;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Bengine.Studio.ViewModels;

/// <summary>
/// Builds the runtime columns of the result and data grids: one column per
/// result column, bound to <see cref="RowItem"/> by position, with the column's
/// database type under its name, key/foreign-key marks, numbers right-aligned
/// in amber, <c>&lt;null&gt;</c> dimmed, and edited cells tinted.
/// </summary>
public static class GridColumns
{
    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "smallint", "integer", "bigint", "int", "int2", "int4", "int8", "tinyint", "mediumint", "numeric", "decimal",
        "real", "double precision", "double", "float", "money", "oid", "smallmoney", "float4", "float8",
    };

    private static readonly IBrush NumberBrush = new SolidColorBrush(Color.Parse("#f7b98c"));
    private static readonly IBrush NullBrush = new SolidColorBrush(Color.Parse("#5d6a7d"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#d6deeb"));
    private static readonly IBrush ModifiedBrush = new SolidColorBrush(Color.Parse("#1d3557"));
    private static readonly IBrush TypeBrush = new SolidColorBrush(Color.Parse("#5d6a7d"));

    /// <summary>Gets whether a database type is numeric.</summary>
    /// <param name="type">The type name.</param>
    /// <returns><c>true</c> for numeric types.</returns>
    public static bool IsNumeric(string type) => NumericTypes.Contains(type.Split('(')[0].Trim());

    /// <summary>Rebuilds a grid's columns.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="columns">The column names.</param>
    /// <param name="types">The type names.</param>
    /// <param name="primaryKey">Primary-key columns (marked with a key).</param>
    /// <param name="foreignKeys">Foreign-key columns (marked with a link).</param>
    /// <param name="editable">Whether cells can be edited.</param>
    public static void Build(
        DataGrid grid,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> types,
        IReadOnlyList<string>? primaryKey = null,
        IReadOnlyList<string>? foreignKeys = null,
        bool editable = false)
    {
        grid.Columns.Clear();
        for (var i = 0; i < columns.Count; i++)
        {
            var type = i < types.Count ? types[i] : string.Empty;
            var numeric = IsNumeric(type);
            var isKey = primaryKey?.Contains(columns[i], StringComparer.OrdinalIgnoreCase) == true;
            var isLink = foreignKeys?.Contains(columns[i], StringComparer.OrdinalIgnoreCase) == true;
            var index = i;
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = Header(columns[i], type, isKey, isLink),
                Tag = columns[i],
                CellTemplate = new FuncDataTemplate<RowItem>((_, _) => Cell(index, numeric)),
                CellEditingTemplate = editable ? new FuncDataTemplate<RowItem>((_, _) => Editor(index)) : null,
                IsReadOnly = !editable,
                Width = DataGridLength.Auto,
                MaxWidth = 460,
                MinWidth = 50,
                SortMemberPath = columns[i],
                CanUserSort = true,
            });
        }
    }

    private static Control Cell(int index, bool numeric)
    {
        var text = new TextBlock
        {
            Margin = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            HorizontalAlignment = numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            [!TextBlock.TextProperty] = new Binding($"[{index}]") { Converter = SingleLine },
            [!TextBlock.ForegroundProperty] = new Binding($"Flags[{index}]") { Converter = numeric ? NumberForeground : TextForeground },
            [!TextBlock.FontStyleProperty] = new Binding($"Flags[{index}]") { Converter = NullStyle },
        };

        return new Border
        {
            Child = text,
            [!Border.BackgroundProperty] = new Binding($"Flags[{index}]") { Converter = CellBackground },
        };
    }

    private static Control Editor(int index) => new TextBox
    {
        Padding = new Thickness(6, 2),
        MinHeight = 0,
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(0),
        VerticalContentAlignment = VerticalAlignment.Center,
        [!TextBox.TextProperty] = new Binding($"[{index}]") { Mode = BindingMode.TwoWay },
    };

    private static StackPanel Header(string name, string type, bool isKey, bool isLink) => new()
    {
        Spacing = 0,
        Children =
        {
            new TextBlock { Text = (isKey ? "🔑 " : isLink ? "🔗 " : string.Empty) + name, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = type, FontSize = 11, FontWeight = FontWeight.Normal, Foreground = TypeBrush, HorizontalAlignment = HorizontalAlignment.Left },
        },
    };

    private static readonly Avalonia.Data.Converters.IValueConverter SingleLine =
        new Avalonia.Data.Converters.FuncValueConverter<string?, string?>(v => v is { Length: > 0 } s && (s.Contains('\n') || s.Length > 600)
            ? s.ReplaceLineEndings(" ⏎ ")[..Math.Min(600, s.ReplaceLineEndings(" ⏎ ").Length)]
            : v);

    private static readonly Avalonia.Data.Converters.IValueConverter NumberForeground =
        new Avalonia.Data.Converters.FuncValueConverter<CellFlag, IBrush>(f => f.HasFlag(CellFlag.Null) || f.HasFlag(CellFlag.Default) ? NullBrush : NumberBrush);

    private static readonly Avalonia.Data.Converters.IValueConverter TextForeground =
        new Avalonia.Data.Converters.FuncValueConverter<CellFlag, IBrush>(f => f.HasFlag(CellFlag.Null) || f.HasFlag(CellFlag.Default) ? NullBrush : TextBrush);

    private static readonly Avalonia.Data.Converters.IValueConverter NullStyle =
        new Avalonia.Data.Converters.FuncValueConverter<CellFlag, FontStyle>(f => f.HasFlag(CellFlag.Null) || f.HasFlag(CellFlag.Default) ? FontStyle.Italic : FontStyle.Normal);

    private static readonly Avalonia.Data.Converters.IValueConverter CellBackground =
        new Avalonia.Data.Converters.FuncValueConverter<CellFlag, IBrush>(f => f.HasFlag(CellFlag.Modified) ? ModifiedBrush : Brushes.Transparent);
}
