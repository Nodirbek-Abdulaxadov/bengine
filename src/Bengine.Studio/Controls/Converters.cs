namespace Bengine.Studio.Controls;

using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

/// <summary>Small value converters used by the views.</summary>
public static class Converters
{
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#ff8f8f"));
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#3ccf7e"));
    private static readonly IBrush Line = new SolidColorBrush(Color.Parse("#1f2835"));
    private static readonly IBrush StatementBrush = new SolidColorBrush(Color.Parse("#82aaff"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#8b98ab"));
    private static readonly IBrush AmberBrush = new SolidColorBrush(Color.Parse("#e0b341"));

    /// <summary>Gets a converter from <c>true</c> to italic text.</summary>
    public static IValueConverter ItalicIfTrue { get; } =
        new FuncValueConverter<bool, FontStyle>(value => value ? FontStyle.Italic : FontStyle.Normal);

    /// <summary>Gets a converter from <c>true</c> to semi-bold text.</summary>
    public static IValueConverter BoldIfTrue { get; } =
        new FuncValueConverter<bool, FontWeight>(value => value ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>Gets a converter from an error flag to a red brush (or the inherited foreground).</summary>
    public static IValueConverter ErrorBrush { get; } =
        new FuncValueConverter<bool, object?>(value => value ? Error : AvaloniaProperty.UnsetValue);

    /// <summary>Gets a converter that is <c>true</c> when an integer is zero.</summary>
    public static IValueConverter IsZero { get; } = new FuncValueConverter<int, bool>(value => value == 0);

    /// <summary>Gets a converter that is <c>true</c> when an integer is not zero.</summary>
    public static IValueConverter IsNonZero { get; } = new FuncValueConverter<int, bool>(value => value != 0);

    /// <summary>Gets a converter from an error flag to red or green status text.</summary>
    public static IValueConverter StatusBrush { get; } =
        new FuncValueConverter<bool, IBrush>(value => value ? Error : Ok);

    /// <summary>Gets a converter from a server color (<c>#rrggbb</c>) to a brush, or the default line color.</summary>
    public static IValueConverter AccentBrush { get; } =
        new FuncValueConverter<string?, IBrush>(value => Accent(value) ?? Line);

    /// <summary>Gets a converter from a server color to a translucent tint for its tree rows, or transparent.</summary>
    public static IValueConverter AccentTint { get; } =
        new FuncValueConverter<string?, IBrush>(value => value is not null && Color.TryParse(value, out var c)
            ? new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B))
            : Brushes.Transparent);

    /// <summary>Gets a converter that is <c>true</c> for the manual transaction mode.</summary>
    public static IValueConverter IsManual { get; } =
        new FuncValueConverter<ViewModels.TransactionMode, bool>(value => value == ViewModels.TransactionMode.Manual);

    /// <summary>Gets a converter from an output line kind to its color.</summary>
    public static IValueConverter MessageBrush { get; } =
        new FuncValueConverter<ViewModels.MessageKind, IBrush>(kind => kind switch
        {
            ViewModels.MessageKind.Error => Error,
            ViewModels.MessageKind.Success => Ok,
            ViewModels.MessageKind.Statement => StatementBrush,
            _ => InfoBrush,
        });

    /// <summary>Gets a converter from a difference marker (+, −, ~) to its color.</summary>
    public static IValueConverter MarkerBrush { get; } =
        new FuncValueConverter<string?, IBrush>(marker => marker switch
        {
            "+" => Ok,
            "−" => Error,
            _ => AmberBrush,
        });

    /// <summary>Gets a converter that is <c>true</c> for a non-null, non-empty string.</summary>
    public static IValueConverter HasText { get; } = new FuncValueConverter<string?, bool>(value => !string.IsNullOrEmpty(value));

    /// <summary>Gets the brush of a server color, or <c>null</c>.</summary>
    /// <param name="value">The <c>#rrggbb</c> color.</param>
    /// <returns>The brush.</returns>
    public static IBrush? Accent(string? value) =>
        value is not null && Color.TryParse(value, out var color) ? new SolidColorBrush(color) : null;

}
