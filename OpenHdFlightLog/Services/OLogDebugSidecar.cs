using System.Text.Json;

namespace OpenHdFlightLog.Services;

public sealed record PacketTiming(long ElapsedMs, string Timestamp);

public static class OLogDebugSidecar
{
    public static IReadOnlyDictionary<int, PacketTiming> LoadPacketTimings(string logPath)
    {
        var sidecar = ResolveSidecarPath(logPath);
        if (sidecar is null)
        {
            return new Dictionary<int, PacketTiming>();
        }

        var timings = new Dictionary<int, PacketTiming>();
        foreach (var line in File.ReadLines(sidecar))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("packet_first", out var firstProperty) ||
                !root.TryGetProperty("packet_count", out var countProperty) ||
                !root.TryGetProperty("elapsed_ms", out var elapsedProperty) ||
                !root.TryGetProperty("datetime_utc", out var timestampProperty))
            {
                continue;
            }

            var first = firstProperty.GetInt32();
            var count = countProperty.GetInt32();
            var elapsed = elapsedProperty.GetInt64();
            var timestamp = timestampProperty.GetString() ?? "";

            for (var i = 0; i < count; i++)
            {
                timings[first + i] = new PacketTiming(elapsed, timestamp);
            }
        }

        return timings;
    }

    private static string? ResolveSidecarPath(string logPath)
    {
        var direct = logPath + ".debug.jsonl";
        if (File.Exists(direct))
        {
            return direct;
        }

        var directory = Path.GetDirectoryName(logPath) ?? "";
        var withoutExtension = Path.GetFileNameWithoutExtension(logPath);
        var sibling = Path.Combine(directory, withoutExtension + ".debug.jsonl");
        return File.Exists(sibling) ? sibling : null;
    }
}
