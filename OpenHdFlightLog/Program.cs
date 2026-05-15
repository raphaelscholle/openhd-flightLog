using Avalonia;
using System;

namespace OpenHdFlightLog;

sealed class Program
{
    // Einstiegspunkt der Desktop-Anwendung.
    //
    // Wichtig bei Avalonia: Vor StartWithClassicDesktopLifetime sollte keine UI-Logik
    // laufen. Avalonia initialisiert Threading, Plattformintegration und Ressourcen erst
    // beim Start der Desktop-Lifetime vollstaendig.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Zentrale Avalonia-Konfiguration. Der XAML-Designer verwendet diese Methode auch,
    // deshalb bleibt sie als separater Builder bestehen.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
