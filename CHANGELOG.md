# Changelog

## Unreleased

### Fixed

* Fixed Auction House mailbox item delivery and claim performance.
  * Listing escrow keeps the real item instead of rebuilding it.
  * Cancel moves the same escrow item into returned-item mailbox storage.
  * Buy moves the same escrow item into the buyer `ItemDelivery` mailbox storage.
  * Buyer delivery and seller payout mailboxes are persisted before empty escrow storage cleanup.
  * Individual claim and claim-all use the safe real-DB batch item graph loader, with the full graph repository path retained as fallback.
  * Mailbox entries are deleted only after successful player save; unresolved mailbox items remain blocked and intact.
  * Temporary packet byte diagnostics were removed from normal runtime logging or limited to Debug.

### Known Behavior

* Empty `ItemDelivery` entries created by older builds can remain blocked because no item can be recovered from the mailbox storage. New buys should not create these entries.
