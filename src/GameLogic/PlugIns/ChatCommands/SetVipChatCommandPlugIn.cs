// <copyright file="SetVipChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// BarnaMu: GM chat command which grants VIP time by setting the account's
/// <see cref="DataModel.Entities.Account.VipExpirationDate"/>. VIP is a computed timer/status
/// bridge (see <see cref="VipAccountExtensions.IsVipActive"/>); this command writes only the
/// expiration date and changes no other account or player state. No gameplay perk is wired in
/// this branch.
/// Usage: <c>/setvip &lt;character&gt; [days]</c>. When the days argument is omitted (or 0), a
/// default of 30 days is used. VIP "expires" automatically once the date passes (the computed
/// check simply reads as not-VIP); no revert job is required.
/// </summary>
[Guid("D4E8F1A2-3B6C-4D7E-9F0A-1B2C3D4E5F60")]
[PlugIn]
[Display(Name = "Set VIP Chat Command", Description = "BarnaMu: GM command to grant VIP time. Usage: /setvip <character> [days] (default 30).")]
[ChatCommandHelp(Command, typeof(Arguments), MinimumStatus)]
public class SetVipChatCommandPlugIn : ChatCommandPlugInBase<SetVipChatCommandPlugIn.Arguments>
{
    private const string Command = "/setvip";
    private const int DefaultVipDays = 30;
    private const CharacterStatus MinimumStatus = CharacterStatus.GameMaster;

    /// <inheritdoc />
    public override string Key => Command;

    /// <inheritdoc />
    public override CharacterStatus MinCharacterStatusRequirement => MinimumStatus;

    /// <inheritdoc />
    protected override async ValueTask DoHandleCommandAsync(Player gameMaster, Arguments arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments.CharacterName))
        {
            await gameMaster.ShowBlueMessageAsync("Uso: /setvip <personaje> [dias]  (por defecto 30 dias)").ConfigureAwait(false);
            return;
        }

        var days = arguments.Days > 0 ? arguments.Days : DefaultVipDays;
        var expiration = DateTime.UtcNow.AddDays(days);

        // If the player is online, update the in-memory account so VIP takes effect immediately
        // without a reconnect, then persist. Only VipExpirationDate is changed.
        var targetPlayer = gameMaster.GameContext.GetPlayerByCharacterName(arguments.CharacterName);
        if (targetPlayer?.Account is { } onlineAccount
            && targetPlayer.SelectedCharacter?.Name is { } selectedName
            && selectedName.Equals(arguments.CharacterName, StringComparison.OrdinalIgnoreCase))
        {
            onlineAccount.VipExpirationDate = expiration;
            await targetPlayer.SaveProgressAsync().ConfigureAwait(false);

            // BarnaMu VIP command access: refresh the Stats.IsVip attribute immediately so VIP-gated
            // commands recognize the change without the player having to re-enter the world (the
            // VipAttributeUpdatePlugIn otherwise sets this on the next EnteredWorld).
            targetPlayer.Attributes?.SetStatAttribute(Stats.IsVip, onlineAccount.IsVipActive() ? 1.0f : 0.0f);

            await targetPlayer.ShowBlueMessageAsync($"Ahora sos VIP por {days} dia(s). Vence: {expiration:yyyy-MM-dd HH:mm} UTC.").ConfigureAwait(false);
            await gameMaster.ShowBlueMessageAsync($"VIP asignado a '{arguments.CharacterName}' por {days} dia(s) (jugador online).").ConfigureAwait(false);
            return;
        }

        // Offline player: update directly through a new player context. Only VipExpirationDate is changed.
        using var context = gameMaster.GameContext.PersistenceContextProvider.CreateNewPlayerContext(gameMaster.GameContext.Configuration);
        var account = await context.GetAccountByCharacterNameAsync(arguments.CharacterName).ConfigureAwait(false);
        if (account is null)
        {
            await gameMaster.ShowBlueMessageAsync($"No se encontro la cuenta del personaje '{arguments.CharacterName}'.").ConfigureAwait(false);
            return;
        }

        account.VipExpirationDate = expiration;
        await context.SaveChangesAsync().ConfigureAwait(false);
        await gameMaster.ShowBlueMessageAsync($"VIP asignado a '{arguments.CharacterName}' por {days} dia(s). Vence: {expiration:yyyy-MM-dd HH:mm} UTC.").ConfigureAwait(false);
    }

    /// <summary>
    /// Arguments for the <c>/setvip</c> command.
    /// </summary>
    public class Arguments : ArgumentsBase
    {
        /// <summary>
        /// Gets or sets the name of a character of the account to grant VIP to.
        /// </summary>
        public string? CharacterName { get; set; }

        /// <summary>
        /// Gets or sets the amount of VIP days. When 0 or not provided, a default of 30 days is used.
        /// </summary>
        public int Days { get; set; }
    }
}
