namespace Bengine.Studio.Controls;

using Avalonia;
using Avalonia.Controls;

/// <summary>Shows the 16×16 tree icon for a node kind, settable from XAML bindings.</summary>
public sealed class IconView : ContentControl
{
    /// <summary>Defines the <see cref="Kind"/> property.</summary>
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<IconView, string?>(nameof(Kind));

    static IconView() => KindProperty.Changed.AddClassHandler<IconView>((view, _) => view.Rebuild());

    /// <summary>Creates the view.</summary>
    public IconView()
    {
        Width = 16;
        Height = 16;
    }

    /// <summary>Gets or sets the node kind to draw.</summary>
    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private void Rebuild() => Content = string.IsNullOrEmpty(Kind) ? null : TreeIcons.Create(Kind);
}
