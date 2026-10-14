namespace Bengine.Studio.Views;

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Bengine.Studio.Controls;
using Bengine.Studio.ViewModels;

/// <summary>The Modify Table / New Table dialog; returns <c>true</c> when the table was changed.</summary>
public partial class TableEditorDialog : Window
{
    private TableEditorViewModel? _bound;

    /// <summary>Creates the dialog.</summary>
    public TableEditorDialog()
    {
        AvaloniaXamlLoader.Load(this);
        foreach (var name in new[] { "ColumnsGrid", "IndexesGrid", "KeysGrid", "ChecksGrid" })
        {
            this.FindControl<DataGrid>(name)!.CellEditEnded += (_, _) => _bound?.Refresh();
        }

        DataContextChanged += (_, _) => Bind();
    }

    private SqlEditor Preview => this.FindControl<SqlEditor>("PreviewEditor")!;

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnPropertyChanged;
        }

        _bound = DataContext as TableEditorViewModel;
        if (_bound is null)
        {
            return;
        }

        _bound.PropertyChanged += OnPropertyChanged;
        _bound.Closed += changed => Close(changed);
        Preview.Provider = _bound.Design.Provider;
        Preview.Text = _bound.Preview;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableEditorViewModel.Preview) && _bound is not null)
        {
            Preview.Text = _bound.Preview;
        }
    }

    private void OnChanged(object? sender, SelectionChangedEventArgs e) => _bound?.Refresh();
}
