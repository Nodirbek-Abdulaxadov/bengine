namespace Bengine.Studio.Views;

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Bengine.Studio.Controls;
using Bengine.Studio.ViewModels;

/// <summary>The SQL tab: the selected object's DDL with syntax highlighting.</summary>
public partial class SqlView : UserControl
{
    private SqlDocumentViewModel? _bound;

    /// <summary>Creates the view.</summary>
    public SqlView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Bind();
    }

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnChanged;
        }

        _bound = DataContext as SqlDocumentViewModel;
        if (_bound is not null)
        {
            _bound.PropertyChanged += OnChanged;
            Render();
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlDocumentViewModel.Sql))
        {
            Render();
        }
    }

    // Inlines are not bindable; rebuild the highlighted runs when the SQL changes.
    private void Render()
    {
        var code = this.FindControl<SelectableTextBlock>("Code");
        if (code is null)
        {
            return;
        }

        code.Inlines = _bound?.Sql is { } sql ? SqlHighlighter.Highlight(sql) : null;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (_bound?.Sql is { } sql && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(sql);
        }
    }

    private void OnOpenInQuery(object? sender, RoutedEventArgs e)
    {
        if (_bound?.Sql is { } sql)
        {
            _bound.OpenInQueryTool?.Invoke(sql);
        }
    }
}
