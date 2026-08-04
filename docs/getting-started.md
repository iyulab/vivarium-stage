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

```bash
dotnet add package Vivarium.Stage
```

Requires .NET 10. The [`Vivarium.Changeset`](https://www.nuget.org/packages/Vivarium.Changeset)
.NET SDK comes along as a dependency — Stage consumes the changeset contract,
it does not define it.

## 1. Wire the pieces

A lifecycle run needs two things you choose: a **backend adapter** (where
the application state lives) and a **ledger store** (where the audit trail
lives). The in-memory reference implementations are real, fully functional
members of the API — use them for tests, demos, and this guide; swap in
your application's backend adapter (see [adapter-api.md](adapter-api.md))
and a durable `ILedgerStore` for production.

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

var landed = await session.ApplyAsync(actor: "getting-started-operator");

if (session.State != SessionState.Applied) throw new Exception("apply lands atomically");
if (!adapter.ActiveWorldCanonical("app").Contains("Orders"))
    throw new Exception("the live target shows the change");

// The apply reports what it did — the same facts it wrote to the ledger.
// Ask the adapter what is *live*; ask the outcome what *this apply* landed.
// The two answer different questions and diverge under a concurrent flip.
if (landed.Operation != "apply" || landed.PreviousStateRef == landed.NewStateRef)
    throw new Exception("the outcome names the flip's two ends");
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
`FingerprintGate`, `DriftGate`, `DegradedAdapter`, `PrepareIncomplete`,
`InvalidStateTransition`) — that is *which gate*. Where the gate observed
something a caller can act on, `Details` carries it as data, so presenting
*what* does not mean parsing the sentence either:

```csharp
var stale = ChangesetFingerprint.Stamp(unapproved); // never mind the approval here
((JsonObject)stale["provenance"]!)["baseState"] = new JsonArray(new JsonObject
{
    ["kind"] = "ui-artifact",
    ["ref"] = "screen-main",
    ["fingerprint"] = "sha256:" + new string('0', 64), // a base that never existed
});
var rebased = ChangesetFingerprint.Stamp(stale);
rebased["approvals"] = new JsonArray(new JsonObject
{
    ["fingerprint"] = rebased["fingerprint"]!.GetValue<string>(),
    ["approvedBy"] = "reviewer",
    ["approvedAt"] = "2026-08-04T00:00:00Z",
});

var drifting = new ChangeSession(rebased, "app", adapter, ledger);
await drifting.BranchAsync();
drifting.RecordSimulation();
try
{
    await drifting.ApplyAsync(actor: "getting-started-operator");
    throw new Exception("a drifted base must not land");
}
catch (StageRefusedException refusal) when (refusal.Reason == RefusalReason.DriftGate)
{
    // every drifted ref, not just the first: re-basing is the author's job, and
    // one ref per refusal would make learning the full picture N round trips
    var driftedRefs = (JsonArray)refusal.Details!["drifted"]!;
    if (driftedRefs.Count == 0) throw new Exception("expected drift details");
    foreach (var entry in driftedRefs.OfType<JsonObject>())
    {
        var which = entry["ref"]!.GetValue<string>();
        var expected = entry["expected"]!.GetValue<string>();
        var actual = entry["actual"]?.GetValue<string>();  // null means the ref is absent
        Console.WriteLine($"re-base '{which}': authored against {expected}, live is {actual ?? "absent"}");
    }
}
await drifting.DiscardAsync();
```

`Details` is `null` wherever the refusal has no fact a caller could act on
differently — the fingerprint gate refuses a fingerprint the caller just
submitted, and echoing it back would inform nobody. Its members are per-gate
and additive: read the ones you know, ignore the rest.

## 5. Surviving a restart: rehydration

Rollback requires an Applied session — but a host process that restarts has
lost its in-memory objects, and re-driving the lifecycle is impossible by
design (the drift gate refuses: live has moved past the changeset's
`baseState`). `RehydrateAppliedAsync` reconstructs the Applied session from
durable state instead — **verified, never asserted**: it refuses unless the
ledger's latest completed entry is an apply of exactly this changeset *and*
the live active state matches what that entry recorded (an unreconciled
crash entry must be resolved by `StageRecovery` first):

```csharp
var rehydrated = await ChangeSession.RehydrateAppliedAsync(changeset, "app", adapter, ledger);

if (rehydrated.State != SessionState.Applied) throw new Exception("rehydration reconstructs the applied session");
if (rehydrated.Fingerprint != changeset["fingerprint"]!.GetValue<string>())
    throw new Exception("rehydration is bound to the exact changeset");
```

This is what keeps "every apply has a return path" true across process
lifetimes, not just within one.

## 6. Rollback, and the ledger that makes it possible

Rollback is not an afterthought — the apply recorded its return path in the
ledger, and the rollback flips back to it atomically:

```csharp
var returned = await session.RollbackAsync(actor: "getting-started-operator");

if (session.State != SessionState.RolledBack) throw new Exception("rollback is a first-class path");
if (!adapter.ActiveWorldCanonical("app").Contains("Home"))
    throw new Exception("rollback returns the previous state");

// The rollback's ends are the apply's, reversed — read from the ledger entry it
// undoes, not from whatever happens to be live at the time.
if (returned.NewStateRef != landed.PreviousStateRef)
    throw new Exception("a rollback returns to where the apply came from");
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
target by reading which state is actually active.

Recovery reports a verdict per target rather than throwing, so one
unaccountable target never blocks the rest:

```csharp
var recovery = await StageRecovery.RecoverAsync(ledger, adapter);

// The ledger is what recovery reasoned from, so it says what it made of it:
// intact | broken | unverifiable — the last meaning nothing was checked,
// which is not the same answer as "nothing was wrong".
Console.WriteLine($"ledger: {recovery.Integrity.Verdict}");

foreach (var outcome in recovery.Outcomes)
{
    // PendingOperation: apply | rollback   (which operation was in flight)
    // Resolution: completed | aborted | unresolved
    // Reason: active-matches-new | active-matches-previous
    //       | active-matches-neither | active-state-unreadable
    if (outcome.Resolution == "unresolved")
        Console.WriteLine($"operator needed for {outcome.Target}: {outcome.Reason}");
    else // the two axes name the entry reconciliation appended
        Console.WriteLine($"{outcome.Target}: {outcome.PendingOperation}-{outcome.Resolution}");
}
```

The verdict carries both axes because an aborted rollback is not an aborted
apply: with `rollback` + `aborted` the apply is still in effect, with `apply` +
`aborted` nothing landed. `PendingOperation` is present on every outcome —
including the `unresolved` ones, where the ledger still knows what was pending —
so reporting never needs a second read of the ledger to say what happened.

`unresolved` means recovery **appended nothing** — the active state either
moved out-of-band or could not be read at all (an adapter must throw for a
target it does not know rather than invent a pointer). The pending entry
stays visible until an operator resolves it: Stage refuses rather than
guessing, here as everywhere.

### The operator's half

Someone with knowledge the library does not have closes an `unresolved`
target. That is a judgement, not a reading, and the ledger records it as one:

```csharp
foreach (var stuck in recovery.Outcomes.Where(o => o.Resolution == "unresolved"))
{
    var resolved = await StageRecovery.ResolveAsync(
        ledger, stuck.Target, resolution: "aborted", actor: "alice");

    // Resolution: what the operator declared (completed | aborted)
    // Reason: operator-declared — the one verdict no live state supports
    Console.WriteLine($"{resolved.Target}: {resolved.Resolution} ({resolved.Reason})");
}
```

The entry is admitted only if that target actually has an operation in
flight, takes its apply token and state refs from the pending entry rather
than from the caller, and records the operator as its actor. `"stage-recovery"`
is reserved for resolutions the library verified itself, so an assertion can
never be read back as a verification.

### Whether the history can be trusted

Entries are chained: each carries its own hash and the hash of the one before
it, so an entry that was edited, removed from the middle, inserted or
reordered no longer fits. Verification is a separate reading, because an audit
is someone holding the exported file:

```csharp
var integrity = LedgerIntegrity.Verify(ReleaseLedger.ParseExport(export));
if (integrity.Verdict == "broken")
    foreach (var finding in integrity.Findings)
        Console.WriteLine($"seq {finding.Seq}: {finding.Message}");
```

Three verdicts, and the third earns its place: `unverifiable` means no entry
carries a chain — history written before this ledger began chaining verifies
as that rather than as `intact`, and `UnverifiedPrefix` counts how many
entries the check could not speak for. Such history is never re-hashed on
import; doing so would assert it was never altered instead of verifying it.
Older exports therefore need no migration.

By default recovery reports the verdict and carries on: a damaged ledger is
when a host may most need to recover, so refusing by default would take the
recovery path down with the check. A host for whom stopping is the right
answer says so, and gets a refusal carrying the findings:

```csharp
try
{
    await StageRecovery.RecoverAsync(ledger, adapter,
        policy: new StagePolicy { RequireIntactLedger = true });
}
catch (StageRefusedException refusal)
    when (refusal.Reason == RefusalReason.LedgerIntegrityGate)
{
    // refusal.Details carries verdict, unverifiedPrefix and findings[]
}
```

**What the chain does not catch**: dropping the newest entries. A shorter
history is a self-consistent one — nothing inside a ledger says how far it
should reach — and appending afterwards closes over the gap rather than
exposing it. Detecting that needs a fixed point held where the store cannot
reach it, which this version does not have. What the chain does buy is that
every other edit becomes visible, and that a convincing forgery costs the
whole ledger from the tampered entry onward instead of one line.

## Real backends: the adapter boundary

Everything above ran against the in-memory adapter. Real backends implement
`IBackendAdapter` — five operations (`ActiveStateAsync`, `BranchAsync`,
`PrepareAsync`, `FlipAsync`, `DiscardAsync`) plus a **capability manifest**
declaring what the backend can honestly promise (atomic flip or not,
branching fidelity per facet). The contract, including who owns what, is
specified in [adapter-api.md](adapter-api.md). The signatures were
finalized against a live backend service (project-per-state branching,
atomic flip via a control-table transaction); real-backend adapters are
owned by the consuming application, not this repository.

Two rules adapters live by:

- **Declare, don't overpromise.** A branch that differs from production
  (shared resources, stubbed integrations) must say so in its fidelity
  declaration — Stage refuses undeclared gaps.
- **Stage owns the gates; the adapter owns the mechanics.** Adapters never
  decide whether a change may land — only how state is branched, prepared,
  and flipped.

### Checking your adapter against the contract

You do not have to take the contract on faith. `AdapterConformance` runs it
against your implementation and hands back a report — including the clauses an
adapter's own tests rarely reach, like throwing for a target it does not know
rather than inventing a pointer, and staying idempotent when recovery re-issues
a flip token:

```csharp
using Vivarium.Stage.Conformance;

var fixtureAdapter = new InMemoryBackendAdapter();   // swap in your own adapter
fixtureAdapter.SeedTarget("fixture-app", new JsonObject
{
    ["schema"] = new JsonObject { ["entities"] = new JsonObject() },
    ["data"] = new JsonObject(),
    ["artifacts"] = new JsonObject { ["screen-main"] = "export default function mount(root) {}" },
});

var conformance = await AdapterConformance.RunAsync(
    fixtureAdapter,
    new ConformanceFixture(
        KnownTarget: "fixture-app",        // the run FLIPS this target — never production
        UnknownTarget: "no-such-target",
        Patches: new JsonObject
        {
            ["uiPatches"] = new JsonArray(new JsonObject
            {
                ["artifactId"] = "screen-main",
                ["profile"] = "whole-artifact@0",
                ["newContent"] = "export default function mount(root) { root.textContent = 'ok'; }",
            }),
        }));

if (!conformance.AllPassed) throw new Exception(conformance.ToString());
```

Each check is named for the clause it enforces (`§Error-taxonomy/unknown-target-throws`),
so a failure tells you what to read in [adapter-api.md](adapter-api.md) §7.
The suite reports rather than throws, needs no test framework, and marks a check
`Skipped` where the contract genuinely does not constrain your adapter — it will
not fail you for a case the boundary leaves open.

## Where to go next

- [fault-model.md](fault-model.md) — the partial-failure matrix (F1–F6) and
  crash-consistency rules this design is tested against.
- [adapter-api.md](adapter-api.md) — the full adapter contract, for writing
  your own backend.
- [`vivarium-agent`](https://github.com/iyulab/vivarium-agent) — produces
  the verified changesets this lifecycle consumes.
