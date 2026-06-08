// <copyright file="IDuelLadderHistoryPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Views.Duel;

/// <summary>
/// BarnaMu: view plugin which sends the player's Duel Ladder match history to the client
/// as a 0xBF / sub-code 0x32, op=3 packet.
/// </summary>
public interface IDuelLadderHistoryPlugIn : IViewPlugIn
{
    /// <summary>
    /// Sends the requesting player's recent ranked-duel results to the client (newest first).
    /// </summary>
    /// <param name="entries">The recent match-history entries.</param>
    /// <returns>The value task.</returns>
    ValueTask ShowHistoryAsync(IReadOnlyList<DuelMatchHistoryEntry> entries);
}
