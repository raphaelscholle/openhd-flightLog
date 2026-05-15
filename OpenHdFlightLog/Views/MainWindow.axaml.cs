using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OpenHdFlightLog.ViewModels;

namespace OpenHdFlightLog.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
