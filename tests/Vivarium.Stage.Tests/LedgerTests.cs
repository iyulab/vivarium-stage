using Vivarium.Stage.Ledger;

namespace Vivarium.Stage.Tests;

public class LedgerTests
{
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
        Assert.Equal("live-0", entries[0].PreviousStateRef);
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
        Assert.Equal("live-0", view.ActiveStateRef); // rolled back to the original state

        // the replayed active ref matches the adapter's reality
        var active = await world.Inner.ActiveStateAsync(TestWorld.TargetName);
        Assert.Equal(active.StateRef, view.ActiveStateRef);
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
