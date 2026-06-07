// <copyright file="UpdateMuHelperConfigurationAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.MuHelper;

using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.Views.MuHelper;

/// <summary>
/// Updates the selected character's Mu Helper configuration blob.
/// </summary>
public class UpdateMuHelperConfigurationAction
{
    /// <summary>
    /// Saves the raw Mu Helper configuration data.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="data">The raw 257-byte configuration blob.</param>
    public async ValueTask SaveDataAsync(Player player, Memory<byte> data)
    {
        if (player.SelectedCharacter is not { } character)
        {
            return;
        }

        var normalized = new byte[MuHelperSettingsSerializer.BlobLength];
        data.Span[..Math.Min(data.Length, normalized.Length)].CopyTo(normalized);

        character.MuHelperConfiguration = normalized;
        player.MuHelperSettings = MuHelperSettingsSerializer.TryDeserialize(normalized);

        await player.InvokeViewPlugInAsync<IMuHelperConfigurationUpdatePlugIn>(p => p.UpdateMuHelperConfigurationAsync(normalized)).ConfigureAwait(false);
    }
}
