// <copyright file="IItemGraphLoader.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence;

using System.Threading;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;

/// <summary>
/// Loads persisted item graphs for performance-sensitive item transfers.
/// </summary>
public interface IItemGraphLoader
{
    /// <summary>
    /// Loads persisted account-data item rows, their per-item option links, and item-set links by id.
    /// Implementations may resolve immutable configuration references through <paramref name="gameConfiguration"/>,
    /// but must not invent per-item links which are not persisted for the item.
    /// </summary>
    /// <param name="itemIds">The item identifiers.</param>
    /// <param name="gameConfiguration">The active game configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The loaded items, keyed by item id.</returns>
    ValueTask<IReadOnlyDictionary<Guid, Item>> LoadItemGraphsByIdsAsync(
        IEnumerable<Guid> itemIds,
        GameConfiguration gameConfiguration,
        CancellationToken cancellationToken = default);
}
