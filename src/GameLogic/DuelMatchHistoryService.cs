// <copyright file="DuelMatchHistoryService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

using System.Collections.Concurrent;
using MUnique.OpenMU.DataModel.Entities;

/// <summary>
/// BarnaMu: a single recorded ranked-duel result from one character's point of view.
/// </summary>
public sealed class DuelMatchHistoryEntry
{
    /// <summary>Gets the opponent's character name.</summary>
    public string Opponent { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether this character won.</summary>
    public bool Win { get; init; }

    /// <summary>Gets this character's duel score.</summary>
    public byte MyScore { get; init; }

    /// <summary>Gets the opponent's duel score.</summary>
    public byte OppScore { get; init; }

    /// <summary>Gets the ELO rating change (can be negative; 0 when the duel didn't affect rating).</summary>
    public int RatingChange { get; init; }

    /// <summary>Gets the reset bracket the duel was fought in.</summary>
    public byte Bracket { get; init; }

    /// <summary>Gets when the duel finished.</summary>
    public DateTimeOffset When { get; init; }
}

/// <summary>
/// BarnaMu: in-memory ranked-duel match history, keyed by character name. Deliberately isolated
/// and persistence-free (no schema change): keeps the most recent results per character for the
/// Duel Ladder "Match History" tab. History is per-process and resets on server restart — a
/// later upgrade can persist it if needed.
/// </summary>
public static class DuelMatchHistoryService
{
    private const int MaxPerCharacter = 20;

    private static readonly ConcurrentDictionary<string, List<DuelMatchHistoryEntry>> History =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records a finished ranked duel into both characters' histories (one as a win, one as a loss).
    /// </summary>
    public static void Record(Character? winner, Character? loser, byte winnerScore, byte loserScore, int winnerRatingChange, int loserRatingChange)
    {
        if (winner?.Name is not { Length: > 0 } winnerName || loser?.Name is not { Length: > 0 } loserName)
        {
            return;
        }

        var when = DateTimeOffset.UtcNow;
        Add(winnerName, new DuelMatchHistoryEntry
        {
            Opponent = loserName,
            Win = true,
            MyScore = winnerScore,
            OppScore = loserScore,
            RatingChange = winnerRatingChange,
            Bracket = (byte)winner.DuelResetBracket,
            When = when,
        });
        Add(loserName, new DuelMatchHistoryEntry
        {
            Opponent = winnerName,
            Win = false,
            MyScore = loserScore,
            OppScore = winnerScore,
            RatingChange = loserRatingChange,
            Bracket = (byte)loser.DuelResetBracket,
            When = when,
        });
    }

    /// <summary>Gets the recent match history for a character (newest first).</summary>
    public static IReadOnlyList<DuelMatchHistoryEntry> GetHistory(string? characterName)
    {
        if (characterName is not { Length: > 0 } || !History.TryGetValue(characterName, out var list))
        {
            return Array.Empty<DuelMatchHistoryEntry>();
        }

        lock (list)
        {
            return list.AsEnumerable().Reverse().ToList();
        }
    }

    private static void Add(string characterName, DuelMatchHistoryEntry entry)
    {
        var list = History.GetOrAdd(characterName, _ => new List<DuelMatchHistoryEntry>());
        lock (list)
        {
            list.Add(entry);
            if (list.Count > MaxPerCharacter)
            {
                list.RemoveAt(0);
            }
        }
    }
}
