// <copyright file="DuelLadderViewPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.MuHelper;

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.Views.Duel;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// BarnaMu: default implementation of the Duel Ladder view plugins. Writes the two
/// Duel Ladder responses to the client as 0xBF, sub-code 0x32 packets:
/// op=0 top-10 (variable length), op=1 own profile (21 bytes).
/// </summary>
[PlugIn]
[Display(Name = nameof(DuelLadderViewPlugIn), Description = "BarnaMu: sends Duel Ladder top-10 and profile responses to the client.")]
[Guid("D5E30CF3-9D4A-4A3E-9F56-C3D4E5F6A7B8")]
public class DuelLadderViewPlugIn : IDuelLadderTopPlugIn, IDuelLadderProfilePlugIn, IDuelLadderWaitingPlugIn, IDuelLadderHistoryPlugIn, IDuelLadderHallOfFamePlugIn
{
    private const int NameBytes = 10;
    private const int EntryBytes = NameBytes + 1 + 4 + 4 + 4; // name[10] + class[1] + rating[4] + wins[4] + losses[4] = 23
    private const int WaitingEntryBytes = NameBytes + 1 + 4 + 1 + 1 + 4; // name[10]+class[1]+rating[4]+tier[1]+bracket[1]+wait[4] = 21
    private const int TopHeaderBytes = 7; // C1 + len + 0xBF + 0x32 + op + bracket/selfListed + count
    private const int ProfilePacketLength = 21; // header(4) + op(1) + bracket(1) + tier(1) + rating(4) + wins(4) + losses(4) + rank(2)
    private const int MaxTopEntries = 10;
    private const int MaxWaitingEntries = 10; // keep 7 + count*21 within the 0xC1 single-byte length

    private readonly RemotePlayer _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="DuelLadderViewPlugIn"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public DuelLadderViewPlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc />
    public async ValueTask ShowTopAsync(byte bracket, IReadOnlyList<DuelLadderEntry> entries)
    {
        var connection = this._player.Connection;
        if (connection is null)
        {
            return;
        }

        var count = Math.Min(entries.Count, MaxTopEntries);
        var length = TopHeaderBytes + (count * EntryBytes);

        int Write()
        {
            var span = connection.Output.GetSpan(length)[..length];
            span.Clear();
            span[0] = 0xC1;
            span[1] = (byte)length;
            span[2] = 0xBF;
            span[3] = 0x32;
            span[4] = 0x00; // op = top
            span[5] = bracket;
            span[6] = (byte)count;

            for (int i = 0; i < count; i++)
            {
                var entry = entries[i];
                var offset = TopHeaderBytes + (i * EntryBytes);
                WriteName(span.Slice(offset, NameBytes), entry.Name);
                span[offset + NameBytes] = entry.ClassNumber;
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + NameBytes + 1, 4), (uint)entry.Rating);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + NameBytes + 5, 4), (uint)entry.Wins);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + NameBytes + 9, 4), (uint)entry.Losses);
            }

            return length;
        }

        await connection.SendAsync(Write).ConfigureAwait(false);

        // Guild names are sent in a separate op-5 packet: 10 entries * (23 + guild) would overflow
        // the op-0 packet's single-byte (0xC1) length, so op-0 stays unchanged and guilds ride along
        // here, matched to the entries by index.
        var guildLength = TopHeaderBytes + (count * NameBytes);

        int WriteGuilds()
        {
            var span = connection.Output.GetSpan(guildLength)[..guildLength];
            span.Clear();
            span[0] = 0xC1;
            span[1] = (byte)guildLength;
            span[2] = 0xBF;
            span[3] = 0x32;
            span[4] = 0x05; // op = top guild names
            span[5] = bracket;
            span[6] = (byte)count;

            for (int i = 0; i < count; i++)
            {
                WriteName(span.Slice(TopHeaderBytes + (i * NameBytes), NameBytes), entries[i].Guild);
            }

            return guildLength;
        }

        await connection.SendAsync(WriteGuilds).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ShowProfileAsync(byte bracket, byte skillTier, int rating, int wins, int losses, ushort rankInBracket)
    {
        var connection = this._player.Connection;
        if (connection is null)
        {
            return;
        }

        int Write()
        {
            var span = connection.Output.GetSpan(ProfilePacketLength)[..ProfilePacketLength];
            span.Clear();
            span[0] = 0xC1;
            span[1] = ProfilePacketLength;
            span[2] = 0xBF;
            span[3] = 0x32;
            span[4] = 0x01; // op = profile
            span[5] = bracket;
            span[6] = skillTier;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(7, 4), (uint)rating);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(11, 4), (uint)wins);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(15, 4), (uint)losses);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(19, 2), rankInBracket);
            return ProfilePacketLength;
        }

        await connection.SendAsync(Write).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ShowWaitingAsync(bool selfListed, IReadOnlyList<DuelWaitingEntry> entries)
    {
        var connection = this._player.Connection;
        if (connection is null)
        {
            return;
        }

        var count = Math.Min(entries.Count, MaxWaitingEntries);
        var length = TopHeaderBytes + (count * WaitingEntryBytes);

        int Write()
        {
            var span = connection.Output.GetSpan(length)[..length];
            span.Clear();
            span[0] = 0xC1;
            span[1] = (byte)length;
            span[2] = 0xBF;
            span[3] = 0x32;
            span[4] = 0x02; // op = waiting list
            span[5] = (byte)(selfListed ? 1 : 0);
            span[6] = (byte)count;

            for (int i = 0; i < count; i++)
            {
                var entry = entries[i];
                var offset = TopHeaderBytes + (i * WaitingEntryBytes);
                WriteName(span.Slice(offset, NameBytes), entry.Name);
                span[offset + NameBytes] = entry.ClassNumber;
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + NameBytes + 1, 4), (uint)entry.Rating);
                span[offset + NameBytes + 5] = entry.Tier;
                span[offset + NameBytes + 6] = entry.Bracket;
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + NameBytes + 7, 4), (uint)entry.WaitSeconds);
            }

            return length;
        }

        await connection.SendAsync(Write).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ShowHistoryAsync(IReadOnlyList<DuelMatchHistoryEntry> entries)
    {
        var connection = this._player.Connection;
        if (connection is null)
        {
            return;
        }

        const int historyEntryBytes = NameBytes + 1 + 1 + 1 + 4 + 1 + 4; // opp[10]+result+my+opp+ratingChange[4]+bracket+when[4] = 22
        const int historyHeaderBytes = 6; // C1 + len + 0xBF + 0x32 + op + count
        const int maxHistory = 10; // keep 6 + count*22 within the 0xC1 single-byte length

        var count = Math.Min(entries.Count, maxHistory);
        var length = historyHeaderBytes + (count * historyEntryBytes);

        int Write()
        {
            var span = connection.Output.GetSpan(length)[..length];
            span.Clear();
            span[0] = 0xC1;
            span[1] = (byte)length;
            span[2] = 0xBF;
            span[3] = 0x32;
            span[4] = 0x03; // op = match history
            span[5] = (byte)count;

            for (int i = 0; i < count; i++)
            {
                var entry = entries[i];
                var offset = historyHeaderBytes + (i * historyEntryBytes);
                WriteName(span.Slice(offset, NameBytes), entry.Opponent);
                span[offset + NameBytes] = (byte)(entry.Win ? 1 : 0);
                span[offset + NameBytes + 1] = entry.MyScore;
                span[offset + NameBytes + 2] = entry.OppScore;
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + NameBytes + 3, 4), (uint)entry.RatingChange);
                span[offset + NameBytes + 7] = entry.Bracket;
                var secondsAgo = (uint)Math.Max(0, (int)(DateTimeOffset.UtcNow - entry.When).TotalSeconds);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + NameBytes + 8, 4), secondsAgo);
            }

            return length;
        }

        await connection.SendAsync(Write).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ShowHallOfFameAsync(IReadOnlyList<DuelSeasonService.HallOfFameRecord> entries)
    {
        var connection = this._player.Connection;
        if (connection is null)
        {
            return;
        }

        const int hofEntryBytes = 1 + 1 + 1 + NameBytes + 1 + 4 + 4 + 4; // season+bracket+rank+name[10]+class+rating[4]+wins[4]+losses[4] = 26
        const int hofHeaderBytes = 6; // C1 + len + 0xBF + 0x32 + op + count
        const int maxHof = 9; // keep 6 + count*26 within the 0xC1 single-byte length

        var count = Math.Min(entries.Count, maxHof);
        var length = hofHeaderBytes + (count * hofEntryBytes);

        int Write()
        {
            var span = connection.Output.GetSpan(length)[..length];
            span.Clear();
            span[0] = 0xC1;
            span[1] = (byte)length;
            span[2] = 0xBF;
            span[3] = 0x32;
            span[4] = 0x04; // op = hall of fame
            span[5] = (byte)count;

            for (int i = 0; i < count; i++)
            {
                var entry = entries[i];
                var offset = hofHeaderBytes + (i * hofEntryBytes);
                span[offset] = (byte)entry.Season;
                span[offset + 1] = entry.Bracket;
                span[offset + 2] = entry.Rank;
                WriteName(span.Slice(offset + 3, NameBytes), entry.Name);
                span[offset + 3 + NameBytes] = entry.ClassNumber;
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + 4 + NameBytes, 4), (uint)entry.Rating);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + 8 + NameBytes, 4), (uint)entry.Wins);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + 12 + NameBytes, 4), (uint)entry.Losses);
            }

            return length;
        }

        await connection.SendAsync(Write).ConfigureAwait(false);
    }

    private static void WriteName(Span<byte> dest, string name)
    {
        dest.Clear();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var trimmed = name.Length > dest.Length ? name.Substring(0, dest.Length) : name;
        var bytes = Encoding.ASCII.GetBytes(trimmed);
        bytes.AsSpan(0, Math.Min(bytes.Length, dest.Length)).CopyTo(dest);
    }
}
