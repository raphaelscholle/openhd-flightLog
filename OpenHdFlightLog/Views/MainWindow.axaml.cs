using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OpenHdFlightLog.ViewModels;

namespace OpenHdFlightLog.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Die View kennt Avalonia-spezifische APIs wie StorageProvider. Das ViewModel
        // bleibt dadurch UI-framework-arm: Es ruft nur den Delegate auf und bekommt einen
        // Dateipfad zurueck.
        Opened += (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.OpenLogFileRequested = PickLogFileAsync;
            }
        };
    }

    private async Task<string?> PickLogFileAsync()
    {
        // Avalonia liefert abstrakte StorageFile-Objekte. Fuer den Import brauchen wir
        // einen lokalen Pfad, deshalb wird am Ende TryGetLocalPath verwendet.
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Raw MAVLink Log öffnen",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("MAVLink Logs")
                {
                    Patterns = ["*.oLog", "*.olog", "*.tlog", "*.bin", "*.log", "*.mavlink", "*.*"]
                }
            ]
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
