namespace Bengine.Studio.Views;

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Bengine.Studio.Controls;
using Bengine.Studio.ViewModels;

/// <summary>
/// The Query Tool tab: the SQL editor over the result tabs. Ctrl+Enter runs the
/// statement under the caret (or the selection), Ctrl+Shift+Enter the whole
/// script, Ctrl+F2 cancels, Ctrl+Alt+E opens the history. The editor text is
/// kept in sync with the view model; server error positions become markers.
/// </summary>
public partial class QueryView : UserControl
{
    private readonly SqlEditor _editor;
    private QueryDocumentViewModel? _bound;
    private bool _syncing;

    /// <summary>Creates the view.</summary>
    public QueryView()
    {
        AvaloniaXamlLoader.Load(this);
        _editor = this.FindControl<SqlEditor>("Editor")!;
        _editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        _editor.TextChanged += (_, _) =>
        {
            if (_bound is not null && !_syncing)
            {
                _bound.QueryText = _editor.Text;
            }
        };
        DataContextChanged += (_, _) => Bind();
    }

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.MarkersChanged -= OnMarkers;
            _bound.InsertRequested -= OnInsert;
            _bound.LineRequested -= OnLine;
            _bound.PropertyChanged -= OnChanged;
        }

        _bound = DataContext as QueryDocumentViewModel;
        if (_bound is null)
        {
            return;
        }

        _bound.MarkersChanged += OnMarkers;
        _bound.InsertRequested += OnInsert;
        _bound.LineRequested += OnLine;
        _bound.PropertyChanged += OnChanged;
        _editor.Provider = _bound.Provider;
        _editor.ReadOnlyConnection = _bound.IsReadOnly;
        var vm = _bound;
        _editor.CatalogSource = () => vm.Catalog;
        _syncing = true;
        _editor.Text = _bound.QueryText;
        _syncing = false;
        var line = _bound.PendingLine;
        Dispatcher.UIThread.Post(
            () =>
            {
                _editor.Focus();
                if (line is { } l)
                {
                    OnLine(l);
                }
            },
            DispatcherPriority.Background);
    }

    private void OnLine(int line)
    {
        if (_editor.Document.LineCount == 0)
        {
            return;
        }

        var target = _editor.Document.GetLineByNumber(Math.Clamp(line, 1, _editor.Document.LineCount));
        _editor.CaretOffset = target.Offset;
        _editor.Select(target.Offset, target.Length);
        _editor.ScrollToLine(target.LineNumber);
        _bound?.ClearPendingLine();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryDocumentViewModel.QueryText) && _bound is not null && _editor.Text != _bound.QueryText)
        {
            _syncing = true;
            _editor.Text = _bound.QueryText;
            _syncing = false;
        }
        else if (e.PropertyName == nameof(QueryDocumentViewModel.Catalog))
        {
            // Names resolve against the new metadata.
            _editor.RunInspections();
        }
    }

    private void OnMarkers(IReadOnlyList<EditorMarker> markers) => _editor.Markers = markers;

    private void OnInsert(string sql)
    {
        if (_editor.Document.TextLength == 0)
        {
            _editor.Text = sql;
            return;
        }

        var at = _editor.Document.TextLength;
        var text = (_editor.Text.EndsWith('\n') ? "\n" : "\n\n") + sql;
        _editor.Document.Insert(at, text);
        _editor.Select(at + text.Length - sql.Length, sql.Length);
        _editor.ScrollToLine(_editor.Document.GetLineByOffset(_editor.Document.TextLength).LineNumber);
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        var keys = Keymap.Current;
        if (keys.Matches("query.runScript", e))
        {
            e.Handled = true;
            RunScript();
        }
        else if (keys.Matches("query.run", e))
        {
            e.Handled = true;
            Run();
        }
        else if (keys.Matches("query.cancel", e))
        {
            e.Handled = true;
            _bound?.CancelCommand.Execute(null);
        }
        else if (keys.Matches("history.show", e))
        {
            e.Handled = true;
            _bound?.ShowHistoryCommand.Execute(null);
        }
        else if (keys.Matches("query.explain", e))
        {
            e.Handled = true;
            Explain(analyze: false);
        }
        else if (keys.Matches("query.explainAnalyze", e))
        {
            e.Handled = true;
            Explain(analyze: true);
        }
    }

    /// <summary>Gets the SQL editor (for the action palette).</summary>
    public SqlEditor EditorControl => _editor;

    private void OnRun(object? sender, RoutedEventArgs e) => Run();

    private void OnRunScript(object? sender, RoutedEventArgs e) => RunScript();

    private void OnExplain(object? sender, RoutedEventArgs e) => Explain(analyze: false);

    private void OnExplainAnalyze(object? sender, RoutedEventArgs e) => Explain(analyze: true);

    private void OnFormat(object? sender, RoutedEventArgs e) => _editor.FormatSql();

    /// <summary>Runs the statement under the caret, or the selection.</summary>
    public void Run()
    {
        if (_bound is not null && _editor.CurrentStatement() is { } statement)
        {
            _ = _bound.ExecuteAsync(statement.Sql, statement.Start);
        }
    }

    /// <summary>Runs the whole script.</summary>
    public void RunScript()
    {
        if (_bound is not null)
        {
            _ = _bound.ExecuteAsync(_editor.Text, 0);
        }
    }

    /// <summary>Shows the plan of the statement under the caret.</summary>
    /// <param name="analyze">Whether to run it and report actual rows and times.</param>
    public void Explain(bool analyze)
    {
        if (_bound is not null && _editor.CurrentStatement() is { } statement)
        {
            _ = _bound.ExplainAsync(statement.Sql, statement.Start, analyze);
        }
    }
}
