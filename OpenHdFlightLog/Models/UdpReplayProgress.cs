namespace OpenHdFlightLog.Models;

public sealed class UdpReplayProgress
{
    public int SentPackets { get; set; }
    public int TotalPackets { get; set; }
    public long TimeMs { get; set; }
}
