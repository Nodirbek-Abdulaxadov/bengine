namespace Bengine.Studio.Views;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Bengine.Studio.Controls;
using Bengine.Studio.ViewModels;

/// <summary>The ER diagram tab: a toolbar (schema, views, keys-only, search, zoom, fit, layout, PNG) over the canvas.</summary>
public sealed class DiagramView : UserControl
{
    private readonly DiagramCanvas _canvas = new();
    private readonly TextBlock _zoom = new() { Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, Width = 46 };
    private DiagramDocumentViewModel? _bound;

    /// <summary>Creates the view.</summary>
    public DiagramView()
    {
        var schema = new ComboBox { MinWidth = 160, PlaceholderText = "all schemas", [!ComboBox.ItemsSourceProperty] = new Avalonia.Data.Binding("Schemas"), [!ComboBox.SelectedItemProperty] = new Avalonia.Data.Binding("Schema") { Mode = Avalonia.Data.BindingMode.TwoWay } };
        var views = new CheckBox { Content = "Views", [!ToggleButton.IsCheckedProperty] = new Avalonia.Data.Binding("IncludeViews") { Mode = Avalonia.Data.BindingMode.TwoWay } };
        var keys = new CheckBox { Content = "Keys only" };
        keys.IsCheckedChanged += (_, _) =>
        {
            _canvas.KeysOnly = keys.IsChecked == true;
            _canvas.Relayout();
            Dispatcher.UIThread.Post(_canvas.Fit, DispatcherPriority.Background);
        };
        var search = new TextBox { Width = 180, Watermark = "Find table…" };
        search.TextChanged += (_, _) => _canvas.Reveal(search.Text ?? string.Empty);
        var fit = Button("Fit", _canvas.Fit);
        var layout = Button("Re-layout", () =>
        {
            _canvas.Relayout();
            _canvas.Fit();
        });
        var zoomIn = Button("＋", () => _canvas.SetZoom(_canvas.Zoom * 1.25));
        var zoomOut = Button("－", () => _canvas.SetZoom(_canvas.Zoom / 1.25));
        var refresh = Button("⟳", () => _bound?.RefreshCommand.Execute(null));
        var png = Button("Export PNG…", () => _ = ExportAsync());
        var status = new TextBlock { Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, [!TextBlock.TextProperty] = new Avalonia.Data.Binding("Status") };

        var toolbar = new Border
        {
            Classes = { "toolbar" },
            Child = new DockPanel
            {
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, [DockPanel.DockProperty] = Dock.Left, Children = { schema, views, keys, search, zoomOut, _zoom, zoomIn, fit, layout, refresh, png } },
                    status,
                },
            },
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        Content = new DockPanel { Children = { toolbar, _canvas } };
        _canvas.ZoomChanged += zoom => _zoom.Text = $"{zoom * 100:0}%";
        _canvas.TableActivated += relation => _bound?.Open(relation);
        DataContextChanged += (_, _) => Bind();
    }

    /// <summary>Gets the canvas (for tests).</summary>
    public DiagramCanvas Canvas => _canvas;

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Classes = { "ghost" } };
        button.Click += (_, _) => action();
        return button;
    }

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.Changed -= Redraw;
        }

        _bound = DataContext as DiagramDocumentViewModel;
        if (_bound is not null)
        {
            _bound.Changed += Redraw;
            Redraw();
        }
    }

    private void Redraw()
    {
        if (_bound is null)
        {
            return;
        }

        _canvas.Show(_bound.Relations, _bound.Keys);
        Dispatcher.UIThread.Post(_canvas.Fit, DispatcherPriority.Background);
    }

    private async Task ExportAsync()
    {
        if (TopLevel.GetTopLevel(this) is not IDialogService dialogs || _bound is null
            || await dialogs.SaveFileAsync($"{_bound.Schema ?? _bound.Database ?? "diagram"}.png") is not { } path)
        {
            return;
        }

        await using var file = File.Create(path);
        _canvas.SavePng(file);
    }
}
