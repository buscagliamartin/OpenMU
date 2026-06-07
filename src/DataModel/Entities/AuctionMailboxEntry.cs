// <copyright file="AuctionMailboxEntry.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.DataModel.Entities;

/// <summary>
/// The kind of a pending auction mailbox entry.
/// </summary>
public enum AuctionMailboxEntryType
{
    /// <summary>
    /// A bought item waiting for the buyer.
    /// </summary>
    ItemDelivery,

    /// <summary>
    /// A seller payout waiting for the seller.
    /// </summary>
    SellerPayout,

    /// <summary>
    /// An unsold item returned to the seller.
    /// </summary>
    ReturnedItem,
}

/// <summary>
/// Durable Auction House mailbox entry for bought items, returned items, and seller payouts.
/// </summary>
[AggregateRoot]
public class AuctionMailboxEntry
{
    /// <summary>
    /// Gets or sets the account which owns this mailbox entry.
    /// </summary>
    public Guid OwnerAccountId { get; set; }

    /// <summary>
    /// Gets or sets the character which owns this mailbox entry.
    /// </summary>
    public Guid OwnerCharacterId { get; set; }

    /// <summary>
    /// Gets or sets the owner character name snapshot.
    /// </summary>
    [Required]
    public string OwnerCharacterName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source listing number.
    /// </summary>
    public long ListingNumber { get; set; }

    /// <summary>
    /// Gets or sets the entry type.
    /// </summary>
    public AuctionMailboxEntryType Type { get; set; }

    /// <summary>
    /// Gets or sets the item held by this mailbox entry.
    /// </summary>
    public virtual Item? Item { get; set; }

    /// <summary>
    /// Gets or sets the storage which owns the mailbox item while it is pending.
    /// </summary>
    [MemberOfAggregate]
    public virtual ItemStorage? ItemStorage { get; set; }

    /// <summary>
    /// Gets or sets a display snapshot of the item or payout source.
    /// </summary>
    [Required]
    public string ItemDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source/sender character name snapshot.
    /// </summary>
    public string SenderCharacterName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item definition group.
    /// </summary>
    public byte ItemGroup { get; set; }

    /// <summary>
    /// Gets or sets the item definition number.
    /// </summary>
    public short ItemNumber { get; set; }

    /// <summary>
    /// Gets or sets the item level.
    /// </summary>
    public byte ItemLevel { get; set; }

    /// <summary>
    /// Gets or sets the amount associated with the entry.
    /// </summary>
    public long Amount { get; set; }

    /// <summary>
    /// Gets or sets the currency associated with the amount.
    /// </summary>
    public AuctionCurrency Currency { get; set; }

    /// <summary>
    /// Gets or sets the jewel bank slot when <see cref="Currency"/> is <see cref="AuctionCurrency.Jewel"/>.
    /// </summary>
    public int? JewelBankSlot { get; set; }

    /// <summary>
    /// Gets or sets the UTC creation timestamp.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the UTC claim timestamp.
    /// </summary>
    public DateTime? ClaimedAt { get; set; }
}
