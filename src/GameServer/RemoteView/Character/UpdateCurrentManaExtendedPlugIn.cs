// <copyright file="UpdateCurrentManaExtendedPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.Character;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Views.Character;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// The extended implementation of the <see cref="IUpdateCurrentManaPlugIn"/> which forwards current stats to the game client.
/// </summary>
[PlugIn(nameof(UpdateCurrentManaExtendedPlugIn), "The extended implementation of the IUpdateCurrentManaPlugIn which forwards current stats to the game client.")]
[Guid("3207CB3C-6159-4621-A89D-9EAF53C52B7F")]
[MinimumClient(106, 3, ClientLanguage.Invariant)]
public class UpdateCurrentManaExtendedPlugIn : IUpdateCurrentManaPlugIn
{
    private readonly RemotePlayer _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateCurrentManaExtendedPlugIn"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public UpdateCurrentManaExtendedPlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc/>
    public ValueTask UpdateCurrentManaAsync() => this.SendCurrentStatsAsync();

    private async ValueTask SendCurrentStatsAsync()
    {
        var connection = this._player.Connection;
        if (connection is null || this._player.Attributes is null)
        {
            return;
        }

        var attackSpeed = (ushort)this._player.Attributes[Stats.AttackSpeed];
        await connection.SendCurrentStatsExtendedAsync(
            (uint)Math.Max(this._player.Attributes[Stats.CurrentHealth], 0f),
            (uint)Math.Max(this._player.Attributes[Stats.CurrentShield], 0f),
            (uint)Math.Max(this._player.Attributes[Stats.CurrentMana], 0f),
            (uint)Math.Max(this._player.Attributes[Stats.CurrentAbility], 0f),
            attackSpeed,
            attackSpeed).ConfigureAwait(false);
    }
}
