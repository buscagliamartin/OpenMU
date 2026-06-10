// <copyright file="ChatMessageNormalProcessor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Chat;

using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Views;

/// <summary>
/// A chat message processor for normal chat.
/// </summary>
public class ChatMessageNormalProcessor : BannableChatMessageBaseProcessor
{
    /// <inheritdoc/>
    public override async ValueTask SubclassProcessMessageAsync(Player sender, (string Message, string PlayerName) content)
    {
        sender.Logger.LogDebug("Sending Chat Message to Observers, Count: {0}", sender.Observers.Count);

        // BarnaMu VIP perks Phase 1: server-side chat tag. Prefix the message with [GM] for a game
        // master, or [VIP] for an account with active VIP (computed from Account.VipExpirationDate via
        // VipAccountExtensions.IsVipActive). Non-VIP / non-GM players are unaffected. This only changes
        // the outgoing message string; the chat packet structure and client/protocol are unchanged.
        var prefix = sender.Account?.State switch
        {
            AccountState.GameMaster => "[GM] ",
            AccountState.GameMasterInvisible => "[GM] ",
            _ => sender.Account.IsVipActive() ? "[VIP] " : string.Empty,
        };
        var message = prefix.Length > 0 ? prefix + content.Message : content.Message;

        await sender.ForEachWorldObserverAsync<IChatViewPlugIn>(p => p.ChatMessageAsync(message, sender.SelectedCharacter!.Name, ChatMessageType.Normal), true).ConfigureAwait(false);
    }
}