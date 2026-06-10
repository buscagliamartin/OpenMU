// <copyright file="VipAttributeUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// BarnaMu VIP command access: when a player enters the world, sets the existing
/// <see cref="Stats.IsVip"/> attribute from the account's current VIP state, computed from
/// <see cref="DataModel.Entities.Account.VipExpirationDate"/> via
/// <see cref="VipAccountExtensions.IsVipActive"/>. This populates the attribute that the upstream
/// <c>MinimumVipLevel</c>-gated commands (e.g. <c>/openware</c>, <c>/npc</c>) read. It changes no
/// gameplay by itself — those commands only gate when an admin configures a positive minimum VIP
/// level. Runtime/in-memory only: uses the existing <c>SetStatAttribute</c> mechanism (the same
/// one used for the safezone flag), with no schema, model, migration, or Player.cs change.
/// </summary>
[PlugIn]
[Display(Name = "BarnaMu VIP Attribute Update", Description = "Sets the Stats.IsVip attribute from the account's active VIP (Account.VipExpirationDate) when the player enters the world, so the upstream VIP-gated commands recognize VIP players.")]
[Guid("7C9E2B14-3A6D-4F58-9E21-8B0C5D2A4F6E")]
public sealed class VipAttributeUpdatePlugIn : IPlayerStateChangedPlugIn
{
    /// <inheritdoc />
    public ValueTask PlayerStateChangedAsync(Player player, State previousState, State currentState)
    {
        if (currentState != PlayerState.EnteredWorld)
        {
            return ValueTask.CompletedTask;
        }

        player.Attributes?.SetStatAttribute(Stats.IsVip, player.Account.IsVipActive() ? 1.0f : 0.0f);
        return ValueTask.CompletedTask;
    }
}
