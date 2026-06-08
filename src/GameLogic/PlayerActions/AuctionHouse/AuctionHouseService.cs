// <copyright file="AuctionHouseService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.AuctionHouse;

using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.PlayerActions.CashShop;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Market-wide filters for active Auction House listings.
/// </summary>
public sealed class AuctionHouseListingFilter
{
    /// <summary>
    /// Gets or initializes the optional listing currency filter.
    /// </summary>
    public AuctionCurrency? Currency { get; init; }

    /// <summary>
    /// Gets or initializes the optional jewel bank slot filter for jewel-priced listings.
    /// </summary>
    public int? JewelBankSlot { get; init; }

    /// <summary>
    /// Gets or initializes the optional minimum item level.
    /// </summary>
    public int? MinLevel { get; init; }

    /// <summary>
    /// Gets or initializes the optional maximum item level.
    /// </summary>
    public int? MaxLevel { get; init; }

    /// <summary>
    /// Gets or initializes the optional luck filter.
    /// </summary>
    public bool? HasLuck { get; init; }

    /// <summary>
    /// Gets or initializes the optional item type filter.
    /// </summary>
    public AuctionListingItemTypeFilter ItemType { get; init; }

    /// <summary>
    /// Gets or initializes the optional item-name search.
    /// </summary>
    public string? NameSearch { get; init; }
}

/// <summary>
/// Type filter values for Auction House browse results.
/// </summary>
public enum AuctionListingItemTypeFilter : byte
{
    /// <summary>
    /// All listings.
    /// </summary>
    All = 0,

    /// <summary>
    /// Items without special option/category markers.
    /// </summary>
    Common = 1,

    /// <summary>
    /// Jewels, stones, boxes, and similar consumable market items.
    /// </summary>
    JewelOrBox = 2,

    /// <summary>
    /// Wings, capes, and cloaks.
    /// </summary>
    Wings = 3,

    /// <summary>
    /// Ancient set items.
    /// </summary>
    Set = 4,

    /// <summary>
    /// Excellent items.
    /// </summary>
    Excellent = 5,

    /// <summary>
    /// Items which provide a skill.
    /// </summary>
    Skill = 6,

    /// <summary>
    /// Items with luck.
    /// </summary>
    Luck = 7,

    /// <summary>
    /// Socket items.
    /// </summary>
    Socket = 8,

    /// <summary>
    /// Harmony-enhanced items.
    /// </summary>
    Harmony = 9,
}

/// <summary>
/// DB-backed auction house service with real item escrow.
/// </summary>
public class AuctionHouseService
{
    /// <summary>
    /// Default listing duration.
    /// </summary>
    public static readonly TimeSpan ListingDuration = TimeSpan.FromDays(7);

    private const int MaxActiveListingsPerCharacter = 10;
    private const int ListingsPageSize = 6;
    private const int SalesTaxPercent = 5;
    private const int MaxItemDisplayNameLength = 160;
    private const long MaxCurrencyAmount = 2_000_000_000;
    private const long SlowAuctionStepLogThresholdMilliseconds = 500;
    private const long SlowMailboxStepLogThresholdMilliseconds = 500;
    private const byte AuctionEmptySocket = 0xFE;
    private const byte AuctionNoSocket = 0xFF;
    private const byte AuctionMaximumSocketOptions = 50;
    private static readonly byte[] SocketOptionIndexOffsets = { 0, 10, 16, 21, 29, 36 };
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static readonly object CacheSync = new();
    private static IReadOnlyDictionary<long, AuctionListingSnapshot>? ListingCache;

    /// <summary>
    /// Lists a backpack item in the auction house.
    /// </summary>
    public async ValueTask<string> CreateListingAsync(Player player, byte itemSlot, AuctionCurrency currency, long price, int? jewelBankSlot)
    {
        if (player.Account is null || player.Inventory is null || player.SelectedCharacter is null)
        {
            return "Auction House: character is not ready.";
        }

        if (!this.IsBackpackSlot(itemSlot))
        {
            return "Auction House: only backpack items can be listed.";
        }

        if (!this.IsValidPrice(currency, price, jewelBankSlot, out var priceError))
        {
            return priceError;
        }

        var item = player.Inventory.GetItem(itemSlot);
        if (item?.Definition is null)
        {
            return $"Auction House: no item found in slot {itemSlot}.";
        }

        if (item.Definition.IsBoundToCharacter)
        {
            return "Auction House: bound items cannot be listed.";
        }

        var listingNumber = (long?)null;
        var totalWatch = Stopwatch.StartNew();
        var stepWatch = Stopwatch.StartNew();
        await Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            this.LogSlowAuctionStep(player, "create-listing", listingNumber, "wait-lock", stepWatch);
            var listingCache = await this.GetListingCacheAsync(player).ConfigureAwait(false);
            this.LogSlowAuctionStep(player, "create-listing", listingNumber, "load-cache", stepWatch);

            var characterId = player.SelectedCharacter.GetId();
            var activeListings = listingCache.Values.Count(listing =>
                listing.SellerCharacterId == characterId
                && listing.HasEscrow
                && listing.EffectiveStatus == AuctionListingStatus.Active);
            if (activeListings >= MaxActiveListingsPerCharacter)
            {
                return $"Auction House: maximum {MaxActiveListingsPerCharacter} active listings per character.";
            }

            item = player.Inventory.GetItem(itemSlot);
            if (item?.Definition is null)
            {
                return $"Auction House: no item found in slot {itemSlot}.";
            }

            var now = DateTime.UtcNow;
            var oldSlot = item.ItemSlot;
            var rawItemDisplayName = item.ToString();
            var itemDisplayName = this.ToAuctionItemDisplayName(item);
            var itemGroup = item.Definition.Group;
            var itemNumber = item.Definition.Number;
            var itemLevel = item.Level;
            var itemId = item.GetId();
            var optionCount = item.ItemOptions.Count();
            var hasAncient = ItemAuditLogger.HasAncientSet(item);
            var ancientGroupCount = item.ItemSetGroups?.Count() ?? 0;
            var socketCount = item.SocketCount;
            var displayNameLength = rawItemDisplayName.Length;
            var displayNameWasTruncated = itemDisplayName.Length != rawItemDisplayName.Length;
            var clientItemData = BuildClientItemData(item);

            ItemAuditLogger.Log(
                ItemAuditLogger.AuditSource.AuctionListingAttempt,
                player,
                item,
                $"slot={oldSlot} price={this.FormatAmount(price, currency, jewelBankSlot)} optionLinks={optionCount} hasAncient={hasAncient} ancientGroups={ancientGroupCount} sockets={socketCount} displayLength={displayNameLength} displayTruncated={displayNameWasTruncated}");

            // Persist first, before removing it from the live inventory. Freshly created or socketed
            // items can still be Added in the player context; attaching such an item as existing in a
            // separate auction context would make option links point to a missing item row.
            await player.SaveProgressAsync().ConfigureAwait(false);
            this.LogSlowAuctionStep(player, "create-listing", listingNumber, "save-player-before-escrow", stepWatch);
            itemId = item.GetId();

            // Auction escrow must be an actual storage owner. Inventory items are aggregate members of
            // the character ItemStorage. If we only assign listing.EscrowItem = item and then remove the
            // item from the inventory storage, EF can orphan/delete the item and SetNull the listing FK.
            // Therefore we create a dedicated escrow ItemStorage and move the persisted item into it.
            using var auctionContext = this.CreateAuctionContext(player);
            var escrowStorage = auctionContext.CreateNew<ItemStorage>();
            var escrowItem = this.CloneItemForContext(item, player, includeItemSetGroups: false);
            auctionContext.Attach(escrowItem);
            this.LogSlowAuctionStep(player, "create-listing", listingNumber, "attach-item-to-auction-context", stepWatch);
            if (escrowItem?.Definition is null)
            {
                player.Logger.LogError(
                    "Auction House: listing persistence failed before escrow move. Character={Character}, ItemId={ItemId}, Slot={Slot}, Item={Item}, Group={Group}, Number={Number}, Level={Level}, OptionLinks={OptionLinks}, HasAncient={HasAncient}, AncientGroups={AncientGroups}, SocketCount={SocketCount}, Currency={Currency}, Price={Price}, JewelBankSlot={JewelBankSlot}.",
                    player.SelectedCharacter?.Name,
                    itemId,
                    oldSlot,
                    rawItemDisplayName,
                    itemGroup,
                    itemNumber,
                    itemLevel,
                    optionCount,
                    hasAncient,
                    ancientGroupCount,
                    socketCount,
                    currency,
                    price,
                    jewelBankSlot);
                return "Auction House: listing failed; item was not moved.";
            }

            item.StorePrice = null;
            await player.Inventory.RemoveItemAsync(item).ConfigureAwait(false);
            this.DetachItemGraph(player.PersistenceContext, item);
            this.LogSlowAuctionStep(player, "create-listing", listingNumber, "remove-from-inventory", stepWatch);

            escrowStorage.Items.Add(escrowItem);
            escrowItem.StorePrice = null;
            escrowItem.ItemSlot = 0;

            var listing = auctionContext.CreateNew<AuctionListing>();
            listing.ListingNumber = await this.GetNextListingNumberAsync(player, listingCache).ConfigureAwait(false);
            listing.SellerAccountId = player.Account.GetId();
            listing.SellerCharacterId = characterId;
            listing.SellerCharacterName = player.SelectedCharacter.Name ?? string.Empty;
            listing.EscrowItem = escrowItem;
            listing.EscrowStorage = escrowStorage;
            listing.ItemDisplayName = itemDisplayName;
            listing.ClientItemData = clientItemData;
            listing.ItemGroup = itemGroup;
            listing.ItemNumber = itemNumber;
            listing.ItemLevel = itemLevel;
            listing.Price = price;
            listing.Currency = currency;
            listing.JewelBankSlot = jewelBankSlot;
            listing.Status = AuctionListingStatus.Active;
            listing.CreatedAt = now;
            listing.ExpiresAt = now.Add(ListingDuration);
            listing.BuyerCharacterName = string.Empty;
            listingNumber = listing.ListingNumber;

            try
            {
                await this.SaveAuctionChangesAsync(auctionContext).ConfigureAwait(false);
                this.LogSlowAuctionStep(player, "create-listing", listingNumber, "save-auction", stepWatch);
            }
            catch (Exception ex)
            {
                this.LogSlowAuctionStep(player, "create-listing", listingNumber, "save-auction-failed", stepWatch);
                player.Logger.LogError(
                    ex,
                    "Auction House: listing persistence failed. Character={Character}, ItemId={ItemId}, Slot={Slot}, Item={Item}, Group={Group}, Number={Number}, Level={Level}, OptionLinks={OptionLinks}, HasAncient={HasAncient}, AncientGroups={AncientGroups}, SocketCount={SocketCount}, DisplayNameLength={DisplayNameLength}, DisplayNameTruncated={DisplayNameTruncated}, Currency={Currency}, Price={Price}, JewelBankSlot={JewelBankSlot}, ListingNumber={ListingNumber}, SellerAccountId={SellerAccountId}, SellerCharacterId={SellerCharacterId}. Restoring item.",
                    player.SelectedCharacter?.Name,
                    itemId,
                    oldSlot,
                    rawItemDisplayName,
                    itemGroup,
                    itemNumber,
                    itemLevel,
                    optionCount,
                    hasAncient,
                    ancientGroupCount,
                    socketCount,
                    displayNameLength,
                    displayNameWasTruncated,
                    currency,
                    price,
                    jewelBankSlot,
                    listing.ListingNumber,
                    listing.SellerAccountId,
                    listing.SellerCharacterId);

                ItemAuditLogger.Log(
                    ItemAuditLogger.AuditSource.AuctionListingFailedRestored,
                    player,
                    item,
                    $"slot={oldSlot} itemId={itemId} price={this.FormatAmount(price, currency, jewelBankSlot)} optionLinks={optionCount} hasAncient={hasAncient} ancientGroups={ancientGroupCount} sockets={socketCount} error={ex.GetType().Name}: {ex.Message}");

                await this.RestoreListingItemAsync(player, item, oldSlot).ConfigureAwait(false);
                return "Auction House: listing failed; item was restored.";
            }

            ItemAuditLogger.Log(
                ItemAuditLogger.AuditSource.AuctionListed,
                player,
                item,
                $"listing={listing.ListingNumber} price={this.FormatPrice(listing)} seller={listing.SellerCharacterName}");

            await player.InvokeViewPlugInAsync<IItemRemovedPlugIn>(p => p.RemoveItemAsync(oldSlot)).ConfigureAwait(false);
            this.LogSlowAuctionStep(player, "create-listing", listingNumber, "inventory-remove-packet", stepWatch);
            await player.SaveProgressAsync().ConfigureAwait(false);
            this.LogSlowAuctionStep(player, "create-listing", listingNumber, "save-player-after-escrow", stepWatch);
            UpsertCachedListing(listing);
            return $"Auction House: listed #{listing.ListingNumber} {listing.ItemDisplayName} for {this.FormatPrice(listing)}.";
        }
        finally
        {
            this.LogSlowAuctionTotal(player, "create-listing", listingNumber, totalWatch);
            Lock.Release();
        }
    }

    /// <summary>
    /// Gets active listings for display.
    /// </summary>
    public async ValueTask<IReadOnlyList<AuctionListing>> GetActiveListingsAsync(Player player, AuctionCurrency? currency, int page)
    {
        return await this.GetActiveListingsAsync(player, new AuctionHouseListingFilter { Currency = currency }, page).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets active listings for display.
    /// </summary>
    public async ValueTask<IReadOnlyList<AuctionListing>> GetActiveListingsAsync(Player player, AuctionHouseListingFilter filter, int page)
    {
        var listings = await this.GetCachedListingsAsync(player).ConfigureAwait(false);
        var query = listings
            .Where(listing => listing.Status == AuctionListingStatus.Active)
            .Where(listing => listing.ExpiresAt > DateTime.UtcNow)
            .Where(listing => filter.Currency is null || listing.Currency == filter.Currency)
            .Where(listing => filter.Currency != AuctionCurrency.Jewel || filter.JewelBankSlot is null || listing.JewelBankSlot == filter.JewelBankSlot);

        if (filter.MinLevel is { } minLevel)
        {
            query = query.Where(listing => GetEffectiveListingLevel(listing) >= minLevel);
        }

        if (filter.MaxLevel is { } maxLevel)
        {
            query = query.Where(listing => GetEffectiveListingLevel(listing) <= maxLevel);
        }

        if (filter.HasLuck is { } hasLuck)
        {
            query = query.Where(listing => ContainsListingOptionText(listing, "Luck") == hasLuck);
        }

        if (filter.ItemType != AuctionListingItemTypeFilter.All)
        {
            query = query.Where(listing => this.MatchesListingItemType(player, listing, filter.ItemType));
        }

        if (!string.IsNullOrWhiteSpace(filter.NameSearch))
        {
            query = query.Where(listing => listing.ItemDisplayName.Contains(filter.NameSearch, StringComparison.OrdinalIgnoreCase));
        }

        return query
            .OrderBy(listing => listing.Price)
            .ThenBy(listing => listing.ListingNumber)
            .Skip(Math.Max(page - 1, 0) * ListingsPageSize)
            .Take(ListingsPageSize)
            .ToList();
    }

    /// <summary>
    /// Gets the player's active and sold listings.
    /// </summary>
    public async ValueTask<IReadOnlyList<AuctionListing>> GetOwnListingsAsync(Player player)
    {
        if (player.SelectedCharacter is null)
        {
            return Array.Empty<AuctionListing>();
        }

        var characterId = player.SelectedCharacter.GetId();
        var listings = await this.GetCachedListingsAsync(player).ConfigureAwait(false);

        return listings
            .Where(listing => listing.SellerCharacterId == characterId)
            .Where(listing => listing.Status is AuctionListingStatus.Active or AuctionListingStatus.Expired or AuctionListingStatus.Sold)
            .OrderBy(listing => listing.ListingNumber)
            .ToList();
    }

    /// <summary>
    /// Gets pending auction mailbox entries for the selected character.
    /// </summary>
    public async ValueTask<IReadOnlyList<AuctionListing>> GetMailboxEntriesAsync(Player player)
    {
        var entries = await this.GetPendingMailboxEntriesAsync(player).ConfigureAwait(false);
        return entries
            .Select(entry => this.ToMailboxListing(player, entry))
            .ToList();
    }

    /// <summary>
    /// Gets pending deliveries for the buyer.
    /// </summary>
    public async ValueTask<IReadOnlyList<AuctionListing>> GetPendingDeliveriesAsync(Player player)
    {
        var entries = await this.GetPendingMailboxEntriesAsync(player, AuctionMailboxEntryType.ItemDelivery, AuctionMailboxEntryType.ReturnedItem).ConfigureAwait(false);
        return entries
            .Select(entry => this.ToMailboxListing(player, entry))
            .ToList();
    }

    /// <summary>
    /// Gets pending seller payouts.
    /// </summary>
    public async ValueTask<IReadOnlyList<AuctionListing>> GetPendingPayoutsAsync(Player player)
    {
        var entries = await this.GetPendingMailboxEntriesAsync(player, AuctionMailboxEntryType.SellerPayout).ConfigureAwait(false);
        return entries
            .Select(entry => this.ToMailboxListing(player, entry))
            .ToList();
    }

    /// <summary>
    /// Buys an active listing.
    /// </summary>
    public async ValueTask<string> BuyAsync(Player player, long listingNumber, AuctionCurrency currency, long price, int? jewelBankSlot)
    {
        if (player.Account is null || player.SelectedCharacter is null)
        {
            return "Auction House: character is not ready.";
        }

        var totalWatch = Stopwatch.StartNew();
        var stepWatch = Stopwatch.StartNew();
        await Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            this.LogSlowAuctionStep(player, "buy", listingNumber, "wait-lock", stepWatch);
            var listingId = await this.GetListingIdByNumberAsync(player, listingNumber).ConfigureAwait(false);
            this.LogSlowAuctionStep(player, "buy", listingNumber, "lookup-listing-id", stepWatch);
            if (listingId is null)
            {
                return $"Auction House: listing #{listingNumber} not found.";
            }

            using var auctionContext = this.CreateAuctionContext(player);
            var listing = await auctionContext.GetByIdAsync<AuctionListing>(listingId.Value).ConfigureAwait(false);
            this.LogSlowAuctionStep(player, "buy", listingNumber, "load-listing", stepWatch);
            if (listing is null)
            {
                return $"Auction House: listing #{listingNumber} not found.";
            }

            if (listing.Status == AuctionListingStatus.Active && listing.ExpiresAt <= DateTime.UtcNow)
            {
                listing.Status = AuctionListingStatus.Expired;
                await this.SaveAuctionChangesAsync(auctionContext).ConfigureAwait(false);
                this.LogSlowAuctionStep(player, "buy", listingNumber, "expire-listing-save", stepWatch);
                UpsertCachedListing(listing);
                return $"Auction House: listing #{listingNumber} is not active.";
            }

            if (listing.Status != AuctionListingStatus.Active)
            {
                return $"Auction House: listing #{listingNumber} is not active.";
            }

            var escrow = await this.GetEscrowAsync(auctionContext, listing).ConfigureAwait(false);
            this.LogSlowAuctionStep(player, "buy", listingNumber, "load-escrow", stepWatch);
            if (escrow.Item is null || escrow.Storage is null)
            {
                listing.Status = AuctionListingStatus.Expired;
                await this.SaveAuctionChangesAsync(auctionContext).ConfigureAwait(false);
                this.LogSlowAuctionStep(player, "buy", listingNumber, "missing-escrow-save", stepWatch);
                UpsertCachedListing(listing);
                return $"Auction House: listing #{listingNumber} is no longer available.";
            }

            if (listing.SellerAccountId == player.Account.GetId())
            {
                return "Auction House: you cannot buy from your own account.";
            }

            if (listing.Currency != currency || listing.Price != price || listing.JewelBankSlot != jewelBankSlot)
            {
                return $"Auction House: confirmation mismatch. Expected {this.FormatPrice(listing)}.";
            }

            var paymentResult = await this.TryDebitBuyerAsync(player, listing).ConfigureAwait(false);
            this.LogSlowAuctionStep(player, "buy", listingNumber, "debit-buyer", stepWatch);
            if (paymentResult is not null)
            {
                return paymentResult;
            }

            var now = DateTime.UtcNow;
            var fee = listing.Price * SalesTaxPercent / 100;
            listing.FeeAmount = fee;
            listing.SellerPayoutAmount = listing.Price - fee;
            listing.BuyerAccountId = player.Account.GetId();
            listing.BuyerCharacterId = player.SelectedCharacter.GetId();
            listing.BuyerCharacterName = player.SelectedCharacter.Name ?? string.Empty;
            listing.SoldAt = now;

            escrow.Storage.Items.Remove(escrow.Item);
            this.CreateItemMailboxEntry(
                auctionContext,
                listing,
                player.Account.GetId(),
                player.SelectedCharacter.GetId(),
                player.SelectedCharacter.Name ?? string.Empty,
                listing.SellerCharacterName,
                AuctionMailboxEntryType.ItemDelivery,
                escrow.Item,
                listing.Price,
                now);
            this.CreatePayoutMailboxEntry(auctionContext, listing, now);

            listing.EscrowItem = null;
            listing.EscrowStorage = null;
            listing.DeliveryClaimedAt = now;
            listing.SellerPayoutClaimedAt = now;
            listing.Status = AuctionListingStatus.Completed;
            await auctionContext.DeleteAsync(escrow.Storage).ConfigureAwait(false);
            await auctionContext.DeleteAsync(listing).ConfigureAwait(false);

            await this.SaveAuctionThenPlayerAsync(auctionContext, player).ConfigureAwait(false);
            this.LogSlowAuctionStep(player, "buy", listingNumber, "save-auction-and-player", stepWatch);
            ItemAuditLogger.Log(
                ItemAuditLogger.AuditSource.AuctionBought,
                player,
                escrow.Item,
                $"listing={listing.ListingNumber} seller={listing.SellerCharacterName} buyer={listing.BuyerCharacterName} price={this.FormatPrice(listing)} mailbox=item+payout");
            RemoveCachedListing(listing.ListingNumber);
            return $"Auction House: bought #{listing.ListingNumber}. Item and seller payout were sent to mailbox.";
        }
        finally
        {
            this.LogSlowAuctionTotal(player, "buy", listingNumber, totalWatch);
            Lock.Release();
        }
    }

    /// <summary>
    /// Cancels or returns an active/expired seller listing.
    /// </summary>
    public async ValueTask<string> CancelAsync(Player player, long listingNumber)
    {
        if (player.Account is null || player.SelectedCharacter is null)
        {
            return "Auction House: character is not ready.";
        }

        await Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var listingId = await this.GetListingIdByNumberAsync(player, listingNumber).ConfigureAwait(false);
            if (listingId is null)
            {
                return $"Auction House: listing #{listingNumber} not found for this character.";
            }

            using var auctionContext = this.CreateAuctionContext(player);
            var listing = await auctionContext.GetByIdAsync<AuctionListing>(listingId.Value).ConfigureAwait(false);
            if (listing is null || listing.SellerCharacterId != player.SelectedCharacter.GetId())
            {
                return $"Auction House: listing #{listingNumber} not found for this character.";
            }

            if (listing.Status is not (AuctionListingStatus.Active or AuctionListingStatus.Expired))
            {
                return $"Auction House: listing #{listingNumber} cannot be cancelled.";
            }

            var escrow = await this.GetEscrowAsync(auctionContext, listing).ConfigureAwait(false);
            if (escrow.Item is null || escrow.Storage is null)
            {
                return $"Auction House: listing #{listingNumber} has no escrow item.";
            }

            var now = DateTime.UtcNow;
            escrow.Storage.Items.Remove(escrow.Item);
            this.CreateItemMailboxEntry(
                auctionContext,
                listing,
                player.Account.GetId(),
                player.SelectedCharacter.GetId(),
                player.SelectedCharacter.Name ?? string.Empty,
                "Auction House",
                AuctionMailboxEntryType.ReturnedItem,
                escrow.Item,
                0,
                now);

            listing.EscrowItem = null;
            listing.EscrowStorage = null;
            listing.Status = AuctionListingStatus.Cancelled;
            listing.CancelledAt = now;
            await auctionContext.DeleteAsync(escrow.Storage).ConfigureAwait(false);
            await auctionContext.DeleteAsync(listing).ConfigureAwait(false);

            await this.SaveAuctionChangesAsync(auctionContext).ConfigureAwait(false);
            ItemAuditLogger.Log(
                ItemAuditLogger.AuditSource.AuctionCancelledToMailbox,
                player,
                escrow.Item,
                $"listing={listing.ListingNumber} seller={listing.SellerCharacterName} mailbox=returned");
            RemoveCachedListing(listing.ListingNumber);
            return $"Auction House: listing #{listing.ListingNumber} cancelled and item sent to mailbox.";
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Receives a bought item.
    /// </summary>
    public async ValueTask<string> ReceiveAsync(Player player, long listingNumber)
    {
        if (player.Account is null || player.Inventory is null || player.SelectedCharacter is null)
        {
            return "Auction House: character is not ready.";
        }

        await Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (await this.TryClaimMailboxEntryAsync(player, listingNumber).ConfigureAwait(false) is { } mailboxResult)
            {
                return mailboxResult;
            }

            var listingId = await this.GetListingIdByNumberAsync(player, listingNumber).ConfigureAwait(false);
            if (listingId is null)
            {
                return $"Auction House: delivery #{listingNumber} not found for this character.";
            }

            using var auctionContext = this.CreateAuctionContext(player, includeItemOptionGraph: true);
            var listing = await auctionContext.GetByIdAsync<AuctionListing>(listingId.Value).ConfigureAwait(false);
            if (listing is null || listing.BuyerCharacterId != player.SelectedCharacter.GetId())
            {
                return $"Auction House: delivery #{listingNumber} not found for this character.";
            }

            if (listing.Status != AuctionListingStatus.Sold || listing.DeliveryClaimedAt is not null)
            {
                return $"Auction House: delivery #{listingNumber} is not pending.";
            }

            var escrow = await this.GetEscrowAsync(auctionContext, listing).ConfigureAwait(false);
            if (escrow.Item is null || escrow.Storage is null)
            {
                return $"Auction House: delivery #{listingNumber} has no escrow item.";
            }

            var inventoryItem = this.CloneItemForContext(escrow.Item, player, includeItemSetGroups: false);
            this.RestoreAncientSetGroups(inventoryItem, listing.ItemDisplayName, player);
            var targetSlot = this.GetInventoryTargetSlot(player, inventoryItem);
            if (targetSlot is null)
            {
                return "Auction House: not enough inventory space to receive the item.";
            }

            escrow.Storage.Items.Remove(escrow.Item);
            listing.EscrowItem = null;
            listing.DeliveryClaimedAt = DateTime.UtcNow;
            this.CompleteIfDone(listing);

            this.DetachItemGraph(auctionContext, escrow.Item);
            if (!await this.AttachItemToPlayerContextAsync(player, inventoryItem).ConfigureAwait(false))
            {
                return $"Auction House: delivery #{listingNumber} is already in inventory.";
            }

            if (!await this.AddEscrowItemToInventoryAsync(player, inventoryItem, targetSlot.Value).ConfigureAwait(false))
            {
                player.PersistenceContext.Detach(inventoryItem);
                return "Auction House: not enough inventory space to receive the item.";
            }

            await this.SaveAuctionThenPlayerAsync(auctionContext, player).ConfigureAwait(false);
            UpsertCachedListing(listing);
            return $"Auction House: received #{listing.ListingNumber} {listing.ItemDisplayName}.";
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Claims a sold listing payout.
    /// </summary>
    public async ValueTask<string> ClaimPayoutAsync(Player player, long listingNumber)
    {
        if (player.Account is null || player.SelectedCharacter is null)
        {
            return "Auction House: character is not ready.";
        }

        await Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (await this.TryClaimMailboxEntryAsync(player, listingNumber, AuctionMailboxEntryType.SellerPayout).ConfigureAwait(false) is { } mailboxResult)
            {
                return mailboxResult;
            }

            var listingId = await this.GetListingIdByNumberAsync(player, listingNumber).ConfigureAwait(false);
            if (listingId is null)
            {
                return $"Auction House: payout #{listingNumber} not found for this character.";
            }

            using var auctionContext = this.CreateAuctionContext(player, includeItemOptionGraph: true);
            var listing = await auctionContext.GetByIdAsync<AuctionListing>(listingId.Value).ConfigureAwait(false);
            if (listing is null || listing.SellerCharacterId != player.SelectedCharacter.GetId())
            {
                return $"Auction House: payout #{listingNumber} not found for this character.";
            }

            if (listing.Status != AuctionListingStatus.Sold || listing.SellerPayoutClaimedAt is not null)
            {
                return $"Auction House: payout #{listingNumber} is not pending.";
            }

            var payoutResult = await this.TryCreditSellerAsync(player, listing).ConfigureAwait(false);
            if (payoutResult is not null)
            {
                return payoutResult;
            }

            listing.SellerPayoutClaimedAt = DateTime.UtcNow;
            this.CompleteIfDone(listing);

            await this.SaveAuctionThenPlayerAsync(auctionContext, player).ConfigureAwait(false);
            UpsertCachedListing(listing);
            return $"Auction House: claimed #{listing.ListingNumber} payout of {this.FormatAmount(listing.SellerPayoutAmount, listing.Currency, listing.JewelBankSlot)}.";
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Formats a listing for temporary chat-command display.
    /// </summary>
    public string FormatListing(AuctionListing listing)
    {
        return $"#{listing.ListingNumber} {listing.ItemDisplayName} | {this.FormatPrice(listing)} | Seller: {listing.SellerCharacterName} | {listing.Status}";
    }

    /// <summary>
    /// Parses a currency token.
    /// </summary>
    public bool TryParseCurrency(string value, out AuctionCurrency currency)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "zen":
                currency = AuctionCurrency.Zen;
                return true;
            case "wcoin":
            case "w":
            case "wc":
                currency = AuctionCurrency.WCoin;
                return true;
            case "jewel":
            case "jewels":
            case "j":
                currency = AuctionCurrency.Jewel;
                return true;
            default:
                currency = default;
                return false;
        }
    }

    /// <summary>
    /// Tries to resolve a jewel bank slot alias.
    /// </summary>
    public bool TryResolveJewelBankSlot(string input, out int slot)
    {
        slot = input.Trim().ToLowerInvariant() switch
        {
            "bless" or "jewelofbless" => 0,
            "soul" or "jewelofsoul" => 1,
            "life" or "jeweloflife" => 2,
            "creation" or "jewelofcreation" => 3,
            "guardian" or "jewelofguardian" => 4,
            "gemstone" or "gem" => 5,
            "harmony" or "jewelofharmony" => 6,
            "chaos" or "jewelofchaos" => 7,
            "lowref" or "lowerrefine" or "lowerrefinestone" => 8,
            "highref" or "higherrefine" or "higherrefinestone" => 9,
            "bok1" or "kundun1" => 10,
            "bok2" or "kundun2" => 11,
            "bok3" or "kundun3" => 12,
            "bok4" or "kundun4" => 13,
            "bok5" or "kundun5" => 14,
            "bluechoco" or "bluechocolate" => 15,
            "pinkchoco" or "pinkchocolate" => 16,
            _ => -1,
        };

        return slot >= 0;
    }

    private async ValueTask<string?> TryDebitBuyerAsync(Player player, AuctionListing listing)
    {
        switch (listing.Currency)
        {
            case AuctionCurrency.Zen:
                if (listing.Price > int.MaxValue || player.Money < listing.Price || !player.TryRemoveMoney((int)listing.Price))
                {
                    return $"Auction House: not enough Zen. Need {listing.Price:N0}.";
                }

                await player.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync()).ConfigureAwait(false);
                return null;

            case AuctionCurrency.WCoin:
                if (player.Account is null)
                {
                    return "Auction House: not enough W Coin.";
                }

                if (!WCoinService.TryApply(player.PersistenceContext, player.Account, -listing.Price, "AuctionPurchase", "AuctionHouse", player.Name, $"Listing {listing.ListingNumber}", out var debitError))
                {
                    return debitError ?? "Auction House: not enough W Coin.";
                }

                return null;

            case AuctionCurrency.Jewel:
                if (listing.JewelBankSlot is not { } jewelSlot)
                {
                    return "Auction House: listing has no jewel currency type.";
                }

                return await this.TryDebitJewelsAsync(player, jewelSlot, listing.Price).ConfigureAwait(false);

            default:
                return "Auction House: unsupported currency.";
        }
    }

    private async ValueTask<string?> TryCreditSellerAsync(Player player, AuctionListing listing)
    {
        return await this.TryCreditPayoutAsync(
            player,
            listing.SellerPayoutAmount,
            listing.Currency,
            listing.JewelBankSlot,
            $"Listing {listing.ListingNumber}").ConfigureAwait(false);
    }

    private async ValueTask<string?> TryCreditMailboxPayoutAsync(Player player, AuctionMailboxEntry entry)
    {
        return await this.TryCreditPayoutAsync(
            player,
            entry.Amount,
            entry.Currency,
            entry.JewelBankSlot,
            $"Auction mailbox listing {entry.ListingNumber}").ConfigureAwait(false);
    }

    private async ValueTask<string?> TryCreditPayoutAsync(Player player, long amount, AuctionCurrency currency, int? jewelBankSlot, string note)
    {
        switch (currency)
        {
            case AuctionCurrency.Zen:
                if (amount > int.MaxValue || player.Money + amount > int.MaxValue || !player.TryAddMoney((int)amount))
                {
                    return "Auction House: not enough Zen capacity to claim payout.";
                }

                await player.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync()).ConfigureAwait(false);
                return null;

            case AuctionCurrency.WCoin:
                if (player.Account is null)
                {
                    return "Auction House: W Coin payout failed.";
                }

                if (!WCoinService.TryApply(player.PersistenceContext, player.Account, amount, "AuctionSale", "AuctionHouse", "AuctionHouse", note, out var creditError))
                {
                    return creditError ?? "Auction House: W Coin payout failed.";
                }

                return null;

            case AuctionCurrency.Jewel:
                if (player.Account is null || jewelBankSlot is not { } jewelSlot || amount > int.MaxValue)
                {
                    return "Auction House: jewel payout failed.";
                }

                if (!this.TryAddJewelBankCount(player.Account, jewelSlot, (int)amount))
                {
                    return "Auction House: jewel payout failed.";
                }

                await player.InvokeViewPlugInAsync<IJewelBankBalancesPlugIn>(p => p.ShowBalancesAsync()).ConfigureAwait(false);
                return null;

            default:
                return "Auction House: unsupported payout currency.";
        }
    }

    private async ValueTask<string?> TryDebitJewelsAsync(Player player, int jewelSlot, long amount)
    {
        if (player.Account is null || player.Inventory is null || amount <= 0 || amount > int.MaxValue)
        {
            return "Auction House: invalid jewel amount.";
        }

        var needed = (int)amount;
        var bankCount = this.GetJewelBankCount(player.Account, jewelSlot);
        var inventorySingles = this.CountInventorySingles(player, jewelSlot);
        if (bankCount + inventorySingles < needed)
        {
            return $"Auction House: not enough {this.GetJewelBankSlotName(jewelSlot)}. Need {needed}.";
        }

        var fromBank = Math.Min(bankCount, needed);
        if (fromBank > 0 && !this.TryAddJewelBankCount(player.Account, jewelSlot, -fromBank))
        {
            return "Auction House: jewel bank debit failed.";
        }

        needed -= fromBank;
        while (needed > 0)
        {
            var item = this.FindInventorySingle(player, jewelSlot);
            if (item is null)
            {
                return "Auction House: jewel inventory debit failed.";
            }

            var slot = item.ItemSlot;
            await player.Inventory.RemoveItemAsync(item).ConfigureAwait(false);
            await player.InvokeViewPlugInAsync<IItemRemovedPlugIn>(p => p.RemoveItemAsync(slot)).ConfigureAwait(false);
            needed--;
        }

        if (fromBank > 0)
        {
            await player.InvokeViewPlugInAsync<IJewelBankBalancesPlugIn>(p => p.ShowBalancesAsync()).ConfigureAwait(false);
        }

        return null;
    }

    private async ValueTask<List<AuctionMailboxEntry>> GetPendingMailboxEntriesAsync(Player player, params AuctionMailboxEntryType[] entryTypes)
    {
        if (player.Account is null || player.SelectedCharacter is null)
        {
            return new List<AuctionMailboxEntry>();
        }

        var accountId = player.Account.GetId();
        var characterId = player.SelectedCharacter.GetId();
        using var context = this.CreateMailboxContext(player);
        var entries = (await context.GetAsync<AuctionMailboxEntry>().ConfigureAwait(false)).ToList();
        var filteredEntries = entries
            .Where(entry => entry.OwnerAccountId == accountId)
            .Where(entry => entry.OwnerCharacterId == characterId)
            .Where(entry => entry.ClaimedAt is null);

        if (entryTypes.Length > 0)
        {
            filteredEntries = filteredEntries.Where(entry => entryTypes.Contains(entry.Type));
        }

        return filteredEntries
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.ListingNumber)
            .ThenBy(entry => entry.Type)
            .ToList();
    }

    private AuctionListing ToMailboxListing(Player player, AuctionMailboxEntry entry)
    {
        var item = entry.Item ?? entry.ItemStorage?.Items.FirstOrDefault();
        this.RestoreAncientSetGroups(item, entry.ItemDisplayName, player);
        return new AuctionListing
        {
            ListingNumber = entry.ListingNumber,
            SellerAccountId = entry.OwnerAccountId,
            SellerCharacterId = entry.OwnerCharacterId,
            SellerCharacterName = entry.SenderCharacterName,
            BuyerAccountId = entry.OwnerAccountId,
            BuyerCharacterId = entry.OwnerCharacterId,
            BuyerCharacterName = entry.OwnerCharacterName,
            ItemDisplayName = this.GetMailboxDisplayName(entry),
            EscrowItem = item,
            ClientItemData = BuildClientItemData(item),
            ItemGroup = entry.ItemGroup,
            ItemNumber = entry.ItemNumber,
            ItemLevel = entry.ItemLevel,
            Price = entry.Amount,
            SellerPayoutAmount = entry.Amount,
            Currency = entry.Currency,
            JewelBankSlot = entry.JewelBankSlot,
            Status = entry.Type == AuctionMailboxEntryType.ReturnedItem
                ? AuctionListingStatus.Cancelled
                : AuctionListingStatus.Sold,
            CreatedAt = entry.CreatedAt,
            ExpiresAt = entry.CreatedAt.AddYears(1),
        };
    }

    private string GetMailboxDisplayName(AuctionMailboxEntry entry)
    {
        return entry.Type switch
        {
            AuctionMailboxEntryType.SellerPayout => $"Payout: {entry.ItemDisplayName}",
            AuctionMailboxEntryType.ReturnedItem => $"Returned: {entry.ItemDisplayName}",
            _ => entry.ItemDisplayName,
        };
    }

    private AuctionMailboxEntry CreateItemMailboxEntry(
        IContext context,
        AuctionListing listing,
        Guid ownerAccountId,
        Guid ownerCharacterId,
        string ownerCharacterName,
        string senderCharacterName,
        AuctionMailboxEntryType entryType,
        Item item,
        long amount,
        DateTime now)
    {
        var storage = context.CreateNew<ItemStorage>();
        var entry = context.CreateNew<AuctionMailboxEntry>();
        entry.OwnerAccountId = ownerAccountId;
        entry.OwnerCharacterId = ownerCharacterId;
        entry.OwnerCharacterName = ownerCharacterName;
        entry.ListingNumber = listing.ListingNumber;
        entry.Type = entryType;
        entry.ItemDisplayName = listing.ItemDisplayName;
        entry.SenderCharacterName = senderCharacterName;
        entry.ItemGroup = listing.ItemGroup;
        entry.ItemNumber = listing.ItemNumber;
        entry.ItemLevel = listing.ItemLevel;
        entry.Amount = amount;
        entry.Currency = listing.Currency;
        entry.JewelBankSlot = listing.JewelBankSlot;
        entry.CreatedAt = now;
        entry.ItemStorage = storage;
        entry.Item = item;

        item.StorePrice = null;
        item.ItemSlot = 0;
        storage.Items.Add(item);
        return entry;
    }

    private AuctionMailboxEntry CreatePayoutMailboxEntry(IContext context, AuctionListing listing, DateTime now)
    {
        var entry = context.CreateNew<AuctionMailboxEntry>();
        entry.OwnerAccountId = listing.SellerAccountId;
        entry.OwnerCharacterId = listing.SellerCharacterId;
        entry.OwnerCharacterName = listing.SellerCharacterName;
        entry.ListingNumber = listing.ListingNumber;
        entry.Type = AuctionMailboxEntryType.SellerPayout;
        entry.ItemDisplayName = listing.ItemDisplayName;
        entry.SenderCharacterName = listing.BuyerCharacterName;
        entry.ItemGroup = listing.ItemGroup;
        entry.ItemNumber = listing.ItemNumber;
        entry.ItemLevel = listing.ItemLevel;
        entry.Amount = listing.SellerPayoutAmount;
        entry.Currency = listing.Currency;
        entry.JewelBankSlot = listing.JewelBankSlot;
        entry.CreatedAt = now;
        return entry;
    }

    private async ValueTask<string?> TryClaimMailboxEntryAsync(Player player, long listingNumber, params AuctionMailboxEntryType[] entryTypes)
    {
        var totalWatch = Stopwatch.StartNew();
        var stepWatch = Stopwatch.StartNew();
        using var context = this.CreateMailboxContext(player);
        var entry = await this.GetMailboxEntryByListingNumberAsync(player, context, listingNumber, entryTypes).ConfigureAwait(false);
        this.LogSlowMailboxStep(player, listingNumber, "lookup", stepWatch);
        if (entry is null)
        {
            return null;
        }

        if (entry.ClaimedAt is not null)
        {
            return $"Auction House: mailbox entry #{listingNumber} is not pending.";
        }

        var result = entry.Type == AuctionMailboxEntryType.SellerPayout
            ? await this.ClaimMailboxPayoutAsync(player, context, entry).ConfigureAwait(false)
            : await this.ClaimMailboxItemAsync(player, context, entry).ConfigureAwait(false);
        this.LogSlowMailboxTotal(player, listingNumber, entry.Type, totalWatch);
        return result;
    }

    private async ValueTask<AuctionMailboxEntry?> GetMailboxEntryByListingNumberAsync(Player player, IContext context, long listingNumber, params AuctionMailboxEntryType[] entryTypes)
    {
        if (player.Account is null || player.SelectedCharacter is null)
        {
            return null;
        }

        var accountId = player.Account.GetId();
        var characterId = player.SelectedCharacter.GetId();
        var entries = await context.GetAsync<AuctionMailboxEntry>().ConfigureAwait(false);
        var filteredEntries = entries
            .Where(entry => entry.OwnerAccountId == accountId)
            .Where(entry => entry.OwnerCharacterId == characterId)
            .Where(entry => entry.ListingNumber == listingNumber)
            .Where(entry => entry.ClaimedAt is null);

        if (entryTypes.Length > 0)
        {
            filteredEntries = filteredEntries.Where(entry => entryTypes.Contains(entry.Type));
        }

        foreach (var entry in filteredEntries.OrderBy(entry => entry.Type == AuctionMailboxEntryType.SellerPayout ? 1 : 0))
        {
            return entry;
        }

        return null;
    }

    private async ValueTask<string> ClaimMailboxItemAsync(Player player, IContext context, AuctionMailboxEntry entry)
    {
        if (player.Inventory is null)
        {
            return "Auction House: character is not ready.";
        }

        var stepWatch = Stopwatch.StartNew();
        var mailboxItem = await this.GetMailboxItemAsync(context, entry).ConfigureAwait(false);
        this.LogSlowMailboxStep(player, entry.ListingNumber, "load-item", stepWatch);
        if (mailboxItem.Item is null)
        {
            entry.ClaimedAt = DateTime.UtcNow;
            if (mailboxItem.Storage is not null)
            {
                await context.DeleteAsync(mailboxItem.Storage).ConfigureAwait(false);
            }

            await context.DeleteAsync(entry).ConfigureAwait(false);
            await this.SaveAuctionChangesAsync(context).ConfigureAwait(false);
            return $"Auction House: mailbox item #{entry.ListingNumber} is missing.";
        }

        var inventoryItem = this.CloneItemForContext(mailboxItem.Item, player, includeItemSetGroups: false);
        this.RestoreAncientSetGroups(inventoryItem, entry.ItemDisplayName, player);
        var targetSlot = this.GetInventoryTargetSlot(player, inventoryItem);
        this.LogSlowMailboxStep(player, entry.ListingNumber, "clone-and-space-check", stepWatch);
        if (targetSlot is null)
        {
            return "Auction House: not enough inventory space to claim the mailbox item.";
        }

        var listingNumber = entry.ListingNumber;
        var entryType = entry.Type;
        var itemDisplayName = entry.ItemDisplayName;
        var senderCharacterName = entry.SenderCharacterName;
        mailboxItem.Storage?.Items.Remove(mailboxItem.Item);
        this.DetachItemGraph(context, mailboxItem.Item);
        if (!await this.AttachItemToPlayerContextAsync(player, inventoryItem).ConfigureAwait(false))
        {
            return $"Auction House: mailbox item #{listingNumber} is already in inventory.";
        }

        if (!await this.AddEscrowItemToInventoryAsync(player, inventoryItem, targetSlot.Value).ConfigureAwait(false))
        {
            player.PersistenceContext.Detach(inventoryItem);
            return "Auction House: not enough inventory space to claim the mailbox item.";
        }
        this.LogSlowMailboxStep(player, entry.ListingNumber, "inventory-appear", stepWatch);

        entry.Item = null;
        entry.ClaimedAt = DateTime.UtcNow;
        if (mailboxItem.Storage is not null)
        {
            await context.DeleteAsync(mailboxItem.Storage).ConfigureAwait(false);
        }

        await context.DeleteAsync(entry).ConfigureAwait(false);
        await this.SaveAuctionThenPlayerAsync(context, player).ConfigureAwait(false);
        this.LogSlowMailboxStep(player, listingNumber, "save", stepWatch);
        ItemAuditLogger.Log(
            ItemAuditLogger.AuditSource.AuctionMailboxItemClaimed,
            player,
            inventoryItem,
            $"listing={listingNumber} mailboxType={entryType} targetSlot={targetSlot.Value} sender={senderCharacterName}");
        return entryType == AuctionMailboxEntryType.ReturnedItem
            ? $"Auction House: claimed returned item #{listingNumber} {itemDisplayName}."
            : $"Auction House: received mailbox item #{listingNumber} {itemDisplayName}.";
    }

    private async ValueTask<string> ClaimMailboxPayoutAsync(Player player, IContext context, AuctionMailboxEntry entry)
    {
        var payoutResult = await this.TryCreditMailboxPayoutAsync(player, entry).ConfigureAwait(false);
        if (payoutResult is not null)
        {
            return payoutResult;
        }

        entry.ClaimedAt = DateTime.UtcNow;
        await context.DeleteAsync(entry).ConfigureAwait(false);
        await this.SaveAuctionThenPlayerAsync(context, player).ConfigureAwait(false);
        ItemAuditLogger.Log(
            ItemAuditLogger.AuditSource.AuctionMailboxPayoutClaimed,
            player,
            "Auction mailbox payout",
            $"listing={entry.ListingNumber} amount={this.FormatAmount(entry.Amount, entry.Currency, entry.JewelBankSlot)} sender={entry.SenderCharacterName}");
        return $"Auction House: claimed mailbox payout #{entry.ListingNumber} of {this.FormatAmount(entry.Amount, entry.Currency, entry.JewelBankSlot)}.";
    }

    private async ValueTask<(Item? Item, ItemStorage? Storage)> GetMailboxItemAsync(IContext context, AuctionMailboxEntry entry)
    {
        var storage = entry.ItemStorage;
        if (storage is null && this.TryGetGuidProperty(entry, "ItemStorageId") is { } itemStorageId)
        {
            storage = await context.GetByIdAsync<ItemStorage>(itemStorageId).ConfigureAwait(false);
            entry.ItemStorage = storage;
        }

        var item = storage?.Items.FirstOrDefault() ?? entry.Item;
        if (item is null && this.TryGetGuidProperty(entry, "ItemId") is { } itemId)
        {
            item = await context.GetByIdAsync<Item>(itemId).ConfigureAwait(false);
        }

        return (item, storage);
    }

    private IContext CreateAuctionContext(Player player, bool includeItemOptionGraph = false)
    {
        return includeItemOptionGraph
            ? player.GameContext.PersistenceContextProvider.CreateNewContext()
            : player.GameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(AuctionListing), useCache: false, player.GameContext.Configuration);
    }

    private IContext CreateMailboxContext(Player player)
    {
        return player.GameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(AuctionMailboxEntry), useCache: false, player.GameContext.Configuration);
    }

    private async ValueTask SaveAuctionThenPlayerAsync(IContext auctionContext, Player player)
    {
        await this.SaveAuctionChangesAsync(auctionContext).ConfigureAwait(false);
        await player.SaveProgressAsync().ConfigureAwait(false);
    }

    private async ValueTask SaveAuctionChangesAsync(IContext auctionContext)
    {
        using var notificationSuspension = auctionContext.SuspendChangeNotifications();
        await auctionContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private void LogSlowAuctionStep(Player player, string operation, long? listingNumber, string step, Stopwatch stopwatch)
    {
        var elapsed = stopwatch.ElapsedMilliseconds;
        if (elapsed >= SlowAuctionStepLogThresholdMilliseconds)
        {
            player.Logger.LogWarning(
                "Auction House slow step. Character={Character}, Operation={Operation}, ListingNumber={ListingNumber}, Step={Step}, ElapsedMs={ElapsedMs}.",
                player.SelectedCharacter?.Name,
                operation,
                listingNumber,
                step,
                elapsed);
        }

        stopwatch.Restart();
    }

    private void LogSlowAuctionTotal(Player player, string operation, long? listingNumber, Stopwatch stopwatch)
    {
        var elapsed = stopwatch.ElapsedMilliseconds;
        if (elapsed >= SlowAuctionStepLogThresholdMilliseconds)
        {
            player.Logger.LogWarning(
                "Auction House slow total. Character={Character}, Operation={Operation}, ListingNumber={ListingNumber}, ElapsedMs={ElapsedMs}.",
                player.SelectedCharacter?.Name,
                operation,
                listingNumber,
                elapsed);
        }
    }

    private void LogSlowMailboxStep(Player player, long listingNumber, string step, Stopwatch stopwatch)
    {
        var elapsed = stopwatch.ElapsedMilliseconds;
        if (elapsed >= SlowMailboxStepLogThresholdMilliseconds)
        {
            player.Logger.LogWarning(
                "Auction House mailbox claim slow step. Character={Character}, ListingNumber={ListingNumber}, Step={Step}, ElapsedMs={ElapsedMs}.",
                player.SelectedCharacter?.Name,
                listingNumber,
                step,
                elapsed);
        }

        stopwatch.Restart();
    }

    private void LogSlowMailboxTotal(Player player, long listingNumber, AuctionMailboxEntryType type, Stopwatch stopwatch)
    {
        var elapsed = stopwatch.ElapsedMilliseconds;
        if (elapsed >= SlowMailboxStepLogThresholdMilliseconds)
        {
            player.Logger.LogWarning(
                "Auction House mailbox claim slow total. Character={Character}, ListingNumber={ListingNumber}, Type={Type}, ElapsedMs={ElapsedMs}.",
                player.SelectedCharacter?.Name,
                listingNumber,
                type,
                elapsed);
        }
    }

    private async ValueTask<List<AuctionListing>> GetAuctionListingsAsync(IContext context)
    {
        var listings = (await context.GetAsync<AuctionListing>().ConfigureAwait(false)).ToList();
        await this.MarkExpiredAsync(context, listings).ConfigureAwait(false);
        return listings;
    }

    private async ValueTask<Guid?> GetListingIdByNumberAsync(Player player, long listingNumber)
    {
        var cache = await this.GetListingCacheAsync(player).ConfigureAwait(false);
        return cache.TryGetValue(listingNumber, out var listing) ? listing.Id : null;
    }

    private async ValueTask<IReadOnlyList<AuctionListing>> GetCachedListingsAsync(Player player)
    {
        var cache = await this.GetListingCacheAsync(player).ConfigureAwait(false);
        return cache.Values
            .Where(listing => listing.HasEscrow || listing.Status != AuctionListingStatus.Active)
            .Select(listing => listing.ToListing())
            .ToList();
    }

    private async ValueTask<IReadOnlyDictionary<long, AuctionListingSnapshot>> GetListingCacheAsync(Player player)
    {
        lock (CacheSync)
        {
            if (ListingCache is { } cache)
            {
                return cache;
            }
        }

        await CacheLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (CacheSync)
            {
                if (ListingCache is { } cache)
                {
                    return cache;
                }
            }

            using var auctionContext = this.CreateAuctionContext(player, includeItemOptionGraph: true);
            var listings = await this.GetAuctionListingsAsync(auctionContext).ConfigureAwait(false);
            var loadedCache = listings
                .Select(AuctionListingSnapshot.FromListing)
                .ToDictionary(listing => listing.ListingNumber);

            lock (CacheSync)
            {
                ListingCache = loadedCache;
                return ListingCache;
            }
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private async ValueTask<long> GetNextListingNumberAsync(Player player, IReadOnlyDictionary<long, AuctionListingSnapshot> listingCache)
    {
        var highestListingNumber = listingCache.Count == 0 ? 0 : listingCache.Values.Max(listing => listing.ListingNumber);
        using var mailboxContext = this.CreateMailboxContext(player);
        var mailboxEntries = await mailboxContext.GetAsync<AuctionMailboxEntry>().ConfigureAwait(false);
        var highestMailboxNumber = mailboxEntries
            .Select(entry => entry.ListingNumber)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(highestListingNumber, highestMailboxNumber) + 1;
    }

    private static void UpsertCachedListing(AuctionListing listing)
    {
        lock (CacheSync)
        {
            if (ListingCache is null)
            {
                return;
            }

            var snapshot = AuctionListingSnapshot.FromListing(listing);
            var updatedCache = ListingCache.ToDictionary(entry => entry.Key, entry => entry.Value);
            updatedCache[snapshot.ListingNumber] = snapshot;
            ListingCache = updatedCache;
        }
    }

    private static void RemoveCachedListing(long listingNumber)
    {
        lock (CacheSync)
        {
            if (ListingCache is null || !ListingCache.ContainsKey(listingNumber))
            {
                return;
            }

            var updatedCache = ListingCache.ToDictionary(entry => entry.Key, entry => entry.Value);
            updatedCache.Remove(listingNumber);
            ListingCache = updatedCache;
        }
    }

    private async ValueTask MarkExpiredAsync(IContext context, IEnumerable<AuctionListing> listings)
    {
        var now = DateTime.UtcNow;
        var changed = false;
        foreach (var listing in listings.Where(l => l.Status == AuctionListingStatus.Active && l.ExpiresAt <= now))
        {
            listing.Status = AuctionListingStatus.Expired;
            changed = true;
        }

        if (changed)
        {
            await this.SaveAuctionChangesAsync(context).ConfigureAwait(false);
        }
    }

    private async ValueTask<(Item? Item, ItemStorage? Storage)> GetEscrowAsync(IContext context, AuctionListing listing)
    {
        var storage = listing.EscrowStorage;
        if (storage is null && this.TryGetGuidProperty(listing, "EscrowStorageId") is { } escrowStorageId)
        {
            storage = await context.GetByIdAsync<ItemStorage>(escrowStorageId).ConfigureAwait(false);
            listing.EscrowStorage = storage;
        }

        var item = storage?.Items.FirstOrDefault() ?? listing.EscrowItem;
        if (item is null && this.TryGetGuidProperty(listing, "EscrowItemId") is { } escrowItemId)
        {
            item = await context.GetByIdAsync<Item>(escrowItemId).ConfigureAwait(false);
        }

        if (item is null)
        {
            return (null, storage);
        }

        if (storage is null)
        {
            storage = context.CreateNew<ItemStorage>();
            listing.EscrowStorage = storage;
        }

        if (!storage.Items.Contains(item))
        {
            item.ItemSlot = 0;
            storage.Items.Add(item);
        }

        listing.EscrowItem = item;
        return (item, storage);
    }

    private Guid? TryGetGuidProperty(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName)?.GetValue(instance) as Guid?;
    }

    private void DetachItemGraph(IContext context, Item item)
    {
        foreach (var optionLink in item.ItemOptions.ToList())
        {
            context.Detach(optionLink);
        }

        context.Detach(item);
    }

    private async ValueTask<bool> AttachItemToPlayerContextAsync(Player player, Item item)
    {
        try
        {
            player.PersistenceContext.Attach(item);
            return true;
        }
        catch (InvalidOperationException ex) when (IsDuplicateTrackedItemException(ex))
        {
            var itemId = item.GetId();
            if (player.Inventory?.Items.Any(existingItem => existingItem.GetId() == itemId) == true)
            {
                player.Logger.LogWarning(
                    ex,
                    "Auction House: item {ItemId} is already present in inventory while claiming mailbox/delivery.",
                    itemId);
                return false;
            }

            var trackedItem = await player.PersistenceContext.GetByIdAsync<Item>(itemId).ConfigureAwait(false);
            if (trackedItem is not null && !ReferenceEquals(trackedItem, item))
            {
                this.DetachItemGraph(player.PersistenceContext, trackedItem);
            }

            player.PersistenceContext.Attach(item);
            return true;
        }
    }

    private static bool IsDuplicateTrackedItemException(InvalidOperationException ex)
    {
        return ex.Message.Contains("entity type 'Item'", StringComparison.Ordinal)
               && ex.Message.Contains("same key value", StringComparison.Ordinal);
    }

    private Item CloneItemForContext(Item item, Player player, bool includeItemSetGroups = true)
    {
        var clonedItem = item.Clone(player.GameContext.Configuration);
        clonedItem.StorePrice = null;
        this.PreserveItemOptionLinkIds(item, clonedItem);
        if (!includeItemSetGroups)
        {
            // Ancient set links are persisted already after the pre-escrow player save.
            // Keeping cloned join entities here makes EF try to insert ItemItemOfItemSet again.
            clonedItem.ItemSetGroups.Clear();
        }

        return clonedItem;
    }

    private void PreserveItemOptionLinkIds(Item source, Item cloned)
    {
        var sourceOptions = source.ItemOptions.ToList();
        var clonedOptions = cloned.ItemOptions.ToList();
        for (var index = 0; index < Math.Min(sourceOptions.Count, clonedOptions.Count); index++)
        {
            if (sourceOptions[index] is IIdentifiable sourceIdentifiable
                && clonedOptions[index] is IIdentifiable clonedIdentifiable)
            {
                clonedIdentifiable.Id = sourceIdentifiable.Id;
            }
        }
    }

    private async ValueTask RestoreListingItemAsync(Player player, Item item, byte oldSlot)
    {
        if (player.Inventory is null)
        {
            return;
        }

        item.ItemSlot = oldSlot;
        item.StorePrice = null;

        try
        {
            player.PersistenceContext.Attach(item);
        }
        catch (InvalidOperationException ex)
        {
            player.Logger.LogWarning(ex, "Auction House: item {item} was already tracked while restoring failed listing.", item);
        }

        if (!await player.Inventory.AddItemAsync(oldSlot, item).ConfigureAwait(false)
            && !await player.Inventory.AddItemAsync(item).ConfigureAwait(false))
        {
            player.Logger.LogError("Auction House: failed to restore item {item} after listing save failed.", item);
        }
    }

    private byte? GetInventoryTargetSlot(Player player, Item item)
    {
        if (player.Inventory is null)
        {
            return null;
        }

        var targetSlot = player.Inventory.CheckInvSpace(item);
        if (targetSlot is null)
        {
            return null;
        }

        return targetSlot;
    }

    private async ValueTask<bool> AddEscrowItemToInventoryAsync(Player player, Item item, byte targetSlot)
    {
        if (player.Inventory is null)
        {
            return false;
        }

        if (!await player.Inventory.AddItemAsync(targetSlot, item).ConfigureAwait(false)
            && !await player.Inventory.AddItemAsync(item).ConfigureAwait(false))
        {
            return false;
        }

        await player.InvokeViewPlugInAsync<IItemAppearPlugIn>(p => p.ItemAppearAsync(item)).ConfigureAwait(false);
        return true;
    }

    private bool IsBackpackSlot(byte itemSlot)
    {
        return itemSlot >= InventoryConstants.EquippableSlotsCount
               && itemSlot < InventoryConstants.FirstStoreItemSlotIndex;
    }

    private bool IsValidPrice(AuctionCurrency currency, long price, int? jewelBankSlot, out string error)
    {
        if (price <= 0 || price > MaxCurrencyAmount)
        {
            error = $"Auction House: price must be between 1 and {MaxCurrencyAmount:N0}.";
            return false;
        }

        if (currency == AuctionCurrency.Jewel && jewelBankSlot is not (>= 0 and <= 16))
        {
            error = "Auction House: invalid jewel currency.";
            return false;
        }

        if (currency != AuctionCurrency.Jewel && jewelBankSlot is not null)
        {
            error = "Auction House: jewel currency can only be used with jewel listings.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static int GetEffectiveListingLevel(AuctionListing listing)
    {
        return listing.ItemLevel;
    }

    private static bool ContainsListingOptionText(AuctionListing listing, string optionText)
    {
        return listing.ItemDisplayName.Contains(optionText, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesListingItemType(Player player, AuctionListing listing, AuctionListingItemTypeFilter itemType)
    {
        return itemType switch
        {
            AuctionListingItemTypeFilter.Common => this.IsCommonListing(player, listing),
            AuctionListingItemTypeFilter.JewelOrBox => IsJewelOrBoxListing(listing),
            AuctionListingItemTypeFilter.Wings => IsWingListing(listing),
            AuctionListingItemTypeFilter.Set => this.IsAncientSetListing(player, listing),
            AuctionListingItemTypeFilter.Excellent => IsExcellentListing(listing),
            AuctionListingItemTypeFilter.Skill => HasListingFlag(listing, AuctionItemOptionFlags.HasSkill) || ContainsListingOptionText(listing, "Skill"),
            AuctionListingItemTypeFilter.Luck => HasListingFlag(listing, AuctionItemOptionFlags.HasLuck) || ContainsListingOptionText(listing, "Luck"),
            AuctionListingItemTypeFilter.Socket => HasListingFlag(listing, AuctionItemOptionFlags.HasSockets) || ContainsListingOptionText(listing, "Socket") || ContainsSocketSuffix(listing),
            AuctionListingItemTypeFilter.Harmony => HasListingFlag(listing, AuctionItemOptionFlags.HasHarmony) || ContainsListingOptionText(listing, "Harmony"),
            _ => true,
        };
    }

    private bool IsCommonListing(Player player, AuctionListing listing)
    {
        return !IsJewelOrBoxListing(listing)
               && !IsWingListing(listing)
               && !this.IsAncientSetListing(player, listing)
               && !IsExcellentListing(listing)
               && !HasListingFlag(listing, AuctionItemOptionFlags.HasSkill)
               && !HasListingFlag(listing, AuctionItemOptionFlags.HasLuck)
               && !HasListingFlag(listing, AuctionItemOptionFlags.HasHarmony)
               && !HasListingFlag(listing, AuctionItemOptionFlags.HasSockets)
               && !ContainsListingOptionText(listing, "Skill")
               && !ContainsListingOptionText(listing, "Luck")
               && !ContainsListingOptionText(listing, "Harmony")
               && !ContainsListingOptionText(listing, "Socket")
               && !ContainsSocketSuffix(listing);
    }

    private static bool IsJewelOrBoxListing(AuctionListing listing)
    {
        var displayName = GetDisplayNameWithoutSlotPrefix(listing.ItemDisplayName);
        return listing.ItemGroup == 14
               || displayName.Contains("Jewel", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("Gemstone", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("Box", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("Stone", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("Kundun", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("Chocolate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWingListing(AuctionListing listing)
    {
        var displayName = GetDisplayNameWithoutSlotPrefix(listing.ItemDisplayName);
        return displayName.Contains("Wing", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("Cape", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("Cloak", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAncientSetListing(Player player, AuctionListing listing)
    {
        return HasListingFlag(listing, AuctionItemOptionFlags.HasAncient)
               || ContainsListingOptionText(listing, "Ancient")
               || this.ResolveAncientSetGroup(listing.ItemDisplayName, listing.ItemGroup, listing.ItemNumber, player) is not null;
    }

    private static bool IsExcellentListing(AuctionListing listing)
    {
        return HasListingFlag(listing, AuctionItemOptionFlags.HasExcellent)
               || ContainsListingOptionText(listing, "Excellent");
    }

    private static bool HasListingFlag(AuctionListing listing, AuctionItemOptionFlags flag)
    {
        return listing.ClientItemData is { Length: > 4 }
               && ((AuctionItemOptionFlags)listing.ClientItemData[4]).HasFlag(flag);
    }

    private static bool ContainsSocketSuffix(AuctionListing listing)
    {
        var displayName = listing.ItemDisplayName;
        for (var index = 0; index < displayName.Length - 2; index++)
        {
            if (displayName[index] != '+' || !char.IsDigit(displayName[index + 1]))
            {
                continue;
            }

            var cursor = index + 1;
            while (cursor < displayName.Length && char.IsDigit(displayName[cursor]))
            {
                cursor++;
            }

            if (cursor < displayName.Length
                && displayName[cursor] == 'S'
                && (cursor + 1 == displayName.Length || !char.IsLetterOrDigit(displayName[cursor + 1])))
            {
                return true;
            }
        }

        return false;
    }

    private void RestoreAncientSetGroups(Item? item, string displayName, Player player)
    {
        if (item?.Definition is null || item.ItemSetGroups.Any(set => set.AncientSetDiscriminator != 0))
        {
            return;
        }

        if (this.ResolveAncientSetGroup(displayName, item.Definition.Group, item.Definition.Number, player) is { } ancientSet)
        {
            item.ItemSetGroups.Add(ancientSet);
        }
    }

    private ItemOfItemSet? ResolveAncientSetGroup(string displayName, int itemGroup, int itemNumber, Player player)
    {
        var candidates = player.GameContext.Configuration.ItemSetGroups
            .SelectMany(group => group.Items)
            .Where(itemOfSet => itemOfSet.AncientSetDiscriminator != 0)
            .Where(itemOfSet => itemOfSet.ItemDefinition?.Group == itemGroup && itemOfSet.ItemDefinition.Number == itemNumber)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var name = GetDisplayNameWithoutSlotPrefix(displayName);
        var namedMatch = candidates.FirstOrDefault(itemOfSet =>
            itemOfSet.ItemSetGroup?.Name.ToString() is { Length: > 0 } setName
            && name.Contains(setName, StringComparison.OrdinalIgnoreCase));
        return namedMatch ?? (candidates.Count == 1 ? candidates[0] : null);
    }

    private static string GetDisplayNameWithoutSlotPrefix(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return string.Empty;
        }

        var separator = displayName.IndexOf(": ", StringComparison.Ordinal);
        return separator >= 0 ? displayName[(separator + 2)..] : displayName;
    }

    private static byte[]? BuildClientItemData(Item? item)
    {
        if (item?.Definition is null)
        {
            return null;
        }

        var options = GetClientItemOptionFlags(item);
        Span<byte> buffer = stackalloc byte[15];
        buffer.Clear();
        // This must match the client's PITEM_EXTENDED_BASE layout used by CNewUIItemMng::CreateItem:
        // UInt16 GroupAndNumber (little-endian), Level, Durability, OptionFlags.
        // Do not use the old packed-nibble format here, or listing/mailbox tooltips are decoded incorrectly.
        var itemType = (ushort)((item.Definition.Group * 512) + item.Definition.Number);
        buffer[0] = (byte)(itemType & 0xFF);
        buffer[1] = (byte)((itemType >> 8) & 0xFF);
        buffer[2] = item.IsTrainablePet() ? (byte)0 : item.Level;
        buffer[3] = item.Durability();
        buffer[4] = (byte)options;

        var offset = 5;
        if (options.HasFlag(AuctionItemOptionFlags.HasOption)
            && item.ItemOptions.FirstOrDefault(o => o.ItemOption?.OptionType == ItemOptionTypes.Option) is { } normalOption)
        {
            buffer[offset++] = (byte)((((normalOption.ItemOption?.Number ?? 0) & 0xF) << 4) | (normalOption.Level & 0xF));
        }

        if (options.HasFlag(AuctionItemOptionFlags.HasExcellent))
        {
            buffer[offset++] = (byte)(GetExcellentByte(item) | GetFenrirByte(item));
        }

        if (options.HasFlag(AuctionItemOptionFlags.HasAncient)
            && item.ItemSetGroups.FirstOrDefault(set => set.AncientSetDiscriminator != 0) is { } ancientSet)
        {
            var ancientBonus = item.ItemOptions.FirstOrDefault(o => o.ItemOption?.OptionType == ItemOptionTypes.AncientBonus);
            var ancientLevel = (ancientBonus?.Level ?? 0) & 0xF;
            var ancientDiscriminator = Convert.ToByte(ancientSet.AncientSetDiscriminator) & 0xF;
            buffer[offset++] = (byte)((ancientLevel << 4) | ancientDiscriminator);
        }

        if (options.HasFlag(AuctionItemOptionFlags.HasHarmony))
        {
            buffer[offset++] = GetHarmonyByte(item);
        }

        if (options.HasFlag(AuctionItemOptionFlags.HasSockets))
        {
            var socketCount = Math.Min(item.SocketCount, 5);
            buffer[offset++] = (byte)((GetSocketBonusByte(item) & 0xF) << 4 | (socketCount & 0xF));
            for (var socketSlot = 0; socketSlot < socketCount; socketSlot++)
            {
                buffer[offset++] = GetSocketByte(item, socketSlot);
            }
        }

        return buffer[..offset].ToArray();
    }

    private static AuctionItemOptionFlags GetClientItemOptionFlags(Item item)
    {
        AuctionItemOptionFlags result = default;
        if (item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Option))
        {
            result |= AuctionItemOptionFlags.HasOption;
        }

        if (item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Luck))
        {
            result |= AuctionItemOptionFlags.HasLuck;
        }

        if (item.HasSkill)
        {
            result |= AuctionItemOptionFlags.HasSkill;
        }

        if (item.ItemOptions.Any(o =>
                o.ItemOption?.OptionType == ItemOptionTypes.Excellent
                || o.ItemOption?.OptionType == ItemOptionTypes.Wing
                || o.ItemOption?.OptionType == ItemOptionTypes.BlackFenrir
                || o.ItemOption?.OptionType == ItemOptionTypes.BlueFenrir
                || o.ItemOption?.OptionType == ItemOptionTypes.GoldFenrir))
        {
            result |= AuctionItemOptionFlags.HasExcellent;
        }

        if (item.ItemSetGroups.Any(set => set.AncientSetDiscriminator != 0))
        {
            result |= AuctionItemOptionFlags.HasAncient;
        }

        if (item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.HarmonyOption))
        {
            result |= AuctionItemOptionFlags.HasHarmony;
        }

        if (item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.GuardianOption))
        {
            result |= AuctionItemOptionFlags.HasGuardian;
        }

        if (item.SocketCount > 0)
        {
            result |= AuctionItemOptionFlags.HasSockets;
        }

        return result;
    }

    private static byte GetExcellentByte(Item item)
    {
        byte result = 0;
        var excellentOptions = item.ItemOptions.Where(o =>
            o.ItemOption?.OptionType == ItemOptionTypes.Excellent
            || o.ItemOption?.OptionType == ItemOptionTypes.Wing);
        foreach (var option in excellentOptions)
        {
            if (option.ItemOption?.Number is > 0 and <= 8)
            {
                result |= (byte)(1 << (option.ItemOption.Number - 1));
            }
        }

        return result;
    }

    private static byte GetFenrirByte(Item item)
    {
        byte result = 0;
        if (item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.BlackFenrir))
        {
            result |= 0x01;
        }

        if (item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.BlueFenrir))
        {
            result |= 0x02;
        }

        if (item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.GoldFenrir))
        {
            result |= 0x04;
        }

        return result;
    }

    private static byte GetHarmonyByte(Item item)
    {
        if (item.ItemOptions.FirstOrDefault(o => o.ItemOption?.OptionType == ItemOptionTypes.HarmonyOption) is not { } harmonyOption)
        {
            return 0;
        }

        return (byte)((((harmonyOption.ItemOption?.Number ?? 0) & 0xF) << 4) | (harmonyOption.Level & 0xF));
    }

    private static byte GetSocketBonusByte(Item item)
    {
        if (item.SocketCount == 0)
        {
            return 0;
        }

        var bonusOption = item.ItemOptions.FirstOrDefault(o => o.ItemOption?.OptionType == ItemOptionTypes.SocketBonusOption);
        return bonusOption?.ItemOption is null ? (byte)0xF : (byte)(bonusOption.ItemOption.Number & 0xF);
    }

    private static byte GetSocketByte(Item item, int socketSlot)
    {
        var optionLink = item.ItemOptions.FirstOrDefault(o => o.ItemOption?.OptionType == ItemOptionTypes.SocketOption && o.Index == socketSlot);
        if (optionLink is null)
        {
            return AuctionEmptySocket;
        }

        var elementType = optionLink.ItemOption!.SubOptionType;
        var elementOption = optionLink.ItemOption.Number;
        if (elementType < 0 || elementType >= SocketOptionIndexOffsets.Length)
        {
            return AuctionNoSocket;
        }

        var optionIndex = SocketOptionIndexOffsets[elementType] + elementOption;
        return (byte)((optionLink.Level * AuctionMaximumSocketOptions) + optionIndex);
    }

    private string FormatPrice(AuctionListing listing)
    {
        return this.FormatAmount(listing.Price, listing.Currency, listing.JewelBankSlot);
    }

    private string ToAuctionItemDisplayName(Item item)
    {
        var displayName = item.ToString();
        return displayName.Length <= MaxItemDisplayNameLength
            ? displayName
            : displayName[..(MaxItemDisplayNameLength - 3)] + "...";
    }

    private string FormatAmount(long amount, AuctionCurrency currency, int? jewelBankSlot)
    {
        return currency switch
        {
            AuctionCurrency.Zen => $"{amount:N0} Zen",
            AuctionCurrency.WCoin => $"{amount:N0} W Coin",
            AuctionCurrency.Jewel when jewelBankSlot is { } slot => $"{amount:N0} {this.GetJewelBankSlotName(slot)}",
            _ => $"{amount:N0} unknown",
        };
    }

    private void CompleteIfDone(AuctionListing listing)
    {
        if (listing.DeliveryClaimedAt is not null && listing.SellerPayoutClaimedAt is not null)
        {
            listing.Status = AuctionListingStatus.Completed;
        }
    }

    private string GetJewelBankSlotName(int slot) => slot switch
    {
        0 => "Jewel of Bless",
        1 => "Jewel of Soul",
        2 => "Jewel of Life",
        3 => "Jewel of Creation",
        4 => "Jewel of Guardian",
        5 => "Gemstone",
        6 => "Jewel of Harmony",
        7 => "Jewel of Chaos",
        8 => "Lower refine stone",
        9 => "Higher refine stone",
        10 => "Box of Kundun +1",
        11 => "Box of Kundun +2",
        12 => "Box of Kundun +3",
        13 => "Box of Kundun +4",
        14 => "Box of Kundun +5",
        15 => "Blue Chocolate Box",
        16 => "Pink Chocolate Box",
        _ => "Unknown jewel",
    };

    private int GetJewelBankCount(Account account, int slot) => slot switch
    {
        0 => account.JewelBankBless,
        1 => account.JewelBankSoul,
        2 => account.JewelBankLife,
        3 => account.JewelBankCreation,
        4 => account.JewelBankGuardian,
        5 => account.JewelBankGemstone,
        6 => account.JewelBankHarmony,
        7 => account.JewelBankChaos,
        8 => account.JewelBankLowerRefineStone,
        9 => account.JewelBankHigherRefineStone,
        10 => account.JewelBankKundun1,
        11 => account.JewelBankKundun2,
        12 => account.JewelBankKundun3,
        13 => account.JewelBankKundun4,
        14 => account.JewelBankKundun5,
        15 => account.JewelBankChocoBlue,
        16 => account.JewelBankChocoPink,
        _ => 0,
    };

    private bool TryAddJewelBankCount(Account account, int slot, int delta)
    {
        var result = this.GetJewelBankCount(account, slot) + delta;
        if (result < 0)
        {
            return false;
        }

        switch (slot)
        {
            case 0: account.JewelBankBless = result; return true;
            case 1: account.JewelBankSoul = result; return true;
            case 2: account.JewelBankLife = result; return true;
            case 3: account.JewelBankCreation = result; return true;
            case 4: account.JewelBankGuardian = result; return true;
            case 5: account.JewelBankGemstone = result; return true;
            case 6: account.JewelBankHarmony = result; return true;
            case 7: account.JewelBankChaos = result; return true;
            case 8: account.JewelBankLowerRefineStone = result; return true;
            case 9: account.JewelBankHigherRefineStone = result; return true;
            case 10: account.JewelBankKundun1 = result; return true;
            case 11: account.JewelBankKundun2 = result; return true;
            case 12: account.JewelBankKundun3 = result; return true;
            case 13: account.JewelBankKundun4 = result; return true;
            case 14: account.JewelBankKundun5 = result; return true;
            case 15: account.JewelBankChocoBlue = result; return true;
            case 16: account.JewelBankChocoPink = result; return true;
            default: return false;
        }
    }

    private int CountInventorySingles(Player player, int slot)
    {
        return player.Inventory?.Items.Count(item => this.IsInventorySingle(item, player, slot)) ?? 0;
    }

    private Item? FindInventorySingle(Player player, int slot)
    {
        return player.Inventory?.Items.FirstOrDefault(item => this.IsInventorySingle(item, player, slot));
    }

    private bool IsInventorySingle(Item item, Player player, int slot)
    {
        if (item.Definition is null)
        {
            return false;
        }

        if (slot < 10)
        {
            var mix = player.GameContext.Configuration.JewelMixes.FirstOrDefault(m => m.Number == slot);
            return mix?.SingleJewel == item.Definition;
        }

        var (group, number, level) = this.GetBoxSlot(slot);
        return item.Definition.Group == group && item.Definition.Number == number && item.Level == level;
    }

    private (int Group, int Number, byte Level) GetBoxSlot(int slot) => slot switch
    {
        10 => (14, 11, 8),
        11 => (14, 11, 9),
        12 => (14, 11, 10),
        13 => (14, 11, 11),
        14 => (14, 11, 12),
        15 => (14, 34, 0),
        16 => (14, 32, 0),
        _ => (0, 0, 0),
    };

    [Flags]
    private enum AuctionItemOptionFlags : byte
    {
        None = 0x00,
        HasOption = 0x01,
        HasLuck = 0x02,
        HasSkill = 0x04,
        HasExcellent = 0x08,
        HasAncient = 0x10,
        HasHarmony = 0x20,
        HasGuardian = 0x40,
        HasSockets = 0x80,
    }

    private sealed record AuctionListingSnapshot(
        Guid Id,
        long ListingNumber,
        Guid SellerAccountId,
        Guid SellerCharacterId,
        string SellerCharacterName,
        Guid? BuyerAccountId,
        Guid? BuyerCharacterId,
        string BuyerCharacterName,
        string ItemDisplayName,
        byte ItemGroup,
        short ItemNumber,
        byte ItemLevel,
        long Price,
        long FeeAmount,
        long SellerPayoutAmount,
        AuctionCurrency Currency,
        int? JewelBankSlot,
        AuctionListingStatus Status,
        DateTime CreatedAt,
        DateTime ExpiresAt,
        DateTime? SoldAt,
        DateTime? CancelledAt,
        DateTime? DeliveryClaimedAt,
        DateTime? SellerPayoutClaimedAt,
        byte[]? ClientItemData,
        bool HasEscrow)
    {
        public static AuctionListingSnapshot FromListing(AuctionListing listing)
        {
            var escrowItem = listing.EscrowItem ?? listing.EscrowStorage?.Items.FirstOrDefault();
            var hasEscrow =
                TryGetGuidProperty(listing, "EscrowItemId") is not null
                || TryGetGuidProperty(listing, "EscrowStorageId") is not null
                || escrowItem is not null
                || listing.EscrowStorage is not null;

            return new(
                listing.GetId(),
                listing.ListingNumber,
                listing.SellerAccountId,
                listing.SellerCharacterId,
                listing.SellerCharacterName,
                listing.BuyerAccountId,
                listing.BuyerCharacterId,
                listing.BuyerCharacterName,
                listing.ItemDisplayName,
                listing.ItemGroup,
                listing.ItemNumber,
                listing.ItemLevel,
                listing.Price,
                listing.FeeAmount,
                listing.SellerPayoutAmount,
                listing.Currency,
                listing.JewelBankSlot,
                listing.Status,
                listing.CreatedAt,
                listing.ExpiresAt,
                listing.SoldAt,
                listing.CancelledAt,
                listing.DeliveryClaimedAt,
                listing.SellerPayoutClaimedAt,
                listing.ClientItemData ?? BuildClientItemData(escrowItem),
                hasEscrow);
        }

        private static Guid? TryGetGuidProperty(AuctionListing listing, string propertyName)
        {
            return listing.GetType().GetProperty(propertyName)?.GetValue(listing) as Guid?;
        }

        public AuctionListing ToListing()
        {
            return new AuctionListing
            {
                ListingNumber = this.ListingNumber,
                SellerAccountId = this.SellerAccountId,
                SellerCharacterId = this.SellerCharacterId,
                SellerCharacterName = this.SellerCharacterName,
                BuyerAccountId = this.BuyerAccountId,
                BuyerCharacterId = this.BuyerCharacterId,
                BuyerCharacterName = this.BuyerCharacterName,
                ItemDisplayName = this.ItemDisplayName,
                ItemGroup = this.ItemGroup,
                ItemNumber = this.ItemNumber,
                ItemLevel = this.ItemLevel,
                Price = this.Price,
                FeeAmount = this.FeeAmount,
                SellerPayoutAmount = this.SellerPayoutAmount,
                Currency = this.Currency,
                JewelBankSlot = this.JewelBankSlot,
                Status = this.EffectiveStatus,
                CreatedAt = this.CreatedAt,
                ExpiresAt = this.ExpiresAt,
                SoldAt = this.SoldAt,
                CancelledAt = this.CancelledAt,
                DeliveryClaimedAt = this.DeliveryClaimedAt,
                SellerPayoutClaimedAt = this.SellerPayoutClaimedAt,
                ClientItemData = this.ClientItemData,
            };
        }

        public AuctionListingStatus EffectiveStatus =>
            this.Status == AuctionListingStatus.Active && this.ExpiresAt <= DateTime.UtcNow
                ? AuctionListingStatus.Expired
                : this.Status;
    }
}
