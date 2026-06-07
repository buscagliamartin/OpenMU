// <copyright file="AuctionHouseViewPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.AuctionHouse;

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Views.AuctionHouse;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Sends Auction House pages to the custom client window.
/// </summary>
[PlugIn]
[Display(Name = nameof(AuctionHouseViewPlugIn), Description = "BarnaMu: sends custom Auction House UI packets to the client.")]
[Guid("4AF0FBB9-3057-4D3E-9D94-A32C47F1F360")]
public class AuctionHouseViewPlugIn : IAuctionHouseViewPlugIn
{
    private const byte Group = 0xBF;
    private const byte SubCode = 0x31;
    private const int NameLength = 48;
    private const int SellerLength = 12;
    private const int MaxItemDataLength = 15;
    private const int MaxRows = 6;
    private const int RowPacketLength = 4 + 1 + 1 + 1 + 1 + 4 + 2 + 1 + 4 + NameLength + SellerLength + 1 + 1 + MaxItemDataLength;

    private readonly RemotePlayer _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuctionHouseViewPlugIn"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public AuctionHouseViewPlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc />
    public async ValueTask ShowListingsAsync(byte view, byte page, IReadOnlyList<AuctionListing> listings)
    {
        var connection = this._player.Connection;
        if (connection is null)
        {
            return;
        }

        var count = Math.Min(listings.Count, MaxRows);
        await connection.SendAsync(() =>
        {
            const int length = 8;
            var span = connection.Output.GetSpan(length)[..length];
            span.Clear();
            span[0] = 0xC1;
            span[1] = length;
            span[2] = Group;
            span[3] = SubCode;
            span[4] = 0;
            span[5] = view;
            span[6] = page;
            span[7] = (byte)count;
            return length;
        }).ConfigureAwait(false);

        for (var i = 0; i < count; i++)
        {
            var listing = listings[i];
            await connection.SendAsync(() =>
            {
                var span = connection.Output.GetSpan(RowPacketLength)[..RowPacketLength];
                span.Clear();
                span[0] = 0xC1;
                span[1] = RowPacketLength;
                span[2] = Group;
                span[3] = SubCode;
                span[4] = 1;
                span[5] = view;
                span[6] = (byte)listing.Status;
                span[7] = (byte)listing.Currency;
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), (uint)listing.ListingNumber);
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(12, 2), (ushort)((listing.ItemGroup * 512) + listing.ItemNumber));
                span[14] = listing.ItemLevel;
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(15, 4), (uint)Math.Min(uint.MaxValue, listing.Price));
                WriteUtf8(span.Slice(19, NameLength), BuildItemTitle(listing));
                WriteUtf8(span.Slice(19 + NameLength, SellerLength), listing.SellerCharacterName);
                span[19 + NameLength + SellerLength] = listing.JewelBankSlot.HasValue ? (byte)listing.JewelBankSlot.Value : (byte)0xFF;
                var itemData = listing.ClientItemData ?? [];
                var itemDataLength = Math.Min(itemData.Length, MaxItemDataLength);
                span[20 + NameLength + SellerLength] = (byte)itemDataLength;
                itemData.AsSpan(0, itemDataLength).CopyTo(span.Slice(21 + NameLength + SellerLength, MaxItemDataLength));
                return RowPacketLength;
            }).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask ShowMessageAsync(string message)
    {
        var connection = this._player.Connection;
        if (connection is null)
        {
            return;
        }

        var byteCount = Math.Min(Encoding.UTF8.GetByteCount(message), 180);
        var length = 5 + byteCount + 1;
        await connection.SendAsync(() =>
        {
            var span = connection.Output.GetSpan(length)[..length];
            span.Clear();
            span[0] = 0xC1;
            span[1] = (byte)length;
            span[2] = Group;
            span[3] = SubCode;
            span[4] = 2;
            WriteUtf8(span.Slice(5, byteCount + 1), message);
            return length;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask OpenMailboxAsync()
    {
        var connection = this._player.Connection;
        if (connection is null)
        {
            return;
        }

        await connection.SendAsync(() =>
        {
            const int length = 6;
            var span = connection.Output.GetSpan(length)[..length];
            span.Clear();
            span[0] = 0xC1;
            span[1] = length;
            span[2] = Group;
            span[3] = SubCode;
            span[4] = 3;   // op 3 = open the Mailbox window (Postman NPC)
            span[5] = 2;   // mailbox view id, for consistency with the other mailbox packets
            return length;
        }).ConfigureAwait(false);
    }

    private static void WriteUtf8(Span<byte> target, string value)
    {
        target.Clear();
        if (target.Length == 0 || string.IsNullOrEmpty(value))
        {
            return;
        }

        var max = Math.Max(target.Length - 1, 0);
        var encoded = Encoding.UTF8.GetBytes(value);
        var count = Math.Min(encoded.Length, max);
        encoded.AsSpan(0, count).CopyTo(target);
        if (count < target.Length)
        {
            target[count] = 0;
        }
    }

    private static string BuildItemSummary(AuctionListing listing)
    {
        var displayName = StripSlotPrefix(listing.ItemDisplayName);
        var title = BuildItemTitle(listing);
        var parts = new List<string>();
        AddOptionPart(parts, listing.ItemDisplayName, "Excellent");
        AddOptionPart(parts, listing.ItemDisplayName, "Ancient");
        AddOptionPart(parts, listing.ItemDisplayName, "Luck");
        AddOptionPart(parts, listing.ItemDisplayName, "Skill");
        AddOptionPart(parts, listing.ItemDisplayName, "Harmony");
        AddOptionPart(parts, listing.ItemDisplayName, "Socket");

        var titleEnd = GetTitleEndIndex(displayName, listing.ItemLevel);
        var optionTail = titleEnd < displayName.Length
            ? displayName[titleEnd..].Trim()
            : string.Empty;
        foreach (var optionPart in SplitOptionTail(optionTail))
        {
            parts.Add(optionPart);
        }

        return string.Join(" | ", parts);
    }

    private static string BuildItemTitle(AuctionListing listing)
    {
        var displayName = StripSlotPrefix(listing.ItemDisplayName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return string.Empty;
        }

        if (listing.ItemLevel > 0)
        {
            var marker = $"+{listing.ItemLevel}";
            var index = displayName.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                var end = index + marker.Length;
                if (end >= displayName.Length || !char.IsDigit(displayName[end]))
                {
                    var baseName = displayName[..index].TrimEnd();
                    return $"{baseName} {marker}";
                }
            }
        }

        var titleEnd = GetTitleEndIndex(displayName, listing.ItemLevel);
        if (titleEnd <= 0 || titleEnd > displayName.Length)
        {
            return displayName;
        }

        var title = displayName[..titleEnd].TrimEnd();
        var plusIndex = title.LastIndexOf('+');
        if (plusIndex > 0 && !char.IsWhiteSpace(title[plusIndex - 1]))
        {
            var levelText = title[plusIndex..];
            var isLevelText = levelText.Length > 1;
            for (var i = 1; i < levelText.Length && isLevelText; i++)
            {
                isLevelText = char.IsDigit(levelText[i]);
            }

            if (isLevelText)
            {
                return $"{title[..plusIndex].TrimEnd()} {levelText}";
            }
        }

        return title;
    }

    private static int GetTitleEndIndex(string displayName, int itemLevel)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return 0;
        }

        if (itemLevel > 0)
        {
            var marker = $"+{itemLevel}";
            var index = displayName.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                var end = index + marker.Length;
                if (end >= displayName.Length || !char.IsDigit(displayName[end]))
                {
                    return end;
                }
            }
        }

        var optionStart = displayName.IndexOf('+');
        if (optionStart > 0)
        {
            var digitEnd = optionStart + 1;
            while (digitEnd < displayName.Length && char.IsDigit(displayName[digitEnd]))
            {
                digitEnd++;
            }

            if (digitEnd > optionStart + 1 && !char.IsWhiteSpace(displayName[optionStart - 1]))
            {
                return digitEnd;
            }

            return optionStart;
        }

        return displayName.Length;
    }

    private static string StripSlotPrefix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var separator = text.IndexOf(": ", StringComparison.Ordinal);
        return separator >= 0 ? text[(separator + 2)..] : text;
    }

    private static string TrimKnownSummaryTokens(string optionTail)
    {
        if (string.IsNullOrWhiteSpace(optionTail))
        {
            return string.Empty;
        }

        var result = optionTail.Trim();
        if (result.StartsWith("+", StringComparison.Ordinal))
        {
            result = result[1..];
        }

        result = result
            .Replace("+Skill", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("+Luck", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("+Excellent", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("+Ancient", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("+Harmony", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("+Socket", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (result.Equals("Skill", StringComparison.OrdinalIgnoreCase)
            || result.Equals("Luck", StringComparison.OrdinalIgnoreCase)
            || result.Equals("Excellent", StringComparison.OrdinalIgnoreCase)
            || result.Equals("Ancient", StringComparison.OrdinalIgnoreCase)
            || result.Equals("Harmony", StringComparison.OrdinalIgnoreCase)
            || result.Equals("Socket", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return result.Trim('+', '|', ' ');
    }

    private static IEnumerable<string> SplitOptionTail(string optionTail)
    {
        optionTail = TrimKnownSummaryTokens(optionTail);
        if (string.IsNullOrWhiteSpace(optionTail))
        {
            yield break;
        }

        var start = 0;
        for (var index = 1; index < optionTail.Length; index++)
        {
            if (optionTail[index] != '+')
            {
                continue;
            }

            var next = index + 1 < optionTail.Length ? optionTail[index + 1] : '\0';
            if (!char.IsLetterOrDigit(next))
            {
                continue;
            }

            var part = TrimKnownSummaryTokens(optionTail[start..index]);
            if (!string.IsNullOrWhiteSpace(part))
            {
                yield return part;
            }

            start = index;
        }

        var lastPart = TrimKnownSummaryTokens(optionTail[start..]);
        if (!string.IsNullOrWhiteSpace(lastPart))
        {
            yield return lastPart;
        }
    }

    private static void AddOptionPart(ICollection<string> parts, string displayName, string optionText)
    {
        if (displayName.Contains(optionText, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(optionText);
        }
    }
}
