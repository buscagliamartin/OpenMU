// <copyright file="AuctionMailboxEntryExtensions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework.Extensions.ModelBuilder;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MUnique.OpenMU.Persistence.EntityFramework.Model;

/// <summary>
/// Extensions for the <see cref="EntityTypeBuilder{AuctionMailboxEntry}"/>.
/// </summary>
internal static class AuctionMailboxEntryExtensions
{
    /// <summary>
    /// Applies the settings for the <see cref="AuctionMailboxEntry"/> entity.
    /// </summary>
    /// <param name="builder">The builder.</param>
    public static void Apply(this EntityTypeBuilder<AuctionMailboxEntry> builder)
    {
        const string itemStorageId = "ItemStorageId";
        var itemStorageNavigation = typeof(AuctionMailboxEntry).GetProperty("RawItemStorage") is null
            ? nameof(AuctionMailboxEntry.ItemStorage)
            : "RawItemStorage";

        builder.Property(entry => entry.OwnerCharacterName).HasMaxLength(32).IsRequired();
        builder.Property(entry => entry.ItemDisplayName).HasMaxLength(160).IsRequired();
        builder.Property(entry => entry.SenderCharacterName).HasMaxLength(32).IsRequired();
        builder.Property<Guid?>(itemStorageId);

        builder.HasIndex(entry => new { entry.OwnerCharacterId, entry.ClaimedAt, entry.Type });
        builder.HasIndex(entry => entry.ListingNumber);
        builder.HasIndex(itemStorageId).IsUnique();

        builder.HasOne(entry => entry.RawItem)
            .WithMany()
            .HasForeignKey(entry => entry.ItemId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<ItemStorage>(itemStorageNavigation)
            .WithOne()
            .HasForeignKey<AuctionMailboxEntry>(itemStorageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
