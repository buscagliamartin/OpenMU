// <copyright file="IMuHelperSettings.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.MuHelper;

/// <summary>
/// Read-only Mu Helper settings parsed from the 257-byte client blob.
/// </summary>
public interface IMuHelperSettings
{
    /// <summary>
    /// Gets the server-side helper mode stored at blob offset 29.
    /// </summary>
    MuHelperMode Mode { get; }

    /// <summary>
    /// Gets the primary attack skill id.
    /// </summary>
    int BasicSkillId { get; }

    /// <summary>
    /// Gets the first conditional attack skill id.
    /// </summary>
    int ActivationSkill1Id { get; }

    /// <summary>
    /// Gets the second conditional attack skill id.
    /// </summary>
    int ActivationSkill2Id { get; }

    /// <summary>
    /// Gets the first conditional skill interval in seconds.
    /// </summary>
    int DelayMinSkill1 { get; }

    /// <summary>
    /// Gets the second conditional skill interval in seconds.
    /// </summary>
    int DelayMinSkill2 { get; }

    /// <summary>
    /// Gets a value indicating whether combo mode is configured.
    /// </summary>
    bool UseCombo { get; }

    /// <summary>
    /// Gets the hunting range nibble. Phase 1 parses but does not use it for server-side walking.
    /// </summary>
    int HuntingRange { get; }

    /// <summary>
    /// Gets the obtain range nibble. Phase 1 parses but does not use it for server-side walking.
    /// </summary>
    int ObtainRange { get; }

    /// <summary>
    /// Gets the first buff skill id.
    /// </summary>
    int BuffSkill0Id { get; }

    /// <summary>
    /// Gets the second buff skill id.
    /// </summary>
    int BuffSkill1Id { get; }

    /// <summary>
    /// Gets the third buff skill id.
    /// </summary>
    int BuffSkill2Id { get; }

    /// <summary>
    /// Gets the potion use threshold in percent.
    /// </summary>
    int PotionThresholdPercent { get; }

    /// <summary>
    /// Gets the self-heal threshold in percent.
    /// </summary>
    int HealThresholdPercent { get; }

    /// <summary>
    /// Gets a value indicating whether party support is configured.
    /// </summary>
    bool SupportParty { get; }

    /// <summary>
    /// Gets a value indicating whether all nearby items should be picked.
    /// </summary>
    bool PickAllItems { get; }

    /// <summary>
    /// Gets a value indicating whether selected item filters should be used.
    /// </summary>
    bool PickSelectItems { get; }

    /// <summary>
    /// Gets a value indicating whether jewels should be picked.
    /// </summary>
    bool PickJewel { get; }

    /// <summary>
    /// Gets a value indicating whether zen should be picked.
    /// </summary>
    bool PickZen { get; }

    /// <summary>
    /// Gets the extra item name filters.
    /// </summary>
    IReadOnlyList<string> ExtraItemNames { get; }
}
