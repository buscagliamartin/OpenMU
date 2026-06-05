// <copyright file="Account.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.DataModel.Entities;

using MUnique.OpenMU.DataModel.Configuration;

/// <summary>
/// The state of an account.
/// </summary>
public enum AccountState
{
    /// <summary>
    /// Normal player account.
    /// </summary>
    Normal,

    /// <summary>
    /// Spectator account, invisible to players and monsters.
    /// </summary>
    Spectator,

    /// <summary>
    /// Game Master account.
    /// </summary>
    GameMaster,

    /// <summary>
    /// Game Master account, invisible to players and monsters.
    /// </summary>
    GameMasterInvisible,

    /// <summary>
    /// Banned account.
    /// </summary>
    Banned,

    /// <summary>
    /// Temporarily banned account.
    /// </summary>
    TemporarilyBanned,
}

/// <summary>
/// The account of a player.
/// </summary>
[AggregateRoot]
public class Account
{
    /// <summary>
    /// Gets or sets the unique login name.
    /// </summary>
    [Required]
    public string LoginName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the hash of the password, preferrably of BCrypt.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the security code which is used to confirm character deletion and guild kicks.
    /// </summary>
    public string SecurityCode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the e mail address.
    /// </summary>
    public string EMail { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the unlocked character classes which are locked by default.
    /// </summary>
    /// <remarks>
    /// Some classes are only available when the player reached a certain level before, or when he paid for some unlock ticket.
    /// </remarks>
    [HiddenAtCreation]
    public virtual ICollection<CharacterClass> UnlockedCharacterClasses { get; protected set; } = null!;

    /// <summary>
    /// Gets or sets the registration date.
    /// </summary>
    public DateTime RegistrationDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the state.
    /// </summary>
    public AccountState State { get; set; }

    /// <summary>
    /// Gets or sets the date and time until which VIP state is valid.
    /// </summary>
    /// <remarks>
    /// Foundation field only. VIP account state and benefit behavior are migrated separately.
    /// </remarks>
    public DateTime? VipExpirationDate { get; set; }

    /// <summary>
    /// Gets or sets the W Coin balance of the account.
    /// </summary>
    /// <remarks>
    /// Foundation field only. Cash shop purchase, gift, storage, consume, and real-money behavior are not enabled by this model.
    /// </remarks>
    public long WCoin { get; set; }

    /// <summary>
    /// Gets or sets the timezone of the player, difference to UTC.
    /// </summary>
    public short TimeZone { get; set; }

    /// <summary>
    /// Gets or sets the vault password.
    /// </summary>
    [HiddenAtCreation]
    public string VaultPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the vault.
    /// </summary>
    [MemberOfAggregate]
    [HiddenAtCreation]
    public virtual ItemStorage? Vault { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this instance is vault extended.
    /// </summary>
    public bool IsVaultExtended { get; set; }

    /// <summary>
    /// Gets or sets the Jewel of Bless balance held by the account jewel bank.
    /// </summary>
    public int JewelBankBless { get; set; }

    /// <summary>
    /// Gets or sets the Jewel of Soul balance held by the account jewel bank.
    /// </summary>
    public int JewelBankSoul { get; set; }

    /// <summary>
    /// Gets or sets the Jewel of Life balance held by the account jewel bank.
    /// </summary>
    public int JewelBankLife { get; set; }

    /// <summary>
    /// Gets or sets the Jewel of Creation balance held by the account jewel bank.
    /// </summary>
    public int JewelBankCreation { get; set; }

    /// <summary>
    /// Gets or sets the Jewel of Guardian balance held by the account jewel bank.
    /// </summary>
    public int JewelBankGuardian { get; set; }

    /// <summary>
    /// Gets or sets the Gemstone balance held by the account jewel bank.
    /// </summary>
    public int JewelBankGemstone { get; set; }

    /// <summary>
    /// Gets or sets the Jewel of Harmony balance held by the account jewel bank.
    /// </summary>
    public int JewelBankHarmony { get; set; }

    /// <summary>
    /// Gets or sets the Jewel of Chaos balance held by the account jewel bank.
    /// </summary>
    public int JewelBankChaos { get; set; }

    /// <summary>
    /// Gets or sets the Lower Refine Stone balance held by the account jewel bank.
    /// </summary>
    public int JewelBankLowerRefineStone { get; set; }

    /// <summary>
    /// Gets or sets the Higher Refine Stone balance held by the account jewel bank.
    /// </summary>
    public int JewelBankHigherRefineStone { get; set; }

    /// <summary>
    /// Gets or sets the Box of Kundun +1 balance held by the account item bank.
    /// </summary>
    public int JewelBankKundun1 { get; set; }

    /// <summary>
    /// Gets or sets the Box of Kundun +2 balance held by the account item bank.
    /// </summary>
    public int JewelBankKundun2 { get; set; }

    /// <summary>
    /// Gets or sets the Box of Kundun +3 balance held by the account item bank.
    /// </summary>
    public int JewelBankKundun3 { get; set; }

    /// <summary>
    /// Gets or sets the Box of Kundun +4 balance held by the account item bank.
    /// </summary>
    public int JewelBankKundun4 { get; set; }

    /// <summary>
    /// Gets or sets the Box of Kundun +5 balance held by the account item bank.
    /// </summary>
    public int JewelBankKundun5 { get; set; }

    /// <summary>
    /// Gets or sets the Blue Chocolate Box balance held by the account item bank.
    /// </summary>
    public int JewelBankChocoBlue { get; set; }

    /// <summary>
    /// Gets or sets the Pink Chocolate Box balance held by the account item bank.
    /// </summary>
    public int JewelBankChocoPink { get; set; }

    /// <summary>
    /// Gets or sets the characters.
    /// </summary>
    [MemberOfAggregate]
    [HiddenAtCreation]
    public virtual ICollection<Character> Characters { get; protected set; } = null!;

    /// <inheritdoc />
    public override string ToString()
    {
        return this.LoginName;
    }
}
