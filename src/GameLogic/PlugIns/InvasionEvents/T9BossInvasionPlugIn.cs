// <copyright file="T9BossInvasionPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.InvasionEvents;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// BarnaMu: enables the T9 Boss Invasion feature (Selupan, Erohim, Dark Elf).
/// On a schedule, it spawns the three T9 bosses on their respective maps and shows a
/// golden announcement on screen. The bosses despawn when the event duration ends (if
/// not killed before).
/// Self-contained for the aligned baseline: it extends the clean invasion framework,
/// defines its mob spawns inline via <see cref="CreateDefaultConfig"/>, and uses no
/// map-event UI state (<c>base(null)</c>), so there is no protocol / map-event-state
/// involvement.
/// </summary>
[PlugIn]
[Display(Name = "T9 Boss Invasion", Description = "BarnaMu: spawns the T9 bosses (Selupan, Erohim, Dark Elf) on a schedule with a screen announcement.")]
[Guid("B7E3A1C4-9F22-4D6E-A8B1-3C5D7E9F0A12")]
public sealed class T9BossInvasionPlugIn : BaseInvasionPlugIn<PeriodicInvasionConfiguration>, ISupportDefaultCustomConfiguration
{
    // Monster numbers (verified against the BarnaMu Season 6 config / preserved DB).
    private const ushort SelupanId = 459;
    private const ushort ErohimId = 295;
    private const ushort DarkElfId = 412;

    // Map numbers (VersionSeasonSix maps).
    private const ushort RaklionBossMapId = 58;
    private const ushort LandOfTrialsMapId = 31;
    private const ushort BalgassRefugeMapId = 42;

    /// <summary>
    /// Initializes a new instance of the <see cref="T9BossInvasionPlugIn"/> class.
    /// Passes <c>null</c> for the map-event type, so no map-event state update (minimap
    /// indicator) is broadcast to clients.
    /// </summary>
    public T9BossInvasionPlugIn()
        : base(null)
    {
    }

    /// <inheritdoc />
    public object CreateDefaultConfig() => new PeriodicInvasionConfiguration
    {
        TaskDuration = TimeSpan.FromMinutes(30),
        PreStartMessageDelay = TimeSpan.FromSeconds(3),
        StartMessage = "T9 Boss Invasion! Selupan, Erohim & Dark Elf have appeared!",
        EndMessage = "The T9 Boss Invasion has ended.",
        Timetable = PeriodicTaskConfiguration.GenerateTimeSequence(TimeSpan.FromHours(12), new TimeOnly(3, 15)).ToList(),
        Mobs =
        [
            new(SelupanId, 1, [RaklionBossMapId], SpawnMapStrategy.RandomMap),
            new(ErohimId, 1, [LandOfTrialsMapId], SpawnMapStrategy.RandomMap),
            new(DarkElfId, 1, [BalgassRefugeMapId], SpawnMapStrategy.RandomMap),
        ],
    };
}
