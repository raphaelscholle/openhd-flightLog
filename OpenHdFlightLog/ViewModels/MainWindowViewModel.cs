using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenHdFlightLog.Models;
using OpenHdFlightLog.Services;

namespace OpenHdFlightLog.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    // Das ViewModel ist die Schaltzentrale zwischen UI und Datenhaltung. Es kennt keine
    // Avalonia-Controls direkt, sondern arbeitet mit ObservableCollections und Commands.
    private FlightLogDatabase? database;
    private bool isInitialized;

    // Diese Collections sind direkt an DataGrids in MainWindow.axaml gebunden. Wenn hier
    // Elemente hinzugefuegt oder entfernt werden, aktualisiert Avalonia die UI automatisch.
    public ObservableCollection<LogFileRecord> Logs { get; } = [];
    public ObservableCollection<MavlinkMessageRecord> Messages { get; } = [];
    public ObservableCollection<MessageFieldRecord> Fields { get; } = [];
    public ObservableCollection<LogVariableRecord> LogVariables { get; } = [];
    public ObservableCollection<OsdReplayRecord> OsdReplayFrames { get; } = [];
    public ObservableCollection<UserVariableRecord> Variables { get; } = [];
    public ObservableCollection<MavlinkMessageDefinitionRecord> Definitions { get; } = [];
    public ObservableCollection<MavlinkFieldDefinitionRecord> DefinitionFields { get; } = [];
    public ObservableCollection<DebugEventRecord> DebugEvents { get; } = [];

    public Func<Task<string?>>? OpenLogFileRequested { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteLogCommand))]
    private LogFileRecord? selectedLog;

    [ObservableProperty]
    private MavlinkMessageRecord? selectedMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveFieldCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteFieldCommand))]
    private MessageFieldRecord? selectedField;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveVariableCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteVariableCommand))]
    private UserVariableRecord? selectedVariable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDefinitionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteDefinitionCommand))]
    private MavlinkMessageDefinitionRecord? selectedDefinition;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDefinitionFieldCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteDefinitionFieldCommand))]
    private MavlinkFieldDefinitionRecord? selectedDefinitionField;

    [ObservableProperty]
    private string status = "";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private long osdReplayTimeMs;

    [ObservableProperty]
    private long osdReplayMaxMs;

    [ObservableProperty]
    private OsdReplayRecord currentOsdFrame = new();

    private FlightLogDatabase Database => database ?? throw new InvalidOperationException("Datenbank ist noch nicht initialisiert.");

    public string DatabasePath => database?.DatabasePath ?? "nicht verbunden";
    public string OpenHdRepositoryPath => MavlinkDefinitionLoader.DefaultOpenHdRoot;

    public MainWindowViewModel()
    {
        // Die Datenbankinitialisierung kann MySQL/Docker starten und darf deshalb nicht
        // im Konstruktor laufen. Sonst bleibt das Fenster beim Start schwarz.
        Status = "Datenbank wird initialisiert...";
    }

    public async Task InitializeAsync()
    {
        if (isInitialized)
        {
            return;
        }

        isInitialized = true;
        IsBusy = true;
        Status = "Datenbank wird initialisiert...";
        try
        {
            database = await Task.Run(() => new FlightLogDatabase(AddDebugEvent));
            RefreshAll();
            Status = $"Datenbank: {DatabasePath}";
        }
        catch (Exception ex)
        {
            Status = $"Datenbank konnte nicht initialisiert werden: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedLogChanged(LogFileRecord? value)
    {
        // CommunityToolkit.Mvvm erzeugt diese Partial-Hooks fuer ObservableProperty.
        // Sobald in der UI ein anderes Log gewaehlt wird, werden alle abhaengigen
        // Detailansichten geleert und anschliessend aus der Datenbank neu aufgebaut.
        Messages.Clear();
        Fields.Clear();
        LogVariables.Clear();
        OsdReplayFrames.Clear();
        SelectedMessage = null;
        SelectedField = null;
        CurrentOsdFrame = new OsdReplayRecord();
        OsdReplayTimeMs = 0;
        OsdReplayMaxMs = 0;

        if (value is null)
        {
            return;
        }

        foreach (var message in Database.GetMessages(value.Id))
        {
            Messages.Add(message);
        }

        foreach (var variable in Database.GetLogVariables(value.Id))
        {
            LogVariables.Add(variable);
        }

        foreach (var frame in Database.GetOsdReplayFrames(value.Id))
        {
            OsdReplayFrames.Add(frame);
        }

        // Der OSD-Replay-Slider braucht Min/Max-Werte. Die Frames sind bereits aus den
        // dekodierten Logvariablen aggregiert.
        if (OsdReplayFrames.Count > 0)
        {
            OsdReplayMaxMs = OsdReplayFrames.Max(frame => frame.TimeMs);
            OsdReplayTimeMs = OsdReplayFrames.Min(frame => frame.TimeMs);
            UpdateCurrentOsdFrame();
        }

        Status = $"{Messages.Count:N0} Nachrichten und {LogVariables.Count:N0} Variablen geladen.";
    }

    partial void OnOsdReplayTimeMsChanged(long value)
    {
        UpdateCurrentOsdFrame();
    }

    partial void OnSelectedMessageChanged(MavlinkMessageRecord? value)
    {
        // Die Feldliste ist eine Detailansicht zur aktuell markierten MAVLink-Nachricht.
        Fields.Clear();
        SelectedField = null;

        if (value is null)
        {
            return;
        }

        foreach (var field in Database.GetFields(value.Id))
        {
            Fields.Add(field);
        }
    }

    partial void OnSelectedDefinitionChanged(MavlinkMessageDefinitionRecord? value)
    {
        // Gleiches Muster fuer MAVLink-Definitionen: Auswahl oben, Feldlayout unten.
        DefinitionFields.Clear();
        SelectedDefinitionField = null;

        if (value is null)
        {
            return;
        }

        foreach (var field in Database.GetDefinitionFields(value.Id))
        {
            DefinitionFields.Add(field);
        }
    }

    [RelayCommand]
    private async Task ImportLogAsync()
    {
        if (OpenLogFileRequested is null)
        {
            Status = "Dateiauswahl ist nicht verfuegbar.";
            return;
        }

        if (database is null)
        {
            Status = "Datenbank ist noch nicht bereit.";
            return;
        }

        var path = await OpenLogFileRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        Status = "Import laeuft...";
        try
        {
            // Vor dem Import werden Definitionen geladen, wenn die Datenbank noch keine
            // enthaelt. Dadurch koennen OpenHD-spezifische Felder sofort dekodiert werden.
            EnsureDefinitionsLoadedForImport();
            var result = await Database.ImportLogAsync(path);
            RefreshLogs();
            SelectedLog = Logs.FirstOrDefault(log => log.Id == result.LogId);
            Status = $"{Path.GetFileName(path)} importiert: {result.MessageCount:N0} MAVLink-Nachrichten.";
        }
        catch (Exception ex)
        {
            Status = $"Import fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void LoadOpenHdDefinitions()
    {
        // Manueller Reload der generierten OpenHD-MAVLink-Header. Das ist nuetzlich,
        // wenn sich die Header-Dateien geaendert haben oder der automatische Import
        // wegen fehlendem Repository-Pfad uebersprungen wurde.
        IsBusy = true;
        Status = "OpenHD MAVLink-Header werden gelesen...";
        try
        {
            if (database is null)
            {
                Status = "Datenbank ist noch nicht bereit.";
                return;
            }

            var definitions = MavlinkDefinitionLoader.LoadFromOpenHdHeaders();
            var count = Database.ImportDefinitions(definitions);
            RefreshDefinitions();
            Status = $"{count:N0} MAVLink-Definitionen aus OpenHD geladen.";
        }
        catch (Exception ex)
        {
            Status = $"OpenHD-Definitionen konnten nicht geladen werden: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteLog))]
    private void DeleteLog()
    {
        if (SelectedLog is null)
        {
            return;
        }

        // Die Datenbank nutzt FOREIGN KEY ... ON DELETE CASCADE. Ein Delete auf log_files
        // entfernt daher automatisch die dazugehoerigen Nachrichten und Felder.
        Database.DeleteLog(SelectedLog.Id);
        RefreshLogs();
        Messages.Clear();
        Fields.Clear();
        LogVariables.Clear();
        OsdReplayFrames.Clear();
        Status = "Log und verknuepfte Nachrichten/Felder geloescht.";
    }

    private bool CanDeleteLog() => SelectedLog is not null;

    [RelayCommand(CanExecute = nameof(CanSaveField))]
    private void SaveField()
    {
        if (SelectedField is null)
        {
            return;
        }

        Database.SaveField(SelectedField);
        Status = "Feld gespeichert.";
    }

    private bool CanSaveField() => SelectedField is not null;

    [RelayCommand(CanExecute = nameof(CanSaveField))]
    private void DeleteField()
    {
        if (SelectedField is null)
        {
            return;
        }

        var field = SelectedField;
        Database.DeleteField(field.Id);
        Fields.Remove(field);
        SelectedField = null;
        Status = "Feld geloescht.";
    }

    [RelayCommand]
    private void AddVariable()
    {
        var variable = new UserVariableRecord
        {
            Name = "NeueVariable",
            ValueText = "",
            DataType = "text",
            Notes = ""
        };
        Variables.Add(variable);
        SelectedVariable = variable;
        Status = "Neue Variable angelegt. Nach dem Bearbeiten speichern.";
    }

    [RelayCommand(CanExecute = nameof(CanSaveVariable))]
    private void SaveVariable()
    {
        if (SelectedVariable is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedVariable.Name))
        {
            Status = "Variable braucht einen Namen.";
            return;
        }

        // SaveVariable fuehrt Insert oder Update aus. Bei neuen Variablen kommt die neue
        // Datenbank-ID zurueck und wird im Objekt gespeichert.
        SelectedVariable.Id = Database.SaveVariable(SelectedVariable);
        RefreshVariables(SelectedVariable.Id);
        Status = "Variable gespeichert.";
    }

    private bool CanSaveVariable() => SelectedVariable is not null;

    [RelayCommand(CanExecute = nameof(CanSaveVariable))]
    private void DeleteVariable()
    {
        if (SelectedVariable is null)
        {
            return;
        }

        var variable = SelectedVariable;
        if (variable.Id != 0)
        {
            Database.DeleteVariable(variable.Id);
        }

        Variables.Remove(variable);
        SelectedVariable = null;
        Status = "Variable geloescht.";
    }

    [RelayCommand(CanExecute = nameof(CanSaveDefinition))]
    private void SaveDefinition()
    {
        if (SelectedDefinition is null)
        {
            return;
        }

        Database.SaveDefinition(SelectedDefinition);
        RefreshDefinitions(SelectedDefinition.Id);
        Status = "Message-Definition gespeichert.";
    }

    private bool CanSaveDefinition() => SelectedDefinition is not null;

    [RelayCommand(CanExecute = nameof(CanSaveDefinition))]
    private void DeleteDefinition()
    {
        if (SelectedDefinition is null)
        {
            return;
        }

        var definition = SelectedDefinition;
        Database.DeleteDefinition(definition.Id);
        Definitions.Remove(definition);
        DefinitionFields.Clear();
        SelectedDefinition = null;
        Status = "Message-Definition geloescht.";
    }

    [RelayCommand(CanExecute = nameof(CanSaveDefinitionField))]
    private void SaveDefinitionField()
    {
        if (SelectedDefinitionField is null)
        {
            return;
        }

        Database.SaveDefinitionField(SelectedDefinitionField);
        Status = "Feld-Definition gespeichert.";
    }

    private bool CanSaveDefinitionField() => SelectedDefinitionField is not null;

    [RelayCommand(CanExecute = nameof(CanSaveDefinitionField))]
    private void DeleteDefinitionField()
    {
        if (SelectedDefinitionField is null)
        {
            return;
        }

        var field = SelectedDefinitionField;
        Database.DeleteDefinitionField(field.Id);
        DefinitionFields.Remove(field);
        SelectedDefinitionField = null;
        Status = "Feld-Definition geloescht.";
    }

    [RelayCommand]
    private void PreviousOsdFrame()
    {
        if (OsdReplayFrames.Count == 0)
        {
            return;
        }

        var previous = OsdReplayFrames.LastOrDefault(frame => frame.TimeMs < OsdReplayTimeMs) ?? OsdReplayFrames.First();
        OsdReplayTimeMs = previous.TimeMs;
    }

    [RelayCommand]
    private void NextOsdFrame()
    {
        if (OsdReplayFrames.Count == 0)
        {
            return;
        }

        var next = OsdReplayFrames.FirstOrDefault(frame => frame.TimeMs > OsdReplayTimeMs) ?? OsdReplayFrames.Last();
        OsdReplayTimeMs = next.TimeMs;
    }

    [RelayCommand]
    private void ClearDebug()
    {
        DebugEvents.Clear();
        Status = "Debug-Ansicht geleert.";
    }

    [RelayCommand]
    private void RefreshAll()
    {
        RefreshLogs();
        RefreshVariables();
        RefreshDefinitions();
    }

    private void RefreshLogs()
    {
        var selectedId = SelectedLog?.Id;
        Logs.Clear();
        if (database is null)
        {
            return;
        }

        foreach (var log in Database.GetLogs())
        {
            Logs.Add(log);
        }

        SelectedLog = Logs.FirstOrDefault(log => log.Id == selectedId) ?? Logs.FirstOrDefault();
    }

    private void RefreshVariables(long? selectedId = null)
    {
        Variables.Clear();
        if (database is null)
        {
            return;
        }

        foreach (var variable in Database.GetVariables())
        {
            Variables.Add(variable);
        }

        if (selectedId is not null)
        {
            SelectedVariable = Variables.FirstOrDefault(variable => variable.Id == selectedId);
        }
    }

    private void RefreshDefinitions(long? selectedId = null)
    {
        Definitions.Clear();
        if (database is null)
        {
            return;
        }

        foreach (var definition in Database.GetDefinitions())
        {
            Definitions.Add(definition);
        }

        if (selectedId is not null)
        {
            SelectedDefinition = Definitions.FirstOrDefault(definition => definition.Id == selectedId);
        }
    }

    private void EnsureDefinitionsLoadedForImport()
    {
        // Import ohne Definitionen funktioniert, liefert fuer unbekannte Messages aber
        // nur payload_hex. Deshalb wird hier einmalig versucht, OpenHD-Definitionen zu
        // laden. Fehler werden nur im Debug-Tab protokolliert, damit der Log-Import nicht
        // komplett blockiert wird.
        if (Definitions.Count > 0)
        {
            return;
        }

        try
        {
            var definitions = MavlinkDefinitionLoader.LoadFromOpenHdHeaders();
            var count = Database.ImportDefinitions(definitions);
            RefreshDefinitions();
            AddDebugEvent(new DebugEventRecord
            {
                Timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff"),
                Category = "MAVLINK",
                Detail = $"auto-loaded {count:N0} OpenHD definitions before import"
            });
        }
        catch (Exception ex)
        {
            AddDebugEvent(new DebugEventRecord
            {
                Timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff"),
                Category = "MAVLINK",
                Detail = $"auto-load skipped: {ex.Message}"
            });
        }
    }

    private void AddDebugEvent(DebugEventRecord record)
    {
        // Debug-Ereignisse erscheinen newest-first. Die harte Grenze verhindert, dass ein
        // langer Import die UI mit beliebig vielen Debug-Zeilen wachsen laesst.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddDebugEvent(record));
            return;
        }

        DebugEvents.Insert(0, record);
        while (DebugEvents.Count > 1000)
        {
            DebugEvents.RemoveAt(DebugEvents.Count - 1);
        }
    }

    private void UpdateCurrentOsdFrame()
    {
        // Fuer einen beliebigen Slider-Zeitpunkt wird der letzte bekannte Frame vor oder
        // genau an dieser Zeit angezeigt. Dadurch bleibt die Anzeige stabil, auch wenn
        // nicht fuer jede Millisekunde ein Datensatz existiert.
        if (OsdReplayFrames.Count == 0)
        {
            CurrentOsdFrame = new OsdReplayRecord();
            return;
        }

        CurrentOsdFrame = OsdReplayFrames.LastOrDefault(frame => frame.TimeMs <= OsdReplayTimeMs)
            ?? OsdReplayFrames.First();
    }
}
