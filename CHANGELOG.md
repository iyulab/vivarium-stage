# Changelog

All notable changes to `Vivarium.Stage` are documented here.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) ·
versioning: 0.x — minor for surface changes, patch for fixes. Stage versions
independently of the changeset spec: it consumes the contract, it does not
define it.

## 0.10.0 — 2026-09-24

### Added
- The package now carries XML documentation for every public member, so IDEs show
  the contract (what is refused, when, and why) at the call site.
- Conformance kit: `§3/prepare-applies-spec-order` checks that prepare applies one
  document in the changeset spec's §5.4 order (additive schema operations, then data,
  then removing ones). Set `ConformanceFixture.OrderProbeEntity` to an entity of the
  fixture target that holds at least one row; the kit adds, writes and removes a probe
  field in one document and expects the schema and data fingerprints not to move.
  Without it the check reports itself skipped.

### Changed
- Depends on `Vivarium.Changeset` 0.6.0 (spec 0.5.0): an approval record carrying
  `attestation` is now refused by the validator Stage applies.
- README: no longer lists a live-propagation hook. Stage returns the outcome of
  every apply and records it in the ledger; notifying connected clients is the
  host's job. The adapter section now says the signatures are in the adapter
  document rather than still to come.
  It also says what Stage adds over platform-level database branching and restore: an
  approved fingerprint is the only thing that applies, a drifted base is refused, and
  the same changeset carries the UI.

## 0.9.0 — 2026-09-23

### Added
- **Adapter refusals have a type.** `AdapterRefusedException` with an `AdapterRefusalReason` —
  `DocumentRefused`, `UnknownTarget`, `UnknownRef`, `ApplyTokenConflict`, `StateIsActive` — for every
  case the adapter contract requires an adapter to refuse (`docs/adapter-api.md` v0.3, §6). Any other
  exception out of an adapter is a fault, so a host tells "refused" from "broken" by type rather than by
  which call it came out of. `Details` follows `StageRefusedException.Details`; a document refusal lists
  `{ path, message }` under `$.patches`, the shape Stage uses for a changeset that fails validation.
- Conformance kit: `§3/flip-lands-prepared-state` — observes what an adapter did rather than what it
  reported: after the flip, each UI artifact the document wrote must fingerprint to the content it wrote,
  untouched artifacts must not move, and `schema` moves exactly when schema operations were carried. Data,
  and a schema keyed below facet granularity, are reported as not verified. An adapter that reported
  completion and staged nothing used to pass.
- Conformance kit: `§3/refusal-leaves-no-residue` — a document refused part-way must leave nothing it
  asked for on the branch. The other refusal probes all refuse at the first operation.

### Changed
- **Breaking for adapter authors and hosts.** The conformance kit now asserts type, reason, message and —
  for document refusals — a location. An adapter that refuses with another exception type fails those
  checks. Hosts that caught `InvalidOperationException` to recognise a refusal from the reference adapter
  must catch `AdapterRefusedException`. Stage does not wrap adapter refusals; they surface from
  `ChangeSession.ApplyAsync` as thrown.
- Depends on `Vivarium.Changeset` 0.5.0. The getting-started guide now builds approval records with
  `ChangesetApproval.Add` instead of writing the JSON by hand, which takes the fingerprint from the
  document and refuses one that changed after it was finalized.

### Fixed
- The reference adapter staged a document in place, so an operation naming something absent — found only
  while applying — left the earlier operations of the refused document on the branch, and the corrected
  retry could fail. It now stages on a copy and swaps it in only when every operation has applied.
- A document refused by the verified-diff layer-2 check is located at the patch member that did not match
  (`$.patches.ui[0].baseFingerprint`), not only at the patch.

## 0.8.0 — 2026-09-23

### Fixed
- `InMemoryBackendAdapter.SeedTarget` now refuses a world that does not carry the shape the
  adapter reads back — `schema.entities`, `data` and `artifacts` — naming every missing or
  mistyped container in one message. A partial world previously seeded without complaint and
  then raised a `NullReferenceException` from `ActiveStateAsync`, which also stopped
  `AdapterConformance.RunAsync` and `ChangeSession.ApplyAsync` from reaching a result.
- The same guard descends into the shapes the adapter addresses inside those containers: an
  entity's `fields` and `constraints`, each `data` container's row array, and each artifact's
  string content. A world that passed the container check but failed inside one of these raised a
  stack trace naming a member the caller never supplied — and a non-array `data` container raised
  nothing at all: its contents were discarded and the facet still reported complete. Every problem
  is collected into the one refusal, which states the full shape.

### Changed
- **`ConformanceFixture` refuses a patch set the adapter could not read** — a key that is not a
  facet (`schema` / `ui` / `data`), a facet that is not an array, or facets that are all empty.
  Such a fixture made `prepare` stage nothing while the run still came back green, a report
  indistinguishable byte-for-byte from a real pass. The refusal names the keys it found.
- **`prepare reports per-facet completion` now checks coverage, not count.** The verdict states
  which facets the document carried and fails an adapter whose report omits one of them or claims
  one the document never carried. Counting entries passed any adapter answering with a constant.
- **`InMemoryBackendAdapter.PrepareAsync` reports the facets the document actually carried**
  instead of a fixed `schema`/`ui`/`data` triple. As the reference implementation of the contract,
  it was modelling the habit the check above now fails.
- `adapter-api.md` states the per-facet rule explicitly; getting-started's conformance example
  uses the changeset's own facet key (`ui`) — the previous key was not one the adapter reads.

- **The reference adapter applies a document in the order changeset spec §5.4 fixes** — additive
  schema operations, then data operations, then removing ones — and a `field.remove` /
  `entity.remove` now takes the stored values with it. It previously applied every schema
  operation first and left removed values in place, so a document that cleared a field and
  removed it left rows carrying a member no schema declared, and a data operation could not
  address a field on its way out. `adapter-api.md` states the order.

### Dependencies
- `Vivarium.Changeset` 0.4.0 (was 0.3.0).

> **Upgrade note for adapter authors**: a fixture whose patch set is empty or misspelled now throws
> `ArgumentException` at construction instead of producing a green run. That is the point of the
> change — the run it used to produce was not evidence of anything. Separately, if your adapter
> applies all schema operations before data operations, spec §5.4 now says it must not.

## 0.7.0 — 2026-09-19

### Changed
- **Accepts changeset spec 0.3.0 documents**, including the `data` base-state kind. The drift gate
  already compared every declared entry generically; what was missing was admission — a document
  declaring the data facet it patches was refused as an unsupported spec version before any gate ran.
  A changeset that declares `data` is now refused with `DriftGate` when the live rows moved under it,
  like any other declared facet. Depends on `Vivarium.Changeset` 0.3.0 (was 0.2.0).

### Docs
- Getting started notes that the drift gate checks only what is declared, and that a changeset
  patching data should declare a `data` entry.

### CI
- **Publish workflow** is rerun-safe and its registry check is conclusive: `dotnet nuget push --skip-duplicate`, and an
  exhausted verification window fails with the reason instead of falling through to a restore error.

## 0.6.0 — 2026-08-11

**Binary breaking (2)**: `ApplyAsync`/`RollbackAsync` return `Task<FlipOutcome>`
instead of `Task`, and `StageRecovery.RecoverAsync` returns `Task<RecoveryReport>`
instead of `Task<IReadOnlyList<RecoveryOutcome>>`. Both are noted again inline
below, where the reasoning lives.

### Added
- **The adapter contract says what identifies a refusal, since the type does not.**
  A host has to tell "this document was refused" from "something broke" — different
  words belong in front of a person, and only one of the two is the author's to fix.
  The contract leaves the exception type to the adapter, so the identifier is the
  call: `prepare` is the door, and a throw from it is a verdict on the document as
  given. That was already implied and nowhere stated, which left every host to
  re-derive it. The clause states it, and adds the half that makes it actionable —
  a refusal must leave the branch as preparable as it found it, or "fix the document
  and try again" lands on a branch the first attempt already spoiled. The conformance
  suite gains `§3/refusal-leaves-branch-preparable` (23rd clause), which re-prepares
  a known-good document after a refusal. An adapter that refuses correctly every
  time but stages before it finishes checking now fails it while every refusal clause
  stays green — the shape the new check exists for.
- **The ledger can say whether its own history was rewritten.** Fixed principle 6 —
  history is never rewritten — was a policy the library followed without being one it
  could detect the violation of: entries carried no binding to the entry before them,
  so a store that edited its own file was invisible to every reader. Each entry now
  carries its own hash and the hash of the one before it, computed with the same JCS
  canonicalization and `sha256:` prefix this family already uses to seal changesets.

  ```csharp
  var integrity = LedgerIntegrity.Verify(ReleaseLedger.ParseExport(export));
  if (integrity.Verdict == "broken")
      foreach (var finding in integrity.Findings)
          Console.WriteLine($"seq {finding.Seq}: {finding.Message}");
  ```

  Verification takes entries rather than a ledger, because an audit is someone holding
  the exported file with no live store in hand. Three verdicts, and the third earns its
  place: `unverifiable` means no entry carries a chain, so nothing was checked —
  collapsing it into `intact` would let "green" mean either *verified* or *nobody
  looked*. `UnverifiedPrefix` counts the entries the check could not speak for.

  **No migration.** History written before the chain parses as before and reports
  itself as an unverified prefix. It is deliberately not re-hashed on import: doing so
  would assert that history was never altered rather than verify it, which is the
  distinction `RehydrateAppliedAsync` has always kept.

  **What it does not catch, stated where the check is**: dropping the newest entries.
  A shorter history is self-consistent, and appending afterwards closes over the gap
  rather than exposing it. Detecting that needs a fixed point held where the store
  cannot reach it, which this version does not have. Every other edit — in place, from
  the middle, from the head, inserted, reordered — becomes visible, and a convincing
  forgery costs the whole ledger from the tampered entry onward rather than one line.

- **An operator can close what recovery would not guess.** `unresolved` means recovery
  appended nothing and the pending entry stands. Getting past that point previously
  meant hand-writing entries into an append-only trail — which admits a completion for
  a target with nothing in flight, under a token no entry carries, naming a state that
  was never staged, permanently. `StageRecovery.ResolveAsync` admits only a resolution
  the pending entry can take and reads the token and state refs from that entry rather
  than from the caller.

  The outcome's reason is `operator-declared`, kept apart from the four an active state
  supports: a resolution asserted by a person and one verified against live state are
  not the same claim, and an audit that cannot tell them apart is worth less than one
  that can. The actor the library writes on entries it reconciled itself is reserved,
  so an assertion cannot be read back later as a verification.

- **`StagePolicy.RequireIntactLedger`** — refuse to recover from a ledger that does not
  verify, instead of reporting the verdict and continuing. Off by default: a damaged
  ledger is when a host may most need to recover, so refusing by default would take the
  recovery path down with the check. Only a `broken` verdict refuses; `unverifiable`
  does not, or the switch would be unusable for deployments that have a past. The
  refusal carries `RefusalReason.LedgerIntegrityGate` and the findings in `Details` —
  its own reason because the response is unlike the others here: they are answered by
  changing the changeset or the target, this one by going to look at the store.

- **Refusals carry the facts, not just the verdict.** `StageRefusedException` gains
  `Details`, a `JsonObject?` snapshot of what the refusing gate observed. `Reason`
  already said *which gate*; the specifics — the mismatched ref, the fingerprint the
  changeset was authored against, the one that is live now — existed only inside
  `Message`, so a host that wanted to offer "re-base this" had to parse an English
  sentence to find them.

  ```csharp
  catch (StageRefusedException refusal) when (refusal.Reason == RefusalReason.DriftGate)
  {
      var which = refusal.Details?["ref"]?.GetValue<string>();
      var expected = refusal.Details?["expected"]?.GetValue<string>();
      var actual = refusal.Details?["actual"]?.GetValue<string>();  // null when the ref is absent
  }
  ```

  Populated where a caller can act on the difference — the drift gate (base-state and
  active-state), the spec-validation refusal (which keeps the validator's per-error
  `path`/`message` instead of flattening them into one string), prepare-incompleteness
  (the facet names), and state-transition refusals (the expected and actual state). It is
  deliberately **`null`** elsewhere: the fingerprint gate refuses a fingerprint the caller
  just submitted, and echoing it back informs nobody. A payload on every refusal would be
  a bigger surface that says less.

  Members are per-gate and additive — read the ones you know, ignore the rest. Adding a
  member is not a breaking change; removing one is. `Message` remains the human sentence
  and the only place a detail is guaranteed to appear.

  Additive: existing `catch` blocks and `Reason` branches are unaffected.

- **`ApplyAsync` and `RollbackAsync` return what they landed** — a `FlipOutcome`
  carrying the operation (`apply` | `rollback`), target, changeset fingerprint, apply
  token, and both ends of the flip. These are the facts the methods already wrote to
  the ledger and then discarded.

  Callers had to reconstruct them, and the usual reconstruction — read the active
  state back after the call — answers a different question. "What did my apply land?"
  and "what is active now?" diverge the moment another flip lands between the two,
  and the caller then reports someone else's state as its own. The outcome is a record
  of the past, so it does not go stale; ask the adapter when you need to know what is
  live. `RehydrateAppliedAsync` still verifies rather than asserts — nothing about
  that discipline changes, because a past fact is not a claim about the present.

  Properties are init-only, not positional: this type will grow as more of what the
  ledger knows becomes useful, and positional records break deconstruction on every
  addition (0.3.0 and 0.4.0 both did).

  **Binary breaking**: `Task` → `Task<FlipOutcome>`. Source-compatible for callers
  that ignore the result, but recompilation is required, and method-group conversions
  or overrides of these signatures need updating.

- **The drift gate reports every drifted ref, not just the first.** It refused at the
  first violation, so an author re-basing a proposal with three stale refs learned
  about them one apply at a time. `Details.drifted` is now an array of
  `{ kind, ref, expected, actual }` — `actual` is `null` where the ref is absent from
  the live target rather than merely different — and the message lists all of them.

### Changed
- **Recovery reports what it made of the ledger it read.** `StageRecovery.RecoverAsync`
  returns a `RecoveryReport` — the integrity verdict plus the per-target outcomes —
  rather than the outcomes alone. The verdict rides on the sweep rather than on each
  outcome because of the quiet case: a ledger can be tampered with and still leave
  nothing in flight, and a per-outcome verdict would vanish exactly where an operator
  most needs it.

  **Binary breaking**: `Task<IReadOnlyList<RecoveryOutcome>>` → `Task<RecoveryReport>`.
  Call sites read `.Outcomes`; the outcome type itself is unchanged.

- **The reference adapter validates the data operations it is handed.** `prepare`
  receives a document authored elsewhere; it now checks every data operation before
  staging anything and refuses one it cannot execute honestly, naming what is wrong
  and where. Three failure modes are gone, and the loudest was not the worst:

  - dereferencing an absent member on a predicate that was not `{ field, equals }`
    produced a null-reference fault — a fault is not a reason;
  - an operation outside the vocabulary fell through the dispatch and did nothing,
    while `prepare` reported the data facet complete — completion reported for work
    that did not happen;
  - a missing predicate was treated as "match every row", so a delete that lost its
    `where` would have staged the removal of everything.

  The check is total and runs before any mutation, so a refused document leaves the
  branch untouched rather than half-staged. The reference adapter is what adapter
  authors copy, so each of these was on its way into every real backend.

- **adapter-api §3 gains a normative clause** — *Data operations: the adapter
  validates its own input* — stating that the upstream validator is not a substitute
  and that the exception type is the adapter's choice while the message is not
  optional.

  The rule covers **all three facets**, not only data. The changeset validator does
  constrain schema and UI patches, but `prepare` is public API — a host can call it
  without ever building a `ChangeSession` — so "the validator checked it" is not a
  property the operation gets to assume. Two silent-wrong-result paths went with it:
  a schema operation outside the vocabulary fell through its dispatch (staging
  nothing, reporting the facet complete), and renaming or retyping a field the entity
  does not have wrote an empty declaration under the new name — a field no later read
  tells apart from a real one. Both now refuse, naming what was expected and where.

- **The conformance suite checks it** (19 → 21 clauses):
  `adapter-api §3/prepare-refuses-malformed-data-operation` feeds a predicate written
  as a key/value map — the shape a producer reaches for first — and
  `…/prepare-refuses-malformed-schema-operation` feeds an operation outside the
  vocabulary. Neither requires a particular exception type, but both fail a
  `NullReferenceException` and an empty message.

- **`ActiveState.FacetFingerprints` key vocabulary is now documented** as drift-gate
  refs rather than facet names: one entry for `schema`, one for `data`, and **one per
  UI artifact** — because UI drift is per-artifact, and editing one screen must not
  refuse a proposal that touches another. The fidelity declaration (§4) is keyed by
  facet name; the two answer different questions and their key sets differ on purpose.
  The same freedom applies to `schema` and `data`: an adapter that can compute a
  deterministic fingerprint below facet granularity may key those facets' entries that
  way too — the reference adapter's current whole-facet granularity is its own choice,
  not a ceiling the contract imposes.

### Fixed
- **Removing something that was never there is refused rather than reported as done.**
  The adapter contract requires refusing a well-formed operation whose target does not
  exist, and the reference adapter applied that to renaming and retyping a field —
  where an invented target is visible in the world afterwards — but not to removing an
  entity, a field, or a constraint. Removal is where a quiet success is most
  convincing, because the target is supposed to end up absent either way. A document
  saying a field would be dropped could name a field nobody had, stage nothing, flip,
  and be recorded in the ledger as applied: a reviewer approved a sentence that never
  became true. Constraint removal was the easiest of the three to miss — a constraint
  is addressed by its whole shape rather than by a name, so "remove the ones that
  match" reads like a filter.

  A data operation whose `where` selects no rows is deliberately unaffected: a
  predicate that matches nothing has done what it said. The distinction the contract
  now states is whether the document **named** a thing it expected to find.

  The conformance suite gains `…/prepare-refuses-absent-schema-target`, so the rule is
  a property of the contract rather than of one implementation — an adapter can no
  longer skip it and pass. The check states the part it cannot reach: its probe
  addresses an entity, the only absent target the suite can name without knowing the
  fixture's schema. **Adapter authors: an implementation that accepted removals of
  absent targets now fails conformance.** No API change; behaviour change on three
  paths that previously succeeded silently.

## 0.5.0 — 2026-08-03

### Added — an executable conformance suite for backend adapters

`Vivarium.Stage.Conformance.AdapterConformance.RunAsync` checks an
`IBackendAdapter` implementation against the normative boundary in
[docs/adapter-api.md](docs/adapter-api.md) and returns a structured
`ConformanceReport`:

```csharp
var report = await AdapterConformance.RunAsync(
    myAdapter,
    new ConformanceFixture(knownTarget: "fixture-app", unknownTarget: "no-such-target", patches));

if (!report.AllPassed) throw new Exception(report.ToString());
```

Until now the adapter contract was normative prose: an adapter author could
read what their implementation must do but had no way to find out whether it
did. The clauses that matter most are also the ones least likely to be
exercised by an adapter's own tests — throwing for an unknown target instead of
inventing a pointer, staying idempotent when recovery re-issues a flip token,
refusing that same token for a different state, and declaring branch fidelity
honestly. Each check is named for the clause it enforces
(`adapter-api §Error-taxonomy/unknown-target-throws`), so a failure says what
to read.

The suite reports rather than throws, takes no test-framework dependency, and
records `Skipped` where the contract genuinely does not constrain a case — an
over-strict suite that fails honest adapters would be worse than none. It
**mutates the fixture target's live state** (it flips, then flips back) and
must never be pointed at production; the restore runs last and is reported as
its own check.

## 0.4.0 — 2026-07-22

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
  reference in-memory implementation. Real-backend adapters are consumer-owned —
  this library knows no specific backend product.
