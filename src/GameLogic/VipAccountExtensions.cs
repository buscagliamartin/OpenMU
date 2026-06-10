// <copyright file="VipAccountExtensions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

using MUnique.OpenMU.DataModel.Entities;

/// <summary>
/// BarnaMu: read-only helpers for the VIP timer/status bridge.
/// VIP is represented purely by <see cref="Account.VipExpirationDate"/> (already part of the
/// account economy foundation). There is intentionally no persisted account-state flag and no
/// gameplay perk wired in this branch — VIP here is only a timer/status bridge that grants no
/// in-game benefits yet.
/// </summary>
public static class VipAccountExtensions
{
    /// <summary>
    /// Determines whether the account currently has an active VIP period, computed live from
    /// <see cref="Account.VipExpirationDate"/> against the current UTC time. No database read or
    /// write is performed. An elapsed (or unset) expiration date simply reads as not-VIP, so no
    /// revert job is required when VIP time runs out.
    /// </summary>
    /// <param name="account">The account to check. May be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> if the account has a <see cref="Account.VipExpirationDate"/> that is
    /// in the future; otherwise <see langword="false"/>.
    /// </returns>
    public static bool IsVipActive(this Account? account)
    {
        return account?.VipExpirationDate is { } expiration && expiration > DateTime.UtcNow;
    }
}
