namespace Bengine.Studio.Views;

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Bengine.Introspection.Compare;
using Bengine.Studio.Controls;
using Bengine.Studio.ViewModels;

/// <summary>The Data Compare tab: a grid of differing rows (one column per compared column) over the sync script.</summary>
public partial class DataCompareView : UserControl
{
    private static readonly IBrush Added = new SolidColorBrush(Color.Parse("#173322"));
    private static readonly IBrush Removed = new SolidColorBrush(Color.Parse("#3a1d1d"));
    private static readonly IBrush Changed = new SolidColorBrush(Color.Parse("#3a2f12"));
    private readonly DataGrid _grid;
    private readonly SqlEditor _script;
    private DataCompareViewModel? _bound;

    /// <summary>Creates the view.</summary>
    public DataCompareView()
    {
        AvaloniaXamlLoader.Load(this);
        _grid = this.FindControl<DataGrid>("Diffs")!;
        _script = this.FindControl<SqlEditor>("ScriptEditor")!;
        _grid.LoadingRow += (_, e) => e.Row.Background = (e.Row.DataContext as DataDiffRow)?.Kind switch
        {
            RowDiffKind.OnlyInSource => Added,
            RowDiffKind.OnlyInTarget => Removed,
            RowDiffKind.Different => Changed,
            _ => null,
        };
        DataContextChanged += (_, _) => Bind();
    }

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.ResultChanged -= Rebuild;
            _bound.PropertyChanged -= OnChanged;
        }

        _bound = DataContext as DataCompareViewModel;
        if (_bound is null)
        {
            return;
        }

        _bound.ResultChanged += Rebuild;
        _bound.PropertyChanged += OnChanged;
        Rebuild();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DataCompareViewModel.Script) && _bound is not null)
        {
            _script.Provider = _bound.Target.Server?.Provider ?? Bengine.Enums.DatabaseProvider.PostgreSql;
            _script.Text = _bound.Script;
        }
    }

    private void Rebuild()
    {
        _grid.Columns.Clear();
        if (_bound?.Result is not { } result)
        {
            return;
        }

        _grid.Columns.Add(new DataGridTextColumn { Header = string.Empty, Binding = new Binding(nameof(DataDiffRow.Marker)), Width = new DataGridLength(32) });
        for (var i = 0; i < result.Columns.Count; i++)
        {
            var key = result.KeyColumns.Contains(result.Columns[i], StringComparer.OrdinalIgnoreCase);
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = (key ? "🔑 " : string.Empty) + result.Columns[i],
                Binding = new Binding($"{nameof(DataDiffRow.Cells)}[{i}]"),
                FontFamily = (FontFamily?)Avalonia.Application.Current?.FindResource("Mono") ?? FontFamily.Default,
            });
        }

        _script.Text = _bound.Script;
    }
}
