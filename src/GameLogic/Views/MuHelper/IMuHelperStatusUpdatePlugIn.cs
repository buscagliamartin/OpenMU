// <copyright file="IMuHelperStatusUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Views.MuHelper;

/// <summary>
/// View plugin which sends Mu Helper status changes to the client.
/// </summary>
public interface IMuHelperStatusUpdatePlugIn : IViewPlugIn
{
    /// <summary>
    /// Sends the start acknowledgement.
    /// </summary>
    ValueTask StartAsync();

    /// <summary>
    /// Sends the stop acknowledgement.
    /// </summary>
    ValueTask StopAsync();

    /// <summary>
    /// Sends a cost-consumption update.
    /// </summary>
    /// <param name="money">The consumed zen.</param>
    ValueTask ConsumeMoneyAsync(uint money);
}
