// <copyright file="UpdateInventoryListPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.Inventory;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// The default implementation of the <see cref="IUpdateInventoryListPlugIn"/> which is forwarding everything to the game client with specific data packets.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.UpdateInventoryListPlugIn_Name), Description = nameof(PlugInResources.UpdateInventoryListPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("ba8ca7c7-a497-497e-b2f7-9f9366ff6ac5")]
public class UpdateInventoryListPlugIn : IUpdateInventoryListPlugIn
{
    private readonly RemotePlayer _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateInventoryListPlugIn"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public UpdateInventoryListPlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc/>
    public async ValueTask UpdateInventoryListAsync()
    {
        var items = this._player.Inventory?.Items ?? this._player.SelectedCharacter?.Inventory?.Items ?? Enumerable.Empty<Item>();
        await this.UpdateInventoryListAsync(items).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask UpdateInventoryListAsync(IEnumerable<Item> inventoryItems)
    {
        var connection = this._player.Connection;
        if (connection is null)
        {
            return;
        }

        // C4 00 00 00 F3 10 ...
        var items = inventoryItems
            .OrderBy(item => item.ItemSlot)
            .ToList();
        var packetSize = 0;
        var serializedItemCount = 0;
        var logItemBytes = this._player.Logger.IsEnabled(LogLevel.Debug);
        var serializedItems = new List<(Guid ItemId, byte Slot, int? Group, int? Number, byte Level, int ItemSize, string Bytes)>();
        int Write()
        {
            serializedItems.Clear();
            var itemSerializer = this._player.ItemSerializer;
            var lengthPerItem = StoredItemRef.GetRequiredSize(itemSerializer.NeededSpace);
            var size = CharacterInventoryRef.GetRequiredSize(items.Count, lengthPerItem);
            var span = connection.Output.GetSpan(size)[..size];
            var packet = new CharacterInventoryRef(span)
            {
                ItemCount = 0,
            };

            int headerSize = CharacterInventoryRef.GetRequiredSize(0, 0);
            int actualSize = headerSize;
            var seenSlots = new HashSet<byte>();
            foreach (var item in items)
            {
                if (item.Definition is null)
                {
                    this._player.Logger.LogWarning("Item {0} has no definition.", item);
                    continue;
                }

                if (!seenSlots.Add(item.ItemSlot))
                {
                    this._player.Logger.LogWarning(
                        "Duplicate item slot {Slot} detected in inventory list update for player {Player}. Skipping item {Item}.",
                        item.ItemSlot,
                        this._player,
                        item);
                    continue;
                }

                var storedItem = new StoredItemRef(span[actualSize..]);
                storedItem.ItemSlot = item.ItemSlot;
                var itemSize = itemSerializer.SerializeItem(storedItem.ItemData, item);
                if (logItemBytes)
                {
                    serializedItems.Add((
                        item.GetId(),
                        item.ItemSlot,
                        item.Definition?.Group,
                        item.Definition?.Number,
                        item.Level,
                        itemSize,
                        Convert.ToHexString(storedItem.ItemData[..itemSize])));
                }

                actualSize += StoredItemRef.GetRequiredSize(itemSize);
                packet.ItemCount++;
            }

            span.Slice(0, actualSize).SetPacketSize();
            packetSize = actualSize;
            serializedItemCount = packet.ItemCount;
            return actualSize;
        }

        await connection.SendAsync(Write).ConfigureAwait(false);
        this._player.Logger.LogInformation(
            "Inventory full sync packet sent. Path=CharacterInventory(F3-10), Character={Character}, ItemCount={ItemCount}, PacketSize={PacketSize}.",
            this._player.SelectedCharacter?.Name,
            serializedItemCount,
            packetSize);
        if (logItemBytes)
        {
            foreach (var serializedItem in serializedItems)
            {
                this._player.Logger.LogDebug(
                    "Inventory item packet bytes. Path=CharacterInventory(F3-10), Character={Character}, ItemId={ItemId}, Slot={Slot}, Group={Group}, Number={Number}, Level={Level}, ItemSize={ItemSize}, PacketSize={PacketSize}, Bytes={Bytes}.",
                    this._player.SelectedCharacter?.Name,
                    serializedItem.ItemId,
                    serializedItem.Slot,
                    serializedItem.Group,
                    serializedItem.Number,
                    serializedItem.Level,
                    serializedItem.ItemSize,
                    packetSize,
                    serializedItem.Bytes);
            }
        }
    }
}
