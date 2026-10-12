namespace Bengine.Studio.ViewModels;

using Bengine.Introspection.Servers;

/// <summary>What the user typed into the "Connect to Server" prompt.</summary>
/// <param name="Password">The password.</param>
/// <param name="Save">Whether to persist it with the registration.</param>
public sealed record PasswordAnswer(string Password, bool Save);

/// <summary>
/// The dialogs the view models need, implemented by the main window so view
/// models never touch windows directly.
/// </summary>
public interface IDialogService
{
    /// <summary>Asks for a server's password.</summary>
    /// <param name="server">The server.</param>
    /// <param name="error">An error from the previous attempt, if any.</param>
    /// <returns>The answer, or <c>null</c> if the user cancelled.</returns>
    Task<PasswordAnswer?> AskPasswordAsync(ServerProfile server, string? error);

    /// <summary>Shows the Register/Edit Server dialog.</summary>
    /// <param name="server">The server to edit, or <c>null</c> to register a new one.</param>
    /// <returns>The saved registration, or <c>null</c> if cancelled.</returns>
    Task<ServerProfile?> EditServerAsync(ServerProfile? server);

    /// <summary>Shows the pgAdmin import dialog.</summary>
    /// <returns>The number of servers imported.</returns>
    Task<int> ImportFromPgAdminAsync();

    /// <summary>Asks a yes/no question.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">The question.</param>
    /// <returns><c>true</c> if confirmed.</returns>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>Asks where to save a file.</summary>
    /// <param name="suggestedName">The proposed file name.</param>
    /// <returns>The chosen path, or <c>null</c> if cancelled.</returns>
    Task<string?> SaveFileAsync(string suggestedName);
}
