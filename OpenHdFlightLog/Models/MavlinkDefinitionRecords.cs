using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenHdFlightLog.Models;

public partial class MavlinkMessageDefinitionRecord : ObservableObject
{
    // Datenbank-Primary-Key der Definition. MessageId ist die fachliche MAVLink-ID.
    public long Id { get; set; }

    [ObservableProperty]
    private int messageId;

    [ObservableProperty]
    private string name = "";

    [ObservableProperty]
    private string dialect = "";

    [ObservableProperty]
    private int payloadLength;

    [ObservableProperty]
    private int crcExtra;

    [ObservableProperty]
    private string sourceFile = "";

    [ObservableProperty]
    private string notes = "";
}

public partial class MavlinkFieldDefinitionRecord : ObservableObject
{
    public long Id { get; set; }

    // Fremdschluessel auf message_definitions.id.
    public long DefinitionId { get; set; }

    [ObservableProperty]
    private string fieldName = "";

    [ObservableProperty]
    private string valueType = "";

    [ObservableProperty]
    private int arrayLength;

    [ObservableProperty]
    private int payloadOffset;

    [ObservableProperty]
    private string unit = "";

    [ObservableProperty]
    private string description = "";
}
