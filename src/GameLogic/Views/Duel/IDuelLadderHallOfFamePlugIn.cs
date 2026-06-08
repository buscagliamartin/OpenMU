// <copyright file="IDuelLadderHallOfFamePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Views.Duel;

/// <summary>
/// BarnaMu: view plugin which sends the Duel Ladder Hall of Fame (past-season bracket champions)
/// to the client as a 0xBF / sub-code 0x32, op=4 packet.
/// </summary>
public interface IDuelLadderHallOfFamePlugIn : IViewPlugIn
{
    /// <summary>
    /// Sends the recent Hall of Fame records to the client (newest first).
    /// </summary>
    /// <param name="entries">The archived champion records.</param>
    /// <returns>The value task.</returns>
    ValueTask ShowHallOfFameAsync(IReadOnlyList<DuelSeasonService.HallOfFameRecord> entries);
}
