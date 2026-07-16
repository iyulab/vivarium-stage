using System.Text.Json.Nodes;

namespace Vivarium.Stage.Tests;

public class StateMachineTests
{
    [Fact]
    public async Task HappyPathProposedToApplied()
    {
        var world = new TestWorld();
        var session = await world.SessionAsync();
        Assert.Equal(SessionState.Proposed, session.State);

        var branch = await session.BranchAsync();
        Assert.Equal(SessionState.Branched, session.State);
        Assert.Equal("full", branch.Fidelity.PerFacet["schema"].Mode);

        session.RecordSimulation();
        Assert.Equal(SessionState.Simulated, session.State);

        await session.ApplyAsync("operator-1");
        Assert.Equal(SessionState.Applied, session.State);

        // the live world is the prepared branch now
        var active = await world.Inner.ActiveStateAsync(TestWorld.TargetName);
        Assert.Equal(branch.BranchRef, active.StateRef);
        Assert.Contains(TestWorld.NewArtifact.Split('\n')[1],
            world.Inner.ActiveWorldCanonical(TestWorld.TargetName));
    }

    [Fact]
    public async Task RollbackReturnsToPreApplyState()
    {
        var world = new TestWorld();
        var before = world.Inner.ActiveWorldCanonical(TestWorld.TargetName);
        var session = await world.SimulatedSessionAsync();
        await session.ApplyAsync("operator-1");
        Assert.NotEqual(before, world.Inner.ActiveWorldCanonical(TestWorld.TargetName));

        await session.RollbackAsync("operator-1");
        Assert.Equal(SessionState.RolledBack, session.State);
        Assert.Equal(before, world.Inner.ActiveWorldCanonical(TestWorld.TargetName));
    }

    [Fact]
    public async Task OutOfOrderTransitionsAreRefused()
    {
        var world = new TestWorld();
        var session = await world.SessionAsync();

        // apply before branch/simulate
        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => session.ApplyAsync("x"));
        Assert.Equal(RefusalReason.InvalidStateTransition, ex.Reason);

        // simulate before branch
        Assert.Throws<StageRefusedException>(() => session.RecordSimulation());

        // rollback before apply
        await Assert.ThrowsAsync<StageRefusedException>(() => session.RollbackAsync("x"));

        // double branch
        await session.BranchAsync();
        await Assert.ThrowsAsync<StageRefusedException>(() => session.BranchAsync());
    }

    [Fact]
    public async Task DiscardIsAPreApplyExit()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();
        await session.DiscardAsync();
        Assert.Equal(SessionState.Discarded, session.State);

        // live state untouched by the whole branched lifecycle
        Assert.Contains("amount", world.Inner.ActiveWorldCanonical(TestWorld.TargetName));
        Assert.DoesNotContain("dueDate", world.Inner.ActiveWorldCanonical(TestWorld.TargetName));

        // discard after apply is refused
        var applied = await world.SimulatedSessionAsync();
        await applied.ApplyAsync("operator-1");
        await Assert.ThrowsAsync<StageRefusedException>(() => applied.DiscardAsync());
    }

    [Fact]
    public async Task InvalidChangesetIsRefusedAtAdmission()
    {
        var world = new TestWorld();
        var doc = await world.ApprovedChangesetAsync();
        doc["vendorExtra"] = true; // closed model violation
        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => world.SessionAsync(doc));
        Assert.Equal(RefusalReason.InvalidChangeset, ex.Reason);

        var unstamped = await world.ApprovedChangesetAsync();
        unstamped.Remove("fingerprint");
        var ex2 = await Assert.ThrowsAsync<StageRefusedException>(() => world.SessionAsync(unstamped));
        Assert.Equal(RefusalReason.FingerprintGate, ex2.Reason);
    }
}
