using System.Text.Json;

namespace OpenHdFlightLog.Services;

public sealed record PacketTiming(long ElapsedMs, string Timestamp);

public static class OLogDebugSidecar
{
    // Manche Beispiel- oder Replay-Logs besitzen eine .debug.jsonl-Datei daneben.
    // Darin steht, welche Paketindizes zu welchem Replay-Zeitpunkt gehoeren. Diese
    // Zusatzdaten machen die OSD-Ansicht zeitlich genauer als der reine Paketindex.
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

            // Ein JSONL-Eintrag kann mehrere aufeinanderfolgende Pakete beschreiben.
            // Alle betroffenen Paketindizes erhalten dieselbe Zeitinformation.
            for (var i = 0; i < count; i++)
            {
                timings[first + i] = new PacketTiming(elapsed, timestamp);
            }
        }

        return timings;
    }

    private static string? ResolveSidecarPath(string logPath)
    {
        // Unterstuetzte Namensformen:
        // - sample.oLog.debug.jsonl
        // - sample.debug.jsonl
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
