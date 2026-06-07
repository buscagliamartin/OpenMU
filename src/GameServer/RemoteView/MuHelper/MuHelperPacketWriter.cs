// <copyright file="MuHelperPacketWriter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.MuHelper;

using System.Buffers.Binary;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.Network;

/// <summary>
/// Manual Mu Helper packet writer used while generated packet files stay untouched.
/// </summary>
internal static class MuHelperPacketWriter
{
    private const int StatusPacketLength = 16;
    private const int ConfigurationPacketLength = 4 + MuHelperSettingsSerializer.BlobLength;

    /// <summary>
    /// Sends <c>C1 10 BF 51</c> Mu Helper status update.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="consumeMoney">If set to <c>true</c>, this packet only reports consumed money.</param>
    /// <param name="money">The consumed zen.</param>
    /// <param name="pauseStatus">If set to <c>true</c>, the client pauses the helper.</param>
    public static ValueTask SendStatusUpdateAsync(this IConnection? connection, bool consumeMoney, uint money, bool pauseStatus)
    {
        if (connection is null)
        {
            return default;
        }

        return connection.SendAsync(() =>
        {
            var packet = connection.Output.GetSpan(StatusPacketLength)[..StatusPacketLength];
            packet.Clear();
            packet[0] = 0xC1;
            packet[1] = StatusPacketLength;
            packet[2] = 0xBF;
            packet[3] = 0x51;
            packet[4] = consumeMoney ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt32LittleEndian(packet[8..], money);
            packet[12] = pauseStatus ? (byte)1 : (byte)0;
            return StatusPacketLength;
        });
    }

    /// <summary>
    /// Sends <c>C2 01 05 AE</c> Mu Helper configuration update with a 257-byte blob.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="helperData">The raw helper configuration blob.</param>
    public static ValueTask SendConfigurationDataAsync(this IConnection? connection, Memory<byte> helperData)
    {
        if (connection is null)
        {
            return default;
        }

        return connection.SendAsync(() =>
        {
            var packet = connection.Output.GetSpan(ConfigurationPacketLength)[..ConfigurationPacketLength];
            packet.Clear();
            packet[0] = 0xC2;
            BinaryPrimitives.WriteUInt16BigEndian(packet[1..], ConfigurationPacketLength);
            packet[3] = 0xAE;
            helperData.Span[..Math.Min(helperData.Length, MuHelperSettingsSerializer.BlobLength)].CopyTo(packet[4..]);
            return ConfigurationPacketLength;
        });
    }
}
