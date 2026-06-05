// <copyright file="UpdateMaximumHealthExtendedPlugIn.cs" company="MUnique">
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
/// The extended implementation of the <see cref="IUpdateMaximumHealthPlugIn"/> which forwards maximum stats to the game client.
/// </summary>
[PlugIn(nameof(UpdateMaximumHealthExtendedPlugIn), "The extended implementation of the IUpdateMaximumHealthPlugIn which forwards maximum stats to the game client.")]
[Guid("4D9E88B8-48B9-4864-8C02-9C75DB7D364C")]
[MinimumClient(106, 3, ClientLanguage.Invariant)]
public class UpdateMaximumHealthExtendedPlugIn : IUpdateMaximumHealthPlugIn
{
    private readonly RemotePlayer _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateMaximumHealthExtendedPlugIn"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public UpdateMaximumHealthExtendedPlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc/>
    public ValueTask UpdateMaximumHealthAsync() => this.SendMaximumStatsAsync();

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
