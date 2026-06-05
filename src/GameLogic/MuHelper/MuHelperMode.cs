// <copyright file="MuHelperMode.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.MuHelper;

/// <summary>
/// Mu Helper behavior modes stored in the client settings blob at offset 29.
/// </summary>
public enum MuHelperMode : byte
{
    /// <summary>
    /// Uses configured attack skills.
    /// </summary>
    Attack = 0,

    /// <summary>
    /// Runs buff/heal behavior without attack behavior.
    /// </summary>
    Buff = 1,

    /// <summary>
    /// Requests basic attack mode. Runtime basic attack is deferred in Phase 1.
    /// </summary>
    BasicAttack = 2,
}
