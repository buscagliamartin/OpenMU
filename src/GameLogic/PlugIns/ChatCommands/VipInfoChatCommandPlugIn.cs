// <copyright file="VipInfoChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;

using System.Runtime.InteropServices;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// BarnaMu: player chat command to check the VIP status of the own account and how much time is
/// left before it expires. Read-only — performs no database writes. VIP is computed live from
/// <see cref="DataModel.Entities.Account.VipExpirationDate"/> via <see cref="VipAccountExtensions.IsVipActive"/>.
/// Usage: <c>/vipinfo</c>.
/// </summary>
[Guid("C1A2B3D4-5E6F-4A7B-8C9D-0E1F2A3B4C5D")]
[PlugIn]
[Display(Name = "VIP Info Chat Command", Description = "BarnaMu: player command to check VIP status and remaining time. Usage: /vipinfo")]
[ChatCommandHelp(Command, typeof(Arguments), MinimumStatus)]
public class VipInfoChatCommandPlugIn : ChatCommandPlugInBase<VipInfoChatCommandPlugIn.Arguments>
{
    private const string Command = "/vipinfo";
    private const CharacterStatus MinimumStatus = CharacterStatus.Normal;

    /// <inheritdoc />
    public override string Key => Command;

    /// <inheritdoc />
    public override CharacterStatus MinCharacterStatusRequirement => MinimumStatus;

    /// <inheritdoc />
    protected override async ValueTask DoHandleCommandAsync(Player player, Arguments arguments)
    {
        if (player.Account is not { } account)
        {
            return;
        }

        if (!account.IsVipActive())
        {
            await player.ShowBlueMessageAsync("No tenes VIP activo. Consulta en la web como conseguirlo.").ConfigureAwait(false);
            return;
        }

        // IsVipActive() guarantees VipExpirationDate is set and in the future.
        var expiration = account.VipExpirationDate!.Value;
        var remaining = expiration - DateTime.UtcNow;
        await player.ShowBlueMessageAsync(
            $"VIP activo. Te quedan {remaining.Days} dia(s) y {remaining.Hours} hora(s). Vence: {expiration:yyyy-MM-dd HH:mm} UTC.")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Arguments for the <c>/vipinfo</c> command (none).
    /// </summary>
    public class Arguments : ArgumentsBase
    {
    }
}
