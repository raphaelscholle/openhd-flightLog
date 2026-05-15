namespace OpenHdFlightLog.Models;

public sealed class DebugEventRecord
{
    public string Timestamp { get; set; } = "";
    public string Category { get; set; } = "";
    public string Detail { get; set; } = "";
}
