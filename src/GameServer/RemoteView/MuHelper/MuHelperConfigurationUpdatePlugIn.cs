// <copyright file="MuHelperConfigurationUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.MuHelper;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Views.MuHelper;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Sends saved Mu Helper configuration data to the client.
/// </summary>
[PlugIn("Mu Helper configuration update", "Sends the 257-byte Mu Helper configuration blob to the client.")]
[Guid("E152E151-543C-437E-8BFD-2D92391822F5")]
public class MuHelperConfigurationUpdatePlugIn : IMuHelperConfigurationUpdatePlugIn
{
    private readonly RemotePlayer _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="MuHelperConfigurationUpdatePlugIn"/> class.
    /// </summary>
    /// <param name="player">The remote player.</param>
    public MuHelperConfigurationUpdatePlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc />
    public ValueTask UpdateMuHelperConfigurationAsync(Memory<byte> data)
        => this._player.Connection.SendConfigurationDataAsync(data);
}
