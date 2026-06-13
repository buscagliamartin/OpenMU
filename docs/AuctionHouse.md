# Auction House

## Mailbox Claims

Auction House item transfers keep the real persisted item object through every
step. Creating a listing moves the live inventory item into an auction escrow
storage; it does not clone or rebuild the item. Cancelling a listing removes
that same escrow item from escrow storage and puts it into a returned-item
mailbox storage. Buying a listing removes the same escrow item from escrow
storage and puts it into the buyer's `ItemDelivery` mailbox storage.

The buy flow persists the buyer delivery mailbox and the seller payout mailbox
before it attempts to clean up the now-empty escrow storage. If the cleanup save
fails, the buyer delivery and seller payout remain durable and a warning is
logged. This mirrors the cancel flow, where the returned mailbox item is saved
before the empty escrow storage is deleted.

Claiming a returned item or bought item moves the real mailbox item into the
claiming character's inventory. The mailbox entry and mailbox storage are
deleted only after the player save succeeds. If the mailbox item or its full
item graph cannot be resolved, the claim is blocked and the mailbox entry is
left intact for investigation instead of being deleted.

Seller payouts are stored as mailbox entries without item storage. Claiming a
seller payout credits the configured currency and deletes the payout entry only
after the save succeeds.

## Item Graph Loading

Individual mailbox claim and claim-all use the persistence-layer
`IItemGraphLoader` batch path before falling back to the full item graph
repository load. The batch loader reads real account-data item rows, including
persisted `ItemOptionLink` rows and ancient item-set joins, and resolves the
required immutable configuration definitions for serialization.

Do not replace this with config-only hydration. Config data can resolve item
definitions and possible option definitions, but it cannot reconstruct which
per-item option links actually belong to a persisted item. Earlier config
hydration produced incomplete item bytes/options for excellent items and wings.
The safe full-graph fallback remains available for cases where the batch loader
cannot prove the graph is complete.

Claim and claim-all keep concise Information-level timing summaries for graph
loads, attach/move, player save, live inventory refresh, and auction-context
cleanup. Temporary per-item inventory byte dumps from the mailbox-claim
investigation were removed from normal runtime logging; packet byte dumps are
available only through Debug-level inventory logs. Mailbox slow-claim warnings
start at 3000 ms.

## Known Behavior

Already-existing broken empty `ItemDelivery` mailbox entries created before the
buy-flow fix can remain blocked because they have a mailbox storage but no
recoverable item. New buys should not create those empty delivery entries.
