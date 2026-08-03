namespace Vivarium.Stage.Tests;

/// <summary>
/// A flip knows what it landed. Making the caller ask "what is active now?" instead
/// answers a different question, and the two diverge exactly when it matters — under
/// a concurrent flip.
/// </summary>
public class FlipOutcomeTests
{
    [Fact]
    public async Task ApplyReportsWhatItLandedAndWhatItCameFrom()
    {
        var world = new TestWorld();
        var before = (await world.Adapter.ActiveStateAsync(TestWorld.TargetName)).StateRef;
        var session = await world.SimulatedSessionAsync();

        var outcome = await session.ApplyAsync("operator-1", applyToken: "token-1");

        Assert.Equal("apply", outcome.Operation);
        Assert.Equal(TestWorld.TargetName, outcome.Target);
        Assert.Equal(session.Fingerprint, outcome.ChangesetFingerprint);
        Assert.Equal("token-1", outcome.ApplyToken);
        Assert.Equal(before, outcome.PreviousStateRef);
        Assert.Equal((await world.Adapter.ActiveStateAsync(TestWorld.TargetName)).StateRef, outcome.NewStateRef);
    }

    [Fact]
    public async Task ApplyReportsTheTokenItGeneratedWhenTheCallerSuppliesNone()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();

        var outcome = await session.ApplyAsync("operator-1");

        Assert.False(string.IsNullOrWhiteSpace(outcome.ApplyToken));
        var started = (await world.Ledger.ReadAllAsync()).Single(e => e.Kind == "apply-started");
        Assert.Equal(started.ApplyToken, outcome.ApplyToken);
    }

    [Fact]
    public async Task RollbackReportsTheReturnPathItTook()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();
        var applied = await session.ApplyAsync("operator-1");

        var outcome = await session.RollbackAsync("operator-1");

        Assert.Equal("rollback", outcome.Operation);
        Assert.Equal(session.Fingerprint, outcome.ChangesetFingerprint);
        // a rollback undoes what the apply landed and returns to what preceded it
        Assert.Equal(applied.NewStateRef, outcome.PreviousStateRef);
        Assert.Equal(applied.PreviousStateRef, outcome.NewStateRef);
        Assert.Equal(outcome.NewStateRef, (await world.Adapter.ActiveStateAsync(TestWorld.TargetName)).StateRef);
    }

    /// <summary>
    /// The point of returning the facts rather than re-reading them: a flip that lands
    /// between the apply and the re-read makes the re-read report someone else's state.
    /// The outcome is a past fact and stays true.
    /// </summary>
    [Fact]
    public async Task AnOutcomeStaysTrueAfterAnotherFlipMovesTheTarget()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();
        var outcome = await session.ApplyAsync("operator-1");
        var landed = outcome.NewStateRef;

        // someone else moves the target after our apply returned
        await session.RollbackAsync("operator-2");

        Assert.Equal(landed, outcome.NewStateRef);
        Assert.NotEqual(landed, (await world.Adapter.ActiveStateAsync(TestWorld.TargetName)).StateRef);
    }

    [Fact]
    public async Task TheOutcomeMatchesWhatTheLedgerRecorded()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();

        var outcome = await session.ApplyAsync("operator-1");

        // same facts, one returned and one durable — a consumer joining the ledger on
        // the token must find exactly what it was handed
        var completed = (await world.Ledger.ReadAllAsync())
            .Single(e => e.Kind == "apply-completed" && e.ApplyToken == outcome.ApplyToken);
        Assert.Equal(outcome.NewStateRef, completed.NewStateRef);
        Assert.Equal(outcome.ChangesetFingerprint, completed.ChangesetFingerprint);
        Assert.Equal(outcome.Target, completed.Target);
    }
}
