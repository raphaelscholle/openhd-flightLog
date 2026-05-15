namespace OpenHdFlightLog.Models;

public sealed class LogVariableRecord
{
    public long FieldId { get; set; }
    public long MessageRowId { get; set; }
    public int PacketIndex { get; set; }
    public long TimeMs { get; set; }
    public string Timestamp { get; set; } = "";
    public string Route { get; set; } = "";
    public string Dialect { get; set; } = "";
    public int SystemId { get; set; }
    public int ComponentId { get; set; }
    public int MessageId { get; set; }
    public string MessageName { get; set; } = "";
    public string FieldName { get; set; } = "";
    public string ValueText { get; set; } = "";
    public double? NumericValue { get; set; }
    public string Unit { get; set; } = "";
}
