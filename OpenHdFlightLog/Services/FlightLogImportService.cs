using OpenHdFlightLog.Models;

namespace OpenHdFlightLog.Services;

public sealed class FlightLogImportService
{
    private readonly FlightLogDatabase database;
    private readonly Action<DebugEventRecord>? debug;

    public FlightLogImportService(FlightLogDatabase database, Action<DebugEventRecord>? debug = null)
    {
        this.database = database;
        this.debug = debug;
    }

    public async Task<ImportResult> ImportLogAsync(string path)
    {
        Log("IMPORT", $"read file {path}");
        var bytes = await File.ReadAllBytesAsync(path);
        var frames = MavlinkParser.Parse(bytes);
        var fieldDefinitions = database.GetFieldDefinitionsByMessageId();
        var messageDefinitions = database.GetDefinitions().ToDictionary(definition => definition.MessageId);
        var packetTimings = OLogDebugSidecar.LoadPacketTimings(path);

        if (packetTimings.Count > 0)
        {
            Log("IMPORT", $"loaded OSD replay sidecar timings: {packetTimings.Count} packets");
        }

        var importedMessages = new List<ImportedMavlinkMessage>();
        foreach (var frame in frames)
        {
            fieldDefinitions.TryGetValue(frame.MessageId, out var fields);
            messageDefinitions.TryGetValue(frame.MessageId, out var messageDefinition);
            packetTimings.TryGetValue(frame.PacketIndex, out var timing);

            importedMessages.Add(new ImportedMavlinkMessage(
                frame,
                timing,
                messageDefinition?.Name ?? MavlinkMessageDecoder.GetMessageName(frame.MessageId),
                messageDefinition?.Dialect ?? "",
                RouteFor(frame.SystemId),
                DynamicMavlinkDecoder.Decode(frame, fields ?? [])));
        }

        return database.SaveImportedLog(path, importedMessages);
    }

    private void Log(string category, string detail)
    {
        debug?.Invoke(new DebugEventRecord
        {
            Timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff"),
            Category = category,
            Detail = detail
        });
    }

    private static string RouteFor(int systemId)
    {
        return systemId switch
        {
            100 => "OpenHD Ground",
            101 => "OpenHD Air",
            255 => "QOpenHD",
            1 or 0 => "Flight Controller",
            _ => $"System {systemId}"
        };
    }
}

public sealed record ImportedMavlinkMessage(
    MavlinkFrame Frame,
    PacketTiming? Timing,
    string MessageName,
    string Dialect,
    string Route,
    IReadOnlyList<DecodedField> Fields);

public sealed record ImportResult(long LogId, int MessageCount, string DatabasePath);

