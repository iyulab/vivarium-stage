using Vivarium.Stage.Ledger;

namespace Vivarium.Stage.Tests;

/// <summary>
/// The fault-model §2 partial-failure matrix, executed. Every crash point must
/// reduce to "no live effect" or "old or new, never mixed" — the acceptance
/// criterion for Phase 4.b's core (half-applied is structurally impossible).
/// </summary>
public class FaultInjectionTests
{
    private static void AssertNoLiveEffect(TestWorld world, string before) =>
        Assert.Equal(before, world.Inner.ActiveWorldCanonical(TestWorld.TargetName));

    [Fact]
    public async Task F1_CrashDuringBranch_LiveUntouched_DiscardIncomplete()
    {
        var world = new TestWorld();
        var before = world.Inner.ActiveWorldCanonical(TestWorld.TargetName);
        var session = await world.SessionAsync();

        world.Adapter.Fault = FaultPoint.DuringBranch;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => session.BranchAsync());

        AssertNoLiveEffect(world, before);
        Assert.Equal(SessionState.Proposed, session.State); // resolution: retry or discard
        await session.BranchAsync(); // retry succeeds after "restart"
        Assert.Equal(SessionState.Branched, session.State);
        AssertNoLiveEffect(world, before);
    }

    [Fact]
    public async Task F2_CrashDuringPrepare_LiveUntouched_RetrySucceeds()
    {
        var world = new TestWorld();
        var before = world.Inner.ActiveWorldCanonical(TestWorld.TargetName);
        var session = await world.SimulatedSessionAsync();

        world.Adapter.Fault = FaultPoint.DuringPrepare;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => session.ApplyAsync("operator-1"));

        AssertNoLiveEffect(world, before);
        Assert.Empty(await world.Ledger.ReadAllAsync()); // crash was before write-ahead

        await session.ApplyAsync("operator-1"); // resolution: retry prepare (idempotent)
        Assert.Equal(SessionState.Applied, session.State);
    }

    [Fact]
    public async Task F3_CrashAfterPrepareBeforeFlip_LiveUntouched_RecoveryAborts_RetryLands()
    {
        var world = new TestWorld();
        var before = world.Inner.ActiveWorldCanonical(TestWorld.TargetName);
        var session = await world.SimulatedSessionAsync();

        world.Adapter.Fault = FaultPoint.BeforeFlip;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => session.ApplyAsync("operator-1", applyToken: "tok-f3"));

        AssertNoLiveEffect(world, before); // prepared but never flipped

        // ledger has started-without-completed; recovery reads the active state and aborts
        var outcomes = await StageRecovery.RecoverAsync(world.Ledger, world.Adapter, world.Clock);
        var outcome = Assert.Single(outcomes);
        Assert.Equal("aborted", outcome.Resolution);
        AssertNoLiveEffect(world, before);

        // resolution: resume — re-apply with a fresh token; prepare is idempotent
        await session.ApplyAsync("operator-1", applyToken: "tok-f3-resume");
        Assert.Equal(SessionState.Applied, session.State);
    }

    [Fact]
    public async Task F4_CrashDuringFlip_OldOrNew_NeverMixed()
    {
        // variant (a): the swap did not land — live is entirely the old world
        var worldA = new TestWorld();
        var beforeA = worldA.Inner.ActiveWorldCanonical(TestWorld.TargetName);
        var sessionA = await worldA.SimulatedSessionAsync();
        worldA.Adapter.Fault = FaultPoint.BeforeFlip;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => sessionA.ApplyAsync("operator-1"));
        Assert.Equal(beforeA, worldA.Inner.ActiveWorldCanonical(TestWorld.TargetName));

        // variant (b): the swap landed, the crash hit before control returned —
        // live is entirely the new world
        var worldB = new TestWorld();
        var beforeB = worldB.Inner.ActiveWorldCanonical(TestWorld.TargetName);
        var sessionB = await worldB.SimulatedSessionAsync();
        worldB.Adapter.Fault = FaultPoint.AfterFlip;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => sessionB.ApplyAsync("operator-1"));

        var after = worldB.Inner.ActiveWorldCanonical(TestWorld.TargetName);
        Assert.NotEqual(beforeB, after);
        Assert.Contains("dueDate", after);      // schema facet landed
        Assert.Contains("2026-08-01", after);   // data facet landed
        Assert.Contains("amount, dueDate", after); // ui facet landed — together or not at all
    }

    [Fact]
    public async Task F5_CrashAfterFlipBeforeLedgerCompletion_RecoveryReconcilesFromActiveState()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();

        world.Adapter.Fault = FaultPoint.AfterFlip;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => session.ApplyAsync("operator-1", applyToken: "tok-f5"));

        // started-without-completed is visible in the replay
        var pendingView = LedgerProjection.Replay(await world.Ledger.ReadAllAsync())[TestWorld.TargetName];
        Assert.NotNull(pendingView.PendingStarted);
        Assert.Equal("tok-f5", pendingView.PendingStarted!.ApplyToken);

        // recovery: the active state's fingerprint decides — never guesses
        var outcomes = await StageRecovery.RecoverAsync(world.Ledger, world.Adapter, world.Clock);
        Assert.Equal("completed", Assert.Single(outcomes).Resolution);

        var entries = await world.Ledger.ReadAllAsync();
        var completion = entries.Last();
        Assert.Equal("apply-completed", completion.Kind);
        Assert.True(completion.Reconciled);
        Assert.Equal("tok-f5", completion.ApplyToken);

        // reconciliation appended — the started entry is untouched (append-only)
        Assert.Equal("apply-started", entries.First(e => e.ApplyToken == "tok-f5").Kind);
        var view = LedgerProjection.Replay(entries)[TestWorld.TargetName];
        Assert.Null(view.PendingStarted);
        Assert.Equal(session.Fingerprint, view.ActiveChangesetFingerprint);
    }

    [Fact]
    public async Task F6_CrashDuringRollback_SameGuaranteesAsF4()
    {
        var world = new TestWorld();
        var preApply = world.Inner.ActiveWorldCanonical(TestWorld.TargetName);
        var session = await world.SimulatedSessionAsync();
        await session.ApplyAsync("operator-1");
        var postApply = world.Inner.ActiveWorldCanonical(TestWorld.TargetName);

        // crash after the rollback's re-flip landed, before ledger completion
        world.Adapter.Fault = FaultPoint.AfterFlip;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => session.RollbackAsync("operator-1", applyToken: "tok-f6"));

        var active = world.Inner.ActiveWorldCanonical(TestWorld.TargetName);
        Assert.True(active == preApply || active == postApply, "live state must be entirely old or entirely new");
        Assert.Equal(preApply, active); // this variant landed

        // recovery closes the rollback from the active state
        var outcomes = await StageRecovery.RecoverAsync(world.Ledger, world.Adapter, world.Clock);
        Assert.Equal("completed", Assert.Single(outcomes).Resolution);
        Assert.Equal("rollback-completed", (await world.Ledger.ReadAllAsync()).Last().Kind);
    }

    [Fact]
    public async Task FlipIsIdempotentUnderApplyToken()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();
        await session.ApplyAsync("operator-1", applyToken: "tok-idem");
        var after = world.Inner.ActiveWorldCanonical(TestWorld.TargetName);

        // recovery-style re-issue of the same flip is a no-op
        var active = await world.Inner.ActiveStateAsync(TestWorld.TargetName);
        await world.Inner.FlipAsync(TestWorld.TargetName, active.StateRef, "tok-idem");
        Assert.Equal(after, world.Inner.ActiveWorldCanonical(TestWorld.TargetName));

        // the same token for a DIFFERENT state ref is a contract violation
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Inner.FlipAsync(TestWorld.TargetName, "live-app", "tok-idem"));
    }
}
