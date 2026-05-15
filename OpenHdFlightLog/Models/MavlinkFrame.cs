namespace OpenHdFlightLog.Models;

public sealed class MavlinkFrame
{
    public int PacketIndex { get; init; }
    public int ByteOffset { get; init; }
    public int Version { get; init; }
    public int Sequence { get; init; }
    public int SystemId { get; init; }
    public int ComponentId { get; init; }
    public int MessageId { get; init; }
    public byte[] Payload { get; init; } = [];
    public ushort Checksum { get; init; }
    public byte[] RawPacket { get; init; } = [];
}
