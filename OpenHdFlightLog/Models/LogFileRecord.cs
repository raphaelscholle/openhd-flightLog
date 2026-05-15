namespace OpenHdFlightLog.Models;

public sealed class LogFileRecord
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public string Path { get; set; } = "";
    public string ImportedAt { get; set; } = "";
    public int MessageCount { get; set; }
}
