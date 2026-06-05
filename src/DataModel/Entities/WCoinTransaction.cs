// <copyright file="WCoinTransaction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.DataModel.Entities;

/// <summary>
/// A ledger entry for W Coin balance changes.
/// </summary>
/// <remarks>
/// Foundation model only. Cash shop purchase, gift, storage, consume, and real-money behavior are not enabled by this entity.
/// </remarks>
[AggregateRoot]
public class WCoinTransaction
{
    /// <summary>
    /// Gets or sets the account whose balance changed.
    /// </summary>
    [Required]
    public virtual Account? Account { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the balance change.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the signed balance delta.
    /// </summary>
    public long Amount { get; set; }

    /// <summary>
    /// Gets or sets the balance after the transaction.
    /// </summary>
    public long BalanceAfter { get; set; }

    /// <summary>
    /// Gets or sets the reason code.
    /// </summary>
    [Required]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source system.
    /// </summary>
    [Required]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the actor that caused the balance change.
    /// </summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional note.
    /// </summary>
    public string Note { get; set; } = string.Empty;
}
