// <copyright file="DuelWaitingListService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

using System.Collections.Concurrent;

/// <summary>
/// BarnaMu: in-memory registry of players who listed themselves as "waiting to fight" on the
/// Duel Ladder. Deliberately isolated from the ranking/profile persistence queries — it holds
/// only live <see cref="Player"/> references plus the moment each one listed. Dead/logged-out
/// entries are pruned on read, so no explicit logout hook is required.
/// </summary>
public static class DuelWaitingListService
{
    private static readonly ConcurrentDictionary<Player, DateTimeOffset> WaitingPlayers = new();

    /// <summary>Lists the player as waiting (idempotent; refreshes the timestamp if already listed).</summary>
    public static void Add(Player player) => WaitingPlayers[player] = DateTimeOffset.UtcNow;

    /// <summary>Removes the player from the waiting list.</summary>
    public static void Remove(Player player) => WaitingPlayers.TryRemove(player, out _);

    /// <summary>Gets a value indicating whether the player is currently listed as waiting.</summary>
    public static bool IsWaiting(Player player) => WaitingPlayers.ContainsKey(player);

    /// <summary>
    /// Returns the live waiting players in the same game context, ordered by rating descending then
    /// name, each paired with the whole seconds they have been waiting. Prunes entries whose player
    /// is no longer in the world (logged out / disconnected).
    /// </summary>
    /// <param name="context">The requesting player's game context (only same-server players are returned).</param>
    public static IReadOnlyList<(Player Player, int WaitSeconds)> GetWaiting(IGameContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<(Player Player, int WaitSeconds)>();
        foreach (var kvp in WaitingPlayers)
        {
            var player = kvp.Key;
            var isLive = player.SelectedCharacter is not null
                && player.GameContext == context
                && player.PlayerState.CurrentState == PlayerState.EnteredWorld;
            if (!isLive)
            {
                WaitingPlayers.TryRemove(player, out _);
                continue;
            }

            result.Add((player, (int)(now - kvp.Value).TotalSeconds));
        }

        return result
            .OrderByDescending(t => t.Player.SelectedCharacter!.DuelRating)
            .ThenBy(t => t.Player.SelectedCharacter!.Name)
            .ToList();
    }
}
