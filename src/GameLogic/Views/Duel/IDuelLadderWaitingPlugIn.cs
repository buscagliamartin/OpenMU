// <copyright file="IDuelLadderWaitingPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Views.Duel;

using MUnique.OpenMU.GameLogic.PlayerActions;

/// <summary>
/// BarnaMu: view plugin which sends the Duel Ladder "waiting to fight" list to the client
/// as a 0xBF / sub-code 0x32, op=2 packet (selfListed flag + the challengeable players).
/// </summary>
public interface IDuelLadderWaitingPlugIn : IViewPlugIn
{
    /// <summary>
    /// Sends the current waiting-to-fight list to the client.
    /// </summary>
    /// <param name="selfListed">Whether the requesting player is themselves listed as waiting.</param>
    /// <param name="entries">The other players currently waiting (challengeable), ordered by rating.</param>
    /// <returns>The value task.</returns>
    ValueTask ShowWaitingAsync(bool selfListed, IReadOnlyList<DuelWaitingEntry> entries);
}
