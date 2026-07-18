# Getting started — running the changeset lifecycle

This guide takes a host from a project reference to a full lifecycle run:
an approved changeset goes **proposed → branched → simulated → applied**
against a backend adapter, an unapproved one is refused at the gate, and a
rollback returns the previous state — all of it audited by an append-only
ledger.

Every `csharp` code block below is extracted and **executed** as one fresh
consumer program by `tools/verify-docs-examples.ts` (wired into CI). The
examples throw on failure, so they cannot silently drift from the API.

## Install

Vivarium.Stage is not on NuGet yet. Until it is published, consume it as a
project reference against a checkout (it references the sibling
[`vivarium-changeset`](https://github.com/iyulab/vivarium-changeset) .NET
SDK, so check both out side by side):

```xml
<ItemGroup>
  <ProjectReference Include="../vivarium-stage/src/Vivarium.Stage/Vivarium.Stage.csproj" />
</ItemGroup>
```

Requires .NET 10. When the NuGet package lands, this becomes a normal
`PackageReference` — the API below is the same either way.

## 1. Wire the pieces

A lifecycle run needs two things you choose: a **backend adapter** (where
the application state lives) and a **ledger store** (where the audit trail
lives). The in-memory reference implementations are real, fully functional
members of the API — use them for tests, demos, and this guide; swap in a
real adapter (e.g. the [first shipped
adapter](../src/Vivarium.Stage.Adapters.MorphDb)) and a durable
`ILedgerStore` for production.

```csharp
using System.Text.Json.Nodes;
using Vivarium.Changeset;
using Vivarium.Stage;
using Vivarium.Stage.Adapters;
using Vivarium.Stage.Ledger;

var adapter = new InMemoryBackendAdapter();
var ledger = new ReleaseLedger(new InMemoryLedgerStore());

var baseArtifact = "export default function mount(root) { root.textContent = 'Home'; }";
adapter.SeedTarget("app", new JsonObject
{
    ["schema"] = new JsonObject { ["entities"] = new JsonObject() },
    ["data"] = new JsonObject(),
    ["artifacts"] = new JsonObject { ["screen-main"] = baseArtifact },
});
```

## 2. An approved changeset

Stage consumes changesets that follow the
[`vivarium-changeset`](https://github.com/iyulab/vivarium-changeset)
contract — typically produced by an agent (e.g.
[`vivarium-agent`](https://github.com/iyulab/vivarium-agent)) and approved
by a reviewer. Here we author one with the SDK's builder. Two things
matter:

- **`baseState` pins what the change was written against** — facet
  fingerprints taken from the live target. The drift gate compares these at
  apply time.
- **An approval is bound to the exact fingerprint.** Approving "the idea of
  the change" is not a thing; the record names the bytes.

```csharp
var live = await adapter.ActiveStateAsync("app");

var changeset = new ChangesetBuilder(
        intent: "Change the heading to Orders",
        producedBy: "getting-started",
        createdAt: "2026-07-17T00:00:00Z",
        baseState: [new BaseStateEntry("ui-artifact", "screen-main", live.FacetFingerprints["screen-main"])])
    .AddUiPatch(
        "screen-main",
        baseArtifact,
        "export default function mount(root) { root.textContent = 'Orders'; }",
        "Retitle the heading to Orders.")
    .Finalize();

changeset["approvals"] = new JsonArray(new JsonObject
{
    ["fingerprint"] = changeset["fingerprint"]!.GetValue<string>(),
    ["approvedBy"] = "reviewer-1",
    ["approvedAt"] = "2026-07-17T01:00:00Z",
});
```

## 3. The lifecycle: branch → simulate → apply

A `ChangeSession` drives one changeset through the state machine. Admission
is already a gate: an unstamped or spec-invalid document never enters the
lifecycle (the constructor throws `StageRefusedException`).

```csharp
var session = new ChangeSession(changeset, "app", adapter, ledger);

var branch = await session.BranchAsync();
if (branch.Fidelity.PerFacet.Count == 0) throw new Exception("branches declare their fidelity");

// Simulation itself is host territory: render or exercise the branch
// preview however your host does, then record what was observed. The
// branch's fidelity declaration is the interpretation rule for the evidence.
session.RecordSimulation(new JsonObject { ["observed"] = "Orders renders on the branch preview" });

await session.ApplyAsync(actor: "getting-started-operator");

if (session.State != SessionState.Applied) throw new Exception("apply lands atomically");
if (!adapter.ActiveWorldCanonical("app").Contains("Orders"))
    throw new Exception("the live target shows the change");
```

`ApplyAsync` is where the three gates run, every time:

1. **Fingerprint gate** — the content still matches its fingerprint, and an
   approval record names exactly that fingerprint.
2. **Degraded-adapter gate** — an adapter that cannot flip atomically needs
   explicit host consent (`StagePolicy.AcceptDegradedAdapter`).
3. **Drift gate** — every `baseState` entry must match the live target
   *now*. If someone changed the target since the proposal was written, the
   apply refuses rather than landing on assumptions.

All facets of a changeset (schema, data, UI) are prepared on the branch and
land in **one atomic flip** — a half-applied state is structurally
impossible (the F1–F6 fault matrix in [fault-model.md](fault-model.md) is
executed as fault-injection tests).

## 4. What cannot land

The gate is not advisory. A changeset that is stamped and spec-valid but
**unapproved** branches and simulates fine — and refuses at apply:

```csharp
var drifted = await adapter.ActiveStateAsync("app");
var unapproved = new ChangesetBuilder(
        intent: "Sneak a change past review",
        producedBy: "getting-started",
        createdAt: "2026-07-17T02:00:00Z",
        baseState: [new BaseStateEntry("ui-artifact", "screen-main", drifted.FacetFingerprints["screen-main"])])
    .AddUiPatch(
        "screen-main",
        "export default function mount(root) { root.textContent = 'Orders'; }",
        "export default function mount(root) { root.textContent = 'Something else'; }",
        "Change the heading.")
    .Finalize(); // stamped and spec-valid — but nobody approved it

var rogue = new ChangeSession(unapproved, "app", adapter, ledger);
await rogue.BranchAsync();
rogue.RecordSimulation();
try
{
    await rogue.ApplyAsync(actor: "getting-started-operator");
    throw new Exception("an unapproved changeset must not land");
}
catch (StageRefusedException refusal)
{
    if (refusal.Reason != RefusalReason.FingerprintGate) throw;
}
await rogue.DiscardAsync(); // pre-apply exits never touch live state
```

Every refusal carries a `RefusalReason` (`InvalidChangeset`,
`FingerprintGate`, `DriftGate`, `DegradedAdapter`,
`InvalidStateTransition`), so hosts can present *why* without parsing
messages.

## 5. Rollback, and the ledger that makes it possible

Rollback is not an afterthought — the apply recorded its return path in the
ledger, and the rollback flips back to it atomically:

```csharp
await session.RollbackAsync(actor: "getting-started-operator");

if (session.State != SessionState.RolledBack) throw new Exception("rollback is a first-class path");
if (!adapter.ActiveWorldCanonical("app").Contains("Home"))
    throw new Exception("rollback returns the previous state");
```

The ledger is append-only and write-ahead (started/completed pairs), which
makes the whole history replayable — the current state of every target is
derivable from the ledger alone, and an export round-trips:

```csharp
var entries = await ledger.ReadAllAsync();
var view = LedgerProjection.Replay(entries)["app"];

if (view.AppliedHistory.Count != 2) // the apply and the rollback, both audited
    throw new Exception("the ledger replays deterministically");
if (view.PendingStarted is not null)
    throw new Exception("no operation was left half-done");

var export = await ledger.ExportJsonAsync();
if (ReleaseLedger.ParseExport(export).Count != entries.Count)
    throw new Exception("the ledger export round-trips");
```

A `started` entry without its `completed` pair (a crash mid-apply) surfaces
in `PendingStarted` — the input to `StageRecovery`, which reconciles the
target from the ledger.

## Real backends: the adapter boundary

Everything above ran against the in-memory adapter. Real backends implement
`IBackendAdapter` — five operations (`ActiveStateAsync`, `BranchAsync`,
`PrepareAsync`, `FlipAsync`, `DiscardAsync`) plus a **capability manifest**
declaring what the backend can honestly promise (atomic flip or not,
branching fidelity per facet). The contract, including who owns what, is
specified in [adapter-api.md](adapter-api.md); the [first shipped
adapter](../src/Vivarium.Stage.Adapters.MorphDb) is the reference
implementation against a live backend service (project-per-state branching, atomic
flip via a control-table transaction).

Two rules adapters live by:

- **Declare, don't overpromise.** A branch that differs from production
  (shared resources, stubbed integrations) must say so in its fidelity
  declaration — Stage refuses undeclared gaps.
- **Stage owns the gates; the adapter owns the mechanics.** Adapters never
  decide whether a change may land — only how state is branched, prepared,
  and flipped.

## Where to go next

- [fault-model.md](fault-model.md) — the partial-failure matrix (F1–F6) and
  crash-consistency rules this design is tested against.
- [adapter-api.md](adapter-api.md) — the full adapter contract, for writing
  your own backend.
- [`vivarium-agent`](https://github.com/iyulab/vivarium-agent) — produces
  the verified changesets this lifecycle consumes.
