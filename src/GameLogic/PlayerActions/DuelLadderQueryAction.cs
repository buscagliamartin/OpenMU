// <copyright file="DuelLadderQueryAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlayerActions.Duel;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Duel;
using MUnique.OpenMU.Interfaces;

// Disambiguate: 'Character' here is the entity, not the PlayerActions.Character sub-namespace.
using CharacterEntity = MUnique.OpenMU.DataModel.Entities.Character;

/// <summary>
/// BarnaMu: a single Duel Ladder leaderboard entry passed from the action layer
/// to the view layer.
/// </summary>
public sealed class DuelLadderEntry
{
    /// <summary>Gets the character name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the MU class number (from <see cref="DataModel.Configuration.CharacterClass.Number"/>).</summary>
    public byte ClassNumber { get; init; }

    /// <summary>Gets the current ELO rating.</summary>
    public int Rating { get; init; }

    /// <summary>Gets the current-season wins.</summary>
    public int Wins { get; init; }

    /// <summary>Gets the current-season losses.</summary>
    public int Losses { get; init; }

    /// <summary>Gets the character's guild name (empty if none / not resolved).</summary>
    public string Guild { get; init; } = string.Empty;
}

/// <summary>
/// BarnaMu: serves the in-game Duel Ladder window's two queries:
/// top-10 per reset bracket, and the player's own profile (with rank-in-bracket).
/// Reads via a typed Character context so configuration is not eagerly tracked.
/// </summary>
public class DuelLadderQueryAction
{
    private const int TopN = 10;
    private const byte MinBracket = 1;
    private const byte MaxBracket = 5;

    /// <summary>
    /// Loads the top-10 characters in the requested reset bracket (ordered by rating)
    /// and sends them to the client via <see cref="IDuelLadderTopPlugIn"/>.
    /// </summary>
    /// <param name="player">The requesting player.</param>
    /// <param name="bracket">The reset bracket id (1-5).</param>
    public async ValueTask QueryTopAsync(Player player, byte bracket)
    {
        if (bracket < MinBracket || bracket > MaxBracket)
        {
            return;
        }

        try
        {
            using var context = player.GameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CharacterEntity), useCache: false, player.GameContext.Configuration);
            var characters = await context.GetAsync<CharacterEntity>().ConfigureAwait(false);

            var topCharacters = characters
                .Where(c => c.DuelResetBracket == bracket && (c.DuelWins + c.DuelLosses) > 0)
                .OrderByDescending(c => c.DuelRating)
                .ThenBy(c => c.Name)
                .Take(TopN)
                .ToList();

            var guildNames = await ResolveGuildNamesAsync(player, topCharacters).ConfigureAwait(false);

            var entries = topCharacters
                .Select(c => new DuelLadderEntry
                {
                    Name = c.Name ?? string.Empty,
                    ClassNumber = c.CharacterClass is { } cc ? (byte)cc.Number : (byte)0,
                    Rating = c.DuelRating,
                    Wins = c.DuelWins,
                    Losses = c.DuelLosses,
                    Guild = guildNames.TryGetValue(c.Id, out var g) ? g : string.Empty,
                })
                .ToList();

            await player.InvokeViewPlugInAsync<IDuelLadderTopPlugIn>(p => p.ShowTopAsync(bracket, entries)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            player.Logger?.LogError(ex, "Duel Ladder: failed to query top-{N} for bracket {Bracket}.", TopN, bracket);
        }
    }

    /// <summary>
    /// Resolves the guild name for each of the given characters (by character id) via the guild
    /// context. Defensive: any failure returns whatever was resolved so far so the rankings still
    /// render without guilds. Guilds use a Guid key (<see cref="GuildMember.GuildId"/> == guild id),
    /// so no in-memory guild-server id mapping is needed here.
    /// </summary>
    private static async ValueTask<Dictionary<Guid, string>> ResolveGuildNamesAsync(Player player, IReadOnlyList<CharacterEntity> characters)
    {
        var result = new Dictionary<Guid, string>();
        if (characters.Count == 0)
        {
            return result;
        }

        try
        {
            var ids = characters.Select(c => c.Id).ToHashSet();
            using var guildContext = player.GameContext.PersistenceContextProvider.CreateNewGuildContext();

            var members = (await guildContext.GetAsync<GuildMember>().ConfigureAwait(false))
                .Where(m => ids.Contains(m.Id))
                .ToList();
            if (members.Count == 0)
            {
                return result;
            }

            var guildNamesById = (await guildContext.GetAsync<MUnique.OpenMU.DataModel.Entities.Guild>().ConfigureAwait(false))
                .ToDictionary(gg => gg.Id, gg => gg.Name ?? string.Empty);

            foreach (var member in members)
            {
                if (guildNamesById.TryGetValue(member.GuildId, out var name) && !string.IsNullOrEmpty(name))
                {
                    result[member.Id] = name;
                }
            }
        }
        catch (Exception ex)
        {
            player.Logger?.LogError(ex, "Duel Ladder: failed to resolve guild names; rankings shown without guilds.");
        }

        return result;
    }

    /// <summary>
    /// Loads the player's own Duel Ladder profile (bracket, skill tier, rating, W/L,
    /// rank-in-bracket) and sends it to the client via <see cref="IDuelLadderProfilePlugIn"/>.
    /// </summary>
    /// <param name="player">The requesting player.</param>
    public async ValueTask QueryProfileAsync(Player player)
    {
        if (player.SelectedCharacter is not { } selectedCharacter)
        {
            return;
        }

        try
        {
            var resets = player.Attributes is null ? 0 : (int)player.Attributes[Stats.Resets];
            var bracket = DuelLadderService.GetResetBracket(resets);
            var rating = selectedCharacter.DuelRating > 0 ? selectedCharacter.DuelRating : DuelLadderService.BaseRating;
            var skillTier = (byte)DuelLadderService.GetSkillTier(rating);

            using var context = player.GameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CharacterEntity), useCache: false, player.GameContext.Configuration);
            var characters = await context.GetAsync<CharacterEntity>().ConfigureAwait(false);

            // Count characters in the same bracket with a strictly higher rating
            // among those who actually participated this season.
            var ahead = characters.Count(c =>
                c.DuelResetBracket == bracket
                && (c.DuelWins + c.DuelLosses) > 0
                && c.DuelRating > rating);
            var rankInBracket = (ushort)Math.Min(ushort.MaxValue, ahead + 1);

            await player.InvokeViewPlugInAsync<IDuelLadderProfilePlugIn>(
                p => p.ShowProfileAsync(bracket, skillTier, rating, selectedCharacter.DuelWins, selectedCharacter.DuelLosses, rankInBracket))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            player.Logger?.LogError(ex, "Duel Ladder: failed to query own profile.");
        }
    }

    /// <summary>
    /// Sends the player's recent ranked-duel match history (from the in-memory
    /// <see cref="DuelMatchHistoryService"/>) to the client via <see cref="IDuelLadderHistoryPlugIn"/>.
    /// </summary>
    /// <param name="player">The requesting player.</param>
    public async ValueTask QueryHistoryAsync(Player player)
    {
        if (player.SelectedCharacter is not { } character)
        {
            return;
        }

        try
        {
            var entries = DuelMatchHistoryService.GetHistory(character.Name);
            await player.InvokeViewPlugInAsync<IDuelLadderHistoryPlugIn>(p => p.ShowHistoryAsync(entries)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            player.Logger?.LogError(ex, "Duel Ladder: failed to query match history.");
        }
    }

    /// <summary>
    /// Sends the recent Duel Ladder Hall of Fame (past-season champions) to the client.
    /// </summary>
    /// <param name="player">The requesting player.</param>
    public async ValueTask QueryHallOfFameAsync(Player player, byte bracket)
    {
        try
        {
            var entries = DuelSeasonService.ReadHallOfFame(bracket, 9);
            await player.InvokeViewPlugInAsync<IDuelLadderHallOfFamePlugIn>(p => p.ShowHallOfFameAsync(entries)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            player.Logger?.LogError(ex, "Duel Ladder: failed to query hall of fame.");
        }
    }

    /// <summary>
    /// Challenges the player at <paramref name="index"/> of the top list for <paramref name="bracket"/>
    /// (the same ordering the client was shown). Looks the character up among the online players and,
    /// if found, routes through the standard duel-request flow (which validates same-map/eligibility and
    /// requires the target to accept). Shows a message if the player isn't online.
    /// </summary>
    /// <param name="player">The challenger.</param>
    /// <param name="bracket">The reset bracket the client is viewing (1-5).</param>
    /// <param name="index">The row index within that bracket's top list.</param>
    public async ValueTask ChallengeRankAsync(Player player, byte bracket, byte index)
    {
        if (bracket < MinBracket || bracket > MaxBracket)
        {
            return;
        }

        try
        {
            using var context = player.GameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CharacterEntity), useCache: false, player.GameContext.Configuration);
            var characters = await context.GetAsync<CharacterEntity>().ConfigureAwait(false);

            var top = characters
                .Where(c => c.DuelResetBracket == bracket && (c.DuelWins + c.DuelLosses) > 0)
                .OrderByDescending(c => c.DuelRating)
                .ThenBy(c => c.Name)
                .Take(TopN)
                .Select(c => c.Name ?? string.Empty)
                .ToList();

            if (index >= top.Count || string.IsNullOrEmpty(top[index]))
            {
                return;
            }

            var targetName = top[index];
            var target = player.GameContext.GetPlayerByCharacterName(targetName);
            if (target is null || target == player)
            {
                await player.InvokeViewPlugInAsync<IShowMessagePlugIn>(
                    p => p.ShowMessageAsync($"{targetName} is not online for a duel.", MessageType.BlueNormal)).ConfigureAwait(false);
                return;
            }

            // Standard duel-request flow: validates same-map / eligibility and requires acceptance.
            await new DuelActions().HandleDuelRequestAsync(player, target).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            player.Logger?.LogError(ex, "Duel Ladder: rank challenge failed.");
        }
    }
}
