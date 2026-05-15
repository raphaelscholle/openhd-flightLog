namespace OpenHdFlightLog.Models;

public sealed class MavlinkFrame
{
    // Laufende Nummer im importierten Log. Sie ist nicht zwingend identisch mit der
    // MAVLink-Sequence, weil Sequence pro Sender laeuft und ueberlaufen kann.
    public int PacketIndex { get; init; }

    // Byteposition des Startbytes im Original-Log. Hilfreich zum Debuggen und fuer
    // Vergleiche mit Hex-Editoren.
    public int ByteOffset { get; init; }

    // MAVLink-Protokollversion: 1 fuer Startbyte 0xFE, 2 fuer Startbyte 0xFD.
    public int Version { get; init; }
    public int Sequence { get; init; }
    public int SystemId { get; init; }
    public int ComponentId { get; init; }
    public int MessageId { get; init; }

    // Nutzdaten ohne Header, CRC und optionale MAVLink-v2-Signatur.
    public byte[] Payload { get; init; } = [];
    public ushort Checksum { get; init; }

    // Vollstaendiges Paket aus dem Log. Wird als Hex-String in der Datenbank abgelegt.
    public byte[] RawPacket { get; init; } = [];
}
