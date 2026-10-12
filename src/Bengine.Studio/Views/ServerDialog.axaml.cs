namespace Bengine.Studio.Views;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Bengine.Studio.ViewModels;

/// <summary>The Register/Edit Server dialog.</summary>
public partial class ServerDialog : Window
{
    /// <summary>Creates the dialog; it closes itself when its form saves.</summary>
    public ServerDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ServerFormViewModel form)
            {
                form.Completed += Close;
            }
        };
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
