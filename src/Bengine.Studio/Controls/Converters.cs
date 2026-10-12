namespace Bengine.Studio.Controls;

using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

/// <summary>Small value converters used by the views.</summary>
public static class Converters
{
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#ff8f8f"));
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#3ccf7e"));

    /// <summary>Gets a converter from <c>true</c> to italic text.</summary>
    public static IValueConverter ItalicIfTrue { get; } =
        new FuncValueConverter<bool, FontStyle>(value => value ? FontStyle.Italic : FontStyle.Normal);

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
}
