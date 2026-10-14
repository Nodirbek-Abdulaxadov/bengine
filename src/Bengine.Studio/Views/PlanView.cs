namespace Bengine.Studio.Views;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Bengine.Studio.Controls;
using Bengine.Studio.ViewModels;

/// <summary>
/// An execution plan: the operations as an indented grid with cost, estimated
/// and actual rows, loops, time, and a bar for each operation's share; the
/// selected operation's conditions and warnings beside it; and the raw plan.
/// </summary>
public sealed class PlanView : UserControl
{
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#e0b341"));
    private static readonly IBrush BarBrush = new SolidColorBrush(Color.Parse("#e0862f"));

    /// <summary>Creates the view.</summary>
    public PlanView()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserSortColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            [!DataGrid.ItemsSourceProperty] = new Binding(nameof(PlanResultViewModel.Rows)),
            [!DataGrid.SelectedItemProperty] = new Binding(nameof(PlanResultViewModel.Selected)) { Mode = BindingMode.TwoWay },
        };
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Operation",
            Width = new DataGridLength(2, DataGridLengthUnitType.Star),
            MinWidth = 220,
            CellTemplate = new FuncDataTemplate<PlanRow>((_, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "⚠ ", Foreground = WarnBrush, [!IsVisibleProperty] = new Binding(nameof(PlanRow.HasWarning)) },
                    new TextBlock { FontFamily = (FontFamily)Application.Current!.FindResource("Mono")!, FontWeight = FontWeight.SemiBold, [!TextBlock.TextProperty] = new Binding(nameof(PlanRow.Operation)) },
                },
            }),
        });
        grid.Columns.Add(Text("Object", nameof(PlanRow.Object), new DataGridLength(1, DataGridLengthUnitType.Star)));
        grid.Columns.Add(Text("Cost", nameof(PlanRow.Cost), new DataGridLength(84), right: true));
        grid.Columns.Add(Text("Est. rows", nameof(PlanRow.Estimated), new DataGridLength(90), right: true));
        grid.Columns.Add(Text("Rows", nameof(PlanRow.Actual), new DataGridLength(84), right: true));
        grid.Columns.Add(Text("Loops", nameof(PlanRow.Loops), new DataGridLength(66), right: true));
        grid.Columns.Add(Text("Time ms", nameof(PlanRow.Time), new DataGridLength(84), right: true));
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Share",
            Width = new DataGridLength(150),
            CellTemplate = new FuncDataTemplate<PlanRow>((_, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Border { Height = 8, CornerRadius = new CornerRadius(2), Background = BarBrush, [!WidthProperty] = new Binding(nameof(PlanRow.Bar)) },
                    new TextBlock { FontSize = 11.5, Classes = { "dim" }, [!TextBlock.TextProperty] = new Binding(nameof(PlanRow.Percent)) },
                },
            }),
        });

        var notes = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = (FontFamily)Application.Current!.FindResource("Mono")!,
            FontSize = 12,
            Margin = new Thickness(12),
            [!TextBlock.TextProperty] = new Binding("Selected.Notes"),
        };
        var side = new Border
        {
            Width = 300,
            Background = (IBrush?)Application.Current?.FindResource("Panel"),
            Child = new ScrollViewer { Content = notes },
            [DockPanel.DockProperty] = Dock.Right,
        };

        var raw = new SqlEditor { IsReadOnly = true, HighlightStatement = false };
        var rawToggle = new ToggleButton { Content = "Raw", Classes = { "tool" }, [!ToggleButton.IsCheckedProperty] = new Binding(nameof(PlanResultViewModel.ShowRaw)) { Mode = BindingMode.TwoWay } };
        var summary = new TextBlock { Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), [!TextBlock.TextProperty] = new Binding(nameof(PlanResultViewModel.Summary)) };
        var header = new Border
        {
            Background = (IBrush?)Application.Current?.FindResource("Panel"),
            Padding = new Thickness(6, 3),
            Child = new DockPanel { Children = { new Border { [DockPanel.DockProperty] = Dock.Right, Child = rawToggle }, summary } },
            [DockPanel.DockProperty] = Dock.Top,
        };
        var body = new DockPanel { Children = { side, grid } };
        Content = new DockPanel { Children = { header, new Panel { Children = { body, raw } } } };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is PlanResultViewModel plan)
            {
                raw.Text = plan.Plan.Raw;
                raw.IsVisible = plan.ShowRaw;
                body.IsVisible = !plan.ShowRaw;
                plan.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PlanResultViewModel.ShowRaw))
                    {
                        raw.IsVisible = plan.ShowRaw;
                        body.IsVisible = !plan.ShowRaw;
                    }
                };
            }
        };
    }

    private static DataGridTemplateColumn Text(string header, string path, DataGridLength width, bool right = false) => new()
    {
        Header = header,
        Width = width,
        CellTemplate = new FuncDataTemplate<PlanRow>((_, _) => new TextBlock
        {
            Margin = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = right ? new SolidColorBrush(Color.Parse("#f7b98c")) : null,
            [!TextBlock.TextProperty] = new Binding(path),
        }),
    };
}
