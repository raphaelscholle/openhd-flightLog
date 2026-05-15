namespace OpenHdFlightLog.Models;

public sealed class MavlinkMessageRecord
{
    public long Id { get; set; }
    public int PacketIndex { get; set; }
    public long TimeMs { get; set; }
    public string Timestamp { get; set; } = "";
    public int ByteOffset { get; set; }
    public int Version { get; set; }
    public int Sequence { get; set; }
    public int SystemId { get; set; }
    public int ComponentId { get; set; }
    public int MessageId { get; set; }
    public string MessageName { get; set; } = "";
    public string Route { get; set; } = "";
    public string Dialect { get; set; } = "";
    public int PayloadLength { get; set; }
    public string Checksum { get; set; } = "";
}
