using OpenHdFlightLog.Models;

namespace OpenHdFlightLog.Services;

public sealed record DecodedField(string Name, string ValueText, double? NumericValue, string Unit);

public static class MavlinkMessageDecoder
{
    // Kleiner Fallback-Katalog fuer Standard-MAVLink-Nachrichten. Sobald OpenHD-
    // Definitionen geladen sind, uebernimmt DynamicMavlinkDecoder das genaue Feldlayout.
    private static readonly Dictionary<int, string> MessageNames = new()
    {
        [0] = "HEARTBEAT",
        [1] = "SYS_STATUS",
        [24] = "GPS_RAW_INT",
        [30] = "ATTITUDE",
        [33] = "GLOBAL_POSITION_INT",
        [35] = "RC_CHANNELS_RAW",
        [74] = "VFR_HUD",
        [147] = "BATTERY_STATUS"
    };

    public static string GetMessageName(int messageId)
    {
        return MessageNames.TryGetValue(messageId, out var name) ? name : $"MSG_{messageId}";
    }

    public static IReadOnlyList<DecodedField> Decode(MavlinkFrame frame)
    {
        // Fuer bekannte Message-IDs werden die Felder manuell aus dem Payload gelesen.
        // Unbekannte Nachrichten bleiben trotzdem sichtbar, indem der Payload als Hex-
        // String gespeichert wird.
        var p = frame.Payload;
        var fields = frame.MessageId switch
        {
            0 => DecodeHeartbeat(p),
            1 => DecodeSysStatus(p),
            24 => DecodeGpsRawInt(p),
            30 => DecodeAttitude(p),
            33 => DecodeGlobalPositionInt(p),
            74 => DecodeVfrHud(p),
            147 => DecodeBatteryStatus(p),
            _ => []
        };

        if (fields.Count > 0)
        {
            return fields;
        }

        return [new DecodedField("payload_hex", Convert.ToHexString(p), null, "")];
    }

    private static List<DecodedField> DecodeHeartbeat(byte[] p)
    {
        // Jede Decode-Methode prueft zuerst die Mindestlaenge. So fuehrt ein kaputter
        // oder gekuerzter Frame nicht zu einem Indexfehler.
        if (p.Length < 9) return [];
        return
        [
            U32(p, 0, "custom_mode"),
            U8(p, 4, "type"),
            U8(p, 5, "autopilot"),
            U8(p, 6, "base_mode"),
            U8(p, 7, "system_status"),
            U8(p, 8, "mavlink_version")
        ];
    }

    private static List<DecodedField> DecodeSysStatus(byte[] p)
    {
        if (p.Length < 31) return [];
        return
        [
            U32(p, 0, "onboard_control_sensors_present"),
            U32(p, 4, "onboard_control_sensors_enabled"),
            U32(p, 8, "onboard_control_sensors_health"),
            U16(p, 12, "load", "0.1%"),
            U16(p, 14, "voltage_battery", "mV"),
            I16(p, 16, "current_battery", "cA"),
            U16(p, 18, "drop_rate_comm", "0.01%"),
            U16(p, 20, "errors_comm"),
            U16(p, 22, "errors_count1"),
            U16(p, 24, "errors_count2"),
            U16(p, 26, "errors_count3"),
            U16(p, 28, "errors_count4"),
            I8(p, 30, "battery_remaining", "%")
        ];
    }

    private static List<DecodedField> DecodeGpsRawInt(byte[] p)
    {
        if (p.Length < 30) return [];
        return
        [
            U64(p, 0, "time_usec", "us"),
            ScaledI32(p, 8, "lat", 1e-7, "deg"),
            ScaledI32(p, 12, "lon", 1e-7, "deg"),
            I32(p, 16, "alt", "mm"),
            U16(p, 20, "eph", "cm"),
            U16(p, 22, "epv", "cm"),
            U16(p, 24, "vel", "cm/s"),
            U16(p, 26, "cog", "cdeg"),
            U8(p, 28, "fix_type"),
            U8(p, 29, "satellites_visible")
        ];
    }

    private static List<DecodedField> DecodeAttitude(byte[] p)
    {
        if (p.Length < 28) return [];
        return
        [
            U32(p, 0, "time_boot_ms", "ms"),
            F32(p, 4, "roll", "rad"),
            F32(p, 8, "pitch", "rad"),
            F32(p, 12, "yaw", "rad"),
            F32(p, 16, "rollspeed", "rad/s"),
            F32(p, 20, "pitchspeed", "rad/s"),
            F32(p, 24, "yawspeed", "rad/s")
        ];
    }

    private static List<DecodedField> DecodeGlobalPositionInt(byte[] p)
    {
        if (p.Length < 28) return [];
        return
        [
            U32(p, 0, "time_boot_ms", "ms"),
            ScaledI32(p, 4, "lat", 1e-7, "deg"),
            ScaledI32(p, 8, "lon", 1e-7, "deg"),
            I32(p, 12, "alt", "mm"),
            I32(p, 16, "relative_alt", "mm"),
            I16(p, 20, "vx", "cm/s"),
            I16(p, 22, "vy", "cm/s"),
            I16(p, 24, "vz", "cm/s"),
            U16(p, 26, "hdg", "cdeg")
        ];
    }

    private static List<DecodedField> DecodeVfrHud(byte[] p)
    {
        if (p.Length < 20) return [];
        return
        [
            F32(p, 0, "airspeed", "m/s"),
            F32(p, 4, "groundspeed", "m/s"),
            F32(p, 8, "alt", "m"),
            F32(p, 12, "climb", "m/s"),
            I16(p, 16, "heading", "deg"),
            U16(p, 18, "throttle", "%")
        ];
    }

    private static List<DecodedField> DecodeBatteryStatus(byte[] p)
    {
        if (p.Length < 40) return [];
        var fields = new List<DecodedField>
        {
            I32(p, 24, "current_consumed", "mAh"),
            I32(p, 28, "energy_consumed", "hJ"),
            I16(p, 32, "temperature", "cdegC"),
            I16(p, 34, "current_battery", "cA"),
            I8(p, 36, "id"),
            U8(p, 37, "battery_function"),
            U8(p, 38, "type"),
            I8(p, 39, "battery_remaining", "%")
        };

        for (var i = 0; i < 10 && 4 + i * 2 + 1 < p.Length; i++)
        {
            fields.Add(U16(p, 4 + i * 2, $"voltage_cell_{i + 1}", "mV"));
        }

        return fields;
    }

    private static DecodedField U8(byte[] p, int o, string n, string u = "") => Field(n, p[o], u);
    private static DecodedField I8(byte[] p, int o, string n, string u = "") => Field(n, (sbyte)p[o], u);
    private static DecodedField U16(byte[] p, int o, string n, string u = "") => Field(n, BitConverter.ToUInt16(p, o), u);
    private static DecodedField I16(byte[] p, int o, string n, string u = "") => Field(n, BitConverter.ToInt16(p, o), u);
    private static DecodedField U32(byte[] p, int o, string n, string u = "") => Field(n, BitConverter.ToUInt32(p, o), u);
    private static DecodedField I32(byte[] p, int o, string n, string u = "") => Field(n, BitConverter.ToInt32(p, o), u);
    private static DecodedField U64(byte[] p, int o, string n, string u = "") => Field(n, BitConverter.ToUInt64(p, o), u);
    private static DecodedField F32(byte[] p, int o, string n, string u = "") => Field(n, BitConverter.ToSingle(p, o), u);

    private static DecodedField ScaledI32(byte[] p, int o, string n, double scale, string u)
    {
        var value = BitConverter.ToInt32(p, o) * scale;
        return new DecodedField(n, value.ToString("G17"), value, u);
    }

    private static DecodedField Field(string name, double value, string unit)
    {
        return new DecodedField(name, value.ToString("G17"), value, unit);
    }
}
