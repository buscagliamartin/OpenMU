// <copyright file="MuHelperStatusUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.MuHelper;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Views.MuHelper;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Sends Mu Helper status updates to the client.
/// </summary>
[PlugIn("Mu Helper status update", "Acknowledges Mu Helper start, stop and cost updates.")]
[Guid("6F2E1E5F-D130-496A-B2B0-5D01BD001366")]
public class MuHelperStatusUpdatePlugIn : IMuHelperStatusUpdatePlugIn
{
    private readonly RemotePlayer _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="MuHelperStatusUpdatePlugIn"/> class.
    /// </summary>
    /// <param name="player">The remote player.</param>
    public MuHelperStatusUpdatePlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc />
    public ValueTask StartAsync() => this._player.Connection.SendStatusUpdateAsync(false, 0, false);

    /// <inheritdoc />
    public ValueTask StopAsync() => this._player.Connection.SendStatusUpdateAsync(false, 0, true);

    /// <inheritdoc />
    public ValueTask ConsumeMoneyAsync(uint money) => this._player.Connection.SendStatusUpdateAsync(true, money, false);
}
