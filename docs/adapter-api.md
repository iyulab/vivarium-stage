# Backend adapter boundary (v0.2)

Normative boundary between Stage and its backend adapters. v0.1 fixed the
*operations and contracts*; v0.2 records the signatures as finalized with the
first adapter (MorphDB) — see §6. Companion to [fault-model.md](fault-model.md).

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
| `activeState(target)` | Return deterministic fingerprint(s) of the currently active base state — Stage's input for drift refusal and for post-crash ledger reconciliation (fault-model F5). |
| `discard(branch)` | Release a staging world and its resources. Always safe (staging never touches live state). |

Simulation is *not* an adapter operation: Stage and the host drive whatever
runs against the branch (e.g. a UI runtime rendering preview artifacts);
the adapter only guarantees the branch behaves as declared.

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
  `subset` yet (in-memory: cow/full, MorphDB: snapshot/full); specified with
  the first subset-producing adapter, demand-driven.
- Whether `prepare` exposes progress for large facets — not needed at current
  facet sizes; revisit with the first large-data adapter.

## 6. Signatures (finalized with the first adapter — .NET reference)

Resolved in 4.b: the exact boundary is `IBackendAdapter`
(`src/Vivarium.Stage/Adapters/IBackendAdapter.cs`), first implemented by the
in-memory reference adapter and the MorphDB adapter
(`src/Vivarium.Stage.Adapters.MorphDb`).

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

Design points that landed during implementation:

- **`ActiveState` carries the state ref, not just fingerprints.** The active
  pointer's value is the rollback return path and the decider for post-crash
  ledger reconciliation (fault-model F5); per-facet fingerprints serve the
  drift gate. Both are needed, so the operation returns both.
- **Refs are opaque strings.** A branch ref doubles as a state ref once
  flipped (a branch *is* the thing that graduates to an apply). MorphDB
  binds them to project ids; the in-memory adapter to world keys.
- **Error taxonomy (v0)**: gate refusals are Stage's (`StageRefusedException`);
  adapter failures during branch/prepare are retryable-or-discardable (F1/F2);
  `FlipAsync` re-issued with a used token for a *different* state ref MUST
  throw — same token + same state ref is the idempotent recovery no-op.
- **MorphDB flip primitive**: a stage-owned control project holds a targets
  pointer table and a flip log; one MorphDB transaction (PostgreSQL ACID)
  inserts the unique flip token and repoints the target row. Atomic, durable,
  idempotent-under-token — the §2 `atomic-swap` declaration is honest.
