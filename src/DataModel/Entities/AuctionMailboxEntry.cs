// <copyright file="AuctionMailboxEntry.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.DataModel.Entities;

/// <summary>
/// The kind of an auction mailbox entry.
/// </summary>
public enum AuctionMailboxEntryType
{
    /// <summary>
    /// A bought item delivery.
    /// </summary>
    ItemDelivery,

    /// <summary>
    /// A seller payout.
    /// </summary>
    SellerPayout,

    /// <summary>
    /// A returned item.
    /// </summary>
    ReturnedItem,
}

/// <summary>
/// An auction mailbox entry.
/// </summary>
[AggregateRoot]
public class AuctionMailboxEntry
{
    /// <summary>
    /// Gets or sets the owner account identifier.
    /// </summary>
    public Guid OwnerAccountId { get; set; }

    /// <summary>
    /// Gets or sets the owner character identifier.
    /// </summary>
    public Guid OwnerCharacterId { get; set; }

    /// <summary>
    /// Gets or sets the owner character name.
    /// </summary>
    [Required]
    public string OwnerCharacterName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the related listing number.
    /// </summary>
    public long ListingNumber { get; set; }

    /// <summary>
    /// Gets or sets the entry type.
    /// </summary>
    public AuctionMailboxEntryType Type { get; set; }

    /// <summary>
    /// Gets or sets the item.
    /// </summary>
    public virtual Item? Item { get; set; }

    /// <summary>
    /// Gets or sets the item storage.
    /// </summary>
    [MemberOfAggregate]
    public virtual ItemStorage? ItemStorage { get; set; }

    /// <summary>
    /// Gets or sets the item display name.
    /// </summary>
    [Required]
    public string ItemDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sender character name.
    /// </summary>
    public string SenderCharacterName { get; set; } = string.Empty;

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
    /// Gets or sets the amount.
    /// </summary>
    public long Amount { get; set; }

    /// <summary>
    /// Gets or sets the currency.
    /// </summary>
    public AuctionCurrency Currency { get; set; }

    /// <summary>
    /// Gets or sets the jewel bank slot when the entry uses jewel currency.
    /// </summary>
    public int? JewelBankSlot { get; set; }

    /// <summary>
    /// Gets or sets the creation timestamp.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the claimed timestamp.
    /// </summary>
    public DateTime? ClaimedAt { get; set; }
}
