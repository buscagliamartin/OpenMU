// <copyright file="DuelSeasonService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Entities;

/// <summary>
/// BarnaMu: shared Duel Ladder season-end engine, callable from the GM chat command AND the
/// scheduled monthly auto-end. Snapshots the top 3 of each reset bracket, archives the champions
/// to the Hall of Fame file, then bulk-resets every character's rating / W / L / bracket to
/// defaults. The VIP season reward is deferred in this baseline (VIP runtime is a later branch),
/// so champions are recorded/archived but not granted VIP here.
/// </summary>
public static class DuelSeasonService
{
    private const byte BracketCount = 5;
    private const int RewardTopN = 3;
    private const string SeasonFileName = "current_season.txt";
    private const string SeasonNumberFileName = "season_number.txt";
    private const string HallOfFameFileName = "hall_of_fame.dat";

    /// <summary>A season champion captured before the reset wipes their values.</summary>
    public sealed record SeasonChampion(byte Bracket, int Rank, string Name, byte ClassNumber, int Rating, int Wins, int Losses);

    /// <summary>
    /// Ends the current Duel Ladder season: archive the top 3 per bracket to the Hall of Fame, then
    /// reset everyone. Never throws past its own logging; returns human readable report lines for a
    /// chat reply. The VIP reward is deferred in this baseline.
    /// </summary>
    /// <param name="gameContext">The game context (server-wide).</param>
    /// <param name="logger">A logger.</param>
    public static async ValueTask<IReadOnlyList<string>> EndSeasonAsync(IGameContext gameContext, ILogger logger)
    {
        var report = new List<string>();
        var now = DateTime.UtcNow;
        var logDir = GetLogDir();
        var season = ReadSeasonNumber(logDir);

        // Phase A: capture champions + reset, all inside ONE typed Character context that is fully
        // saved and DISPOSED before Phase B.
        var champions = new List<SeasonChampion>();
        int resetCount;
        {
            using var context = gameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(Character), useCache: false, gameContext.Configuration);
            var all = (await context.GetAsync<Character>().ConfigureAwait(false)).ToList();
            resetCount = all.Count;

            for (byte bracket = 1; bracket <= BracketCount; bracket++)
            {
                var top = all
                    .Where(c => c.DuelResetBracket == bracket && (c.DuelWins + c.DuelLosses) > 0)
                    .OrderByDescending(c => c.DuelRating)
                    .ThenBy(c => c.Name)
                    .Take(RewardTopN)
                    .ToList();
                for (int i = 0; i < top.Count; i++)
                {
                    var c = top[i];
                    champions.Add(new SeasonChampion(
                        bracket,
                        i + 1,
                        c.Name ?? string.Empty,
                        c.CharacterClass is { } cc ? (byte)cc.Number : (byte)0,
                        c.DuelRating,
                        c.DuelWins,
                        c.DuelLosses));
                }
            }

            var online = await gameContext.GetPlayersAsync().ConfigureAwait(false);
            foreach (var player in online)
            {
                if (player.SelectedCharacter is { } character)
                {
                    ResetCharacter(character);
                }
            }

            foreach (var character in all)
            {
                ResetCharacter(character);
            }

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        report.Add($"=== Duel Ladder Season {season} Ended === {now:yyyy-MM-dd HH:mm} UTC ===");

        // Phase B: champions are recorded and granted their VIP season reward — VIP time is written to
        // the existing Account.VipExpirationDate via GrantVipAsync (never-shorten semantics).
        foreach (var champ in champions)
        {
            if (string.IsNullOrEmpty(champ.Name))
            {
                continue;
            }

            var expiration = champ.Rank switch
            {
                1 => now.AddMonths(1), // ~until next season
                2 => now.AddDays(15),
                _ => now.AddDays(7),
            };

            try
            {
                await GrantVipAsync(gameContext, champ.Name, expiration).ConfigureAwait(false);
                report.Add($"T{champ.Bracket} #{champ.Rank} {champ.Name}: VIP reward applied (VIP until at least {expiration:yyyy-MM-dd}).");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Duel Ladder: failed to record champion reward for {Name}.", champ.Name);
            }
        }

        // Phase C: archive the Hall of Fame and advance the season files.
        AppendHallOfFame(logDir, season, champions, logger);
        WriteCurrentSeasonStart(now, logDir, logger);
        WriteSeasonNumber(logDir, season + 1, logger);

        logger.LogInformation(
            "Duel Ladder season {Season} ended at {Time}; {Count} characters reset; {Champs} champions archived and granted VIP reward.",
            season, now, resetCount, champions.Count);
        report.Add($"{resetCount} characters reset to {DuelLadderService.BaseRating}; {champions.Count} champions archived and granted VIP reward.");
        return report;
    }

    /// <summary>A persisted Hall of Fame record (a past-season bracket champion).</summary>
    public sealed record HallOfFameRecord(int Season, byte Bracket, byte Rank, string Name, byte ClassNumber, int Rating, int Wins, int Losses);

    /// <summary>
    /// Reads the most recent Hall of Fame champion records from the archive file (newest first).
    /// </summary>
    /// <param name="bracket">The reset bracket to filter by (1-5), or 0 for all brackets.</param>
    /// <param name="maxEntries">The maximum number of records to return.</param>
    public static IReadOnlyList<HallOfFameRecord> ReadHallOfFame(byte bracket, int maxEntries)
    {
        var all = new List<HallOfFameRecord>();
        try
        {
            var file = Path.Combine(GetLogDir(), HallOfFameFileName);
            if (!File.Exists(file))
            {
                return all;
            }

            foreach (var line in File.ReadAllLines(file))
            {
                var parts = line.Split('|');
                if (parts.Length < 8 || !int.TryParse(parts[0], out var season))
                {
                    continue;
                }

                byte.TryParse(parts[1], out var brk);
                if (bracket != 0 && brk != bracket)
                {
                    continue;
                }

                byte.TryParse(parts[2], out var rank);
                byte.TryParse(parts[4], out var cls);
                int.TryParse(parts[5], out var rating);
                int.TryParse(parts[6], out var wins);
                int.TryParse(parts[7], out var losses);
                all.Add(new HallOfFameRecord(season, brk, rank, parts[3], cls, rating, wins, losses));
            }
        }
        catch
        {
            // return whatever parsed so far
        }

        // Newest season first; within a season-bracket, ranks ascending (1, 2, 3).
        return all
            .OrderByDescending(r => r.Season)
            .ThenBy(r => r.Bracket)
            .ThenBy(r => r.Rank)
            .Take(maxEntries)
            .ToList();
    }

    private static async ValueTask GrantVipAsync(IGameContext gameContext, string characterName, DateTime expiration)
    {
        // BarnaMu VIP reward: grant the champion VIP time by writing ONLY the existing
        // Account.VipExpirationDate. VIP is computed (VipAccountExtensions.IsVipActive); there is no
        // AccountState.Vip enum and no other account/player field is touched. Never-shorten semantics:
        // if the account already has a later VIP expiration, keep it; otherwise set the computed
        // bracket expiration -> account.VipExpirationDate = existing > expiration ? existing : expiration.
        // Online: update the in-memory account so VIP applies immediately, then persist progress.
        var onlinePlayer = gameContext.GetPlayerByCharacterName(characterName);
        if (onlinePlayer?.Account is { } onlineAccount
            && onlinePlayer.SelectedCharacter?.Name is { } selectedName
            && selectedName.Equals(characterName, StringComparison.OrdinalIgnoreCase))
        {
            onlineAccount.VipExpirationDate = onlineAccount.VipExpirationDate is { } onlineExisting && onlineExisting > expiration
                ? onlineExisting
                : expiration;
            await onlinePlayer.SaveProgressAsync().ConfigureAwait(false);
            return;
        }

        // Offline: update the account directly in a new player context.
        using var context = gameContext.PersistenceContextProvider.CreateNewPlayerContext(gameContext.Configuration);
        var account = await context.GetAccountByCharacterNameAsync(characterName).ConfigureAwait(false);
        if (account is null)
        {
            return;
        }

        account.VipExpirationDate = account.VipExpirationDate is { } existing && existing > expiration
            ? existing
            : expiration;
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static void ResetCharacter(Character character)
    {
        character.DuelRating = DuelLadderService.BaseRating;
        character.DuelWins = 0;
        character.DuelLosses = 0;
        character.DuelResetBracket = 0;
    }

    private static string GetLogDir() => Environment.GetEnvironmentVariable("BARNAMU_DUEL_LADDER_DIR") ?? @"C:\MuDev\Logs\DuelLadder";

    private static int ReadSeasonNumber(string dir)
    {
        try
        {
            var file = Path.Combine(dir, SeasonNumberFileName);
            if (File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out var n) && n > 0)
            {
                return n;
            }
        }
        catch
        {
            // fall through to default
        }

        return 1;
    }

    private static void WriteSeasonNumber(string dir, int next, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, SeasonNumberFileName), next.ToString());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Duel Ladder: failed to write {File}.", SeasonNumberFileName);
        }
    }

    private static void AppendHallOfFame(string dir, int season, List<SeasonChampion> champions, ILogger logger)
    {
        if (champions.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            foreach (var c in champions)
            {
                // season|bracket|rank|name|class|rating|wins|losses
                var safeName = (c.Name ?? string.Empty).Replace('|', ' ');
                sb.Append(season).Append('|').Append(c.Bracket).Append('|').Append(c.Rank).Append('|')
                    .Append(safeName).Append('|').Append(c.ClassNumber).Append('|')
                    .Append(c.Rating).Append('|').Append(c.Wins).Append('|').Append(c.Losses).Append('\n');
            }

            File.AppendAllText(Path.Combine(dir, HallOfFameFileName), sb.ToString());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Duel Ladder: failed to append Hall of Fame records.");
        }
    }

    private static void WriteCurrentSeasonStart(DateTime time, string dir, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, SeasonFileName), time.ToString("O"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Duel Ladder: failed to update {File}.", SeasonFileName);
        }
    }
}
