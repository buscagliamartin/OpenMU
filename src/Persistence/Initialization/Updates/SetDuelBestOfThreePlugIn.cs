// <copyright file="SetDuelBestOfThreePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// BarnaMu: changes the duel win condition to "best of 3" by lowering the duel
/// <see cref="DuelConfiguration.MaximumScore"/> from the default 10 to 2 (first player
/// to 2 kills wins). Applied to existing databases so the change does not depend on a
/// fresh re-initialization.
/// </summary>
[PlugIn]
[Display(Name = PlugInName, Description = PlugInDescription)]
[Guid("7F3A1C2E-5B4D-4E6F-9A18-2C3D4E5F6071")]
public class SetDuelBestOfThreePlugIn : UpdatePlugInBase
{
    /// <summary>
    /// The plug in name.
    /// </summary>
    internal const string PlugInName = "Set duel to best-of-3";

    /// <summary>
    /// The plug in description.
    /// </summary>
    internal const string PlugInDescription = "Sets the duel MaximumScore to 2 (best-of-3: first to 2 kills wins).";

    /// <inheritdoc />
    public override UpdateVersion Version => UpdateVersion.SetDuelBestOfThree;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInName;

    /// <inheritdoc />
    public override string Description => PlugInDescription;

    /// <inheritdoc />
    public override bool IsMandatory => true;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 05, 30, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        if (gameConfiguration.DuelConfiguration is { } duelConfig)
        {
            duelConfig.MaximumScore = 2;
        }

        return ValueTask.CompletedTask;
    }
}
