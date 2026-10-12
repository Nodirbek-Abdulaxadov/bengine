namespace Bengine.Studio.Views;

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Bengine.Introspection.Servers;
using Bengine.Studio.ViewModels;

/// <summary>pgAdmin's "Connect to Server" prompt; returns a <see cref="PasswordAnswer"/> or <c>null</c>.</summary>
public partial class PasswordDialog : Window
{
    /// <summary>Creates the dialog (for the designer).</summary>
    public PasswordDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Creates the dialog for a server.</summary>
    /// <param name="server">The server to sign in to.</param>
    /// <param name="error">An error from the previous attempt.</param>
    public PasswordDialog(ServerProfile server, string? error)
        : this()
    {
        var prompt = this.FindControl<TextBlock>("Prompt")!;
        prompt.Inlines =
        [
            new Run("Please enter the password for the user "),
            new Run(server.Username ?? "—") { FontWeight = FontWeight.Bold },
            new Run(" to connect the server "),
            new Run(server.Name) { FontWeight = FontWeight.Bold },
            new Run("."),
        ];
        var errorText = this.FindControl<TextBlock>("Error")!;
        errorText.Text = error;
        errorText.IsVisible = error is not null;
        Opened += (_, _) => this.FindControl<TextBox>("Password")!.Focus();
    }

    private void OnOk(object? sender, RoutedEventArgs e) =>
        Close(new PasswordAnswer(this.FindControl<TextBox>("Password")!.Text ?? string.Empty, this.FindControl<CheckBox>("Save")!.IsChecked == true));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
