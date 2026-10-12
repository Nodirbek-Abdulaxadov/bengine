namespace Bengine.Studio.Views;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

/// <summary>A yes/no confirmation; returns <c>true</c> when confirmed.</summary>
public partial class ConfirmDialog : Window
{
    /// <summary>Creates the dialog (for the designer).</summary>
    public ConfirmDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Creates the dialog.</summary>
    /// <param name="title">The title.</param>
    /// <param name="message">The question.</param>
    public ConfirmDialog(string title, string message)
        : this()
    {
        Title = title;
        this.FindControl<TextBlock>("Message")!.Text = message;
    }

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNo(object? sender, RoutedEventArgs e) => Close(false);
}
