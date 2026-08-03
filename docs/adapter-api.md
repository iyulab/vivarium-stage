# Backend adapter boundary (v0.2)

Normative boundary between Stage and its backend adapters. v0.1 fixed the
*operations and contracts*; v0.2 records the signatures as finalized with the
first real adapter — see §6. Companion to [fault-model.md](fault-model.md).

## 1. Division of labor

- **Stage owns**: the lifecycle state machine, the fingerprint gate, drift
  refusal, the release ledger, apply serialization per target, policy
  around degraded adapters.
- **The adapter owns**: how branches are made, how facet states are staged,
  the atomic flip primitive, and the honest description of all three
  (capability manifest + fidelity declarations).
- **The adapter never sees**: approval semantics, ledger contents, or
  anything about how changesets were authored. It consumes prepared facet
  operations and state references, not the review process.

## 2. Capability manifest

An adapter MUST publish, machine-readably, before first use:

- **Flip capability**: `atomic-swap` (with crash-atomicity and
  idempotency-under-token guarantees, fault-model §4) or a **degradation
  declaration** describing the non-atomic window. Stage refuses applies
  through a degraded adapter without explicit host policy consent.
- **Fidelity modes** it can produce per facet (full, subset, stub), so
  hosts can set branching policy against real capabilities.

## 3. Operations

| Operation | Contract |
| --- | --- |
| `branch(target)` | Create an isolated staging world from the target's current state. Returns a branch reference **plus a fidelity declaration** (§4) — a branch without one cannot enter simulation (design rule from the branching decision). No live effect. |
| `prepare(branch, facetOps)` | Stage the changeset's operations (logical schema ops, data ops, UI artifact payloads) into the branch. Idempotent per changeset fingerprint; MUST report per-facet completion so Stage can confirm *all* facets before any flip. No live effect. |
| `flip(target, stateRef, applyToken)` | Atomically activate `stateRef` (a prepared branch, or a previously active state for rollback). Succeeds completely or has no effect; idempotent under `applyToken` so recovery may re-issue it (fault-model F4/F6). |
| `activeState(target)` | Return deterministic fingerprint(s) of the currently active base state — Stage's input for drift refusal and for post-crash ledger reconciliation (fault-model F5). For a target the adapter does not know, it MUST throw rather than invent a pointer (see §Error taxonomy). |
| `discard(branch)` | Release a staging world and its resources. Always safe (staging never touches live state). |

Simulation is *not* an adapter operation: Stage and the host drive whatever
runs against the branch (e.g. a UI runtime rendering preview artifacts);
the adapter only guarantees the branch behaves as declared.

### Data operations: the adapter validates its own input

`prepare` receives a document authored elsewhere. An adapter **MUST** check the
data operations it is handed against the changeset spec's §5.3 shape — the
operation vocabulary, the members each operation requires, and a predicate of
the form `{ field, equals }` — and **MUST refuse** a document it cannot execute
honestly, with a message naming what is wrong and where.

Two failure modes this rules out, both worse than a refusal:

- **Crashing structurally.** Dereferencing an absent member yields a null-reference
  fault, and a fault is not a reason. §Error taxonomy requires the adapter to say
  why; an accident says nothing.
- **Silently doing less.** An unrecognized operation that falls through a dispatch,
  or a missing predicate treated as "match everything", makes `prepare` report
  completion for work it did not do — or did far too much of. Both put a false
  input under the flip.

The upstream validator's strictness is **not** a substitute. It may be older than
the document, it may be a different implementation, and `prepare` is the door: a
door that assumes its input was checked elsewhere is not a door. The check is
cheap, total, and belongs before any staging mutation, so a refusal never leaves a
half-staged branch.

The exception *type* is the adapter's choice (§Error taxonomy leaves it
unspecified in v0); the *message* is not optional.

## 4. Fidelity declaration (minimum schema)

Per branch, machine-readable:

- per facet (`schema` / `data` / `ui`): replication mode — `full`,
  `subset` (with selection rule), or `stub` — and the method tag
  (e.g. `cow`, `snapshot`, `sample`)
- known differences from the live target (empty list is a claim, not an
  omission)

The declaration is the interpretation rule for simulation evidence and is
recorded in the ledger with the apply (branching decision; fault-model §3).

## 5. Still open (deferred with rationale)

- Data subset selection rules for `subset` fidelity — no adapter produces
  `subset` yet (in-memory: cow/full, first adapter: snapshot/full); specified with
  the first subset-producing adapter, demand-driven.
- Whether `prepare` exposes progress for large facets — not needed at current
  facet sizes; revisit with the first large-data adapter.
- **`discard` on a branch that has been flipped.** §3 calls discard "always
  safe", which reads as unconditional, but a flipped branch is no longer a
  staging world — it is the live state, and the reference adapter refuses to
  discard it. Refusing is the right behaviour; the text just does not say so.
  Surfaced by writing the conformance suite (§7), which initially checked
  discard on the flipped branch and so failed a correct adapter. The suite now
  discards a branch that was never flipped. Wording to be settled with the next
  adapter that has an opinion — until then, adapters may refuse.

## 6. Signatures (finalized with the first adapter — .NET reference)

Resolved in 4.b: the exact boundary is `IBackendAdapter`
(`src/Vivarium.Stage/Adapters/IBackendAdapter.cs`), implemented by the
in-memory reference adapter and proven by a first real-backend adapter
(now consumer-owned — real-backend adapters live with the consuming
application).

```csharp
interface IBackendAdapter
{
    CapabilityManifest Capabilities { get; }                    // §2
    Task<BranchInfo>    BranchAsync(string target, CancellationToken ct = default);
    Task<PrepareReport> PrepareAsync(string branchRef, PreparedFacets facets, CancellationToken ct = default);
    Task                FlipAsync(string target, string stateRef, string applyToken, CancellationToken ct = default);
    Task<ActiveState>   ActiveStateAsync(string target, CancellationToken ct = default);
    Task                DiscardAsync(string branchRef, CancellationToken ct = default);
}

record PreparedFacets(string ChangesetFingerprint, JsonObject Patches); // the adapter sees patches + fingerprint, never approvals/ledger
record BranchInfo(string BranchRef, FidelityDeclaration Fidelity);
record PrepareReport(IReadOnlyDictionary<string, bool> FacetComplete);  // Stage confirms ALL before any flip
record ActiveState(string StateRef, IReadOnlyDictionary<string, string> FacetFingerprints);
record FidelityDeclaration(IReadOnlyDictionary<string, FacetFidelity> PerFacet, IReadOnlyList<string> KnownDifferences);
record FacetFidelity(string Mode /* full|subset|stub */, string Method /* cow|snapshot|sample|… */, string? SelectionRule = null);
```

**UI patch resolution inside `PrepareAsync` (spec 0.2)**: a `whole-artifact@0`
patch carries its full `newContent`; a `verified-diff@0` patch must be resolved
against the branch's live base content with the spec's mandatory layer-2
verification (base fingerprint equality → deterministic fail-closed apply →
result fingerprint equality — spec §8). Any mismatch aborts the whole prepare;
a diff never lands partially and never targets an artifact the branch does not
hold (creation stays `whole-artifact@0`). The reference in-memory adapter is
the executable example of these semantics.

Design points that landed during implementation:

- **`ActiveState` carries the state ref, not just fingerprints.** The active
  pointer's value is the rollback return path and the decider for post-crash
  ledger reconciliation (fault-model F5); per-facet fingerprints serve the
  drift gate. Both are needed, so the operation returns both.
- **`FacetFingerprints` keys are drift-gate refs, not facet names.** A changeset's
  `baseState` entries name what the author stood on, and this dictionary must answer
  with the same granularity. That is one entry for `schema` and one for `data`, but
  **one entry per UI artifact** — `screen-orders`, not `ui` — because UI drift is
  per-artifact: editing one screen must not refuse a proposal that touches another.
  A key vocabulary is therefore not a facet-name vocabulary, and a host asking
  "did the UI facet move?" reads the artifact entries it cares about rather than
  looking for a `ui` key. The fidelity declaration (§4) *is* keyed by facet name —
  the two serve different questions and their key sets are deliberately different.
- **Refs are opaque strings.** A branch ref doubles as a state ref once
  flipped (a branch *is* the thing that graduates to an apply). The first
  adapter binds them to backend project ids; the in-memory adapter to world keys.
- **Error taxonomy (v0)**: gate refusals are Stage's (`StageRefusedException`);
  adapter failures during branch/prepare are retryable-or-discardable (F1/F2);
  `FlipAsync` re-issued with a used token for a *different* state ref MUST
  throw — same token + same state ref is the idempotent recovery no-op.
- **Unknown targets: throw, never invent.** `ActiveStateAsync` on a target the
  adapter does not know (state lost, partially restored, renamed) MUST throw.
  Returning a fabricated or empty `ActiveState` would let a *guess* reach the
  gates: the drift gate would compare against fingerprints that describe
  nothing, and recovery would reconcile the ledger against a pointer nobody
  owns. Throwing is the honest answer, and Stage is built to receive it —
  recovery translates it into an `unresolved` verdict
  (`Reason = "active-state-unreadable"`, nothing appended) and carries on with
  the other targets, while the apply path surfaces it as a failed apply.
  The exception type is not specified in v0; adapters should use whatever
  their platform makes idiomatic (the reference adapter throws
  `InvalidOperationException`).
- **First adapter's flip primitive**: a stage-owned control project holds a
  targets pointer table and a flip log; one backend transaction (PostgreSQL ACID)
  inserts the unique flip token and repoints the target row. Atomic, durable,
  idempotent-under-token — the §2 `atomic-swap` declaration is honest.

## 7. Conformance — this document, executable

Everything above is normative prose. An adapter author could read what their
implementation must do but had no way to learn whether it did — and the clauses
that matter most are the ones an adapter's own tests are least likely to reach:
throwing for an unknown target instead of inventing a pointer, staying
idempotent when recovery re-issues a flip token, refusing that token for a
different state, declaring branch fidelity honestly.

`Vivarium.Stage.Conformance.AdapterConformance.RunAsync` checks an
implementation against these clauses and returns a `ConformanceReport`:

```csharp
var report = await AdapterConformance.RunAsync(
    myAdapter,
    new ConformanceFixture(
        knownTarget: "fixture-app",      // a target the adapter knows; the run flips it
        unknownTarget: "no-such-target", // a target it must not know
        patches: patchesMyAdapterCanStage));

if (!report.AllPassed) throw new Exception(report.ToString());
```

Properties of the suite, and why:

- **Each check is named for the clause it enforces** — `§3/flip-idempotent-under-token`,
  `§Error-taxonomy/unknown-target-throws`. A failure says what to read. A check
  with no clause is not a check: the suite verifies this document, it does not
  add to it.
- **It reports rather than throws.** Violations come back as failed checks with
  detail, so the caller's own test framework decides what to assert and nothing
  has to be parsed out of a message. The suite takes no test-framework
  dependency.
- **`Skipped` is a real outcome.** Where this document does not constrain a case
  — a facet the manifest is silent about (§2 does not require it to enumerate
  every facet), a branch that declares no `subset` facet — the check reports
  unverifiable rather than failing. An over-strict suite that fails honest
  adapters would be worse than none. Where a check verifies only part of what it
  appears to, it names the part it could not reach.
- **Exception *types* are never asserted** — §Error taxonomy leaves them
  unspecified in v0, so the checks assert only that a throw happened.
- **It mutates live state.** The run flips the fixture target to a prepared
  branch and flips it back, so it must be pointed at a disposable fixture and
  never at production. The restore runs last, always, and is reported as its own
  check (it is also the rollback primitive, §3) — a mid-run failure still leaves
  a record of whether the fixture came home.

The reference in-memory adapter passes the full suite; that is the suite's
own first test.
