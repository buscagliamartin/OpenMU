// <copyright file="MuHelperSettings.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.MuHelper;

/// <summary>
/// Deserialized Mu Helper settings from the 257-byte client blob.
/// </summary>
public sealed class MuHelperSettings : IMuHelperSettings
{
    /// <inheritdoc />
    public MuHelperMode Mode { get; init; } = MuHelperMode.Attack;

    /// <inheritdoc />
    public int BasicSkillId { get; init; }

    /// <inheritdoc />
    public int ActivationSkill1Id { get; init; }

    /// <inheritdoc />
    public int ActivationSkill2Id { get; init; }

    /// <inheritdoc />
    public int DelayMinSkill1 { get; init; }

    /// <inheritdoc />
    public int DelayMinSkill2 { get; init; }

    /// <inheritdoc />
    public bool UseCombo { get; init; }

    /// <inheritdoc />
    public int HuntingRange { get; init; }

    /// <inheritdoc />
    public int ObtainRange { get; init; }

    /// <inheritdoc />
    public int BuffSkill0Id { get; init; }

    /// <inheritdoc />
    public int BuffSkill1Id { get; init; }

    /// <inheritdoc />
    public int BuffSkill2Id { get; init; }

    /// <inheritdoc />
    public int PotionThresholdPercent { get; init; }

    /// <inheritdoc />
    public int HealThresholdPercent { get; init; }

    /// <inheritdoc />
    public bool SupportParty { get; init; }

    /// <inheritdoc />
    public bool PickAllItems { get; init; }

    /// <inheritdoc />
    public bool PickSelectItems { get; init; }

    /// <inheritdoc />
    public bool PickJewel { get; init; }

    /// <inheritdoc />
    public bool PickZen { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ExtraItemNames { get; init; } = Array.Empty<string>();
}
