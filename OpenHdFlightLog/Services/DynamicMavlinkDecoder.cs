using OpenHdFlightLog.Models;

namespace OpenHdFlightLog.Services;

public static class DynamicMavlinkDecoder
{
    // Der dynamische Decoder wird benutzt, sobald passende MAVLink-Definitionen in der
    // Datenbank vorhanden sind. Ohne Definitionen faellt die Anwendung auf den kleinen
    // eingebauten Decoder fuer Standard-Nachrichten zurueck.
    public static IReadOnlyList<DecodedField> Decode(MavlinkFrame frame, IReadOnlyList<MavlinkFieldDefinitionRecord> fields)
    {
        if (fields.Count == 0)
        {
            return MavlinkMessageDecoder.Decode(frame);
        }

        var decoded = new List<DecodedField>();
        foreach (var field in fields.OrderBy(field => field.PayloadOffset))
        {
            decoded.Add(DecodeField(frame.Payload, field));
        }

        return decoded.Count == 0
            ? [new DecodedField("payload_hex", Convert.ToHexString(frame.Payload), null, "")]
            : decoded;
    }

    private static DecodedField DecodeField(byte[] payload, MavlinkFieldDefinitionRecord field)
    {
        // Die Definition sagt, an welchem Byte-Offset ein Feld beginnt, welchen Datentyp
        // es hat und ob es ein Array ist. MAVLink speichert Zahlen little endian; .NET auf
        // den Zielplattformen liest diese Werte mit BitConverter passend aus.
        var elementSize = SizeOf(field.ValueType);
        var count = field.ArrayLength == 0 ? 1 : field.ArrayLength;
        var available = Math.Max(0, payload.Length - field.PayloadOffset);
        count = Math.Min(count, available / elementSize);

        if (field.ValueType is "char" && field.ArrayLength > 1)
        {
            // MAVLink-char-Arrays sind in der Praxis oft C-Strings: ASCII, mit Nullbytes
            // oder Leerzeichen aufgefuellt. Die Trimmung macht die Anzeige lesbar.
            if (count <= 0)
            {
                return new DecodedField(field.FieldName, "", null, field.Unit);
            }

            var text = System.Text.Encoding.ASCII.GetString(payload, field.PayloadOffset, count).TrimEnd('\0', ' ');
            return new DecodedField(field.FieldName, text, null, field.Unit);
        }

        if (count <= 1)
        {
            var value = ReadNumber(payload, field.PayloadOffset, field.ValueType);
            return new DecodedField(field.FieldName, value.text, value.numeric, field.Unit);
        }

        var values = new List<string>();
        for (var i = 0; i < count; i++)
        {
            values.Add(ReadNumber(payload, field.PayloadOffset + i * elementSize, field.ValueType).text);
        }

        return new DecodedField(field.FieldName, string.Join(", ", values), null, field.Unit);
    }

    private static (string text, double? numeric) ReadNumber(byte[] payload, int offset, string type)
    {
        // Es wird bewusst in einen 8-Byte-Puffer kopiert. Ist das Payload kuerzer als in
        // der Definition beschrieben, kann trotzdem ein Wert gelesen werden, ohne ueber
        // das Arrayende zuzugreifen.
        Span<byte> padded = stackalloc byte[8];
        var size = SizeOf(type);
        var available = Math.Max(0, Math.Min(size, payload.Length - offset));
        if (available > 0)
        {
            payload.AsSpan(offset, available).CopyTo(padded);
        }

        double value = type switch
        {
            "uint8_t" or "uint8" => padded[0],
            "int8_t" or "int8" => (sbyte)padded[0],
            "uint16_t" or "uint16" => BitConverter.ToUInt16(padded[..2]),
            "int16_t" or "int16" => BitConverter.ToInt16(padded[..2]),
            "uint32_t" or "uint32" => BitConverter.ToUInt32(padded[..4]),
            "int32_t" or "int32" => BitConverter.ToInt32(padded[..4]),
            "uint64_t" or "uint64" => BitConverter.ToUInt64(padded[..8]),
            "int64_t" or "int64" => BitConverter.ToInt64(padded[..8]),
            "float" => BitConverter.ToSingle(padded[..4]),
            "double" => BitConverter.ToDouble(padded[..8]),
            "char" => padded[0],
            _ => padded[0]
        };

        return (value.ToString("G17"), value);
    }

    private static int SizeOf(string type)
    {
        return type switch
        {
            "uint8_t" or "int8_t" or "uint8" or "int8" or "char" => 1,
            "uint16_t" or "int16_t" or "uint16" or "int16" => 2,
            "uint32_t" or "int32_t" or "uint32" or "int32" or "float" => 4,
            "uint64_t" or "int64_t" or "uint64" or "int64" or "double" => 8,
            _ => 1
        };
    }
}
