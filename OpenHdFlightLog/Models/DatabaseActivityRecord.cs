namespace OpenHdFlightLog.Models;

public sealed class DatabaseActivityRecord
{
    public long Id { get; set; }
    public string ChangedAt { get; set; } = "";
    public string TableName { get; set; } = "";
    public string ActivityType { get; set; } = "";
    public long? RowId { get; set; }
    public string Summary { get; set; } = "";
}
