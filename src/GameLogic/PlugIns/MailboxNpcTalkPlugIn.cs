// <copyright file="MailboxNpcTalkPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Views.AuctionHouse;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// BarnaMu: when the player talks to the Postman NPC (Lorencia), the auction Mailbox
/// opens on the client. Replaces the old Helper "Mailbox" button. Uses the existing Auction House
/// view channel (0xBF/0x31) so no new protocol is introduced.
/// </summary>
[Guid("E2C9A7D4-3F61-4B28-9A55-6D1E2F3A4B5C")]
[PlugIn]
[Display(Name = "Mailbox NPC (Postman)", Description = "BarnaMu: opens the Mailbox when the player talks to the Postman NPC in Lorencia.")]
public class MailboxNpcTalkPlugIn : IPlayerTalkToNpcPlugIn
{
    private const byte LorenciaMapNumber = 0;

    /// <summary>
    /// Gets the NPC number of the Postman. This uses an existing client-supported passive NPC model.
    /// </summary>
    public static short PostmanNpcNumber => 379;

    /// <inheritdoc />
    public async ValueTask PlayerTalksToNpcAsync(Player player, NonPlayerCharacter npc, NpcTalkEventArgs eventArgs)
    {
        if (npc.Definition.Number != PostmanNpcNumber || npc.CurrentMap.Definition.Number != LorenciaMapNumber)
        {
            return;
        }

        // Mark handled synchronously (the talk-plugin point is invoked without awaiting), then tell
        // the client to open the Mailbox window; it requests its contents over the usual flow.
        eventArgs.HasBeenHandled = true;
        await player.InvokeViewPlugInAsync<IAuctionHouseViewPlugIn>(p => p.OpenMailboxAsync()).ConfigureAwait(false);
    }
}
