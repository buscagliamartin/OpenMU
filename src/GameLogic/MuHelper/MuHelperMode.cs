// <copyright file="MuHelperMode.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.MuHelper;

/// <summary>
/// Server-side Mu Helper behavior mode.
/// </summary>
public enum MuHelperMode
{
    /// <summary>
    /// Uses configured attack skills and optional buffs.
    /// </summary>
    Attack = 0,

    /// <summary>
    /// Only heals and buffs self or party members.
    /// </summary>
    Buff = 1,

    /// <summary>
    /// Ignores configured skills and uses only normal attacks.
    /// </summary>
    BasicAttack = 2,
}
