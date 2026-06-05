// <copyright file="MuHelperStatusChangeRequestHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.MuHelper;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.PlayerActions.MuHelper;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Handles Mu Helper status requests behind the existing 0xBF/0x51 dispatch.
/// </summary>
[PlugIn("Mu Helper status request handler", "Tracks Mu Helper start/stop status for the online client.")]
[Guid("91B5040E-44B6-41FC-A0AB-A881770B2A16")]
[BelongsToGroup(MuHelperGroupHandler.GroupKey)]
public class MuHelperStatusChangeRequestHandlerPlugIn : ISubPacketHandlerPlugIn
{
    private readonly ChangeMuHelperStateAction _action = new();

    /// <inheritdoc />
    public byte Key => 0x51;

    /// <inheritdoc />
    public bool IsEncryptionExpected => false;

    /// <inheritdoc />
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (packet.Length < 5)
        {
            return;
        }

        var status = packet.Span[4] == 0 ? MuHelperStatus.Enabled : MuHelperStatus.Disabled;
        await this._action.ChangeHelperStateAsync(player, status).ConfigureAwait(false);
    }
}
