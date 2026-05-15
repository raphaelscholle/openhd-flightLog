using OpenHdFlightLog.Models;

namespace OpenHdFlightLog.Services;

public static class MavlinkParser
{
    // MAVLink-Pakete sind binaere Frames in einem Byte-Strom. Der Parser sucht nach
    // Startbytes und versucht ab dieser Position entweder MAVLink v1 (0xFE) oder v2
    // (0xFD) zu lesen. Ungueltige oder abgeschnittene Treffer werden ignoriert, damit
    // ein Log mit Rauschen oder Fremddaten trotzdem weiter durchsucht werden kann.
    public static IReadOnlyList<MavlinkFrame> Parse(byte[] bytes)
    {
        var frames = new List<MavlinkFrame>();
        var packetIndex = 0;

        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0xFE)
            {
                var frame = TryParseV1(bytes, i, packetIndex);
                if (frame is null)
                {
                    continue;
                }

                frames.Add(frame);
                packetIndex++;
                i += frame.RawPacket.Length - 1;
            }
            else if (bytes[i] == 0xFD)
            {
                var frame = TryParseV2(bytes, i, packetIndex);
                if (frame is null)
                {
                    continue;
                }

                frames.Add(frame);
                packetIndex++;
                i += frame.RawPacket.Length - 1;
            }
        }

        return frames;
    }

    private static MavlinkFrame? TryParseV1(byte[] bytes, int offset, int packetIndex)
    {
        // MAVLink v1 Header:
        // 0 magic, 1 payload length, 2 sequence, 3 system id, 4 component id, 5 message id.
        // Danach folgen Payload und 2 CRC-Bytes.
        if (offset + 8 > bytes.Length)
        {
            return null;
        }

        var payloadLength = bytes[offset + 1];
        var packetLength = 6 + payloadLength + 2;
        if (offset + packetLength > bytes.Length)
        {
            return null;
        }

        var payload = bytes.Skip(offset + 6).Take(payloadLength).ToArray();
        var checksumOffset = offset + 6 + payloadLength;
        return new MavlinkFrame
        {
            PacketIndex = packetIndex,
            ByteOffset = offset,
            Version = 1,
            Sequence = bytes[offset + 2],
            SystemId = bytes[offset + 3],
            ComponentId = bytes[offset + 4],
            MessageId = bytes[offset + 5],
            Payload = payload,
            Checksum = BitConverter.ToUInt16(bytes, checksumOffset),
            RawPacket = bytes.Skip(offset).Take(packetLength).ToArray()
        };
    }

    private static MavlinkFrame? TryParseV2(byte[] bytes, int offset, int packetIndex)
    {
        // MAVLink v2 Header:
        // 0 magic, 1 payload length, 2 incompat flags, 3 compat flags, 4 sequence,
        // 5 system id, 6 component id, 7..9 message id (24 Bit little endian).
        // Signierte Frames enthalten hinter der CRC zusaetzlich eine 13-Byte-Signatur.
        if (offset + 12 > bytes.Length)
        {
            return null;
        }

        var payloadLength = bytes[offset + 1];
        var signatureLength = (bytes[offset + 2] & 0x01) == 0x01 ? 13 : 0;
        var packetLength = 10 + payloadLength + 2 + signatureLength;
        if (offset + packetLength > bytes.Length)
        {
            return null;
        }

        var messageId = bytes[offset + 7] | (bytes[offset + 8] << 8) | (bytes[offset + 9] << 16);
        var payload = bytes.Skip(offset + 10).Take(payloadLength).ToArray();
        var checksumOffset = offset + 10 + payloadLength;
        return new MavlinkFrame
        {
            PacketIndex = packetIndex,
            ByteOffset = offset,
            Version = 2,
            Sequence = bytes[offset + 4],
            SystemId = bytes[offset + 5],
            ComponentId = bytes[offset + 6],
            MessageId = messageId,
            Payload = payload,
            Checksum = BitConverter.ToUInt16(bytes, checksumOffset),
            RawPacket = bytes.Skip(offset).Take(packetLength).ToArray()
        };
    }
}
