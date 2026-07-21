using Vivarium.Stage.Ledger;

namespace Vivarium.Stage.Tests;

/// <summary>
/// Post-restart rehydration: an Applied session is reconstructed from the
/// ledger and the live active state — never asserted. This is what makes
/// "every apply has a return path" (fixed principle 4) survive a process
/// restart instead of holding only within one process lifetime.
/// </summary>
public class RehydrationTests
{
    /// <summary>Simulate a process restart: same durable store and backend, fresh in-memory objects.</summary>
    private static ReleaseLedger RestartedLedger(TestWorld world) => new(world.Store);

    [Fact]
    public async Task RehydratedAppliedSession_CanRollBack()
    {
        var world = new TestWorld();
        var preApply = world.Inner.ActiveWorldCanonical(TestWorld.TargetName);
        var changeset = await world.ApprovedChangesetAsync();
        var session = await world.SimulatedSessionAsync(changeset);
        await session.ApplyAsync("operator-1");

        // "restart": rebuild the session from durable state only
        var rehydrated = await ChangeSession.RehydrateAppliedAsync(
            changeset, TestWorld.TargetName, world.Adapter, RestartedLedger(world), clock: world.Clock);
        Assert.Equal(SessionState.Applied, rehydrated.State);
        Assert.Equal(session.Fingerprint, rehydrated.Fingerprint);

        await rehydrated.RollbackAsync("operator-2");
        Assert.Equal(SessionState.RolledBack, rehydrated.State);
        Assert.Equal(preApply, world.Inner.ActiveWorldCanonical(TestWorld.TargetName));
        Assert.Equal("rollback-completed", (await world.Ledger.ReadAllAsync()).Last().Kind);
    }

    [Fact]
    public async Task Rehydrate_RefusesWhenChangesetWasNeverApplied()
    {
        var world = new TestWorld();
        var changeset = await world.ApprovedChangesetAsync();

        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => ChangeSession.RehydrateAppliedAsync(
            changeset, TestWorld.TargetName, world.Adapter, world.Ledger, clock: world.Clock));
        Assert.Equal(RefusalReason.InvalidStateTransition, ex.Reason);
    }

    [Fact]
    public async Task Rehydrate_RefusesAfterRollback()
    {
        var world = new TestWorld();
        var changeset = await world.ApprovedChangesetAsync();
        var session = await world.SimulatedSessionAsync(changeset);
        await session.ApplyAsync("operator-1");
        await session.RollbackAsync("operator-1");

        // the latest completed entry is the rollback — there is nothing to return from
        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => ChangeSession.RehydrateAppliedAsync(
            changeset, TestWorld.TargetName, world.Adapter, RestartedLedger(world), clock: world.Clock));
        Assert.Equal(RefusalReason.InvalidStateTransition, ex.Reason);
    }

    [Fact]
    public async Task Rehydrate_RefusesWhenLiveStateDisagreesWithLedger()
    {
        var world = new TestWorld();
        var changeset = await world.ApprovedChangesetAsync();
        var session = await world.SimulatedSessionAsync(changeset);
        await session.ApplyAsync("operator-1");

        // out-of-band: live was flipped elsewhere — the ledger no longer describes it
        var oob = await world.Inner.BranchAsync(TestWorld.TargetName);
        await world.Inner.FlipAsync(TestWorld.TargetName, oob.BranchRef, "oob-token");

        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => ChangeSession.RehydrateAppliedAsync(
            changeset, TestWorld.TargetName, world.Adapter, RestartedLedger(world), clock: world.Clock));
        Assert.Equal(RefusalReason.DriftGate, ex.Reason);
    }

    [Fact]
    public async Task Rehydrate_RefusesWhilePendingStartedIsUnreconciled()
    {
        var world = new TestWorld();
        var changeset = await world.ApprovedChangesetAsync();
        var session = await world.SimulatedSessionAsync(changeset);

        // crash after flip, before ledger completion — recovery has not run yet
        world.Adapter.Fault = FaultPoint.AfterFlip;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => session.ApplyAsync("operator-1"));

        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => ChangeSession.RehydrateAppliedAsync(
            changeset, TestWorld.TargetName, world.Adapter, RestartedLedger(world), clock: world.Clock));
        Assert.Equal(RefusalReason.InvalidStateTransition, ex.Reason);

        // after recovery reconciles, rehydration succeeds
        await StageRecovery.RecoverAsync(RestartedLedger(world), world.Adapter, world.Clock);
        var rehydrated = await ChangeSession.RehydrateAppliedAsync(
            changeset, TestWorld.TargetName, world.Adapter, RestartedLedger(world), clock: world.Clock);
        Assert.Equal(SessionState.Applied, rehydrated.State);
    }
}
