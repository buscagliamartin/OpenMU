// <copyright file="MuHelperSettingsSerializer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.MuHelper;

using System.Text;

/// <summary>
/// Parses the 257-byte <c>PRECEIVE_MUHELPER_DATA</c> blob without changing packet XML or generated files.
/// </summary>
public static class MuHelperSettingsSerializer
{
    /// <summary>
    /// The exact blob size used by the Season 6 Mu Helper config packet.
    /// </summary>
    public const int BlobLength = 257;

    /// <summary>
    /// The mode byte offset in the raw blob.
    /// </summary>
    public const int ModeOffset = 29;

    private const int MinimumReadableLength = 65;
    private const int PickupFlagsOffset = 1;
    private const int RangeFlagsOffset = 2;
    private const int BasicSkillOffset = 5;
    private const int ActivationSkill1Offset = 7;
    private const int DelayMinSkill1Offset = 9;
    private const int ActivationSkill2Offset = 11;
    private const int DelayMinSkill2Offset = 13;
    private const int BuffSkill0Offset = 17;
    private const int BuffSkill1Offset = 19;
    private const int BuffSkill2Offset = 21;
    private const int HpThresholdFlagsOffset = 23;
    private const int BehaviorFlagsOffset = 25;
    private const int Skill2FlagsOffset = 27;
    private const int ExtraItemsOffset = 65;
    private const int ExtraItemSlotCount = 12;
    private const int ExtraItemSlotLength = 15;

    private const int PickJewelFlag = 1 << 3;
    private const int PickZenFlag = 1 << 6;
    private const int PickExtraItemFlag = 1 << 7;
    private const int HuntingRangeMask = 0x0F;
    private const int ObtainRangeShift = 4;
    private const int ObtainRangeMask = 0x0F;
    private const int HpPotionNibbleMask = 0x0F;
    private const int HpHealNibbleShift = 4;
    private const int HpHealNibbleMask = 0x0F;
    private const int HpThresholdMultiplier = 10;
    private const int ComboFlag = 1 << 5;
    private const int PartyFlag = 1 << 6;
    private const int PickAllItemsFlag = 1 << 6;
    private const int PickSelectItemsFlag = 1 << 7;

    /// <summary>
    /// Deserializes settings from the raw blob.
    /// </summary>
    /// <param name="blob">The raw 257-byte Mu Helper configuration blob.</param>
    /// <returns>The parsed settings, or <see langword="null"/> when the blob is too short.</returns>
    public static IMuHelperSettings? TryDeserialize(byte[]? blob)
    {
        if (blob is null || blob.Length < MinimumReadableLength)
        {
            return null;
        }

        return TryDeserialize(blob.AsSpan());
    }

    /// <summary>
    /// Deserializes settings from the raw blob.
    /// </summary>
    /// <param name="blob">The raw 257-byte Mu Helper configuration blob.</param>
    /// <returns>The parsed settings, or <see langword="null"/> when the blob is too short.</returns>
    public static IMuHelperSettings? TryDeserialize(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < MinimumReadableLength)
        {
            return null;
        }

        var pickupFlags = blob[PickupFlagsOffset];
        var rangeFlags = blob[RangeFlagsOffset];
        var hpThresholdFlags = blob[HpThresholdFlagsOffset];
        var behaviorFlags = blob[BehaviorFlagsOffset];
        var skill2Flags = blob[Skill2FlagsOffset];

        return new MuHelperSettings
        {
            Mode = ParseMode(blob[ModeOffset]),
            BasicSkillId = ReadWord(blob, BasicSkillOffset),
            ActivationSkill1Id = ReadWord(blob, ActivationSkill1Offset),
            ActivationSkill2Id = ReadWord(blob, ActivationSkill2Offset),
            DelayMinSkill1 = ReadWord(blob, DelayMinSkill1Offset),
            DelayMinSkill2 = ReadWord(blob, DelayMinSkill2Offset),
            UseCombo = (behaviorFlags & ComboFlag) != 0,
            HuntingRange = rangeFlags & HuntingRangeMask,
            ObtainRange = (rangeFlags >> ObtainRangeShift) & ObtainRangeMask,
            BuffSkill0Id = ReadWord(blob, BuffSkill0Offset),
            BuffSkill1Id = ReadWord(blob, BuffSkill1Offset),
            BuffSkill2Id = ReadWord(blob, BuffSkill2Offset),
            PotionThresholdPercent = (hpThresholdFlags & HpPotionNibbleMask) * HpThresholdMultiplier,
            HealThresholdPercent = ((hpThresholdFlags >> HpHealNibbleShift) & HpHealNibbleMask) * HpThresholdMultiplier,
            SupportParty = (behaviorFlags & PartyFlag) != 0,
            PickAllItems = (skill2Flags & PickAllItemsFlag) != 0,
            PickSelectItems = (skill2Flags & PickSelectItemsFlag) != 0,
            PickJewel = (pickupFlags & PickJewelFlag) != 0,
            PickZen = (pickupFlags & PickZenFlag) != 0,
            ExtraItemNames = (pickupFlags & PickExtraItemFlag) != 0
                ? ReadExtraItemNames(blob)
                : Array.Empty<string>(),
        };
    }

    private static int ReadWord(ReadOnlySpan<byte> blob, int offset)
        => blob[offset] | (blob[offset + 1] << 8);

    private static MuHelperMode ParseMode(byte value)
        => value <= (byte)MuHelperMode.BasicAttack ? (MuHelperMode)value : MuHelperMode.Attack;

    private static IReadOnlyList<string> ReadExtraItemNames(ReadOnlySpan<byte> blob)
    {
        var names = new List<string>();
        var maximumReadableSlots = Math.Min(ExtraItemSlotCount, Math.Max(0, (blob.Length - ExtraItemsOffset) / ExtraItemSlotLength));

        for (var slot = 0; slot < maximumReadableSlots; slot++)
        {
            var itemName = blob.Slice(ExtraItemsOffset + (slot * ExtraItemSlotLength), ExtraItemSlotLength);
            var length = itemName.IndexOf((byte)0);
            length = length < 0 ? ExtraItemSlotLength : length;
            if (length > 0)
            {
                names.Add(Encoding.ASCII.GetString(itemName[..length]));
            }
        }

        return names;
    }
}
