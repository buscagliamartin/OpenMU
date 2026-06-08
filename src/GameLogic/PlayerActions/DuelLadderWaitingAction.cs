// <copyright file="DuelLadderWaitingAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic.PlayerActions.Duel;
using MUnique.OpenMU.GameLogic.Views.Duel;

/// <summary>
/// BarnaMu: a single Duel Ladder "waiting to fight" entry passed from the action layer
/// to the view layer.
/// </summary>
public sealed class DuelWaitingEntry
{
    /// <summary>Gets the character name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the MU class number.</summary>
    public byte ClassNumber { get; init; }

    /// <summary>Gets the current ELO rating.</summary>
    public int Rating { get; init; }

    /// <summary>Gets the skill tier (0-5).</summary>
    public byte Tier { get; init; }

    /// <summary>Gets the reset bracket (1-5).</summary>
    public byte Bracket { get; init; }

    /// <summary>Gets the whole seconds the player has been waiting.</summary>
    public int WaitSeconds { get; init; }
}

/// <summary>
/// BarnaMu: serves the Duel Ladder "waiting to fight" feature — list / remove / query and the
/// challenge of a listed player. The challenge reuses the standard <see cref="DuelActions"/>
/// duel-request flow, so the target still receives the normal accept/decline invitation and the
/// duel only starts after acceptance (no auto-start). No persistence is touched; the live list is
/// the in-memory <see cref="DuelWaitingListService"/>.
/// </summary>
public class DuelLadderWaitingAction
{
    private readonly DuelActions _duelActions = new();

    /// <summary>Lists the requesting player as waiting to fight, then echoes the updated list.</summary>
    public async ValueTask ListMeAsync(Player player)
    {
        if (player.SelectedCharacter is null)
        {
            return;
        }

        DuelWaitingListService.Add(player);
        await this.QueryWaitingAsync(player).ConfigureAwait(false);
    }

    /// <summary>Removes the requesting player from the waiting list, then echoes the updated list.</summary>
    public async ValueTask RemoveMeAsync(Player player)
    {
        DuelWaitingListService.Remove(player);
        await this.QueryWaitingAsync(player).ConfigureAwait(false);
    }

    /// <summary>Sends the current waiting list (and the player's own listed flag) to the client.</summary>
    public async ValueTask QueryWaitingAsync(Player player)
    {
        try
        {
            var entries = DuelWaitingListService.GetWaiting(player.GameContext)
                .Where(t => t.Player != player) // a player never challenges themselves
                .Select(t =>
                {
                    var c = t.Player.SelectedCharacter!;
                    var rating = c.DuelRating > 0 ? c.DuelRating : DuelLadderService.BaseRating;
                    return new DuelWaitingEntry
                    {
                        Name = c.Name ?? string.Empty,
                        ClassNumber = c.CharacterClass is { } cc ? (byte)cc.Number : (byte)0,
                        Rating = rating,
                        Tier = (byte)DuelLadderService.GetSkillTier(rating),
                        Bracket = (byte)c.DuelResetBracket,
                        WaitSeconds = t.WaitSeconds,
                    };
                })
                .ToList();

            await player.InvokeViewPlugInAsync<IDuelLadderWaitingPlugIn>(
                p => p.ShowWaitingAsync(DuelWaitingListService.IsWaiting(player), entries)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            player.Logger?.LogError(ex, "Duel Ladder: failed to query the waiting list.");
        }
    }

    /// <summary>
    /// Challenges the waiting player at the given index (same ordering as the list the client was
    /// sent). Routes through the standard duel-request flow, which validates eligibility, sends the
    /// target an accept/decline invitation and only starts the duel on acceptance.
    /// </summary>
    /// <param name="player">The challenger.</param>
    /// <param name="index">Index into the current waiting list (excluding self).</param>
    public async ValueTask ChallengeWaitingAsync(Player player, byte index)
    {
        var waiting = DuelWaitingListService.GetWaiting(player.GameContext)
            .Where(t => t.Player != player)
            .ToList();
        if (index >= waiting.Count)
        {
            return;
        }

        await this._duelActions.HandleDuelRequestAsync(player, waiting[index].Player).ConfigureAwait(false);
    }
}
