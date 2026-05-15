using System.Text.RegularExpressions;
using OpenHdFlightLog.Models;

namespace OpenHdFlightLog.Services;

public sealed record LoadedMavlinkDefinition(
    MavlinkMessageDefinitionRecord Message,
    IReadOnlyList<MavlinkFieldDefinitionRecord> Fields);

public static partial class MavlinkDefinitionLoader
{
    // Lokaler Standardpfad zu einer OpenHD-Checkout-Struktur. Der Pfad wird nur als
    // Komfort-Default verwendet; LoadFromOpenHdHeaders akzeptiert auch einen anderen
    // Repository-Pfad.
    public const string DefaultOpenHdRoot = @"C:\Users\Raphael\Documents\GitHub\drivers_\OpenHD";

    public static string DefaultHeaderRoot =>
        Path.Combine(DefaultOpenHdRoot, "OpenHD", "ohd_telemetry", "lib", "mavlink-headers", "mavlink", "v2.0");

    public static IReadOnlyList<LoadedMavlinkDefinition> LoadFromOpenHdHeaders(string? repositoryRoot = null)
    {
        // OpenHD liefert generierte MAVLink-C-Header. Diese enthalten alle Informationen,
        // die fuer die Datenbank-Definitionen gebraucht werden: Message-ID, Name,
        // Payload-Laenge, CRC extra und Feldlayout mit Offsets.
        var headerRoot = ResolveHeaderRoot(repositoryRoot);
        if (!Directory.Exists(headerRoot))
        {
            throw new DirectoryNotFoundException(headerRoot);
        }

        var definitions = new List<LoadedMavlinkDefinition>();
        foreach (var file in Directory.EnumerateFiles(headerRoot, "mavlink_msg_*.h", SearchOption.AllDirectories))
        {
            var loaded = TryLoadFile(file, headerRoot);
            if (loaded is not null)
            {
                definitions.Add(loaded);
            }
        }

        return definitions
            // Einige Dialekte koennen dieselbe Message-ID enthalten. Wenn eine OpenHD-
            // Variante vorhanden ist, ist sie fuer diese Anwendung die passendere.
            .GroupBy(definition => definition.Message.MessageId)
            .Select(group => group.OrderByDescending(definition => definition.Message.Dialect.Equals("openhd", StringComparison.OrdinalIgnoreCase)).First())
            .OrderBy(definition => definition.Message.Dialect)
            .ThenBy(definition => definition.Message.MessageId)
            .ToList();
    }

    private static string ResolveHeaderRoot(string? repositoryRoot)
    {
        // Unterstuetzt zwei typische Checkout-Layouts:
        // 1. Repository-Wurzel enthaelt OpenHD/...
        // 2. repositoryRoot zeigt bereits in den OpenHD-Unterordner.
        var root = string.IsNullOrWhiteSpace(repositoryRoot) ? DefaultOpenHdRoot : repositoryRoot;
        var direct = Path.Combine(root, "OpenHD", "ohd_telemetry", "lib", "mavlink-headers", "mavlink", "v2.0");
        if (Directory.Exists(direct))
        {
            return direct;
        }

        var nested = Path.Combine(root, "ohd_telemetry", "lib", "mavlink-headers", "mavlink", "v2.0");
        return Directory.Exists(nested) ? nested : direct;
    }

    private static LoadedMavlinkDefinition? TryLoadFile(string path, string headerRoot)
    {
        // Jede mavlink_msg_*.h-Datei beschreibt genau einen Message-Typ. Dateien ohne
        // passende MAVLINK_MSG_ID_* Definition werden ignoriert.
        var text = File.ReadAllText(path);
        var idMatch = MessageIdRegex().Match(text);
        if (!idMatch.Success)
        {
            return null;
        }

        var macro = idMatch.Groups["macro"].Value;
        var messageId = int.Parse(idMatch.Groups["id"].Value);
        var length = IntDefine(text, $@"MAVLINK_MSG_ID_{Regex.Escape(macro)}_LEN");
        var crc = IntDefine(text, $@"MAVLINK_MSG_ID_{Regex.Escape(macro)}_CRC");
        var dialect = new DirectoryInfo(Path.GetDirectoryName(path)!).Name;
        var sourceFile = Path.GetRelativePath(headerRoot, path);
        var descriptions = ParamDescriptions(text);

        var fields = FieldRegex()
            .Matches(text)
            .Select(match => new MavlinkFieldDefinitionRecord
            {
                FieldName = match.Groups["name"].Value,
                ValueType = match.Groups["type"].Value.Replace("MAVLINK_TYPE_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant(),
                ArrayLength = int.Parse(match.Groups["array"].Value),
                PayloadOffset = int.Parse(match.Groups["offset"].Value),
                Description = descriptions.GetValueOrDefault(match.Groups["name"].Value, "")
            })
            // Der Header kann ein Feld mehrfach erwaehnen, z.B. in verschiedenen
            // Hilfstabellen. Fuer die Dekodierung reicht der erste Feldlayout-Eintrag.
            .GroupBy(field => field.FieldName)
            .Select(group => group.First())
            .OrderBy(field => field.PayloadOffset)
            .ToList();

        return new LoadedMavlinkDefinition(
            new MavlinkMessageDefinitionRecord
            {
                MessageId = messageId,
                Name = macro,
                Dialect = dialect,
                PayloadLength = length,
                CrcExtra = crc,
                SourceFile = sourceFile
            },
            fields);
    }

    private static int IntDefine(string text, string macro)
    {
        var match = Regex.Match(text, $@"^\s*#define\s+{macro}\s+(?<value>\d+)\s*$", RegexOptions.Multiline);
        return match.Success ? int.Parse(match.Groups["value"].Value) : 0;
    }

    private static Dictionary<string, string> ParamDescriptions(string text)
    {
        return ParamRegex()
            .Matches(text)
            .GroupBy(match => match.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Groups["text"].Value.Trim(),
                StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^\s*#define\s+MAVLINK_MSG_ID_(?<macro>(?!\d)[A-Z0-9_]+)\s+(?<id>\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex MessageIdRegex();

    [GeneratedRegex(@"\{\s*""(?<name>[^""]+)""\s*,\s*NULL\s*,\s*(?<type>MAVLINK_TYPE_[A-Z0-9_]+)\s*,\s*(?<array>\d+)\s*,\s*(?<offset>\d+)\s*,\s*offsetof\(", RegexOptions.Multiline)]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"^\s*\*\s*@param\s+(?<name>[A-Za-z0-9_]+)\s+(?<text>.*)$", RegexOptions.Multiline)]
    private static partial Regex ParamRegex();
}
