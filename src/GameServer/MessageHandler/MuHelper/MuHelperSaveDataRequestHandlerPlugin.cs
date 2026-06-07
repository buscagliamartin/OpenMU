// <copyright file="MuHelperSaveDataRequestHandlerPlugin.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.MuHelper;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.PlayerActions.MuHelper;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Handles Mu Helper save-data requests without generated packet changes.
/// </summary>
[PlugIn("Mu Helper save data request handler", "Stores the raw 257-byte Mu Helper settings blob.")]
[Guid("493B12F2-5115-4587-B0CF-B1E4F9B77249")]
public class MuHelperSaveDataRequestHandlerPlugin : IPacketHandlerPlugIn
{
    private readonly UpdateMuHelperConfigurationAction _action = new();

    /// <inheritdoc />
    public byte Key => 0xAE;

    /// <inheritdoc />
    public bool IsEncryptionExpected => false;

    /// <inheritdoc />
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (packet.Length < 4 + MuHelperSettingsSerializer.BlobLength)
        {
            return;
        }

        var helperData = new byte[MuHelperSettingsSerializer.BlobLength];
        packet.Span.Slice(4, helperData.Length).CopyTo(helperData);
        await this._action.SaveDataAsync(player, helperData).ConfigureAwait(false);
    }
}
