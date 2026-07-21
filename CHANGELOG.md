# Changelog

All notable changes to `Vivarium.Stage` are documented here.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) ·
versioning: 0.x — minor for surface changes, patch for fixes. Stage versions
independently of the changeset spec: it consumes the contract, it does not
define it.

## 0.4.0 — unreleased

### Changed — breaking for consumers who deconstruct `RecoveryOutcome`

`RecoveryOutcome` is now a record with **init-only properties** instead of
positional members. Property access is unaffected; positional deconstruction
no longer compiles:

```csharp
// before
var (target, token, fingerprint, resolution, reason) = outcome;
// after — read the properties (they are stable across future additions)
var target = outcome.Target;
```

This is the second release in a row whose additive field broke deconstruction
(0.3.0 was the first). The type is expected to keep growing as recovery reports
more of what the ledger already knows, so it changes shape **once** here to stop
breaking on every future addition.

### Added

- **`RecoveryOutcome.PendingOperation`** — `apply` | `rollback`: which operation
  the reconciled pending entry started. Present on **every** outcome, including
  the `unresolved` ones.

  Recovery already decomposed the ledger's entry kind into two axes and reported
  only one of them (`apply-completed` → `Resolution: "completed"`), so consumers
  who needed the operation — to say "the rollback aborted, so the apply is still
  in effect" rather than "something aborted" — had to snapshot the ledger and
  join on the target. That join is not atomic with recovery's own read: a
  concurrent writer between the two makes it miss, which crashed a consumer's
  startup recovery in practice. The outcome now carries the axis:

  ```csharp
  // the appended entry kind, without re-reading the ledger
  var kind = $"{outcome.PendingOperation}-{outcome.Resolution}"; // e.g. rollback-aborted
  ```

- fault-model **v0.4**: the §3 truth table is now specified in terms of the
  outcome's axes — row = `PendingOperation`, cell = `Resolution`, column =
  `Reason` — so a consumer reads the table straight off the verdict.

## 0.3.0 — 2026-07-21

### Changed — breaking for consumers who deconstruct `RecoveryOutcome`

`RecoveryOutcome` gained positional members (`ChangesetFingerprint`, `Reason`),
which changes its deconstruction arity. Property access is unaffected.

### Fixed

- **One unaccountable target no longer aborts recovery for every other target.**
  `StageRecovery.RecoverAsync` called `ActiveStateAsync` without a guard, so an
  adapter that could not account for a target propagated out of the sweep —
  discarding the verdicts of targets already reconciled. Reading the active
  pointer is now a judgement input: an unreadable target yields `unresolved`
  (`Reason` = `active-state-unreadable`), appends nothing, and the sweep
  continues. Two things still propagate rather than becoming verdicts: caller
  cancellation (not a judgement about any target) and a failed ledger append
  (the audit trail itself is broken, so no verdict is trustworthy).
- **`ActiveChangesetFingerprint` follows lineage after a rollback** — a
  rolled-back changeset must never be reported as the live one; the projection
  now names the apply that produced the state actually active.
- **The ledger validates entry kinds at the write door**, not only on re-import.
  The ledger is append-only, so a typo admitted at write time is permanent:
  replay would ignore the entry (leaving a pending that never resolves) and the
  export would stop round-tripping.

### Added

- `RecoveryOutcome.ChangesetFingerprint` — the ledger already knew it; consumers
  had to re-read a projection and join on the apply token.
- `RecoveryOutcome.Reason`, mapped 1:1 to the fault-model §3 truth table, so an
  operator can tell "the target moved out-of-band" (`active-matches-neither`)
  from "the adapter cannot account for the target" (`active-state-unreadable`) —
  they call for different interventions.
- adapter-api: adapters **MUST throw** for a target they do not know, never
  invent a pointer — an invented `ActiveState` would reach the drift gate and
  reconciliation.
- fault-model v0.3: truth table gains an "Active unreadable" column; recovery is
  specified as per-target and total.

## 0.2.0 — 2026-07-21

### Fixed

- **Recovery no longer guesses.** Reconciliation was a binary "did the flip
  land?", which mislabelled an aborted rollback as `apply-aborted` and forged a
  verdict when the active state matched neither the started entry's new nor
  previous ref. It is now total over the fault-model §3 truth table: the two
  unmatched cases report `unresolved` and **append nothing**, leaving the pending
  entry visible for an operator. Appending a guess to an append-only audit trail
  is unrecoverable.
- `rollback-aborted` entered the ledger vocabulary — an aborted rollback means
  the apply is still in effect, and the audit trail must say so.

### Added

- **`ChangeSession.RehydrateAppliedAsync`** — reconstruct an `Applied` session
  after a process restart, verified rather than asserted: it refuses unless the
  target has no unreconciled pending entry, the latest completed entry is an
  `apply-completed` of exactly this changeset, and the live active state ref
  matches that entry's new ref. Rollback requires an `Applied` session, so
  without this a restarted host had no constitutional way back.

## 0.1.0 — 2026-07-19

- **verified-diff@0 apply path** (changeset spec 0.2.0): patches are applied
  from a verified diff rather than a whole-document replacement.
- Consumes `Vivarium.Changeset` as a `PackageReference` — the sibling source
  dependency is gone, so the package is standalone-consumable.

## 0.0.1 — 2026-07-19

Initial NuGet release: the changeset lifecycle core —

- the `proposed → branched → simulated → applied` state machine with
  `discarded` / `rolled back` exits, one session per changeset per target;
- the gates that refuse loudly and specifically: fingerprint + approval, drift
  (every state-kind base entry must match the live target exactly), degraded
  adapter (a non-atomic flip needs explicit host consent), prepare-incomplete;
- the append-only, write-ahead **release ledger** (started/completed pairs) and
  its deterministic replay — every target's state is derivable from the ledger
  alone, and the export round-trips;
- the **backend adapter boundary** (`IBackendAdapter`: five operations plus a
  capability manifest declaring what the backend can honestly promise) with a
  reference in-memory implementation. Real-backend adapters are consumer-owned
  (umbrella ADR-0014).
