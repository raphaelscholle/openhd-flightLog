namespace OpenHdFlightLog.Models;

public sealed class UdpReplayPacket
{
    public int PacketIndex { get; set; }
    public long TimeMs { get; set; }
    public byte[] RawPacket { get; set; } = [];
}
