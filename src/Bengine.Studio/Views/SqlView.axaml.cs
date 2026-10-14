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

    // The editor text is not bindable; refresh it when the SQL changes.
    private void Render()
    {
        if (this.FindControl<SqlEditor>("Code") is not { } code)
        {
            return;
        }

        code.Provider = _bound?.Provider ?? Bengine.Enums.DatabaseProvider.PostgreSql;
        code.Text = _bound?.Sql ?? string.Empty;
        code.IsVisible = _bound?.Sql is not null;
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
