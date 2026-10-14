namespace Bengine.Studio;

using Avalonia;

/// <summary>Desktop entry point for Bengine Data Studio.</summary>
internal static class Program
{
    /// <summary>Application entry point. Keep free of Avalonia and third-party calls before <see cref="BuildAvaloniaApp"/>.</summary>
    /// <param name="args">Command-line arguments.</param>
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Configures the Avalonia application (used by the visual designer too).</summary>
    /// <returns>The configured app builder.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
