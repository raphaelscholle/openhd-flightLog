using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenHdFlightLog.Models;
using OpenHdFlightLog.Services;

namespace OpenHdFlightLog.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly FlightLogDatabase database;

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

    public string DatabasePath => database.DatabasePath;
    public string OpenHdRepositoryPath => MavlinkDefinitionLoader.DefaultOpenHdRoot;

    public MainWindowViewModel()
    {
        database = new FlightLogDatabase(AddDebugEvent);
        RefreshAll();
        Status = $"Datenbank: {DatabasePath}";
    }

    partial void OnSelectedLogChanged(LogFileRecord? value)
    {
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

        foreach (var message in database.GetMessages(value.Id))
        {
            Messages.Add(message);
        }

        foreach (var variable in database.GetLogVariables(value.Id))
        {
            LogVariables.Add(variable);
        }

        foreach (var frame in database.GetOsdReplayFrames(value.Id))
        {
            OsdReplayFrames.Add(frame);
        }

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
        Fields.Clear();
        SelectedField = null;

        if (value is null)
        {
            return;
        }

        foreach (var field in database.GetFields(value.Id))
        {
            Fields.Add(field);
        }
    }

    partial void OnSelectedDefinitionChanged(MavlinkMessageDefinitionRecord? value)
    {
        DefinitionFields.Clear();
        SelectedDefinitionField = null;

        if (value is null)
        {
            return;
        }

        foreach (var field in database.GetDefinitionFields(value.Id))
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

        var path = await OpenLogFileRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        Status = "Import laeuft...";
        try
        {
            EnsureDefinitionsLoadedForImport();
            var result = await database.ImportLogAsync(path);
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
        IsBusy = true;
        Status = "OpenHD MAVLink-Header werden gelesen...";
        try
        {
            var definitions = MavlinkDefinitionLoader.LoadFromOpenHdHeaders();
            var count = database.ImportDefinitions(definitions);
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

        database.DeleteLog(SelectedLog.Id);
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

        database.SaveField(SelectedField);
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
        database.DeleteField(field.Id);
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

        SelectedVariable.Id = database.SaveVariable(SelectedVariable);
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
            database.DeleteVariable(variable.Id);
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

        database.SaveDefinition(SelectedDefinition);
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
        database.DeleteDefinition(definition.Id);
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

        database.SaveDefinitionField(SelectedDefinitionField);
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
        database.DeleteDefinitionField(field.Id);
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
        foreach (var log in database.GetLogs())
        {
            Logs.Add(log);
        }

        SelectedLog = Logs.FirstOrDefault(log => log.Id == selectedId) ?? Logs.FirstOrDefault();
    }

    private void RefreshVariables(long? selectedId = null)
    {
        Variables.Clear();
        foreach (var variable in database.GetVariables())
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
        foreach (var definition in database.GetDefinitions())
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
        if (Definitions.Count > 0)
        {
            return;
        }

        try
        {
            var definitions = MavlinkDefinitionLoader.LoadFromOpenHdHeaders();
            var count = database.ImportDefinitions(definitions);
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
        DebugEvents.Insert(0, record);
        while (DebugEvents.Count > 1000)
        {
            DebugEvents.RemoveAt(DebugEvents.Count - 1);
        }
    }

    private void UpdateCurrentOsdFrame()
    {
        if (OsdReplayFrames.Count == 0)
        {
            CurrentOsdFrame = new OsdReplayRecord();
            return;
        }

        CurrentOsdFrame = OsdReplayFrames.LastOrDefault(frame => frame.TimeMs <= OsdReplayTimeMs)
            ?? OsdReplayFrames.First();
    }
}
