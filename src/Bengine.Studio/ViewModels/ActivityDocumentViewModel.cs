namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Threading;
using Bengine.Introspection;
using Bengine.Introspection.Activity;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>
/// A server's activity (pgAdmin's dashboard, DataGrip's sessions): the
/// sessions with their state, duration, waits, blockers, and statements, the
/// locks, auto-refreshing every two seconds, with actions to cancel a
/// statement or end a session.
/// </summary>
public sealed partial class ActivityDocumentViewModel : DocumentViewModel, IAsyncDisposable
{
    private readonly ExplorerViewModel _explorer;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DatabaseSession? _session;
    private bool _refreshing;

    [ObservableProperty] private ActivitySession? _selected;
    [ObservableProperty] private bool _hideIdle = true;
    [ObservableProperty] private bool _autoRefresh = true;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>Creates the tab.</summary>
    /// <param name="explorer">The explorer.</param>
    /// <param name="server">The server.</param>
    public ActivityDocumentViewModel(ExplorerViewModel explorer, ServerProfile server)
    {
        _explorer = explorer;
        Server = server;
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>Gets the server.</summary>
    public ServerProfile Server { get; }

    /// <inheritdoc />
    public override string Title => $"Activity {Server.Name}";

    /// <inheritdoc />
    public override string IconKind => "Server";

    /// <inheritdoc />
    public override string? AccentColor => Server.Color;

    /// <summary>Gets the sessions shown.</summary>
    public ObservableCollection<ActivitySession> Sessions { get; } = [];

    /// <summary>Gets the locks.</summary>
    public ObservableCollection<ActivityLock> Locks { get; } = [];

    /// <summary>Starts the view.</summary>
    /// <returns>A task that completes after the first refresh.</returns>
    public async Task InitializeAsync()
    {
        await RefreshAsync();
        if (AutoRefresh)
        {
            _timer.Start();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            _session ??= await _explorer.OpenSessionAsync(Server, null);
            var sessions = await ServerActivity.SessionsAsync(_session);
            var locks = await ServerActivity.LocksAsync(_session);
            var keep = Selected?.Id;
            Sessions.Clear();
            foreach (var session in sessions.Where(s => !HideIdle || !s.IsIdle || s.BlockedBy is not null))
            {
                Sessions.Add(session);
            }

            Locks.Clear();
            foreach (var item in locks.Take(500))
            {
                Locks.Add(item);
            }

            Selected = Sessions.FirstOrDefault(s => s.Id == keep);
            var blocked = sessions.Count(s => s.BlockedBy is not null);
            Summary = $"{sessions.Count} session(s): {sessions.Count(s => !s.IsIdle)} active, {sessions.Count(s => s.IsIdle)} idle" +
                      (blocked > 0 ? $", {blocked} blocked" : string.Empty) + $" · {locks.Count} lock(s), {locks.Count(l => !l.Granted)} awaited · {DateTime.Now:HH:mm:ss}";
            IsError = false;
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
            IsError = true;
            _timer.Stop();
            if (_session is not null)
            {
                await _session.DisposeAsync();
                _session = null;
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    [RelayCommand]
    private async Task CancelQueryAsync()
    {
        if (Selected is not { IsSelf: false } target || _session is null)
        {
            return;
        }

        await ActAsync(() => ServerActivity.CancelAsync(_session, target.Id), $"Cancel requested for session {target.Id}.");
    }

    [RelayCommand]
    private async Task TerminateAsync()
    {
        if (Selected is not { IsSelf: false } target || _session is null || _explorer.Dialogs is not { } dialogs)
        {
            return;
        }

        if (!await dialogs.ConfirmAsync("End Session", $"End session {target.Id} ({target.User}@{target.Client})? Its open transaction is rolled back."))
        {
            return;
        }

        await ActAsync(() => ServerActivity.TerminateAsync(_session, target.Id), $"Session {target.Id} ended.");
    }

    [RelayCommand]
    private void OpenQuery()
    {
        if (Selected?.Query is { Length: > 0 } sql)
        {
            _ = _explorer.OpenQueryAsync(Server, Selected.Database, sql);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    partial void OnHideIdleChanged(bool value) => _ = RefreshAsync();

    private async Task ActAsync(Func<Task> action, string done)
    {
        if (Server.ReadOnly)
        {
            Status = $"\"{Server.Name}\" is read-only.";
            IsError = true;
            return;
        }

        try
        {
            await action();
            Status = done;
            IsError = false;
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
            IsError = true;
        }

        await RefreshAsync();
    }
}
