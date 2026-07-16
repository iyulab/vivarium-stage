# Backend adapter boundary (v0.1)

Normative boundary between Stage and its backend adapters. This fixes the
*operations and contracts* of the boundary; exact signatures are finalized
with the first adapter (MorphDB) and recorded here as they land. Companion
to [fault-model.md](fault-model.md).

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

## 5. Open until the first adapter (4.b)

- Exact wire signatures and error taxonomy
- Data subset selection rules for `subset` fidelity
- Whether `prepare` exposes progress for large facets
