namespace Bengine.Studio.ViewModels;

using Bengine.Introspection.History;
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

    /// <summary>Asks for a file to open.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="extensions">Allowed extensions without dots (<c>csv</c>, <c>sql</c>); empty for any.</param>
    /// <returns>The chosen path, or <c>null</c> if cancelled.</returns>
    Task<string?> OpenFileAsync(string title, IReadOnlyList<string> extensions);

    /// <summary>Asks for the values of a statement's parameters.</summary>
    /// <param name="names">The parameter names.</param>
    /// <param name="previous">Values entered last time, to prefill.</param>
    /// <returns>The value text per name, or <c>null</c> if cancelled.</returns>
    Task<IReadOnlyDictionary<string, string>?> AskParametersAsync(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> previous);

    /// <summary>Shows SQL for review (pending changes, generated DDL) with a confirm button.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="sql">The SQL.</param>
    /// <param name="confirmText">The confirm button text, or <c>null</c> for a read-only view.</param>
    /// <returns><c>true</c> if confirmed.</returns>
    Task<bool> ReviewSqlAsync(string title, string sql, string? confirmText);

    /// <summary>Shows a value in a large editor (JSON pretty-printing, NULL toggle).</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="value">The value text, or <see cref="RowItem.NullText"/>.</param>
    /// <param name="readOnly">Whether the value can be changed.</param>
    /// <returns>The new text (possibly <see cref="RowItem.NullText"/>), or <c>null</c> if cancelled.</returns>
    Task<string?> EditValueAsync(string title, string? value, bool readOnly);

    /// <summary>Asks for a line of text.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="label">The prompt.</param>
    /// <param name="initial">The initial text.</param>
    /// <returns>The text, or <c>null</c> if cancelled.</returns>
    Task<string?> PromptAsync(string title, string label, string initial);

    /// <summary>Shows the query history.</summary>
    /// <param name="history">The history store.</param>
    /// <param name="serverId">The server to show, or <c>null</c> for all.</param>
    /// <returns>The entry chosen to reuse, or <c>null</c>.</returns>
    Task<QueryHistoryEntry?> ShowHistoryAsync(QueryHistory history, string? serverId);

    /// <summary>Shows the table editor.</summary>
    /// <param name="editor">The editor.</param>
    /// <returns><c>true</c> when the table was changed.</returns>
    Task<bool> EditTableAsync(TableEditorViewModel editor);

    /// <summary>Shows the data import dialog.</summary>
    /// <param name="import">The import.</param>
    /// <returns><c>true</c> when rows were imported.</returns>
    Task<bool> ImportDataAsync(ImportDataViewModel import);

    /// <summary>Asks for a folder.</summary>
    /// <param name="title">The dialog title.</param>
    /// <returns>The folder, or <c>null</c>.</returns>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Shows a file's local history.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="versions">The saved versions, newest first.</param>
    /// <param name="current">The current text (to compare with).</param>
    /// <returns>The content to restore, or <c>null</c>.</returns>
    Task<string?> ShowLocalHistoryAsync(string title, IReadOnlyList<Bengine.Introspection.Projects.HistoryVersion> versions, string current);

    /// <summary>Shows where an object is used.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="usages">The usages.</param>
    /// <returns>The usage to open, or <c>null</c>.</returns>
    Task<Bengine.Introspection.Sql.Usage?> ShowUsagesAsync(string title, IReadOnlyList<Bengine.Introspection.Sql.Usage> usages);

    /// <summary>Shows the Dump / Restore dialog.</summary>
    /// <param name="backup">The dialog model.</param>
    /// <returns>A task that completes when the dialog closes.</returns>
    Task ShowBackupAsync(BackupViewModel backup);

    /// <summary>Puts text on the clipboard.</summary>
    /// <param name="text">The text.</param>
    /// <returns>A task that completes when copied.</returns>
    Task SetClipboardAsync(string text);

    /// <summary>Reads text from the clipboard.</summary>
    /// <returns>The text, or <c>null</c>.</returns>
    Task<string?> GetClipboardAsync();
}
