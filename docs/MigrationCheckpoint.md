# Migration Checkpoint

This checkpoint records the stable Auction House mailbox baseline before
continuing migration work. It is documentation-only and should be used to
resume without mixing Auction House fixes, packet migration work, or client UI
work-in-progress.

Date: 2026-06-13

## Repositories

### Server: OpenMU

Canonical request path: `C:\MuDev_clean\OpenMU`

Current Codex workspace path: `C:\MuDev\_clean\OpenMU`

Branch: `barna/aligned-upstream-baseline`

Latest stable Auction House commit:

```text
ad22ebd24 Fix auction house mailbox item delivery and claim performance
```

Recorded remote checkpoint:

```text
buscagliamartin/OpenMU
barna/aligned-upstream-baseline
```

Working tree at checkpoint: clean

Server stash to review later, not to pop automatically:

```text
stash@{0}: WIP leftover packet docs and item move changes before migration resume
```

That stash belongs to later packet or migration work and must not be mixed with
Auction House changes. It includes leftover packet docs, item-move related
changes, and generated persistence-file changes.

### Client: MuMain

Canonical request path: `C:\MuDev_clean\MuMain`

Current Codex workspace path: `C:\MuDev\_clean\MuMain`

Branch: `codex/custom-client-feature-migration-bundle`

Latest stable Auction House client commit:

```text
ffaad60e Fix auction house mailbox client item handling
```

Recorded remote checkpoint:

```text
buscagliamartin/MuMain
codex/custom-client-feature-migration-bundle
```

Working tree at checkpoint: clean

Client stashes:

```text
stash@{0}: AuctionHouseWindow UI WIP
stash@{1}: local ignore files before client feature bundle
```

Do not pop `stash@{0}` during migration unless intentionally returning to the
Auction House window redesign. It contains `AuctionHouseWindow.cpp` and
`AuctionHouseWindow.h`.

## Auction House Stable State

Same-account listing flow is QA-passed:

* Create listing.
* Cancel listing.
* Claim returned item.
* Claim all returned items.
* Item stats and options are preserved.
* Items can be moved and equipped without relog.

Cross-account buy flow is QA-passed:

* Account A lists an item.
* Account B buys the item.
* Account B claims the `ItemDelivery`.
* The item leaves mailbox storage.
* The item appears in Account B's inventory with correct stats and options.
* The item can be moved and equipped without relog.
* Account A can claim seller payout.

Performance is QA-passed:

* Individual mailbox claim no longer takes about 11 seconds.
* Claim All no longer takes about 12 seconds.
* The safe real-DB batch item graph loader is used.
* `FullGraphFallbacks=0` in verified flows.

## Implementation Rules To Preserve

* Auction escrow stores the same real item, not a clone.
* Cancel listing moves the same real escrow item to returned mailbox storage.
* Buy moves the same real escrow item to buyer `ItemDelivery` mailbox storage.
* Seller payout mailbox is persisted alongside buyer delivery.
* Buyer delivery and seller payout are persisted before empty escrow cleanup.
* Empty escrow cleanup happens only after delivery and payout persistence succeeds.
* If escrow cleanup fails, buyer delivery and seller payout remain intact.
* Mailbox entries are deleted only after successful player save.
* If a mailbox item cannot be resolved, claim is blocked and the entry is left intact.
* Full graph fallback remains available for safety.

Do not reintroduce config-only item hydration. It produced incomplete item bytes
and options because config data cannot reconstruct persisted per-item
`ItemOptionLink` rows. The safe batch item graph loader must load real DB item
rows, `ItemOptionLink` rows, ancient joins, and then resolve required immutable
configuration definitions.

Known behavior:

* Old broken empty `ItemDelivery` mailbox entries created before the final
  buy-flow fix can remain blocked.
* New buys should not create empty `ItemDelivery` storages.

## Client Stable State

Committed client fixes:

* `NewUIInventoryActionController.cpp`: preserves the picked item owner so a
  failed move or equip can restore correctly. This prevents item disappearance
  after mailbox claim move/equip failures.
* `MailboxWindow.cpp`: Claim All sends one sentinel request instead of N
  individual claim requests.
* `WSclient.cpp`: clears stale picked-item and equipment-move cursor state
  before authoritative F3-10 inventory rebuilds.

Auction House window UI redesign remains WIP in the client stash and is not
part of the stable mailbox baseline.

## Resume Plan

1. Do not modify Auction House again unless a new reproducible bug appears.
2. Start from clean server and client working trees.
3. Rebuild and smoke test only if needed:

   ```powershell
   & 'C:\MuDev\Bat\Clean\Recompilar Clean.bat'
   & 'C:\MuDev\Bat\Clean\StartServer Clean.bat'
   & 'C:\MuDev\Bat\Clean\Deploy Cliente Clean.bat'
   ```

4. Review the server packet/item-move stash deliberately.
5. Decide whether the server stash should be applied, selectively restored
   file-by-file, or discarded.
6. Do not mix `AuctionHouseWindow` UI WIP with migration work.
7. Continue the main migration from this clean Auction House stable baseline.
