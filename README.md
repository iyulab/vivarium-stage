# Vivarium Stage

> Changeset lifecycle service — branch, simulate, atomically apply, and roll back live application changes.

**Status: core + first adapter implemented (pre-0.1).** The lifecycle state machine, fingerprint gate with drift refusal, append-only release ledger, and the backend adapter boundary (with a reference in-memory adapter) live in [`src/Vivarium.Stage`](src/Vivarium.Stage) (.NET); the [fault model](docs/fault-model.md)'s partial-failure matrix (F1–F6) is executed as fault-injection tests. The first real adapter, [`src/Vivarium.Stage.Adapters.MorphDb`](src/Vivarium.Stage.Adapters.MorphDb), runs the full lifecycle against a live [MorphDB](https://github.com/iyulab/MorphDB) (project-per-state branching, atomic flip via a control-table transaction) — the [adapter boundary signatures](docs/adapter-api.md) are finalized. Storage and deployment topology remain intentionally open.

**To run the lifecycle in your host, start with the [getting-started guide](docs/getting-started.md).**

---

## Why

A reviewable change is only half of safety. The other half is *how it lands*: on a live, multi-tenant application, with users connected, data in motion, and no maintenance window. Three failure modes define this problem:

1. **The half-applied change.** Schema migrated, UI deploy failed — the running app now contradicts its own database. Any design where the facets of a change can land separately will eventually produce this.
2. **The unrehearsed change.** A change that was never observed running against realistic state, applied directly to production, because there was no cheap way to try it first.
3. **The unreturnable change.** Something went wrong and there is no defined path back to the previous good state.

Vivarium Stage exists to make all three structurally impossible. It is the one component in the family with the authority — and the responsibility — to touch running systems.

## The lifecycle

Stage owns a single state machine that every changeset passes through:

```
proposed ──▶ branched ──▶ simulated ──▶ applied
                │              │            │
                └──────────────┴────────────┴──▶ discarded / rolled back
```

- **Branch.** Fork the target application's state (schema, and enough data to be representative) into an isolated preview environment. Cheap enough to do for every proposal.
- **Simulate.** Run the changeset against the branch — schema, data, and UI together — so the change can be *seen working* before it is trusted. This is the "development mode" a human walks through.
- **Apply.** Execute exactly one reviewed fingerprint against the live target, atomically across all facets: everything lands or nothing does. Refuse if the live state has drifted from the changeset's recorded base.
- **Roll back.** Return to the pre-apply state through a defined, tested path — not a heroic manual recovery.

Preview and release are one repository because they are one state machine: a branch *is* the thing that graduates to an apply, and splitting them would force two services to co-own that state.

## What this repository contains

- **The lifecycle service.** The state machine above, exposed as an API: create branch, run simulation, gate and execute apply, roll back, inspect history.
- **The backend adapter boundary.** Stage speaks to schema/data backends through adapters. [MorphDB](https://github.com/iyulab/MorphDB) is the first adapter — its logical/physical separation makes branching natural — but the boundary is designed in from the start; Stage must not be un-portable from it.
- **The release ledger.** An append-only history of what was applied, when, by whom, from which fingerprint — the audit trail a runtime-mutable platform owes its operators.
- **Live propagation hooks.** After a successful apply, connected clients are told to pick up the new world. The mechanism is adapter/host territory; the hook is Stage's.

## What this repository is not

- **Not an authoring tool.** Stage never creates or modifies changesets; it consumes them. Authoring belongs to agents ([`vivarium-agent`](https://github.com/iyulab/vivarium-agent)) or humans.
- **Not a UI runtime.** Stage stores and versions UI artifacts as opaque payloads within changesets; rendering them is the runtime's job ([`vivarium`](https://github.com/iyulab/vivarium)).
- **Not a CI/CD system.** Stage applies application-level changesets to running systems in seconds. It does not build code, run test matrices, or deploy infrastructure.
- **Not a database.** Stage orchestrates backends through adapters; it does not persist tenant data itself.

## Fixed principles

1. **Apply is atomic across facets.** Schema, data, and UI land together or not at all. There is no API for applying part of a changeset.
2. **Apply is fingerprint-gated.** Stage executes exactly a reviewed changeset fingerprint or refuses — the [`vivarium-changeset`](https://github.com/iyulab/vivarium-changeset) gate semantics, enforced at the only place that matters.
3. **Drift refuses, never guesses.** If the live base state no longer matches the changeset's provenance, Stage rejects; re-basing is the author's job, not the applier's.
4. **Every apply has a return path.** A changeset that cannot be rolled back (or explicitly, reviewably declares itself irreversible) does not get applied.
5. **Simulation is honest.** A branch must be faithful enough that "it worked in preview" is evidence, not superstition. Where fidelity is limited, Stage says so rather than pretending.
6. **The ledger is append-only.** History is never rewritten.

## Decided in v0

- **Crash-consistency: prepare-all → atomic flip.** Every facet's new state
  is completed in staging (no live effect, safely discardable), then
  activated by one atomic pointer swap. A crash therefore leaves either the
  old world or the new one — a half-applied state is structurally
  impossible, not managed. Rollback is a re-flip. The full failure model,
  including the partial-failure matrix and ledger write-ahead ordering, is
  in [docs/fault-model.md](docs/fault-model.md).
- **Branching is adapter territory; fidelity declaration is not.** How a
  branch is made (copy-on-write, snapshot, subset sampling) belongs to each
  adapter. What Stage mandates is a machine-readable fidelity declaration —
  what was replicated, how faithfully — without which a branch cannot enter
  simulation, and which is recorded in the ledger alongside the apply.
- **The adapter contract's two pillars.** An adapter either provides the
  atomic swap primitive (idempotent under an apply token) or honestly
  declares its degradation, and applies through a degraded adapter require
  explicit host policy consent. The boundary's operations and contracts are
  fixed in [docs/adapter-api.md](docs/adapter-api.md); exact signatures land
  with the first adapter.

## Deliberately undecided

- Which backends beyond MorphDB get adapters, and the adapter API's final shape
- Deployment topology (per-tenant, shared service, embedded library mode)
- Retention and lifecycle policy for branches and preview environments
- The live-propagation transport (SignalR is the natural first choice via MorphDB, not a commitment)

## Relationship to the Vivarium family

Depends on [`vivarium-changeset`](https://github.com/iyulab/vivarium-changeset) only. It does not know how changesets are authored and does not depend on `vivarium` or `vivarium-agent`. It is the family's sole holder of write authority over live systems — a deliberate concentration: one place to audit, one place to harden.

Standalone use is a first-class scenario: any platform that needs *"preview, atomically apply, and roll back structured changes to a running system"* can adopt Stage with its own adapter, with or without the rest of the family.

## License

Apache-2.0.