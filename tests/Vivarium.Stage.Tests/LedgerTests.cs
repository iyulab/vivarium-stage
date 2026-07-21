using System.Text.Json.Nodes;
using Vivarium.Changeset;
using Vivarium.Stage.Ledger;

namespace Vivarium.Stage.Tests;

public class LedgerTests
{
    [Fact]
    public async Task RollbackRestoresLineage_SeedStateHasNoFingerprint()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();
        await session.ApplyAsync("operator-1");
        await session.RollbackAsync("operator-1");

        // the active state is the seed — it precedes any recorded apply, so no
        // changeset fingerprint may claim it (reporting the rolled-back one
        // would say "this changeset is live" about a state it no longer is)
        var view = LedgerProjection.Replay(await world.Ledger.ReadAllAsync())[TestWorld.TargetName];
        Assert.Equal("live-app", view.ActiveStateRef);
        Assert.Null(view.ActiveChangesetFingerprint);
    }

    [Fact]
    public async Task RollbackRestoresLineage_PriorApplyFingerprintReturns()
    {
        var world = new TestWorld();
        var sessionA = await world.SimulatedSessionAsync();
        await sessionA.ApplyAsync("operator-1");

        // a second changeset written against the post-A live state
        var active = await world.Inner.ActiveStateAsync(TestWorld.TargetName);
        const string v3 = "export function LoanScreen() {\n  return <Form fields={[amount, dueDate, note]} />;\n}";
        var docB = new ChangesetBuilder(
                intent: "Add a note field to the loan screen",
                producedBy: "test-suite",
                createdAt: "2026-07-16T02:00:00Z",
                baseState: [new BaseStateEntry("ui-artifact", "screen-loans", active.FacetFingerprints["screen-loans"])])
            .AddUiPatch("screen-loans", TestWorld.NewArtifact, v3, "Renders the note field")
            .Finalize();
        docB["approvals"] = new JsonArray(new JsonObject
        {
            ["fingerprint"] = docB["fingerprint"]!.GetValue<string>(),
            ["approvedBy"] = "reviewer-1",
            ["approvedAt"] = "2026-07-16T03:00:00Z",
        });
        var sessionB = await world.SimulatedSessionAsync(docB);
        await sessionB.ApplyAsync("operator-1");
        await sessionB.RollbackAsync("operator-1");

        // rolling back B lands on the state A produced — the lineage follows
        var view = LedgerProjection.Replay(await world.Ledger.ReadAllAsync())[TestWorld.TargetName];
        Assert.Equal(sessionA.Fingerprint, view.ActiveChangesetFingerprint);
    }

    [Fact]
    public async Task WriteAheadOrdering_StartedPrecedesFlipPrecedesCompleted()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();
        await session.ApplyAsync("operator-1", applyToken: "tok-order");

        var entries = await world.Ledger.ReadAllAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal("apply-started", entries[0].Kind);
        Assert.Equal("apply-completed", entries[1].Kind);
        Assert.True(entries[0].Seq < entries[1].Seq);
        Assert.Equal("tok-order", entries[0].ApplyToken);
        Assert.Equal(entries[0].ApplyToken, entries[1].ApplyToken);

        // the started entry carries everything recovery needs: fingerprint,
        // fidelity declaration, actor, and the return path
        Assert.Equal(session.Fingerprint, entries[0].ChangesetFingerprint);
        Assert.NotNull(entries[0].Fidelity);
        Assert.Equal("full", entries[0].Fidelity!["perFacet"]!["schema"]!["mode"]!.GetValue<string>());
        Assert.Equal("operator-1", entries[0].Actor);
        Assert.Equal("live-app", entries[0].PreviousStateRef);
    }

    [Fact]
    public async Task ReplayReconstructsStateFromLedgerAlone()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();
        await session.ApplyAsync("operator-1");
        await session.RollbackAsync("operator-1");

        // replay from raw entries — no session, no adapter
        var view = LedgerProjection.Replay(await world.Ledger.ReadAllAsync())[TestWorld.TargetName];
        Assert.Equal(2, view.AppliedHistory.Count); // apply + rollback completions
        Assert.Null(view.PendingStarted);
        Assert.Equal("live-app", view.ActiveStateRef); // rolled back to the original state

        // the replayed active ref matches the adapter's reality
        var active = await world.Inner.ActiveStateAsync(TestWorld.TargetName);
        Assert.Equal(active.StateRef, view.ActiveStateRef);
    }

    [Fact]
    public async Task AppendRefusesAnUnknownKind_TheLedgerCannotBeCorrupted()
    {
        var store = new InMemoryLedgerStore();
        var ledger = new ReleaseLedger(store);

        // FromJson already refuses unknown kinds; the write path must agree.
        // Without this the ledger accepts a typo forever (append-only), Replay
        // silently ignores it, and the export can no longer be re-imported.
        await Assert.ThrowsAsync<ArgumentException>(() => ledger.AppendAsync(
            "apply-complete", "t", "sha256:x", "tok", "actor", "2026-07-21T00:00:00.000Z"));

        Assert.Empty(await ledger.ReadAllAsync()); // refused before it was written
    }

    [Fact]
    public async Task EveryDeclaredKindIsAppendable()
    {
        var store = new InMemoryLedgerStore();
        var ledger = new ReleaseLedger(store);

        foreach (var kind in LedgerEntry.Kinds)
            await ledger.AppendAsync(kind, "t", "sha256:x", "tok", "actor", "2026-07-21T00:00:00.000Z");

        // the guard admits exactly the declared vocabulary, and what it admits
        // still round-trips through the export
        var entries = await ledger.ReadAllAsync();
        Assert.Equal(LedgerEntry.Kinds.Length, entries.Count);
        Assert.Equal(entries.Count, ReleaseLedger.ParseExport(await ledger.ExportJsonAsync()).Count);
    }

    [Fact]
    public async Task ExportIsMachineVerifiableAndRoundTrips()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();
        await session.ApplyAsync("operator-1");

        var json = await world.Ledger.ExportJsonAsync();
        var parsed = ReleaseLedger.ParseExport(json);
        var original = await world.Ledger.ReadAllAsync();

        Assert.Equal(original.Count, parsed.Count);
        for (var i = 0; i < original.Count; i++)
            Assert.Equal(original[i], parsed[i], LedgerEntryComparer.Instance);

        // replay over the re-parsed export reconstructs the same projection
        var view = LedgerProjection.Replay(parsed)[TestWorld.TargetName];
        Assert.Equal(session.Fingerprint, view.ActiveChangesetFingerprint);
    }

    [Fact]
    public async Task SequenceNumbersResumeAfterRehydration()
    {
        var store = new InMemoryLedgerStore();
        var first = new ReleaseLedger(store);
        await first.AppendAsync("apply-started", "t", "sha256:x", "tok", "a", "2026-07-16T00:00:00Z");
        await first.AppendAsync("apply-completed", "t", "sha256:x", "tok", "a", "2026-07-16T00:00:01Z");

        // a new ledger instance over the same store must not reuse sequence numbers
        var second = new ReleaseLedger(store);
        var entry = await second.AppendAsync("apply-started", "t", "sha256:y", "tok2", "a", "2026-07-16T00:00:02Z");
        Assert.Equal(3, entry.Seq);
    }

    private sealed class LedgerEntryComparer : IEqualityComparer<LedgerEntry>
    {
        public static readonly LedgerEntryComparer Instance = new();
        public bool Equals(LedgerEntry? x, LedgerEntry? y) =>
            x is not null && y is not null &&
            x.ToJson().ToJsonString() == y.ToJson().ToJsonString();
        public int GetHashCode(LedgerEntry obj) => obj.ToJson().ToJsonString().GetHashCode();
    }
}
