namespace Bengine.Studio.Views;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Bengine.Studio.ViewModels;

/// <summary>The Dump / Restore dialog; the log follows the newest line.</summary>
public partial class BackupDialog : Window
{
    /// <summary>Creates the dialog.</summary>
    public BackupDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is BackupViewModel vm)
            {
                var log = this.FindControl<ListBox>("LogList")!;
                vm.Closed += Close;
                vm.Log.CollectionChanged += (_, _) =>
                {
                    if (vm.Log.Count > 0)
                    {
                        log.ScrollIntoView(vm.Log.Count - 1);
                    }
                };
            }
        };
    }
}
