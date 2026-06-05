// <copyright file="AuctionListing.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.DataModel.Entities;

/// <summary>
/// The currency of an auction listing.
/// </summary>
public enum AuctionCurrency
{
    /// <summary>
    /// Zen.
    /// </summary>
    Zen,

    /// <summary>
    /// W Coin.
    /// </summary>
    WCoin,

    /// <summary>
    /// Jewel bank balance.
    /// </summary>
    Jewel,
}

/// <summary>
/// The persistence status of an auction listing.
/// </summary>
public enum AuctionListingStatus
{
    /// <summary>
    /// The listing is active.
    /// </summary>
    Active,

    /// <summary>
    /// The listing was sold.
    /// </summary>
    Sold,

    /// <summary>
    /// The listing was cancelled.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The listing expired.
    /// </summary>
    Expired,

    /// <summary>
    /// The listing was completed.
    /// </summary>
    Completed,
}

/// <summary>
/// An auction listing.
/// </summary>
[AggregateRoot]
public class AuctionListing
{
    /// <summary>
    /// Gets or sets the user-facing listing number.
    /// </summary>
    public long ListingNumber { get; set; }

    /// <summary>
    /// Gets or sets the seller account identifier.
    /// </summary>
    public Guid SellerAccountId { get; set; }

    /// <summary>
    /// Gets or sets the seller character identifier.
    /// </summary>
    public Guid SellerCharacterId { get; set; }

    /// <summary>
    /// Gets or sets the seller character name.
    /// </summary>
    [Required]
    public string SellerCharacterName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the buyer account identifier.
    /// </summary>
    public Guid? BuyerAccountId { get; set; }

    /// <summary>
    /// Gets or sets the buyer character identifier.
    /// </summary>
    public Guid? BuyerCharacterId { get; set; }

    /// <summary>
    /// Gets or sets the buyer character name.
    /// </summary>
    public string BuyerCharacterName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the escrow item.
    /// </summary>
    public virtual Item? EscrowItem { get; set; }

    /// <summary>
    /// Gets or sets the escrow item storage.
    /// </summary>
    [MemberOfAggregate]
    public virtual ItemStorage? EscrowStorage { get; set; }

    /// <summary>
    /// Gets or sets the display label of the listed item.
    /// </summary>
    [Required]
    public string ItemDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item group.
    /// </summary>
    public byte ItemGroup { get; set; }

    /// <summary>
    /// Gets or sets the item number.
    /// </summary>
    public short ItemNumber { get; set; }

    /// <summary>
    /// Gets or sets the item level.
    /// </summary>
    public byte ItemLevel { get; set; }

    /// <summary>
    /// Gets or sets the price.
    /// </summary>
    public long Price { get; set; }

    /// <summary>
    /// Gets or sets the fee amount.
    /// </summary>
    public long FeeAmount { get; set; }

    /// <summary>
    /// Gets or sets the seller payout amount.
    /// </summary>
    public long SellerPayoutAmount { get; set; }

    /// <summary>
    /// Gets or sets the currency.
    /// </summary>
    public AuctionCurrency Currency { get; set; }

    /// <summary>
    /// Gets or sets the jewel bank slot when the listing uses jewel currency.
    /// </summary>
    public int? JewelBankSlot { get; set; }

    /// <summary>
    /// Gets or sets the listing status.
    /// </summary>
    public AuctionListingStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the creation timestamp.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the expiration timestamp.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the sold timestamp.
    /// </summary>
    public DateTime? SoldAt { get; set; }

    /// <summary>
    /// Gets or sets the cancellation timestamp.
    /// </summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>
    /// Gets or sets the delivery claimed timestamp.
    /// </summary>
    public DateTime? DeliveryClaimedAt { get; set; }

    /// <summary>
    /// Gets or sets the seller payout claimed timestamp.
    /// </summary>
    public DateTime? SellerPayoutClaimedAt { get; set; }
}
