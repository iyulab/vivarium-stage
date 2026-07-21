using Vivarium.Stage.Adapters;
using Vivarium.Stage.Ledger;

namespace Vivarium.Stage.Tests;

/// <summary>
/// The recovery primitive's own contract, independent of the F1–F6 crash
/// scenarios: what it reports, what it refuses to guess, and what one bad
/// target may not do to the others.
/// </summary>
public class RecoveryTests
{
    private static async Task<ReleaseLedger> LedgerWithPendingAsync(
        InMemoryLedgerStore store, string target, string token, string previousRef, string newRef)
    {
        var ledger = new ReleaseLedger(store);
        await ledger.AppendAsync("apply-started", target, $"sha256:{target}", token, "operator-1",
            "2026-07-21T00:00:00.000Z", previousStateRef: previousRef, newStateRef: newRef);
        return ledger;
    }

    [Fact]
    public async Task UnknownTarget_IsUnresolved_NotAnException()
    {
        var adapter = new InMemoryBackendAdapter(); // knows no target at all
        var store = new InMemoryLedgerStore();
        var ledger = await LedgerWithPendingAsync(store, "ghost", "tok-ghost", "live-ghost", "branch-1");

        // the reference adapter throws on an unknown target — recovery must
        // judge that ("the active pointer is unreadable"), not propagate it
        var outcomes = await StageRecovery.RecoverAsync(ledger, adapter, new FixedTimeProvider());

        var outcome = Assert.Single(outcomes);
        Assert.Equal("unresolved", outcome.Resolution);
        Assert.Equal("active-state-unreadable", outcome.Reason);

        // refusing to guess means appending nothing — the pending entry stands
        var entries = await ledger.ReadAllAsync();
        Assert.Single(entries);
        Assert.NotNull(LedgerProjection.Replay(entries)["ghost"].PendingStarted);
    }

    [Fact]
    public async Task OneUnreadableTarget_DoesNotStopTheOthers()
    {
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("known");
        var store = new InMemoryLedgerStore();

        // "ghost" is unknown to the adapter; "known" is mid-apply and did land
        var ledger = await LedgerWithPendingAsync(store, "ghost", "tok-ghost", "live-ghost", "branch-x");
        var branch = await adapter.BranchAsync("known");
        await ledger.AppendAsync("apply-started", "known", "sha256:known", "tok-known", "operator-1",
            "2026-07-21T00:00:01.000Z", previousStateRef: "live-known", newStateRef: branch.BranchRef);
        await adapter.FlipAsync("known", branch.BranchRef, "tok-known");

        var outcomes = await StageRecovery.RecoverAsync(ledger, adapter, new FixedTimeProvider());

        // both targets are reported — the unreadable one does not abort the sweep
        Assert.Equal(2, outcomes.Count);
        var ghost = outcomes.Single(o => o.Target == "ghost");
        var known = outcomes.Single(o => o.Target == "known");
        Assert.Equal("unresolved", ghost.Resolution);
        Assert.Equal("completed", known.Resolution);
        Assert.Equal("active-matches-new", known.Reason);

        // and the readable one was actually reconciled
        var entries = await ledger.ReadAllAsync();
        var replayed = LedgerProjection.Replay(entries);
        Assert.Null(replayed["known"].PendingStarted);
        Assert.NotNull(replayed["ghost"].PendingStarted);
    }

    [Fact]
    public async Task OutcomeCarriesTheChangesetFingerprint()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();
        world.Adapter.Fault = FaultPoint.AfterFlip;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => session.ApplyAsync("operator-1", applyToken: "tok-fp"));

        var outcome = Assert.Single(await StageRecovery.RecoverAsync(world.Ledger, world.Adapter, world.Clock));

        // the ledger already knows which changeset this was — consumers should
        // not have to re-read a projection and join on the apply token
        Assert.Equal(session.Fingerprint, outcome.ChangesetFingerprint);
        Assert.Equal("completed", outcome.Resolution);
        Assert.Equal("active-matches-new", outcome.Reason);
    }

    [Fact]
    public async Task AbortedAndNeitherCarryDistinctReasons()
    {
        // aborted: crash before the flip — live is still the previous state
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();
        world.Adapter.Fault = FaultPoint.BeforeFlip;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => session.ApplyAsync("operator-1"));
        var aborted = Assert.Single(await StageRecovery.RecoverAsync(world.Ledger, world.Adapter, world.Clock));
        Assert.Equal("aborted", aborted.Resolution);
        Assert.Equal("active-matches-previous", aborted.Reason);

        // neither: an out-of-band flip moved live to a third state
        var other = new TestWorld();
        var s2 = await other.SimulatedSessionAsync();
        other.Adapter.Fault = FaultPoint.AfterFlip;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => s2.ApplyAsync("operator-1"));
        var oob = await other.Inner.BranchAsync(TestWorld.TargetName);
        await other.Inner.FlipAsync(TestWorld.TargetName, oob.BranchRef, "oob");
        var neither = Assert.Single(await StageRecovery.RecoverAsync(other.Ledger, other.Adapter, other.Clock));
        Assert.Equal("unresolved", neither.Resolution);
        Assert.Equal("active-matches-neither", neither.Reason);
    }

    [Fact]
    public async Task OutcomeCarriesThePendingOperation_ApplyAndRollback()
    {
        // apply reconciled as completed
        var applied = new TestWorld();
        var s1 = await applied.SimulatedSessionAsync();
        applied.Adapter.Fault = FaultPoint.AfterFlip;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => s1.ApplyAsync("operator-1"));
        var apply = Assert.Single(await StageRecovery.RecoverAsync(applied.Ledger, applied.Adapter, applied.Clock));
        Assert.Equal("apply", apply.PendingOperation);
        Assert.Equal("completed", apply.Resolution);

        // rollback reconciled as aborted — the pair that must never read as an apply
        var rolled = new TestWorld();
        var s2 = await rolled.SimulatedSessionAsync();
        await s2.ApplyAsync("operator-1");
        rolled.Adapter.Fault = FaultPoint.BeforeFlip;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => s2.RollbackAsync("operator-1"));
        var rollback = Assert.Single(await StageRecovery.RecoverAsync(rolled.Ledger, rolled.Adapter, rolled.Clock));
        Assert.Equal("rollback", rollback.PendingOperation);
        Assert.Equal("aborted", rollback.Resolution);

        // operation + resolution reconstruct the appended entry kind — the join
        // consumers used to make against a ledger snapshot
        Assert.Equal((await rolled.Ledger.ReadAllAsync()).Last().Kind,
            $"{rollback.PendingOperation}-{rollback.Resolution}");
    }

    [Fact]
    public async Task UnresolvedOutcomesStillCarryTheOperation()
    {
        // active-state-unreadable: nothing was reconciled, yet the ledger knows
        // which operation is pending — an outcome must not drop it
        var adapter = new InMemoryBackendAdapter(); // knows no target
        var store = new InMemoryLedgerStore();
        var ledger = new ReleaseLedger(store);
        await ledger.AppendAsync("rollback-started", "ghost", "sha256:ghost", "tok-ghost", "operator-1",
            "2026-07-22T00:00:00.000Z", previousStateRef: "live-ghost", newStateRef: "prior-1");

        var unreadable = Assert.Single(await StageRecovery.RecoverAsync(ledger, adapter, new FixedTimeProvider()));
        Assert.Equal("unresolved", unreadable.Resolution);
        Assert.Equal("active-state-unreadable", unreadable.Reason);
        Assert.Equal("rollback", unreadable.PendingOperation);

        // active-matches-neither: the other unresolved cell, same requirement
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();
        world.Adapter.Fault = FaultPoint.AfterFlip;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => session.ApplyAsync("operator-1"));
        var oob = await world.Inner.BranchAsync(TestWorld.TargetName);
        await world.Inner.FlipAsync(TestWorld.TargetName, oob.BranchRef, "oob");

        var neither = Assert.Single(await StageRecovery.RecoverAsync(world.Ledger, world.Adapter, world.Clock));
        Assert.Equal("active-matches-neither", neither.Reason);
        Assert.Equal("apply", neither.PendingOperation);
    }

    [Fact]
    public async Task CancellationPropagates_ItIsNotAnUnreadableActiveState()
    {
        var adapter = new CancellingAdapter();
        var store = new InMemoryLedgerStore();
        var ledger = await LedgerWithPendingAsync(store, "t", "tok-c", "live-t", "branch-1");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // a cancelled caller must not be reported as "we couldn't read the pointer"
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => StageRecovery.RecoverAsync(ledger, adapter, new FixedTimeProvider(), cts.Token));
        Assert.Single(await ledger.ReadAllAsync()); // nothing appended
    }

    /// <summary>Adapter whose ActiveStateAsync honours cancellation — the case recovery must not swallow.</summary>
    private sealed class CancellingAdapter : IBackendAdapter
    {
        public CapabilityManifest Capabilities { get; } = new(
            FlipCapability.Atomic, new Dictionary<string, IReadOnlyList<string>>());

        public Task<ActiveState> ActiveStateAsync(string target, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ActiveState("live-t", new Dictionary<string, string>()));
        }

        public Task<BranchInfo> BranchAsync(string target, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PrepareReport> PrepareAsync(string branchRef, PreparedFacets facets, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task FlipAsync(string target, string stateRef, string applyToken, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DiscardAsync(string branchRef, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
