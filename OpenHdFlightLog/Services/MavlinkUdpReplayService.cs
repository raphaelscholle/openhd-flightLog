using System.Net.Sockets;
using OpenHdFlightLog.Models;

namespace OpenHdFlightLog.Services;

public static class MavlinkUdpReplayService
{
    public static async Task<int> ReplayAsync(
        IReadOnlyList<UdpReplayPacket> packets,
        string host,
        int port,
        long startTimeMs,
        Func<double> getPlaybackSpeed,
        Func<long?> takeRequestedSeekTimeMs,
        CancellationToken cancellationToken,
        IProgress<UdpReplayProgress>? progress = null)
    {
        using var udp = new UdpClient();
        udp.Connect(host, port);

        var sent = 0;
        long? previousTimeMs = null;
        var replayPackets = packets
            .OrderBy(packet => packet.PacketIndex)
            .ToList();

        var index = FindPacketIndex(replayPackets, startTimeMs);
        while (index < replayPackets.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seekTimeMs = takeRequestedSeekTimeMs();
            if (seekTimeMs is not null)
            {
                index = FindPacketIndex(replayPackets, seekTimeMs.Value);
                previousTimeMs = null;
                continue;
            }

            var packet = replayPackets[index];
            if (previousTimeMs is not null)
            {
                var speed = Math.Clamp(getPlaybackSpeed(), 0.1, 8.0);
                var delayMs = Math.Clamp((packet.TimeMs - previousTimeMs.Value) / speed, 0, 1000);
                if (delayMs > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
                }
            }

            await udp.SendAsync(packet.RawPacket, packet.RawPacket.Length);
            sent++;
            previousTimeMs = packet.TimeMs;
            progress?.Report(new UdpReplayProgress
            {
                SentPackets = sent,
                TotalPackets = replayPackets.Count,
                TimeMs = packet.TimeMs
            });

            index++;
        }

        return sent;
    }

    private static int FindPacketIndex(IReadOnlyList<UdpReplayPacket> packets, long timeMs)
    {
        for (var i = 0; i < packets.Count; i++)
        {
            if (packets[i].TimeMs >= timeMs)
            {
                return i;
            }
        }

        return packets.Count;
    }
}
