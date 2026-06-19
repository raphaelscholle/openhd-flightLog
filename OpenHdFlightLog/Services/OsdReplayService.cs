using OpenHdFlightLog.Models;

namespace OpenHdFlightLog.Services;

public static class OsdReplayService
{
    public static IReadOnlyList<OsdReplayRecord> BuildFrames(IReadOnlyList<LogVariableRecord> variables)
    {
        var frames = new List<OsdReplayRecord>();

        foreach (var group in variables.GroupBy(v => new { v.TimeMs, v.Timestamp }).OrderBy(g => g.Key.TimeMs))
        {
            var values = group.ToDictionary(
                v => $"{v.Route}.{v.MessageName}.{v.FieldName}",
                v => v.ValueText,
                StringComparer.OrdinalIgnoreCase);

            var frame = new OsdReplayRecord
            {
                TimeMs = group.Key.TimeMs,
                Timestamp = group.Key.Timestamp,
                LinkQuality = Lookup(values, "OpenHD Ground.OPENHD_STATS_MONITOR_MODE_WIFI_CARD.rx_signal_quality_adapter"),
                Rssi = Lookup(values, "OpenHD Ground.OPENHD_STATS_MONITOR_MODE_WIFI_CARD.rx_rssi"),
                Snr = Lookup(values, "OpenHD Ground.OPENHD_STATS_MONITOR_MODE_WIFI_CARD.rx_snr_antenna1"),
                AirVideoBitrate = FormatBitrate(Lookup(values, "OpenHD Air.OPENHD_STATS_WB_VIDEO_AIR.curr_measured_encoder_bitrate")),
                InjectedBitrate = FormatBitrate(Lookup(values, "OpenHD Air.OPENHD_STATS_WB_VIDEO_AIR.curr_injected_bitrate")),
                TxPps = Lookup(values, "OpenHD Ground.OPENHD_STATS_TELEMETRY.curr_tx_pps"),
                RxPps = Lookup(values, "OpenHD Ground.OPENHD_STATS_TELEMETRY.curr_rx_pps"),
                PacketLoss = Lookup(values, "OpenHD Ground.OPENHD_STATS_TELEMETRY.curr_rx_packet_loss_perc"),
                DroppedFrames = Lookup(values, "OpenHD Air.OPENHD_STATS_WB_VIDEO_AIR.curr_dropped_frames"),
                CardTemperature = Lookup(values, "OpenHD Ground.OPENHD_STATS_MONITOR_MODE_WIFI_CARD.card_temperature")
            };

            if (HasOsdValue(frame))
            {
                frames.Add(frame);
            }
        }

        return frames;
    }

    private static string Lookup(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) ? value : "-";
    }

    private static string FormatBitrate(string value)
    {
        return double.TryParse(value, out var bitrate) ? $"{bitrate / 1_000_000:0.00} Mbps" : value;
    }

    private static bool HasOsdValue(OsdReplayRecord frame)
    {
        return frame.LinkQuality != "-"
            || frame.Rssi != "-"
            || frame.Snr != "-"
            || frame.AirVideoBitrate != "-"
            || frame.InjectedBitrate != "-"
            || frame.TxPps != "-"
            || frame.RxPps != "-"
            || frame.PacketLoss != "-"
            || frame.DroppedFrames != "-"
            || frame.CardTemperature != "-";
    }
}

