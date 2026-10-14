namespace Bengine.Studio.Views;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

/// <summary>The server activity tab: sessions over locks.</summary>
public partial class ActivityView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ActivityView() => AvaloniaXamlLoader.Load(this);
}
