// <copyright file="AuctionHouseService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.AuctionHouse;

using System.Collections;
using System.Diagnostics;
using System.Reflection;
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
    private const long SlowMailboxStepLogThresholdMilliseconds = 3000;

    /// <summary>
    /// Claim All sends ItemAddedToInventory(0x22)/CharacterInventory(F3-10) straight from the LIVE
    /// player.Inventory (full-graph items after the mailbox claim fix), which avoids the slow
    /// CreateNewPlayerContext full-DB inventory reload. Set to <c>true</c> to fall back to the fresh-DB
    /// snapshot refresh (<see cref="SendBatchFreshInventorySnapshotAsync"/>) for debugging only.
    /// Kept as a static readonly (not const) so both branches stay reachable for the compiler.
    /// </summary>
    private static readonly bool UseFreshSnapshotForClaimAll = false;

    /// <summary>
    /// Retained debug switch for the earlier player-context load experiment. In EF player contexts,
    /// <see cref="Item"/> is registered as a configuration-backed repository, so account-data mailbox item ids
    /// usually resolve to null here and <c>PlayerContextLoads</c> stays 0. The relog fast path is now the
    /// dedicated batch item graph loader; this branch remains guarded and falls through to the authoritative
    /// full-graph fallback on any doubt. Static readonly (not const) so both branches stay reachable.
    /// </summary>
    private static readonly bool UsePlayerContextGraphLoadForClaimAll = true;
    private const byte AuctionEmptySocket = 0xFE;
    private const byte AuctionNoSocket = 0xFF;
    private const byte AuctionMaximumSocketOptions = 50;

    private readonly record struct PlayerContextAttachResult(bool Success, bool AlreadyTrackedItem, bool DetachedTrackedItem, string Strategy);

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

            // P0 FIX: there is NO pre-escrow SaveProgress here anymore. The single-context escrow flow
            // below moves the real item into a dedicated escrow ItemStorage and saves exactly once, so
            // there is nothing to pre-persist -- and the pre-escrow save was the exact line that threw
            // the cross-context DbUpdateConcurrencyException ("expected 1 row, affected 0").

            // BarnaMu P0 FIX -- single-context escrow (restored from the working messy server flow):
            // create the listing AND a dedicated escrow ItemStorage in the PLAYER persistence context,
            // MOVE the real item out of the inventory storage into the escrow storage (so it gets a new
            // owner and EF never orphan-deletes it), point the listing at that SAME item, and SAVE ONCE.
            // No clone, no second context, no pre-escrow save, no DetachItemGraph, no separate delete ->
            // no duplication and no cross-context concurrency. EscrowStorage already exists in the model
            // (MemberOfAggregate of AuctionListing), so no DB migration is required.
            var escrowStorage = player.PersistenceContext.CreateNew<ItemStorage>();
            var listing = player.PersistenceContext.CreateNew<AuctionListing>();
            listing.ListingNumber = await this.GetNextListingNumberAsync(player, listingCache).ConfigureAwait(false);
            listing.SellerAccountId = player.Account.GetId();
            listing.SellerCharacterId = characterId;
            listing.SellerCharacterName = player.SelectedCharacter.Name ?? string.Empty;
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

            // Move the SAME item from the inventory storage into the escrow storage (the standard
            // OpenMU item-move pattern -- buy/cancel move escrow items into mailbox storages the same
            // way). Giving the item a new owning storage is what prevents the orphan-delete the old code
            // feared; no clone is made, so excellent/ancient options stay on the real item.
            item.StorePrice = null;
            await player.Inventory.RemoveItemAsync(item).ConfigureAwait(false);
            escrowStorage.Items.Add(item);
            item.ItemSlot = 0;
            listing.EscrowItem = item;
            listing.EscrowStorage = escrowStorage;
            this.LogSlowAuctionStep(player, "create-listing", listingNumber, "move-item-to-escrow", stepWatch);

            try
            {
                await player.SaveProgressAsync().ConfigureAwait(false);
                this.LogSlowAuctionStep(player, "create-listing", listingNumber, "save-listing", stepWatch);
            }
            catch (Exception ex)
            {
                // Save failed: move the item back into the inventory and abandon the unsaved listing /
                // escrow storage so the item is never lost or stuck. Nothing was persisted yet.
                this.LogSlowAuctionStep(player, "create-listing", listingNumber, "save-listing-failed", stepWatch);
                player.Logger.LogError(
                    ex,
                    "Auction House: create-listing save FAILED ({ExceptionType}); restoring item to inventory. Character={Character}, ItemId={ItemId}, Slot={Slot}, Group={Group}, Number={Number}, Level={Level}, ListingNumber={ListingNumber}.",
                    ex.GetType().Name,
                    player.SelectedCharacter?.Name,
                    itemId,
                    oldSlot,
                    itemGroup,
                    itemNumber,
                    itemLevel,
                    listing.ListingNumber);

                ItemAuditLogger.Log(
                    ItemAuditLogger.AuditSource.AuctionListingFailedRestored,
                    player,
                    item,
                    $"slot={oldSlot} itemId={itemId} price={this.FormatAmount(price, currency, jewelBankSlot)} optionLinks={optionCount} hasAncient={hasAncient} ancientGroups={ancientGroupCount} sockets={socketCount} error={ex.GetType().Name}: {ex.Message}");

                escrowStorage.Items.Remove(item);
                listing.EscrowItem = null;
                listing.EscrowStorage = null;
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

            // Proof / guard: the listed item must no longer be in the live inventory after the escrow
            // move + single save. If it somehow remains, log an error so any regression is caught early.
            var listedItemStillInInventory = player.Inventory?.Items.Any(i => i.GetId() == itemId) ?? false;
            if (listedItemStillInInventory)
            {
                player.Logger.LogError(
                    "Auction House: DUPLICATION GUARD tripped -- listed item {ItemId} still in inventory after escrow move + save. Character={Character}, ListingNumber={ListingNumber}.",
                    itemId,
                    player.SelectedCharacter?.Name,
                    listing.ListingNumber);
            }
            else
            {
                player.Logger.LogInformation(
                    "Auction House: listed item {ItemId} moved to escrow and persisted (single-context, no duplication). Character={Character}, ListingNumber={ListingNumber}.",
                    itemId,
                    player.SelectedCharacter?.Name,
                    listing.ListingNumber);
            }

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

            var escrowStorageId = (escrow.Storage as IIdentifiable)?.Id;
            var deliveryItemId = escrow.Item.GetId();
            escrow.Storage.Items.Remove(escrow.Item);
            var deliveryEntry = this.CreateItemMailboxEntry(
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
            var deliveryStorage = deliveryEntry.ItemStorage;
            player.Logger.LogDebug(
                "Auction House buy: prepared buyer delivery mailbox before listing cleanup. ListingNumber={ListingNumber}, Buyer={Buyer}, Seller={Seller}, EscrowStorageId={EscrowStorageId}, EscrowItemId={EscrowItemId}, BuyerMailboxEntryId={BuyerMailboxEntryId}, BuyerMailboxStorageId={BuyerMailboxStorageId}, DeliveryStorageItemCount={DeliveryStorageItemCount}, DeliveryItemId={DeliveryItemId}.",
                listing.ListingNumber,
                listing.BuyerCharacterName,
                listing.SellerCharacterName,
                escrowStorageId,
                deliveryItemId,
                (deliveryEntry as IIdentifiable)?.Id,
                (deliveryStorage as IIdentifiable)?.Id,
                deliveryStorage?.Items.Count ?? 0,
                deliveryStorage?.Items.FirstOrDefault()?.GetId());

            listing.EscrowItem = null;
            listing.EscrowStorage = null;
            listing.DeliveryClaimedAt = now;
            listing.SellerPayoutClaimedAt = now;
            listing.Status = AuctionListingStatus.Completed;
            await auctionContext.DeleteAsync(listing).ConfigureAwait(false);

            await this.SaveAuctionThenPlayerAsync(auctionContext, player).ConfigureAwait(false);
            this.LogSlowAuctionStep(player, "buy", listingNumber, "save-mailbox-and-player", stepWatch);

            if (!escrow.Storage.Items.Any())
            {
                try
                {
                    await auctionContext.DeleteAsync(escrow.Storage).ConfigureAwait(false);
                    await this.SaveAuctionChangesAsync(auctionContext).ConfigureAwait(false);
                    this.LogSlowAuctionStep(player, "buy", listingNumber, "cleanup-empty-escrow-storage", stepWatch);
                    player.Logger.LogDebug(
                        "Auction House buy: empty escrow storage cleaned up after buyer delivery was persisted. ListingNumber={ListingNumber}, Buyer={Buyer}, Seller={Seller}, EscrowStorageId={EscrowStorageId}, EscrowItemId={EscrowItemId}.",
                        listing.ListingNumber,
                        listing.BuyerCharacterName,
                        listing.SellerCharacterName,
                        escrowStorageId,
                        deliveryItemId);
                }
                catch (Exception ex)
                {
                    player.Logger.LogWarning(
                        ex,
                        "Auction House buy: buyer delivery and seller payout are persisted, but empty escrow storage cleanup failed. ListingNumber={ListingNumber}, Buyer={Buyer}, Seller={Seller}, EscrowStorageId={EscrowStorageId}, EscrowItemId={EscrowItemId}.",
                        listing.ListingNumber,
                        listing.BuyerCharacterName,
                        listing.SellerCharacterName,
                        escrowStorageId,
                        deliveryItemId);
                }
            }

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

            // Use the AuctionListing typed context for escrow resolution. Account Item has the same
            // model type as merchant/config Item, and the normal player context can resolve Item ids
            // through the cached config repository instead of account data. The typed context includes
            // AuctionListing -> EscrowStorage -> Items, so GetEscrowAsync can load the real escrow item.
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
                player.Logger.LogError(
                    "Auction House cancel BLOCKED: listing has no resolvable escrow item; listing left intact. Character={Character}, ListingNumber={ListingNumber}, ListingId={ListingId}, EscrowItemId={EscrowItemId}, EscrowStorageId={EscrowStorageId}.",
                    player.SelectedCharacter?.Name,
                    listingNumber,
                    listingId,
                    this.TryGetGuidProperty(listing, "EscrowItemId"),
                    this.TryGetGuidProperty(listing, "EscrowStorageId"));
                return $"Auction House: listing #{listingNumber} has no escrow item; listing was left intact.";
            }

            var now = DateTime.UtcNow;
            var returnedItemId = escrow.Item.GetId();
            escrow.Storage.Items.Remove(escrow.Item);
            var mailboxEntry = this.CreateItemMailboxEntry(
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
            player.Logger.LogInformation(
                "Auction House cancel: moved escrow item to returned mailbox entry before deleting listing. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}, MailboxEntryId={MailboxEntryId}.",
                player.SelectedCharacter?.Name,
                listing.ListingNumber,
                returnedItemId,
                (mailboxEntry as IIdentifiable)?.Id);

            listing.EscrowItem = null;
            listing.EscrowStorage = null;
            listing.Status = AuctionListingStatus.Cancelled;
            listing.CancelledAt = now;
            await auctionContext.DeleteAsync(listing).ConfigureAwait(false);

            try
            {
                await this.SaveAuctionChangesAsync(auctionContext).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                player.Logger.LogError(
                    ex,
                    "Auction House cancel FAILED before returned mailbox item was persisted; listing should remain intact in DB. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}.",
                    player.SelectedCharacter?.Name,
                    listing.ListingNumber,
                    returnedItemId);
                return $"Auction House: listing #{listingNumber} could not be cancelled; listing was left intact.";
            }

            player.Logger.LogInformation(
                "Auction House cancel: returned mailbox item persisted. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}.",
                player.SelectedCharacter?.Name,
                listing.ListingNumber,
                returnedItemId);
            if (!escrow.Storage.Items.Any())
            {
                try
                {
                    await auctionContext.DeleteAsync(escrow.Storage).ConfigureAwait(false);
                    await this.SaveAuctionChangesAsync(auctionContext).ConfigureAwait(false);
                    player.Logger.LogInformation(
                        "Auction House cancel: empty escrow storage cleaned up after returned mailbox save. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}.",
                        player.SelectedCharacter?.Name,
                        listing.ListingNumber,
                        returnedItemId);
                }
                catch (Exception ex)
                {
                    player.Logger.LogWarning(
                        ex,
                        "Auction House cancel: returned item is already persisted in mailbox, but empty escrow storage cleanup failed. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}.",
                        player.SelectedCharacter?.Name,
                        listing.ListingNumber,
                        returnedItemId);
                }
            }

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
            var attachResult = await this.AttachItemToPlayerContextAsync(player, inventoryItem).ConfigureAwait(false);
            if (!attachResult.Success)
            {
                return $"Auction House: delivery #{listingNumber} is already in inventory.";
            }

            if (!await this.AddEscrowItemToInventoryAsync(player, inventoryItem, targetSlot.Value, notifyItemAppear: false).ConfigureAwait(false))
            {
                player.PersistenceContext.Detach(inventoryItem);
                return "Auction House: not enough inventory space to receive the item.";
            }

            await this.SaveAuctionThenPlayerAsync(auctionContext, player).ConfigureAwait(false);
            _ = await this.SendFreshInventorySnapshotAsync(player, listing.ListingNumber, inventoryItem.GetId(), "delivery receive").ConfigureAwait(false);
            UpsertCachedListing(listing);
            return $"Auction House: received #{listing.ListingNumber} {listing.ItemDisplayName}.";
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Batched "Claim All": claims every pending mailbox entry (items + payouts) in ONE pass.
    /// It moves every claimable item into inventory (full option graph, same as the single-item fix),
    /// saves the player ONCE, sends ONE fresh-snapshot refresh (ItemAddedToInventory 0x22 per claimed item
    /// + a SINGLE CharacterInventory F3-10), and only then deletes the successfully claimed mailbox
    /// entries/storages. Items that do not fit or fail are left intact in the mailbox (no loss). The
    /// conservative single-item claim path (<see cref="ReceiveAsync"/>/<see cref="ClaimPayoutAsync"/>) is
    /// unchanged. Before this path, Claim All ran the full single-item flow N times (N saves + N fresh
    /// full-player snapshots + N F3-10), which was ~10s per item.
    /// </summary>
    public async ValueTask<string> ClaimAllMailboxAsync(Player player)
    {
        if (player.Account is null || player.Inventory is null || player.SelectedCharacter is null)
        {
            return "Auction House: character is not ready.";
        }

        await Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var totalWatch = Stopwatch.StartNew();
            var phaseWatch = Stopwatch.StartNew();
            using var context = this.CreateMailboxContext(player);
            using var fullGraphContext = player.GameContext.PersistenceContextProvider.CreateNewContext();

            var accountId = player.Account.GetId();
            var characterId = player.SelectedCharacter.GetId();
            var entries = (await context.GetAsync<AuctionMailboxEntry>().ConfigureAwait(false))
                .Where(entry => entry.OwnerAccountId == accountId)
                .Where(entry => entry.OwnerCharacterId == characterId)
                .Where(entry => entry.ClaimedAt is null)
                .OrderBy(entry => entry.CreatedAt)
                .ThenBy(entry => entry.ListingNumber)
                .ToList();

            if (entries.Count == 0)
            {
                return "Auction House: your mailbox is empty.";
            }

            var now = DateTime.UtcNow;
            var claimedLiveItems = new List<Item>();
            var entriesToDelete = new List<(AuctionMailboxEntry Entry, ItemStorage? Storage)>();
            var itemsClaimed = 0;
            var payoutsClaimed = 0;
            var skipped = 0;
            var warmGraphHits = 0;
            var playerContextLoads = 0;
            var batchGraphLoads = 0;
            var fullGraphFallbacks = 0;

            // PHASE 1 (MailboxLoadMs): split payouts from item entries, and resolve each item entry's
            // mailbox-context item + storage once (used only for the storage remove / rollback + item id).
            var payoutEntries = new List<AuctionMailboxEntry>();
            var pendingItems = new List<(AuctionMailboxEntry Entry, Item MailboxItem, ItemStorage? Storage, Guid ItemId)>();
            foreach (var entry in entries)
            {
                if (entry.Type == AuctionMailboxEntryType.SellerPayout)
                {
                    payoutEntries.Add(entry);
                    continue;
                }

                var mailboxItem = await this.GetMailboxItemAsync(context, entry).ConfigureAwait(false);
                if (mailboxItem.Item is null)
                {
                    skipped++;
                    continue;
                }

                pendingItems.Add((entry, mailboxItem.Item, mailboxItem.Storage, mailboxItem.Item.GetId()));
            }

            var mailboxLoadMs = phaseWatch.ElapsedMilliseconds;
            phaseWatch.Restart();

            // PHASE 2 (BatchGraphLoadMs / FullGraphLoadMs): resolve each claimed item's full-option-graph instance. PREFER a
            // same-id item already tracked (warm) in the LIVE player context: the player context keeps a
            // full-graph copy of the player's own items (e.g. a returned/cancelled listing). The old code
            // detached that warm graph in AttachItemToPlayerContextAsync and then reloaded the same row from
            // the DB with the expensive full EF item-graph load (~1.4s/item). Reusing the warm instance hits
            // AttachItemToPlayerContextAsync's "already-tracked-same-instance" fast path (no detach, no
            // reload). After relog, resolve the remaining ids through the dedicated batch loader before
            // falling back to fullGraphContext.GetByIdAsync<Item>.
            var resolvedItems = new Dictionary<Guid, (Item Item, bool FromFullGraphContext)>();
            var batchCandidateIds = new List<Guid>();
            foreach (var pending in pendingItems)
            {
                if (resolvedItems.ContainsKey(pending.ItemId))
                {
                    continue;
                }

                var notAlreadyInInventory = player.Inventory?.Items.All(existing => existing.GetId() != pending.ItemId) ?? true;

                // 1. Warm: a same-id full-graph item already tracked in the player context (same-session
                //    items, e.g. a returned listing before relog). Reuse as-is (no DB reload, no detach).
                //    This is correctness-safe: IsTrackedItemGraphSufficient only accepts an item whose option
                //    LINKS are present and fully resolved (the player context loaded the real graph).
                var warmItem = FindTrackedItem(player.PersistenceContext, pending.ItemId);
                if (warmItem is not null && notAlreadyInInventory && IsTrackedItemGraphSufficient(warmItem))
                {
                    resolvedItems[pending.ItemId] = (warmItem, false);
                    warmGraphHits++;
                    continue;
                }

                if (notAlreadyInInventory)
                {
                    batchCandidateIds.Add(pending.ItemId);
                }
            }

            long batchGraphLoadMs = 0;
            if (batchCandidateIds.Count > 0 && fullGraphContext is IItemGraphLoader batchGraphLoader)
            {
                var batchWatch = Stopwatch.StartNew();
                IReadOnlyDictionary<Guid, Item>? batchItems = null;
                try
                {
                    batchItems = await batchGraphLoader.LoadItemGraphsByIdsAsync(batchCandidateIds, player.GameContext.Configuration).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    player.Logger.LogWarning(ex, "Auction House claim-all: batch item graph load failed; using per-item fullGraphContext fallback.");
                }

                batchGraphLoadMs = batchWatch.ElapsedMilliseconds;

                if (batchItems is not null)
                {
                    foreach (var itemId in batchCandidateIds)
                    {
                        if (!resolvedItems.ContainsKey(itemId)
                            && batchItems.TryGetValue(itemId, out var batchItem)
                            && IsTrackedItemGraphSufficient(batchItem))
                        {
                            resolvedItems[itemId] = (batchItem, true);
                            batchGraphLoads++;
                        }
                    }
                }
            }

            long fullGraphLoadMs = 0;
            long fullGraphFirstItemMs = 0;
            long fullGraphSubsequentItemsMs = 0;
            var fullGraphLoadAttempts = 0;
            foreach (var pending in pendingItems)
            {
                if (resolvedItems.ContainsKey(pending.ItemId))
                {
                    continue;
                }

                var notAlreadyInInventory = player.Inventory?.Items.All(existing => existing.GetId() != pending.ItemId) ?? true;
                var warmItem = FindTrackedItem(player.PersistenceContext, pending.ItemId);

                // 2. Retained guarded player-context experiment. In EF this usually returns null for mailbox
                //    item ids, because the player context resolves Item through the cached configuration-item
                //    repository instead of the account-data Item repository. Keep it as a harmless fallback
                //    for non-EF contexts, but never trust it unless the strict graph check passes.
                if (UsePlayerContextGraphLoadForClaimAll && warmItem is null && notAlreadyInInventory)
                {
                    Item? playerContextItem = null;
                    try
                    {
                        playerContextItem = await player.PersistenceContext.GetByIdAsync<Item>(pending.ItemId).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        player.Logger.LogWarning(ex, "Auction House claim-all: warm player-context graph load failed for {ItemId}; using cold fullGraphContext fallback.", pending.ItemId);
                    }

                    if (playerContextItem is not null && IsTrackedItemGraphSufficient(playerContextItem))
                    {
                        resolvedItems[pending.ItemId] = (playerContextItem, false);
                        playerContextLoads++;
                        continue;
                    }
                }

                // 3. Fallback: authoritative cold full-graph DB load. CORRECTNESS OVER SPEED.
                //    (Config hydration was REVERTED on 2026-06-13: it produced option-incomplete items and
                //    truncated 0x22/F3-10/0x24 bytes, e.g. wing C02B0FE90301 -> C02B0FE900. Root cause: the
                //    item's actual option LINKS are per-item DB rows; the lightweight typed mailbox context
                //    does not carry them, so there was nothing to resolve from config and the item serialized
                //    with no options. Config can resolve a link's DEFINITION but cannot reconstruct which
                //    options the item has -- only the DB graph load can. So always load the real graph here.)
                var fullGraphItemWatch = Stopwatch.StartNew();
                var fullItem = await fullGraphContext.GetByIdAsync<Item>(pending.ItemId).ConfigureAwait(false);
                var fullGraphItemMs = fullGraphItemWatch.ElapsedMilliseconds;
                fullGraphLoadMs += fullGraphItemMs;
                if (fullGraphLoadAttempts == 0)
                {
                    fullGraphFirstItemMs = fullGraphItemMs;
                }
                else
                {
                    fullGraphSubsequentItemsMs += fullGraphItemMs;
                }

                fullGraphLoadAttempts++;

                if (fullItem is not null)
                {
                    resolvedItems[pending.ItemId] = (fullItem, true);
                    fullGraphFallbacks++;
                }
            }

            phaseWatch.Restart();

            // PHASE 3 (AttachMoveMs): credit payouts, then move each claimed item (full-graph instance from
            // the dict) into live inventory. The mailbox-context instance is used only for the storage
            // remove / rollback so the storage delete still cannot cascade-delete the item.
            foreach (var entry in payoutEntries)
            {
                var payoutError = await this.TryCreditMailboxPayoutAsync(player, entry).ConfigureAwait(false);
                if (payoutError is null)
                {
                    entriesToDelete.Add((entry, null));
                    payoutsClaimed++;
                }
                else
                {
                    skipped++;
                }
            }

            foreach (var pending in pendingItems)
            {
                Item item;
                bool fromFullGraphContext;
                if (resolvedItems.TryGetValue(pending.ItemId, out var resolved))
                {
                    item = resolved.Item;
                    fromFullGraphContext = resolved.FromFullGraphContext;
                }
                else
                {
                    // Neither a warm graph, the batch DB loader, nor the full-graph DB fallback produced a
                    // sufficient graph. Leave this entry intact; claiming a lightweight mailbox item here
                    // could silently drop per-item option data from live 0x22/F3-10/0x24 packets.
                    skipped++;
                    continue;
                }

                var targetSlot = this.GetInventoryTargetSlot(player, item);
                if (targetSlot is null)
                {
                    // No space right now: leave this entry claimable for later. No loss.
                    skipped++;
                    continue;
                }

                pending.Storage?.Items.Remove(pending.MailboxItem);
                this.DetachItemGraph(context, pending.MailboxItem);
                if (fromFullGraphContext)
                {
                    // Only items freshly loaded through the fullGraphContext must be detached from it before
                    // attaching to the player context. A warm item is already tracked by the player context.
                    this.DetachItemGraph(fullGraphContext, item);
                }

                var attachResult = await this.AttachItemToPlayerContextAsync(player, item).ConfigureAwait(false);
                if (!attachResult.Success)
                {
                    pending.Storage?.Items.Add(pending.MailboxItem);
                    skipped++;
                    continue;
                }

                if (!await this.AddEscrowItemToInventoryAsync(player, item, targetSlot.Value, notifyItemAppear: false).ConfigureAwait(false))
                {
                    player.PersistenceContext.Detach(item);
                    pending.Storage?.Items.Add(pending.MailboxItem);
                    skipped++;
                    continue;
                }

                claimedLiveItems.Add(item);
                entriesToDelete.Add((pending.Entry, pending.Storage));
                itemsClaimed++;
            }

            var attachMoveMs = phaseWatch.ElapsedMilliseconds;
            phaseWatch.Restart();

            if (itemsClaimed == 0 && payoutsClaimed == 0)
            {
                return "Auction House: nothing in your mailbox could be claimed right now.";
            }

            // ONE player save for the whole batch (items now durable in inventory; payouts credited).
            try
            {
                await player.SaveProgressAsync().ConfigureAwait(false);
            }
            catch (Exception saveEx)
            {
                // Nothing committed: every item is still in its mailbox storage in the DB and no entry was
                // deleted, so the whole batch stays claimable. Do not delete anything.
                player.Logger.LogError(
                    saveEx,
                    "Auction House claim-all: player save FAILED ({ExceptionType}); no mailbox entry deleted, items/payouts preserved. Character={Character}, ItemsAttempted={ItemsAttempted}, PayoutsAttempted={PayoutsAttempted}.",
                    saveEx.GetType().Name,
                    player.SelectedCharacter?.Name,
                    itemsClaimed,
                    payoutsClaimed);
                return "Auction House: claim all failed; your mailbox was preserved. Please try again.";
            }

            var playerSaveMs = phaseWatch.ElapsedMilliseconds;
            phaseWatch.Restart();

            // LIVE refresh (no expensive fresh DB reload): the claimed items are already full-graph
            // instances inside player.Inventory (mailbox full-graph fix), so ItemAddedToInventory(0x22) per
            // claimed item and a single CharacterInventory(F3-10) serialized straight from the live
            // inventory match a relog/fresh-snapshot load. The CreateNewPlayerContext snapshot is kept only
            // as an explicit fallback/debug switch (UseFreshSnapshotForClaimAll).
            long live0x22Ms = 0;
            long liveF310Ms = 0;
            long fallbackSnapshotMs = 0;
            if (UseFreshSnapshotForClaimAll)
            {
                await this.SendBatchFreshInventorySnapshotAsync(player, claimedLiveItems.Select(claimed => claimed.GetId()).ToList(), "mailbox claim-all (fallback snapshot)").ConfigureAwait(false);
                fallbackSnapshotMs = phaseWatch.ElapsedMilliseconds;
                phaseWatch.Restart();
            }
            else
            {
                foreach (var liveItem in claimedLiveItems)
                {
                    await player.InvokeViewPlugInAsync<IItemAppearPlugIn>(p => p.ItemAppearAsync(liveItem)).ConfigureAwait(false);
                }

                live0x22Ms = phaseWatch.ElapsedMilliseconds;
                phaseWatch.Restart();

                // ONE CharacterInventory(F3-10) from the LIVE inventory (parameterless overload uses
                // player.Inventory.Items directly -- no DB reload).
                await player.InvokeViewPlugInAsync<IUpdateInventoryListPlugIn>(p => p.UpdateInventoryListAsync()).ConfigureAwait(false);
                liveF310Ms = phaseWatch.ElapsedMilliseconds;
                phaseWatch.Restart();
            }

            // Cleanup only the successfully claimed entries/storages, then ONE auction-context save.
            foreach (var (entry, storage) in entriesToDelete)
            {
                entry.Item = null;
                entry.ClaimedAt = now;
                if (storage is not null)
                {
                    await context.DeleteAsync(storage).ConfigureAwait(false);
                }

                await context.DeleteAsync(entry).ConfigureAwait(false);
            }

            await this.SaveAuctionChangesAsync(context).ConfigureAwait(false);
            var auctionSaveMs = phaseWatch.ElapsedMilliseconds;

            player.Logger.LogInformation(
                "Auction House claim-all done in {ElapsedMs} ms. Character={Character}, ItemsClaimed={ItemsClaimed}, PayoutsClaimed={PayoutsClaimed}, Skipped={Skipped}, WarmGraphHits={WarmGraphHits}, PlayerContextLoads={PlayerContextLoads}, BatchGraphLoads={BatchGraphLoads}, FullGraphFallbacks={FullGraphFallbacks}, MailboxLoadMs={MailboxLoadMs}, BatchGraphLoadMs={BatchGraphLoadMs}, FullGraphLoadMs={FullGraphLoadMs}, FullGraphFirstItemMs={FullGraphFirstItemMs}, FullGraphSubsequentItemsMs={FullGraphSubsequentItemsMs}, AttachMoveMs={AttachMoveMs}, PlayerSaveMs={PlayerSaveMs}, Live0x22Ms={Live0x22Ms}, LiveF310Ms={LiveF310Ms}, AuctionSaveMs={AuctionSaveMs}, FallbackSnapshotMs={FallbackSnapshotMs}, UsedFreshSnapshot={UsedFreshSnapshot}.",
                totalWatch.ElapsedMilliseconds,
                player.SelectedCharacter?.Name,
                itemsClaimed,
                payoutsClaimed,
                skipped,
                warmGraphHits,
                playerContextLoads,
                batchGraphLoads,
                fullGraphFallbacks,
                mailboxLoadMs,
                batchGraphLoadMs,
                fullGraphLoadMs,
                fullGraphFirstItemMs,
                fullGraphSubsequentItemsMs,
                attachMoveMs,
                playerSaveMs,
                live0x22Ms,
                liveF310Ms,
                auctionSaveMs,
                fallbackSnapshotMs,
                UseFreshSnapshotForClaimAll);

            var summary = $"Auction House: claimed {itemsClaimed} item(s) and {payoutsClaimed} payout(s).";
            return skipped > 0 ? summary + $" {skipped} left in your mailbox." : summary;
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

        var totalWatch = Stopwatch.StartNew();
        var stepWatch = Stopwatch.StartNew();
        var listingNumber = entry.ListingNumber;
        var entryType = entry.Type;
        var itemDisplayName = entry.ItemDisplayName;
        var senderCharacterName = entry.SenderCharacterName;

        var mailboxItem = await this.GetMailboxItemAsync(context, entry).ConfigureAwait(false);
        this.LogSlowMailboxStep(player, listingNumber, "load-item", stepWatch);

        player.Logger.LogDebug(
            "Auction House claim START. Character={Character}, MailboxEntryId={EntryId}, ListingNumber={ListingNumber}, ItemId={ItemId}, Type={Type}.",
            player.SelectedCharacter?.Name,
            (entry as IIdentifiable)?.Id,
            listingNumber,
            mailboxItem.Item?.GetId(),
            entryType);

        if (mailboxItem.Item is null)
        {
            player.Logger.LogError(
                "Auction House claim BLOCKED: mailbox entry has no resolvable item; entry left intact for investigation. Character={Character}, MailboxEntryId={EntryId}, ListingNumber={ListingNumber}, Type={Type}, ItemId={ItemId}, ItemStorageId={ItemStorageId}, StorageLoaded={StorageLoaded}.",
                player.SelectedCharacter?.Name,
                (entry as IIdentifiable)?.Id,
                listingNumber,
                entryType,
                this.TryGetGuidProperty(entry, "ItemId"),
                this.TryGetGuidProperty(entry, "ItemStorageId"),
                mailboxItem.Storage is not null);
            return $"Auction House: mailbox item #{listingNumber} is missing; entry was left for investigation.";
        }

        // FULL-GRAPH FIX (mailbox claim): GetMailboxItemAsync loads the item through the lightweight
        // CreateMailboxContext() AuctionMailboxEntry-typed context, which does NOT resolve the deep item
        // option graph. Using that instance as the LIVE player.Inventory item made move/equip 0x24
        // responses serialize incomplete option bytes (stats vanished after a live move) and made the
        // server equip validation reject the item -- a relog masked it by reloading through a full context.
        // Mirror the bought-item ReceiveAsync path (includeItemOptionGraph: true): load the SAME item
        // (same DB row, NOT a clone) through a full game context so the instance moved into live inventory
        // is option-graph-equivalent to a login/relog load. The fresh-snapshot 0x22/F3-10 path is unchanged.
        var mailboxContextItem = mailboxItem.Item;
        var itemId = mailboxContextItem.GetId();
        using var fullGraphContext = player.GameContext.PersistenceContextProvider.CreateNewContext();
        Item? item = null;
        var batchGraphLoads = 0;
        var fullGraphFallbacks = 0;
        long batchGraphLoadMs = 0;
        long fullGraphFallbackMs = 0;

        if (fullGraphContext is IItemGraphLoader graphLoader)
        {
            var batchGraphWatch = Stopwatch.StartNew();
            try
            {
                var batchItems = await graphLoader.LoadItemGraphsByIdsAsync(new[] { itemId }, player.GameContext.Configuration).ConfigureAwait(false);
                if (batchItems.TryGetValue(itemId, out var batchItem) && IsTrackedItemGraphSufficient(batchItem))
                {
                    item = batchItem;
                    batchGraphLoads = 1;
                }
            }
            catch (Exception ex)
            {
                player.Logger.LogWarning(
                    ex,
                    "Auction House claim: batch item graph load failed for {ItemId}; using fullGraphContext fallback. Character={Character}, ListingNumber={ListingNumber}.",
                    itemId,
                    player.SelectedCharacter?.Name,
                    listingNumber);
            }

            batchGraphLoadMs = batchGraphWatch.ElapsedMilliseconds;
        }

        if (item is null)
        {
            var fullGraphFallbackWatch = Stopwatch.StartNew();
            var fullItem = await fullGraphContext.GetByIdAsync<Item>(itemId).ConfigureAwait(false);
            fullGraphFallbackMs = fullGraphFallbackWatch.ElapsedMilliseconds;
            if (fullItem is not null && IsTrackedItemGraphSufficient(fullItem))
            {
                item = fullItem;
                fullGraphFallbacks = 1;
            }
        }

        this.LogSlowMailboxStep(player, listingNumber, "item-graph-load", stepWatch);
        if (item is null)
        {
            player.Logger.LogError(
                "Auction House claim BLOCKED: no sufficient full item graph could be loaded; entry left intact. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}, BatchGraphLoads={BatchGraphLoads}, FullGraphFallbacks={FullGraphFallbacks}, BatchGraphLoadMs={BatchGraphLoadMs}, FullGraphFallbackMs={FullGraphFallbackMs}.",
                player.SelectedCharacter?.Name,
                listingNumber,
                itemId,
                batchGraphLoads,
                fullGraphFallbacks,
                batchGraphLoadMs,
                fullGraphFallbackMs);
            return $"Auction House: mailbox item #{listingNumber} could not be claimed safely; it remains in your mailbox.";
        }

        var usingFullGraphInstance = !ReferenceEquals(item, mailboxContextItem);
        var optionLinkCountBeforeMove = CountItemOptionLinks(item);
        var excellentOptionCountBeforeMove = CountItemOptions(item, ItemOptionTypes.Excellent);
        var wingOptionCountBeforeMove = CountItemOptions(item, ItemOptionTypes.Wing);
        var ancientGroupCountBeforeMove = CountAncientGroups(item);
        player.Logger.LogDebug(
            "Auction House claim item graph BEFORE move. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}, BatchGraphLoads={BatchGraphLoads}, FullGraphFallbacks={FullGraphFallbacks}, BatchGraphLoadMs={BatchGraphLoadMs}, FullGraphFallbackMs={FullGraphFallbackMs}, Group={Group}, Number={Number}, Level={Level}, OptionLinks={OptionLinks}, ExcellentOptions={ExcellentOptions}, WingOptions={WingOptions}, AncientGroups={AncientGroups}, SocketCount={SocketCount}.",
            player.SelectedCharacter?.Name,
            listingNumber,
            itemId,
            batchGraphLoads,
            fullGraphFallbacks,
            batchGraphLoadMs,
            fullGraphFallbackMs,
            item.Definition?.Group,
            item.Definition?.Number,
            item.Level,
            optionLinkCountBeforeMove,
            excellentOptionCountBeforeMove,
            wingOptionCountBeforeMove,
            ancientGroupCountBeforeMove,
            item.SocketCount);

        // Space check BEFORE touching anything, so a full inventory leaves the item safely in mailbox.
        var targetSlot = this.GetInventoryTargetSlot(player, item);
        this.LogSlowMailboxStep(player, listingNumber, "space-check", stepWatch);
        if (targetSlot is null)
        {
            return "Auction House: not enough inventory space to claim the mailbox item.";
        }

        // P0 ITEM-LOSS FIX: move the REAL item (no clone) from the mailbox storage into the player
        // inventory and PERSIST that move FIRST. Only after the item is durably in inventory do we
        // delete the (now-empty) mailbox storage + entry. The previous code cloned the item, then
        // deleted the mailbox storage (cascade-deleting the real item) and relied on a separate clone
        // insert in another context, saved mailbox-first -- so if that insert didn't persist, the real
        // item was permanently lost. Removing the item from the storage before any delete, and saving
        // the inventory move first, prevents both the cascade-delete and the cross-context loss. No
        // clone is made, so excellent/ancient options stay on the real item.
        // Empty the mailbox storage using the mailbox-context instance (so the later storage delete does
        // not cascade-delete the item), then detach both the mailbox-context instance and the full-graph
        // loader instance so only the live player context owns the row when it saves. Ownership flow is
        // unchanged: mailbox ItemStorage -> inventory ItemStorage.
        var attachMoveWatch = Stopwatch.StartNew();
        mailboxItem.Storage?.Items.Remove(mailboxContextItem);
        this.DetachItemGraph(context, mailboxContextItem);
        if (usingFullGraphInstance)
        {
            this.DetachItemGraph(fullGraphContext, item);
        }

        var attachResult = await this.AttachItemToPlayerContextAsync(player, item).ConfigureAwait(false);
        if (!attachResult.Success)
        {
            // Could not take ownership in the player context: put it back in the mailbox storage so the
            // entry stays claimable, and abort without deleting anything.
            mailboxItem.Storage?.Items.Add(mailboxContextItem);
            player.Logger.LogWarning(
                "Auction House claim: could not attach item to player context; item left in mailbox, entry claimable. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}, AlreadyTracked={AlreadyTracked}, DetachedTracked={DetachedTracked}, AttachStrategy={AttachStrategy}.",
                player.SelectedCharacter?.Name,
                listingNumber,
                itemId,
                attachResult.AlreadyTrackedItem,
                attachResult.DetachedTrackedItem,
                attachResult.Strategy);
            return $"Auction House: mailbox item #{listingNumber} could not be claimed right now; it remains in your mailbox.";
        }

        var optionLinkCountAfterAttach = CountItemOptionLinks(item);
        var excellentOptionCountAfterAttach = CountItemOptions(item, ItemOptionTypes.Excellent);
        var wingOptionCountAfterAttach = CountItemOptions(item, ItemOptionTypes.Wing);
        var ancientGroupCountAfterAttach = CountAncientGroups(item);
        player.Logger.LogDebug(
            "Auction House claim attach result. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}, AlreadyTracked={AlreadyTracked}, DetachedTracked={DetachedTracked}, AttachStrategy={AttachStrategy}, OptionLinksBefore={OptionLinksBefore}, OptionLinksAfterAttach={OptionLinksAfterAttach}, ExcellentBefore={ExcellentBefore}, ExcellentAfterAttach={ExcellentAfterAttach}, WingBefore={WingBefore}, WingAfterAttach={WingAfterAttach}, AncientBefore={AncientBefore}, AncientAfterAttach={AncientAfterAttach}.",
            player.SelectedCharacter?.Name,
            listingNumber,
            itemId,
            attachResult.AlreadyTrackedItem,
            attachResult.DetachedTrackedItem,
            attachResult.Strategy,
            optionLinkCountBeforeMove,
            optionLinkCountAfterAttach,
            excellentOptionCountBeforeMove,
            excellentOptionCountAfterAttach,
            wingOptionCountBeforeMove,
            wingOptionCountAfterAttach,
            ancientGroupCountBeforeMove,
            ancientGroupCountAfterAttach);
        if (optionLinkCountAfterAttach < optionLinkCountBeforeMove
            || excellentOptionCountAfterAttach < excellentOptionCountBeforeMove
            || wingOptionCountAfterAttach < wingOptionCountBeforeMove
            || ancientGroupCountAfterAttach < ancientGroupCountBeforeMove)
        {
            player.PersistenceContext.Detach(item);
            mailboxItem.Storage?.Items.Add(mailboxContextItem);
            player.Logger.LogError(
                "Auction House claim BLOCKED: item graph lost options during player-context attach; mailbox entry left intact. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}, OptionLinksBefore={OptionLinksBefore}, OptionLinksAfterAttach={OptionLinksAfterAttach}, ExcellentBefore={ExcellentBefore}, ExcellentAfterAttach={ExcellentAfterAttach}, WingBefore={WingBefore}, WingAfterAttach={WingAfterAttach}, AncientBefore={AncientBefore}, AncientAfterAttach={AncientAfterAttach}.",
                player.SelectedCharacter?.Name,
                listingNumber,
                itemId,
                optionLinkCountBeforeMove,
                optionLinkCountAfterAttach,
                excellentOptionCountBeforeMove,
                excellentOptionCountAfterAttach,
                wingOptionCountBeforeMove,
                wingOptionCountAfterAttach,
                ancientGroupCountBeforeMove,
                ancientGroupCountAfterAttach);
            return $"Auction House: mailbox item #{listingNumber} could not be claimed safely; it remains in your mailbox.";
        }

        if (!await this.AddEscrowItemToInventoryAsync(player, item, targetSlot.Value, notifyItemAppear: false).ConfigureAwait(false))
        {
            player.PersistenceContext.Detach(item);
            mailboxItem.Storage?.Items.Add(mailboxContextItem);
            return "Auction House: not enough inventory space to claim the mailbox item.";
        }

        var attachMoveMs = attachMoveWatch.ElapsedMilliseconds;
        this.LogSlowMailboxStep(player, listingNumber, "inventory-add", stepWatch);
        player.Logger.LogDebug(
            "Auction House claim: item moved into inventory slot {TargetSlot}. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}.",
            targetSlot.Value,
            player.SelectedCharacter?.Name,
            listingNumber,
            itemId);

        // Persist the player inventory FIRST -- the item is now durably owned by the inventory.
        long playerSaveMs;
        var playerSaveWatch = Stopwatch.StartNew();
        try
        {
            await player.SaveProgressAsync().ConfigureAwait(false);
            playerSaveMs = playerSaveWatch.ElapsedMilliseconds;
        }
        catch (Exception saveEx)
        {
            // Nothing committed: the item still exists in the mailbox storage in the DB, so the entry
            // remains claimable. Do NOT delete the entry/storage -- the item is preserved.
            player.Logger.LogError(
                saveEx,
                "Auction House claim: player save FAILED ({ExceptionType}); item preserved in mailbox, entry left claimable. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}.",
                saveEx.GetType().Name,
                player.SelectedCharacter?.Name,
                listingNumber,
                itemId);
            return "Auction House: claim failed; the item remains in your mailbox.";
        }

        var optionLinkCountAfterSave = CountItemOptionLinks(item);
        var excellentOptionCountAfterSave = CountItemOptions(item, ItemOptionTypes.Excellent);
        var wingOptionCountAfterSave = CountItemOptions(item, ItemOptionTypes.Wing);
        var ancientGroupCountAfterSave = CountAncientGroups(item);
        player.Logger.LogDebug(
            "Auction House claim: player save SUCCESS; item is in inventory. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}, OptionLinksAfterSave={OptionLinksAfterSave}, ExcellentAfterSave={ExcellentAfterSave}, WingAfterSave={WingAfterSave}, AncientAfterSave={AncientAfterSave}.",
            player.SelectedCharacter?.Name,
            listingNumber,
            itemId,
            optionLinkCountAfterSave,
            excellentOptionCountAfterSave,
            wingOptionCountAfterSave,
            ancientGroupCountAfterSave);

        var snapshotRefreshWatch = Stopwatch.StartNew();
        if (!await this.SendFreshInventorySnapshotAsync(player, listingNumber, itemId, "mailbox claim").ConfigureAwait(false))
        {
            player.Logger.LogWarning(
                "Auction House claim: mailbox cleanup deferred because fresh inventory snapshot resync could not be sent. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}.",
                player.SelectedCharacter?.Name,
                listingNumber,
                itemId);
            return $"Auction House: mailbox item #{listingNumber} was claimed, but inventory refresh failed; relog if needed.";
        }

        var snapshotRefreshMs = snapshotRefreshWatch.ElapsedMilliseconds;
        this.LogSlowMailboxStep(player, listingNumber, "inventory-resync", stepWatch);

        if (optionLinkCountAfterSave < optionLinkCountBeforeMove
            || excellentOptionCountAfterSave < excellentOptionCountBeforeMove
            || wingOptionCountAfterSave < wingOptionCountBeforeMove
            || ancientGroupCountAfterSave < ancientGroupCountBeforeMove)
        {
            player.Logger.LogError(
                "Auction House claim CLEANUP BLOCKED: player save completed but item graph counts dropped; mailbox evidence left intact. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}, OptionLinksBefore={OptionLinksBefore}, OptionLinksAfterSave={OptionLinksAfterSave}, ExcellentBefore={ExcellentBefore}, ExcellentAfterSave={ExcellentAfterSave}, WingBefore={WingBefore}, WingAfterSave={WingAfterSave}, AncientBefore={AncientBefore}, AncientAfterSave={AncientAfterSave}.",
                player.SelectedCharacter?.Name,
                listingNumber,
                itemId,
                optionLinkCountBeforeMove,
                optionLinkCountAfterSave,
                excellentOptionCountBeforeMove,
                excellentOptionCountAfterSave,
                wingOptionCountBeforeMove,
                wingOptionCountAfterSave,
                ancientGroupCountBeforeMove,
                ancientGroupCountAfterSave);
            return $"Auction House: mailbox item #{listingNumber} was claimed, but cleanup was left for investigation.";
        }

        // The item is safely in the inventory now (its DB owner is the inventory storage). Delete the
        // now-empty mailbox storage + entry. Even if this cleanup fails, the item is NOT lost because it
        // is already persisted in inventory; a later retry will hit the missing-item guard and preserve
        // the stale entry for investigation instead of deleting evidence.
        entry.Item = null;
        entry.ClaimedAt = DateTime.UtcNow;
        if (mailboxItem.Storage is not null)
        {
            await context.DeleteAsync(mailboxItem.Storage).ConfigureAwait(false);
        }

        await context.DeleteAsync(entry).ConfigureAwait(false);
        await this.SaveAuctionChangesAsync(context).ConfigureAwait(false);
        this.LogSlowMailboxStep(player, listingNumber, "mailbox-cleanup-save", stepWatch);
        player.Logger.LogInformation(
            "Auction House mailbox claim timings. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}, BatchGraphLoads={BatchGraphLoads}, FullGraphFallbacks={FullGraphFallbacks}, BatchGraphLoadMs={BatchGraphLoadMs}, FullGraphFallbackMs={FullGraphFallbackMs}, AttachMoveMs={AttachMoveMs}, PlayerSaveMs={PlayerSaveMs}, SnapshotRefreshMs={SnapshotRefreshMs}, TotalMs={TotalMs}.",
            player.SelectedCharacter?.Name,
            listingNumber,
            itemId,
            batchGraphLoads,
            fullGraphFallbacks,
            batchGraphLoadMs,
            fullGraphFallbackMs,
            attachMoveMs,
            playerSaveMs,
            snapshotRefreshMs,
            totalWatch.ElapsedMilliseconds);
        player.Logger.LogDebug(
            "Auction House claim: mailbox entry + storage deleted. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}.",
            player.SelectedCharacter?.Name,
            listingNumber,
            itemId);

        ItemAuditLogger.Log(
            ItemAuditLogger.AuditSource.AuctionMailboxItemClaimed,
            player,
            item,
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

    private ValueTask<PlayerContextAttachResult> AttachItemToPlayerContextAsync(Player player, Item item)
    {
        var itemId = item.GetId();
        var trackedItem = FindTrackedItem(player.PersistenceContext, itemId);
        var alreadyTracked = trackedItem is not null;
        var detachedTrackedItem = false;

        if (trackedItem is not null && ReferenceEquals(trackedItem, item))
        {
            return ValueTask.FromResult(new PlayerContextAttachResult(true, true, false, "already-tracked-same-instance"));
        }

        if (trackedItem is not null)
        {
            if (player.Inventory?.Items.Any(existingItem => existingItem.GetId() == itemId) == true)
            {
                player.Logger.LogWarning(
                    "Auction House: item {ItemId} is already present in inventory while claiming mailbox/delivery; mailbox/delivery evidence left intact.",
                    itemId);
                return ValueTask.FromResult(new PlayerContextAttachResult(false, true, false, "already-in-inventory"));
            }

            var trackedOptionCount = CountItemOptionLinks(trackedItem);
            var trackedAncientCount = CountAncientGroups(trackedItem);
            this.DetachItemGraph(player.PersistenceContext, trackedItem);
            detachedTrackedItem = true;
            player.Logger.LogInformation(
                "Auction House: detached stale same-id item from player context before attaching mailbox/delivery item. Character={Character}, ItemId={ItemId}, TrackedOptionLinks={TrackedOptionLinks}, TrackedAncientGroups={TrackedAncientGroups}.",
                player.SelectedCharacter?.Name,
                itemId,
                trackedOptionCount,
                trackedAncientCount);
        }

        try
        {
            player.PersistenceContext.Attach(item);
            return ValueTask.FromResult(new PlayerContextAttachResult(true, alreadyTracked, detachedTrackedItem, detachedTrackedItem ? "detached-stale-then-attached" : "attached"));
        }
        catch (InvalidOperationException ex) when (IsDuplicateTrackedEntityException(ex))
        {
            var duplicateTrackedItem = FindTrackedItem(player.PersistenceContext, itemId);
            if (duplicateTrackedItem is not null && !ReferenceEquals(duplicateTrackedItem, item))
            {
                if (player.Inventory?.Items.Any(existingItem => existingItem.GetId() == itemId) == true)
                {
                    player.Logger.LogWarning(
                        ex,
                        "Auction House: duplicate tracked item {ItemId} is already present in inventory while claiming mailbox/delivery; mailbox/delivery evidence left intact.",
                        itemId);
                    return ValueTask.FromResult(new PlayerContextAttachResult(false, true, detachedTrackedItem, "duplicate-in-inventory"));
                }

                this.DetachItemGraph(player.PersistenceContext, duplicateTrackedItem);
                detachedTrackedItem = true;
                try
                {
                    player.PersistenceContext.Attach(item);
                    return ValueTask.FromResult(new PlayerContextAttachResult(true, true, true, "retry-detached-stale-then-attached"));
                }
                catch (InvalidOperationException retryEx) when (IsDuplicateTrackedEntityException(retryEx))
                {
                    player.Logger.LogError(
                        retryEx,
                        "Auction House: item {ItemId} still could not attach to player context after stale same-id detach; mailbox/delivery evidence left intact.",
                        itemId);
                    return ValueTask.FromResult(new PlayerContextAttachResult(false, true, detachedTrackedItem, "retry-duplicate-failed"));
                }
            }

            player.Logger.LogError(
                ex,
                "Auction House: item {ItemId} could not attach to player context and no same-id tracked item could be resolved; mailbox/delivery evidence left intact.",
                itemId);
            return ValueTask.FromResult(new PlayerContextAttachResult(false, alreadyTracked, detachedTrackedItem, "duplicate-unresolved"));
        }
    }

    private static bool IsDuplicateTrackedEntityException(InvalidOperationException ex)
    {
        return ex.Message.Contains("same key value", StringComparison.Ordinal);
    }

    private static Item? FindTrackedItem(IContext context, Guid itemId)
    {
        foreach (var entity in EnumerateTrackedEntities(context))
        {
            if (entity is Item item && item.GetId() == itemId)
            {
                return item;
            }
        }

        return null;
    }

    private static IEnumerable<object> EnumerateTrackedEntities(IContext context)
    {
        var dbContext = context.GetType()
            .GetProperty("Context", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?.GetValue(context);
        var changeTracker = dbContext?.GetType().GetProperty("ChangeTracker")?.GetValue(dbContext);
        var entriesMethod = changeTracker?.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method =>
                method.Name == "Entries"
                && !method.IsGenericMethod
                && method.GetParameters().Length == 0);
        var entries = entriesMethod?.Invoke(changeTracker, []);
        if (entries is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (var entry in enumerable)
        {
            var entity = entry?.GetType().GetProperty("Entity")?.GetValue(entry);
            if (entity is not null)
            {
                yield return entity;
            }
        }
    }

    private static int CountItemOptionLinks(Item item)
    {
        return item.ItemOptions?.Count ?? 0;
    }

    private static int CountItemOptions(Item item, ItemOptionType optionType)
    {
        return item.ItemOptions is null ? 0 : GetResolvedItemOptions(item).Count(option => option.Option.OptionType == optionType);
    }

    private static int CountAncientGroups(Item item)
    {
        return item.ItemSetGroups?.Count(group => group.AncientSetDiscriminator != 0) ?? 0;
    }

    /// <summary>
    /// Determines whether a same-id item already tracked in the live player context carries a graph
    /// complete enough to be reused as the live inventory item for a Claim All WITHOUT an expensive full DB
    /// reload. It must have its option-link collection loaded with every link resolved to its option
    /// definition (the item serializer reads <c>link.ItemOption</c>), and its ancient set-group navigation
    /// loaded (<c>null</c> = not loaded, which could silently drop ancient data). Plain items with no
    /// options and no ancient groups are trivially sufficient. If anything is missing we return false and
    /// the caller falls back to the authoritative full-graph DB load, so correctness is never traded away.
    /// </summary>
    private static bool IsTrackedItemGraphSufficient(Item trackedItem)
    {
        if (trackedItem.Definition is null)
        {
            return false;
        }

        if (trackedItem.ItemOptions is null
            || !trackedItem.ItemOptions.All(link => link.ItemOption is not null))
        {
            return false;
        }

        if (trackedItem.ItemSetGroups is null)
        {
            return false;
        }

        return true;
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

    private async ValueTask<bool> AddEscrowItemToInventoryAsync(Player player, Item item, byte targetSlot, bool notifyItemAppear = true)
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

        if (notifyItemAppear)
        {
            await player.InvokeViewPlugInAsync<IItemAppearPlugIn>(p => p.ItemAppearAsync(item)).ConfigureAwait(false);
        }
        else
        {
            player.Logger.LogInformation(
                "Auction House inventory add: skipped ItemAddedToInventory(0x22) live packet; CharacterInventory(F3-10) resync will refresh the slot. Character={Character}, ItemId={ItemId}, Slot={Slot}, Group={Group}, Number={Number}, Level={Level}.",
                player.SelectedCharacter?.Name,
                item.GetId(),
                item.ItemSlot,
                item.Definition?.Group,
                item.Definition?.Number,
                item.Level);
        }

        return true;
    }

    private async ValueTask<bool> SendFreshInventorySnapshotAsync(Player player, long listingNumber, Guid itemId, string reason)
    {
        if (player.Account is null || player.SelectedCharacter is null)
        {
            player.Logger.LogWarning(
                "Auction House {Reason}: fresh inventory snapshot could not be loaded because account/character is unavailable. ListingNumber={ListingNumber}, ItemId={ItemId}.",
                reason,
                listingNumber,
                itemId);
            return false;
        }

        try
        {
            var accountId = player.Account.GetId();
            var characterId = player.SelectedCharacter.GetId();
            using var snapshotContext = player.GameContext.PersistenceContextProvider.CreateNewPlayerContext(player.GameContext.Configuration);
            var freshAccount = await snapshotContext.GetByIdAsync<Account>(accountId).ConfigureAwait(false);
            var freshCharacter = freshAccount?.Characters.FirstOrDefault(character => character.GetId() == characterId);
            var freshItems = freshCharacter?.Inventory?.Items.OrderBy(item => item.ItemSlot).ToList();
            if (freshItems is null)
            {
                player.Logger.LogWarning(
                    "Auction House {Reason}: fresh inventory snapshot did not contain character inventory. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}, AccountId={AccountId}, CharacterId={CharacterId}.",
                    reason,
                    player.SelectedCharacter.Name,
                    listingNumber,
                    itemId,
                    accountId,
                    characterId);
                return false;
            }

            var snapshotItem = freshItems.FirstOrDefault(item => item.GetId() == itemId);
            player.Logger.LogDebug(
                "Auction House {Reason}: loaded fresh DB inventory snapshot for live item add and CharacterInventory(F3-10). Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}, SnapshotItemFound={SnapshotItemFound}, SnapshotItemCount={SnapshotItemCount}, SnapshotSlot={SnapshotSlot}, SnapshotGroup={SnapshotGroup}, SnapshotNumber={SnapshotNumber}, SnapshotLevel={SnapshotLevel}, SnapshotOptionLinks={SnapshotOptionLinks}, SnapshotResolvedOptions={SnapshotResolvedOptions}.",
                reason,
                player.SelectedCharacter.Name,
                listingNumber,
                itemId,
                snapshotItem is not null,
                freshItems.Count,
                snapshotItem?.ItemSlot,
                snapshotItem?.Definition?.Group,
                snapshotItem?.Definition?.Number,
                snapshotItem?.Level,
                snapshotItem is null ? 0 : CountItemOptionLinks(snapshotItem),
                snapshotItem is null ? 0 : GetResolvedItemOptions(snapshotItem).Count());

            if (snapshotItem is null)
            {
                player.Logger.LogWarning(
                    "Auction House {Reason}: fresh DB inventory snapshot did not contain the claimed item; skipped live item add and CharacterInventory(F3-10). Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}.",
                    reason,
                    player.SelectedCharacter.Name,
                    listingNumber,
                    itemId);
                return false;
            }

            await player.InvokeViewPlugInAsync<IItemAppearPlugIn>(p => p.ItemAppearAsync(snapshotItem)).ConfigureAwait(false);
            player.Logger.LogDebug(
                "Auction House {Reason}: sent ItemAddedToInventory(0x22) from fresh DB inventory snapshot before CharacterInventory(F3-10). Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}, Slot={Slot}, Group={Group}, Number={Number}, Level={Level}, OptionLinks={OptionLinks}, ResolvedOptions={ResolvedOptions}.",
                reason,
                player.SelectedCharacter.Name,
                listingNumber,
                itemId,
                snapshotItem.ItemSlot,
                snapshotItem.Definition?.Group,
                snapshotItem.Definition?.Number,
                snapshotItem.Level,
                CountItemOptionLinks(snapshotItem),
                GetResolvedItemOptions(snapshotItem).Count());

            await player.InvokeViewPlugInAsync<IUpdateInventoryListPlugIn>(p => p.UpdateInventoryListAsync(freshItems)).ConfigureAwait(false);
            player.Logger.LogDebug(
                "Auction House {Reason}: sent CharacterInventory(F3-10) from fresh DB inventory snapshot after item persistence. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}.",
                reason,
                player.SelectedCharacter?.Name,
                listingNumber,
                itemId);
            return true;
        }
        catch (Exception syncEx)
        {
            player.Logger.LogWarning(
                syncEx,
                "Auction House {Reason}: fresh DB inventory snapshot resync failed; item persistence remains authoritative. Character={Character}, ListingNumber={ListingNumber}, ItemId={ItemId}.",
                reason,
                player.SelectedCharacter?.Name,
                listingNumber,
                itemId);
            return false;
        }
    }

    /// <summary>
    /// Batch variant of <see cref="SendFreshInventorySnapshotAsync"/>: loads ONE fresh DB inventory
    /// snapshot and sends <see cref="IItemAppearPlugIn"/> (ItemAddedToInventory 0x22) for each claimed
    /// item id, followed by a SINGLE <see cref="IUpdateInventoryListPlugIn"/> (CharacterInventory F3-10).
    /// Used by Claim All so the whole batch costs one snapshot load + one F3-10 instead of one per item.
    /// </summary>
    private async ValueTask SendBatchFreshInventorySnapshotAsync(Player player, IReadOnlyList<Guid> claimedItemIds, string reason)
    {
        if (player.Account is null || player.SelectedCharacter is null)
        {
            return;
        }

        try
        {
            var accountId = player.Account.GetId();
            var characterId = player.SelectedCharacter.GetId();
            using var snapshotContext = player.GameContext.PersistenceContextProvider.CreateNewPlayerContext(player.GameContext.Configuration);
            var freshAccount = await snapshotContext.GetByIdAsync<Account>(accountId).ConfigureAwait(false);
            var freshCharacter = freshAccount?.Characters.FirstOrDefault(character => character.GetId() == characterId);
            var freshItems = freshCharacter?.Inventory?.Items.OrderBy(item => item.ItemSlot).ToList();
            if (freshItems is null)
            {
                player.Logger.LogWarning(
                    "Auction House {Reason}: fresh inventory snapshot had no character inventory; skipped batch 0x22/F3-10. Character={Character}.",
                    reason,
                    player.SelectedCharacter.Name);
                return;
            }

            var appeared = 0;
            foreach (var itemId in claimedItemIds)
            {
                var snapshotItem = freshItems.FirstOrDefault(item => item.GetId() == itemId);
                if (snapshotItem is null)
                {
                    continue;
                }

                await player.InvokeViewPlugInAsync<IItemAppearPlugIn>(p => p.ItemAppearAsync(snapshotItem)).ConfigureAwait(false);
                appeared++;
            }

            // ONE CharacterInventory(F3-10) for the whole batch.
            await player.InvokeViewPlugInAsync<IUpdateInventoryListPlugIn>(p => p.UpdateInventoryListAsync(freshItems)).ConfigureAwait(false);
            player.Logger.LogDebug(
                "Auction House {Reason}: sent {Appeared}/{Requested} ItemAddedToInventory(0x22) + 1 CharacterInventory(F3-10) from one fresh snapshot. Character={Character}.",
                reason,
                appeared,
                claimedItemIds.Count,
                player.SelectedCharacter.Name);
        }
        catch (Exception ex)
        {
            player.Logger.LogWarning(
                ex,
                "Auction House {Reason}: batch fresh snapshot send failed; item persistence remains authoritative. Character={Character}.",
                reason,
                player.SelectedCharacter?.Name);
        }
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
        var resolved = namedMatch ?? (candidates.Count == 1 ? candidates[0] : null);
        if (resolved is null)
        {
            // BarnaMu Auction House diagnostics (logging-only): ancient set candidates exist for this
            // item's group/number, but display-name resolution could not safely disambiguate (no name
            // match and more than one candidate), so the ancient set is NOT restored on claim. Log
            // context to diagnose ancient/option loss (e.g. a truncated display name dropping the set
            // name). Behavior is unchanged — the same (null) result is returned.
            player.Logger.LogWarning(
                "Auction House ancient-restore diagnostic: {Count} ancient candidate(s) for Group={Group} Number={Number} but display-name resolution returned null. DisplayName=\"{DisplayName}\", ResolvedName=\"{ResolvedName}\".",
                candidates.Count,
                itemGroup,
                itemNumber,
                displayName,
                name);
        }

        return resolved;
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

        var resolvedOptions = GetResolvedItemOptions(item).ToList();
        var options = GetClientItemOptionFlags(item, resolvedOptions);
        Span<byte> buffer = stackalloc byte[15];
        buffer.Clear();
        // Match the standard extended item layout parsed by CNewUIItemMng::CreateItem:
        // high nibble = group, low nibble = high item number bits, then low item number byte.
        buffer[0] = (byte)(((item.Definition.Group & 0xF) << 4) | ((item.Definition.Number >> 8) & 0xF));
        buffer[1] = (byte)(item.Definition.Number & 0xFF);
        buffer[2] = item.IsTrainablePet() ? (byte)0 : item.Level;
        buffer[3] = item.Durability();
        buffer[4] = (byte)options;

        var offset = 5;
        if (options.HasFlag(AuctionItemOptionFlags.HasOption)
            && resolvedOptions.FirstOrDefault(o => o.Option.OptionType == ItemOptionTypes.Option) is { Link: { } normalOptionLink, Option: { } normalOption })
        {
            buffer[offset++] = (byte)(((normalOption.Number & 0xF) << 4) | (normalOptionLink.Level & 0xF));
        }

        if (options.HasFlag(AuctionItemOptionFlags.HasExcellent))
        {
            buffer[offset++] = (byte)(GetExcellentByte(resolvedOptions) | GetFenrirByte(resolvedOptions));
        }

        if (options.HasFlag(AuctionItemOptionFlags.HasAncient)
            && item.ItemSetGroups.FirstOrDefault(set => set.AncientSetDiscriminator != 0) is { } ancientSet)
        {
            var ancientBonus = resolvedOptions.FirstOrDefault(o => o.Option.OptionType == ItemOptionTypes.AncientBonus);
            var ancientLevel = (ancientBonus.Link?.Level ?? 0) & 0xF;
            var ancientDiscriminator = Convert.ToByte(ancientSet.AncientSetDiscriminator) & 0xF;
            buffer[offset++] = (byte)((ancientLevel << 4) | ancientDiscriminator);
        }

        if (options.HasFlag(AuctionItemOptionFlags.HasHarmony))
        {
            buffer[offset++] = GetHarmonyByte(resolvedOptions);
        }

        if (options.HasFlag(AuctionItemOptionFlags.HasSockets))
        {
            var socketCount = Math.Min(item.SocketCount, 5);
            buffer[offset++] = (byte)((GetSocketBonusByte(resolvedOptions) & 0xF) << 4 | (socketCount & 0xF));
            for (var socketSlot = 0; socketSlot < socketCount; socketSlot++)
            {
                buffer[offset++] = GetSocketByte(resolvedOptions, socketSlot);
            }
        }

        return buffer[..offset].ToArray();
    }

    private static AuctionItemOptionFlags GetClientItemOptionFlags(Item item, IReadOnlyCollection<(ItemOptionLink Link, IncreasableItemOption Option)> resolvedOptions)
    {
        AuctionItemOptionFlags result = default;
        if (resolvedOptions.Any(o => o.Option.OptionType == ItemOptionTypes.Option))
        {
            result |= AuctionItemOptionFlags.HasOption;
        }

        if (resolvedOptions.Any(o => o.Option.OptionType == ItemOptionTypes.Luck))
        {
            result |= AuctionItemOptionFlags.HasLuck;
        }

        if (item.HasSkill)
        {
            result |= AuctionItemOptionFlags.HasSkill;
        }

        if (resolvedOptions.Any(o =>
                o.Option.OptionType == ItemOptionTypes.Excellent
                || o.Option.OptionType == ItemOptionTypes.Wing
                || o.Option.OptionType == ItemOptionTypes.BlackFenrir
                || o.Option.OptionType == ItemOptionTypes.BlueFenrir
                || o.Option.OptionType == ItemOptionTypes.GoldFenrir))
        {
            result |= AuctionItemOptionFlags.HasExcellent;
        }

        if (item.ItemSetGroups.Any(set => set.AncientSetDiscriminator != 0))
        {
            result |= AuctionItemOptionFlags.HasAncient;
        }

        if (resolvedOptions.Any(o => o.Option.OptionType == ItemOptionTypes.HarmonyOption))
        {
            result |= AuctionItemOptionFlags.HasHarmony;
        }

        if (resolvedOptions.Any(o => o.Option.OptionType == ItemOptionTypes.GuardianOption))
        {
            result |= AuctionItemOptionFlags.HasGuardian;
        }

        if (item.SocketCount > 0)
        {
            result |= AuctionItemOptionFlags.HasSockets;
        }

        return result;
    }

    private static IEnumerable<(ItemOptionLink Link, IncreasableItemOption Option)> GetResolvedItemOptions(Item item)
    {
        foreach (var optionLink in item.ItemOptions)
        {
            if (ResolveItemOption(item, optionLink) is { } option)
            {
                yield return (optionLink, option);
            }
        }
    }

    private static IncreasableItemOption? ResolveItemOption(Item item, ItemOptionLink optionLink)
    {
        if (optionLink.ItemOption is { } loadedOption)
        {
            return loadedOption;
        }

        if (item.Definition is null || TryGetItemOptionId(optionLink) is not { } optionId)
        {
            return null;
        }

        return item.Definition.PossibleItemOptions
                   .SelectMany(optionDefinition => optionDefinition.PossibleOptions)
                   .FirstOrDefault(option => option.GetId() == optionId)
               ?? item.ItemSetGroups
                   .Select(itemSet => itemSet.BonusOption)
                   .FirstOrDefault(option => option?.GetId() == optionId);
    }

    private static Guid? TryGetItemOptionId(ItemOptionLink optionLink)
    {
        var optionId = optionLink.GetType()
            .GetProperty("ItemOptionId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(optionLink);

        return optionId is Guid guid ? guid : null;
    }

    private static byte GetExcellentByte(IEnumerable<(ItemOptionLink Link, IncreasableItemOption Option)> resolvedOptions)
    {
        byte result = 0;
        var excellentOptions = resolvedOptions.Where(o =>
            o.Option.OptionType == ItemOptionTypes.Excellent
            || o.Option.OptionType == ItemOptionTypes.Wing);
        foreach (var option in excellentOptions)
        {
            if (option.Option.Number is > 0 and <= 8)
            {
                result |= (byte)(1 << (option.Option.Number - 1));
            }
        }

        return result;
    }

    private static byte GetFenrirByte(IEnumerable<(ItemOptionLink Link, IncreasableItemOption Option)> resolvedOptions)
    {
        byte result = 0;
        var options = resolvedOptions.ToList();
        if (options.Any(o => o.Option.OptionType == ItemOptionTypes.BlackFenrir))
        {
            result |= 0x01;
        }

        if (options.Any(o => o.Option.OptionType == ItemOptionTypes.BlueFenrir))
        {
            result |= 0x02;
        }

        if (options.Any(o => o.Option.OptionType == ItemOptionTypes.GoldFenrir))
        {
            result |= 0x04;
        }

        return result;
    }

    private static byte GetHarmonyByte(IEnumerable<(ItemOptionLink Link, IncreasableItemOption Option)> resolvedOptions)
    {
        if (resolvedOptions.FirstOrDefault(o => o.Option.OptionType == ItemOptionTypes.HarmonyOption) is not { Link: { } harmonyOptionLink, Option: { } harmonyOption })
        {
            return 0;
        }

        return (byte)(((harmonyOption.Number & 0xF) << 4) | (harmonyOptionLink.Level & 0xF));
    }

    private static byte GetSocketBonusByte(IEnumerable<(ItemOptionLink Link, IncreasableItemOption Option)> resolvedOptions)
    {
        var bonusOption = resolvedOptions.FirstOrDefault(o => o.Option.OptionType == ItemOptionTypes.SocketBonusOption);
        return bonusOption.Option is null ? (byte)0xF : (byte)(bonusOption.Option.Number & 0xF);
    }

    private static byte GetSocketByte(IEnumerable<(ItemOptionLink Link, IncreasableItemOption Option)> resolvedOptions, int socketSlot)
    {
        var optionLink = resolvedOptions.FirstOrDefault(o => o.Option.OptionType == ItemOptionTypes.SocketOption && o.Link.Index == socketSlot);
        if (optionLink.Option is null)
        {
            return AuctionEmptySocket;
        }

        var elementType = optionLink.Option.SubOptionType;
        var elementOption = optionLink.Option.Number;
        if (elementType < 0 || elementType >= SocketOptionIndexOffsets.Length)
        {
            return AuctionNoSocket;
        }

        var optionIndex = SocketOptionIndexOffsets[elementType] + elementOption;
        return (byte)((optionLink.Link.Level * AuctionMaximumSocketOptions) + optionIndex);
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
