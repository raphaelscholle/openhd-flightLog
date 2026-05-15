namespace OpenHdFlightLog.Models;

public sealed class OsdReplayRecord
{
    public long TimeMs { get; set; }
    public string Timestamp { get; set; } = "";
    public string LinkQuality { get; set; } = "-";
    public string Rssi { get; set; } = "-";
    public string Snr { get; set; } = "-";
    public string AirVideoBitrate { get; set; } = "-";
    public string InjectedBitrate { get; set; } = "-";
    public string TxPps { get; set; } = "-";
    public string RxPps { get; set; } = "-";
    public string PacketLoss { get; set; } = "-";
    public string DroppedFrames { get; set; } = "-";
    public string CardTemperature { get; set; } = "-";
}
