// <copyright file="AuctionHouseRequestHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.MuHelper;

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.AuctionHouse;
using MUnique.OpenMU.GameLogic.Views.AuctionHouse;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// BarnaMu: handler for the custom Auction House sub-packets (0xBF group, sub-code 0x31).
/// </summary>
[PlugIn]
[Display(Name = nameof(AuctionHouseRequestHandlerPlugIn), Description = "BarnaMu: handles custom Auction House UI requests.")]
[Guid("4AF0FBB9-3057-4D3E-9D94-A32C47F1F361")]
[BelongsToGroup(MuHelperGroupHandler.GroupKey)]
public class AuctionHouseRequestHandlerPlugIn : ISubPacketHandlerPlugIn
{
    internal const byte SubCode = 0x31;

    private const byte ViewBrowse = 0;
    private const byte ViewOwnListings = 1;
    private const byte ViewMailbox = 2;
    private const byte ViewPayouts = 3;

    /// <summary>
    /// Reserved op-6 listing-number value meaning "claim ALL pending mailbox entries" (items + payouts) in
    /// one batched server pass. Real listing numbers are small sequential values, so this max-uint sentinel
    /// never collides. The wire format / packet contract is unchanged (still op 6 with the listing-number
    /// field); only this reserved value selects the batched path.
    /// </summary>
    private const uint ClaimAllSentinel = 0xFFFFFFFFu;

    private readonly AuctionHouseService _service = new();

    /// <inheritdoc/>
    public bool IsEncryptionExpected => false;

    /// <inheritdoc/>
    public byte Key => SubCode;

    /// <inheritdoc/>
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (packet.Length < 5)
        {
            return;
        }

        var span = packet.Span;
        var operation = span[4];
        switch (operation)
        {
            case 0:
                var browseRequest = this.ParseBrowseRequest(span);
                await this.ShowBrowseAsync(player, browseRequest.Page, browseRequest.Filter).ConfigureAwait(false);
                break;
            case 1:
                if (span.Length >= 12)
                {
                    await this.SellAsync(player, span[5], span[6], span[7], BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4))).ConfigureAwait(false);
                }

                break;
            case 2:
                if (span.Length >= 16)
                {
                    await this.BuyAsync(
                        player,
                        span[6],
                        span[7],
                        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4)),
                        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4))).ConfigureAwait(false);
                }

                break;
            case 3:
                await this.ShowPageAsync(player, ViewOwnListings, 1, null).ConfigureAwait(false);
                break;
            case 4:
                if (span.Length >= 12)
                {
                    await this.CancelAsync(player, BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4))).ConfigureAwait(false);
                }

                break;
            case 5:
                await this.ShowPageAsync(player, ViewMailbox, 1, null).ConfigureAwait(false);
                break;
            case 6:
                if (span.Length >= 12)
                {
                    var receiveNumber = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
                    if (receiveNumber == ClaimAllSentinel)
                    {
                        await this.ClaimAllAsync(player).ConfigureAwait(false);
                    }
                    else
                    {
                        await this.ReceiveAsync(player, receiveNumber).ConfigureAwait(false);
                    }
                }

                break;
            case 7:
                await this.ShowPageAsync(player, ViewPayouts, 1, null).ConfigureAwait(false);
                break;
            case 8:
                if (span.Length >= 12)
                {
                    await this.ClaimAsync(player, BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4))).ConfigureAwait(false);
                }

                break;
            default:
                break;
        }
    }

    private async ValueTask ShowBrowseAsync(Player player, byte requestedPage, AuctionHouseListingFilter filter)
    {
        var page = Math.Max((byte)1, requestedPage);
        await this.ShowPageAsync(player, ViewBrowse, page, filter).ConfigureAwait(false);
    }

    private async ValueTask SellAsync(Player player, byte slot, byte currencyCode, byte requestedJewelSlot, uint price)
    {
        if (!this.TryReadCurrency(currencyCode, requestedJewelSlot, out var currency, out var jewelBankSlot))
        {
            await this.ShowMessageAsync(player, "Auction House: invalid sell request.").ConfigureAwait(false);
            return;
        }

        var result = await this._service.CreateListingAsync(player, slot, currency, price, jewelBankSlot).ConfigureAwait(false);
        await this.ShowMessageAsync(player, result).ConfigureAwait(false);
        await this.ShowPageAsync(player, ViewOwnListings, 1, null).ConfigureAwait(false);
    }

    private async ValueTask BuyAsync(Player player, byte currencyCode, byte requestedJewelSlot, uint listingNumber, uint price)
    {
        if (!this.TryReadCurrency(currencyCode, requestedJewelSlot, out var currency, out var jewelBankSlot))
        {
            await this.ShowMessageAsync(player, "Auction House: invalid buy request.").ConfigureAwait(false);
            return;
        }

        var result = await this._service.BuyAsync(player, listingNumber, currency, price, jewelBankSlot).ConfigureAwait(false);
        await this.ShowMessageAsync(player, result).ConfigureAwait(false);
        await this.ShowPageAsync(player, ViewBrowse, 1, null).ConfigureAwait(false);
    }

    private async ValueTask CancelAsync(Player player, uint listingNumber)
    {
        var result = await this._service.CancelAsync(player, listingNumber).ConfigureAwait(false);
        await this.ShowMessageAsync(player, result).ConfigureAwait(false);
        await this.ShowPageAsync(player, ViewOwnListings, 1, null).ConfigureAwait(false);
    }

    private async ValueTask ReceiveAsync(Player player, uint listingNumber)
    {
        var result = await this._service.ReceiveAsync(player, listingNumber).ConfigureAwait(false);
        await this.ShowMessageAsync(player, result).ConfigureAwait(false);
        await this.ShowPageAsync(player, ViewMailbox, 1, null).ConfigureAwait(false);
    }

    private async ValueTask ClaimAllAsync(Player player)
    {
        // Batched: one service call claims every pending mailbox entry, then ONE mailbox view refresh
        // (instead of the old per-row op 6/op 8 each triggering its own claim + refresh).
        var result = await this._service.ClaimAllMailboxAsync(player).ConfigureAwait(false);
        await this.ShowMessageAsync(player, result).ConfigureAwait(false);
        await this.ShowPageAsync(player, ViewMailbox, 1, null).ConfigureAwait(false);
    }

    private async ValueTask ClaimAsync(Player player, uint listingNumber)
    {
        var result = await this._service.ClaimPayoutAsync(player, listingNumber).ConfigureAwait(false);
        await this.ShowMessageAsync(player, result).ConfigureAwait(false);
        await this.ShowPageAsync(player, ViewMailbox, 1, null).ConfigureAwait(false);
        await this.ShowPageAsync(player, ViewPayouts, 1, null).ConfigureAwait(false);
    }

    private async ValueTask ShowPageAsync(Player player, byte view, byte page, AuctionHouseListingFilter? filter)
    {
        IReadOnlyList<AuctionListing> listings = view switch
        {
            ViewOwnListings => await this._service.GetOwnListingsAsync(player).ConfigureAwait(false),
            ViewMailbox => await this._service.GetMailboxEntriesAsync(player).ConfigureAwait(false),
            ViewPayouts => await this._service.GetPendingPayoutsAsync(player).ConfigureAwait(false),
            _ => await this._service.GetActiveListingsAsync(player, filter ?? new AuctionHouseListingFilter(), page).ConfigureAwait(false),
        };

        await player.InvokeViewPlugInAsync<IAuctionHouseViewPlugIn>(p => p.ShowListingsAsync(view, page, listings)).ConfigureAwait(false);
    }

    private async ValueTask ShowMessageAsync(Player player, string message)
    {
        await player.ShowBlueMessageAsync(message).ConfigureAwait(false);
        await player.InvokeViewPlugInAsync<IAuctionHouseViewPlugIn>(p => p.ShowMessageAsync(message)).ConfigureAwait(false);
    }

    private AuctionCurrency? ParseCurrencyFilter(byte value) => value switch
    {
        1 => AuctionCurrency.Zen,
        2 => AuctionCurrency.WCoin,
        3 => AuctionCurrency.Jewel,
        _ => null,
    };

    private (byte Page, AuctionHouseListingFilter Filter) ParseBrowseRequest(ReadOnlySpan<byte> span)
    {
        var page = span.Length > 5 ? span[5] : (byte)1;
        var currency = this.ParseCurrencyFilter(span.Length > 6 ? span[6] : (byte)0);
        int? jewelBankSlot = currency == AuctionCurrency.Jewel && span.Length > 7 && span[7] <= 16
            ? span[7]
            : null;
        int? minLevel = span.Length > 8 && span[8] <= 15 ? span[8] : null;
        int? maxLevel = span.Length > 9 && span[9] <= 15 ? span[9] : null;
        if (minLevel is { } min && maxLevel is { } max && min > max)
        {
            (minLevel, maxLevel) = (maxLevel, minLevel);
        }

        var searchText = string.Empty;
        if (span.Length > 12)
        {
            var requestedLength = span[12];
            var availableLength = Math.Min(requestedLength, Math.Max(0, span.Length - 13));
            if (availableLength > 0)
            {
                searchText = Encoding.UTF8.GetString(span.Slice(13, availableLength)).Trim();
            }
        }

        return (page, new AuctionHouseListingFilter
        {
            Currency = currency,
            JewelBankSlot = jewelBankSlot,
            MinLevel = minLevel,
            MaxLevel = maxLevel,
            HasLuck = this.ParseTriState(span.Length > 10 ? span[10] : (byte)0),
            ItemType = this.ParseItemTypeFilter(span.Length > 11 ? span[11] : (byte)0),
            NameSearch = string.IsNullOrWhiteSpace(searchText) ? null : searchText,
        });
    }

    private AuctionListingItemTypeFilter ParseItemTypeFilter(byte value)
    {
        return Enum.IsDefined(typeof(AuctionListingItemTypeFilter), value)
            ? (AuctionListingItemTypeFilter)value
            : AuctionListingItemTypeFilter.All;
    }

    private bool? ParseTriState(byte value) => value switch
    {
        1 => true,
        2 => false,
        _ => null,
    };

    private bool TryReadCurrency(byte value, byte jewelSlot, out AuctionCurrency currency, out int? resolvedJewelSlot)
    {
        resolvedJewelSlot = null;
        switch (value)
        {
            case 0:
                currency = AuctionCurrency.Zen;
                return true;
            case 1:
                currency = AuctionCurrency.WCoin;
                return true;
            case 2:
                if (jewelSlot > 16)
                {
                    currency = default;
                    return false;
                }

                currency = AuctionCurrency.Jewel;
                resolvedJewelSlot = jewelSlot;
                return true;
            default:
                currency = default;
                return false;
        }
    }
}
