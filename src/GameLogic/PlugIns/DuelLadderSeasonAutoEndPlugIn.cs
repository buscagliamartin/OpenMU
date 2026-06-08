// <copyright file="DuelLadderSeasonAutoEndPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns;

using System.Runtime.InteropServices;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// BarnaMu: automatically ends the Duel Ladder season at 23:59 Madrid time on the last day of
/// each month, by invoking the shared <see cref="DuelSeasonService"/> (archive the champions to
/// the Hall of Fame, then reset everyone; the VIP reward is deferred in this baseline).
/// Modeled on a self-throttled periodic task. A double fire after a reset is harmless — once
/// everyone is reset to default with 0 W/L there are no eligible champions, so the engine no-ops.
/// </summary>
[PlugIn]
[Display(Name = "BarnaMu Duel Ladder Auto Season End", Description = "Ends the Duel Ladder season automatically at 23:59 Madrid time on the last day of each month (Hall of Fame + reset; VIP reward deferred).")]
[Guid("F1A2B3C4-D5E6-4F70-8190-A2B3C4D5E6F7")]
public class DuelLadderSeasonAutoEndPlugIn : IPeriodicTaskPlugIn
{
    private const int EndHour = 23;
    private const int EndMinute = 59;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeZoneInfo MadridTimeZone = ResolveMadridTimeZone();

    private DateTime _nextRunUtc = DateTime.UtcNow;
    private int _lastEndedYear;
    private int _lastEndedMonth;

    /// <inheritdoc />
    public async ValueTask ExecuteTaskAsync(GameContext gameContext)
    {
        if (DateTime.UtcNow < this._nextRunUtc)
        {
            return;
        }

        this._nextRunUtc = DateTime.UtcNow + CheckInterval;

        DateTime madridNow;
        try
        {
            madridNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, MadridTimeZone);
        }
        catch
        {
            madridNow = DateTime.UtcNow;
        }

        // Fire during the final minute (23:59) of the last calendar day of the month, Madrid time.
        var lastDayOfMonth = DateTime.DaysInMonth(madridNow.Year, madridNow.Month);
        var inWindow = madridNow.Day == lastDayOfMonth
            && madridNow.Hour == EndHour
            && madridNow.Minute >= EndMinute;
        if (!inWindow)
        {
            return;
        }

        // Once per month (in-memory). The 30s check guarantees the 23:59 minute is evaluated.
        if (this._lastEndedYear == madridNow.Year && this._lastEndedMonth == madridNow.Month)
        {
            return;
        }

        this._lastEndedYear = madridNow.Year;
        this._lastEndedMonth = madridNow.Month;

        var logger = gameContext.LoggerFactory.CreateLogger(this.GetType().Name);
        using var scope = logger.BeginScope(gameContext);

        try
        {
            logger.LogInformation("Duel Ladder: automatic monthly season end firing at {Madrid} (Madrid time).", madridNow);
            await DuelSeasonService.EndSeasonAsync(gameContext, logger).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Duel Ladder: automatic season end failed.");
        }
    }

    /// <inheritdoc />
    public void ForceStart()
    {
        this._nextRunUtc = DateTime.UtcNow;
    }

    private static TimeZoneInfo ResolveMadridTimeZone()
    {
        // IANA id works on Linux and modern Windows; the Windows id is the fallback.
        foreach (var id in new[] { "Europe/Madrid", "Romance Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // try the next id
            }
        }

        return TimeZoneInfo.Utc;
    }
}
