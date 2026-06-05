// <copyright file="UpdateMaximumManaExtendedPlugIn.cs" company="MUnique">
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
/// The extended implementation of the <see cref="IUpdateMaximumManaPlugIn"/> which forwards maximum stats to the game client.
/// </summary>
[PlugIn(nameof(UpdateMaximumManaExtendedPlugIn), "The extended implementation of the IUpdateMaximumManaPlugIn which forwards maximum stats to the game client.")]
[Guid("3463A604-175E-45D3-A3D9-ED522A3D645E")]
[MinimumClient(106, 3, ClientLanguage.Invariant)]
public class UpdateMaximumManaExtendedPlugIn : IUpdateMaximumManaPlugIn
{
    private readonly RemotePlayer _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateMaximumManaExtendedPlugIn"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public UpdateMaximumManaExtendedPlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc/>
    public ValueTask UpdateMaximumManaAsync() => this.SendMaximumStatsAsync();

    private async ValueTask SendMaximumStatsAsync()
    {
        var connection = this._player.Connection;
        if (connection is null || this._player.Attributes is null)
        {
            return;
        }

        await connection.SendMaximumStatsExtendedAsync(
            (uint)this._player.Attributes[Stats.MaximumHealth],
            (uint)this._player.Attributes[Stats.MaximumShield],
            (uint)this._player.Attributes[Stats.MaximumMana],
            (uint)this._player.Attributes[Stats.MaximumAbility]).ConfigureAwait(false);
    }
}
