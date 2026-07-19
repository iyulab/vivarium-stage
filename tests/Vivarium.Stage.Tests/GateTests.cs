using System.Text.Json.Nodes;
using Vivarium.Stage.Adapters;

namespace Vivarium.Stage.Tests;

public class GateTests
{
    [Fact]
    public async Task UnapprovedFingerprintIsRefused()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync(await world.ApprovedChangesetAsync(approved: false));
        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => session.ApplyAsync("operator-1"));
        Assert.Equal(RefusalReason.FingerprintGate, ex.Reason);
        // no live effect, no ledger entry
        Assert.Empty(await world.Ledger.ReadAllAsync());
        Assert.DoesNotContain("dueDate", world.Inner.ActiveWorldCanonical(TestWorld.TargetName));
    }

    [Fact]
    public async Task ApprovalForDifferentFingerprintIsRefused()
    {
        var world = new TestWorld();
        var doc = await world.ApprovedChangesetAsync(approved: false);
        doc["approvals"] = new JsonArray(new JsonObject
        {
            ["fingerprint"] = "sha256:" + new string('0', 64), // approves something else
            ["approvedBy"] = "reviewer-1",
            ["approvedAt"] = "2026-07-16T01:00:00Z",
        });
        var session = await world.SimulatedSessionAsync(doc);
        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => session.ApplyAsync("operator-1"));
        Assert.Equal(RefusalReason.FingerprintGate, ex.Reason);
    }

    [Fact]
    public async Task DriftedLiveStateIsRefusedNeverGuessed()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();

        // out-of-band mutation after the changeset recorded its base state
        world.Inner.MutateLiveOutOfBand(TestWorld.TargetName, w =>
            ((JsonObject)w["artifacts"]!)["screen-loans"] = "someone edited this live");

        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => session.ApplyAsync("operator-1"));
        Assert.Equal(RefusalReason.DriftGate, ex.Reason);
        Assert.Contains("drifted", ex.Message);
        Assert.Empty(await world.Ledger.ReadAllAsync()); // refused before write-ahead
    }

    [Fact]
    public async Task MissingBaseStateRefIsRefused()
    {
        var world = new TestWorld();
        var doc = await world.ApprovedChangesetAsync(approved: false);
        // rewrite baseState to reference a ref the live target does not expose
        var stripped = (JsonObject)doc.DeepClone();
        stripped.Remove("fingerprint");
        stripped.Remove("approvals");
        ((JsonObject)stripped["provenance"]!)["baseState"] = new JsonArray(new JsonObject
        {
            ["kind"] = "schema",
            ["ref"] = "no-such-ref",
            ["fingerprint"] = "sha256:" + new string('a', 64),
        });
        var restamped = Vivarium.Changeset.ChangesetFingerprint.Stamp(stripped);
        restamped["approvals"] = new JsonArray(new JsonObject
        {
            ["fingerprint"] = restamped["fingerprint"]!.GetValue<string>(),
            ["approvedBy"] = "reviewer-1",
            ["approvedAt"] = "2026-07-16T01:00:00Z",
        });

        var session = await world.SimulatedSessionAsync(restamped);
        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => session.ApplyAsync("operator-1"));
        Assert.Equal(RefusalReason.DriftGate, ex.Reason);
    }

    [Fact]
    public async Task MalformedBaseStateEntryIsRefusedNotCrashed()
    {
        var world = new TestWorld();
        var doc = await world.ApprovedChangesetAsync(approved: false);
        var stripped = (JsonObject)doc.DeepClone();
        stripped.Remove("fingerprint");
        stripped.Remove("approvals");
        ((JsonObject)stripped["provenance"]!)["baseState"] = new JsonArray(
            new JsonObject { ["kind"] = "schema" }); // missing ref + fingerprint
        var restamped = Vivarium.Changeset.ChangesetFingerprint.Stamp(stripped);
        restamped["approvals"] = new JsonArray(new JsonObject
        {
            ["fingerprint"] = restamped["fingerprint"]!.GetValue<string>(),
            ["approvedBy"] = "reviewer-1",
            ["approvedAt"] = "2026-07-16T01:00:00Z",
        });

        // spec 0.2 §4 enumerates the entry shape, so the SDK validator refuses
        // this at session admission (ctor) — earlier than the old in-gate guard,
        // same fail-closed outcome, still never a crash
        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => world.SimulatedSessionAsync(restamped));
        Assert.Equal(RefusalReason.InvalidChangeset, ex.Reason);
    }

    [Fact]
    public async Task ChangesetLineageEntriesDoNotTriggerDrift()
    {
        var world = new TestWorld();
        var doc = await world.ApprovedChangesetAsync(approved: false);
        var stripped = (JsonObject)doc.DeepClone();
        stripped.Remove("fingerprint");
        stripped.Remove("approvals");
        // add an authoring-lineage entry (proposal refinement) — not live state
        ((JsonArray)stripped["provenance"]!["baseState"]!).Add(new JsonObject
        {
            ["kind"] = "changeset",
            ["ref"] = "proposal-1",
            ["fingerprint"] = "sha256:" + new string('b', 64),
        });
        var restamped = Vivarium.Changeset.ChangesetFingerprint.Stamp(stripped);
        restamped["approvals"] = new JsonArray(new JsonObject
        {
            ["fingerprint"] = restamped["fingerprint"]!.GetValue<string>(),
            ["approvedBy"] = "reviewer-1",
            ["approvedAt"] = "2026-07-16T01:00:00Z",
        });

        var session = await world.SimulatedSessionAsync(restamped);
        await session.ApplyAsync("operator-1"); // lineage entry ignored by the drift gate
        Assert.Equal(SessionState.Applied, session.State);
    }

    [Fact]
    public async Task DegradedAdapterRequiresExplicitPolicyConsent()
    {
        var world = new TestWorld();
        var degraded = new DegradedAdapter(world.Adapter);
        var doc = await world.ApprovedChangesetAsync();

        var refusing = new ChangeSession(doc, TestWorld.TargetName, degraded, world.Ledger, clock: world.Clock);
        await refusing.BranchAsync();
        refusing.RecordSimulation();
        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => refusing.ApplyAsync("operator-1"));
        Assert.Equal(RefusalReason.DegradedAdapter, ex.Reason);

        var consenting = new ChangeSession(doc, TestWorld.TargetName, degraded, world.Ledger,
            new StagePolicy { AcceptDegradedAdapter = true }, world.Clock);
        await consenting.BranchAsync();
        consenting.RecordSimulation();
        await consenting.ApplyAsync("operator-1"); // consent given — proceeds
        Assert.Equal(SessionState.Applied, consenting.State);
    }

    /// <summary>Wraps the in-memory adapter but declares a non-atomic flip.</summary>
    private sealed class DegradedAdapter(IBackendAdapter inner) : IBackendAdapter
    {
        public CapabilityManifest Capabilities { get; } = new(
            FlipCapability.Degraded("flip issues N sequential writes; a crash mid-window leaves earlier writes visible"),
            inner.Capabilities.FidelityModesPerFacet);

        public Task<BranchInfo> BranchAsync(string target, CancellationToken ct = default) => inner.BranchAsync(target, ct);
        public Task<PrepareReport> PrepareAsync(string branchRef, PreparedFacets facets, CancellationToken ct = default) => inner.PrepareAsync(branchRef, facets, ct);
        public Task FlipAsync(string target, string stateRef, string applyToken, CancellationToken ct = default) => inner.FlipAsync(target, stateRef, applyToken, ct);
        public Task<ActiveState> ActiveStateAsync(string target, CancellationToken ct = default) => inner.ActiveStateAsync(target, ct);
        public Task DiscardAsync(string branchRef, CancellationToken ct = default) => inner.DiscardAsync(branchRef, ct);
    }
}
