// <copyright file="ChangeMuHelperStateAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.MuHelper;

using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.Views.MuHelper;

/// <summary>
/// Tracks the requested Mu Helper online status.
/// </summary>
public class ChangeMuHelperStateAction
{
    /// <summary>
    /// Changes the client's online Mu Helper status.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="status">The requested status.</param>
    public async ValueTask ChangeHelperStateAsync(Player player, MuHelperStatus status)
    {
        switch (status)
        {
            case MuHelperStatus.Enabled:
                player.IsMuHelperActive = true;
                await player.InvokeViewPlugInAsync<IMuHelperStatusUpdatePlugIn>(p => p.StartAsync()).ConfigureAwait(false);
                break;
            case MuHelperStatus.Disabled:
                player.IsMuHelperActive = false;
                await player.InvokeViewPlugInAsync<IMuHelperStatusUpdatePlugIn>(p => p.StopAsync()).ConfigureAwait(false);
                break;
        }
    }
}
