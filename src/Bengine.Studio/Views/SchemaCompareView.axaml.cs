namespace Bengine.Studio.Views;

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Bengine.Studio.Controls;
using Bengine.Studio.ViewModels;

/// <summary>The schema comparison tab: two endpoints, the differences, and the migration script.</summary>
public partial class SchemaCompareView : UserControl
{
    private SchemaCompareViewModel? _bound;

    /// <summary>Creates the view.</summary>
    public SchemaCompareView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Bind();
    }

    private SqlEditor ScriptEditorControl => this.FindControl<SqlEditor>("ScriptEditor")!;

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnChanged;
        }

        _bound = DataContext as SchemaCompareViewModel;
        if (_bound is not null)
        {
            _bound.PropertyChanged += OnChanged;
            ScriptEditorControl.Text = _bound.Script;
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_bound is null)
        {
            return;
        }

        if (e.PropertyName == nameof(SchemaCompareViewModel.Script))
        {
            ScriptEditorControl.Provider = _bound.Target.UseFile ? _bound.Source.Provider : _bound.Target.Provider;
            ScriptEditorControl.Text = _bound.Script;
        }
        else if (e.PropertyName == nameof(SchemaCompareViewModel.SelectedChange) && _bound.SelectedChange is { } change)
        {
            // Scroll the script to the first statement about the selected object.
            var name = change.Subject.Split('.')[^1];
            var at = _bound.Script.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var line = ScriptEditorControl.Document.GetLineByOffset(at);
                ScriptEditorControl.ScrollToLine(line.LineNumber);
                ScriptEditorControl.Select(line.Offset, line.Length);
            }
        }
    }
}
