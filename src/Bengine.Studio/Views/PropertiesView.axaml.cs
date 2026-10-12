namespace Bengine.Studio.Views;

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Bengine.Studio.ViewModels;

/// <summary>The Properties tab: a property grid, or a children grid for a collection.</summary>
public partial class PropertiesView : UserControl
{
    private PropertiesDocumentViewModel? _bound;

    /// <summary>Creates the view.</summary>
    public PropertiesView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Bind();
    }

    // DataGrid column headers are not bindable; follow the view model by hand.
    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnChanged;
        }

        _bound = DataContext as PropertiesDocumentViewModel;
        if (_bound is not null)
        {
            _bound.PropertyChanged += OnChanged;
            ApplyHeaders();
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PropertiesDocumentViewModel.FirstHeader) or nameof(PropertiesDocumentViewModel.SecondHeader))
        {
            ApplyHeaders();
        }
    }

    private void ApplyHeaders()
    {
        var grid = this.FindControl<DataGrid>("Grid");
        if (grid is null || _bound is null)
        {
            return;
        }

        grid.Columns[0].Header = _bound.FirstHeader;
        grid.Columns[1].Header = _bound.SecondHeader;
    }
}
