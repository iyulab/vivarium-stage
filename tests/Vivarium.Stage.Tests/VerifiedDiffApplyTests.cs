using System.Text.Json.Nodes;
using Vivarium.Changeset;
using Vivarium.Stage.Adapters;

namespace Vivarium.Stage.Tests;

/// <summary>
/// spec 0.2 verified-diff@0 through the full session lifecycle: the adapter
/// resolves the diff against the branch's live base with mandatory layer-2
/// verification — and refuses fail-closed, never half-applied.
/// </summary>
public class VerifiedDiffApplyTests
{
    private static async Task<JsonObject> VerifiedDiffChangesetAsync(TestWorld world, string authoredBase)
    {
        var active = await world.Inner.ActiveStateAsync(TestWorld.TargetName);
        var doc = new ChangesetBuilder(
                intent: "Rename the loan screen title surgically",
                producedBy: "test-suite",
                createdAt: "2026-07-19T00:00:00Z",
                baseState:
                [
                    new BaseStateEntry("ui-artifact", "screen-loans", active.FacetFingerprints["screen-loans"]),
                ])
            .AddVerifiedDiffPatch("screen-loans", authoredBase, TestWorld.NewArtifact, "Adds the due-date field via diff")
            .Finalize();
        doc["approvals"] = new JsonArray(new JsonObject
        {
            ["fingerprint"] = doc["fingerprint"]!.GetValue<string>(),
            ["approvedBy"] = "reviewer-1",
            ["approvedAt"] = "2026-07-19T01:00:00Z",
        });
        return doc;
    }

    [Fact]
    public async Task VerifiedDiffPatchAppliesThroughFullLifecycle()
    {
        var world = new TestWorld();
        var doc = await VerifiedDiffChangesetAsync(world, TestWorld.BaseArtifact);
        Assert.Equal("0.2.0", doc["specVersion"]!.GetValue<string>()); // builder lifted it (§9 minimality)

        var session = await world.SimulatedSessionAsync(doc);
        await session.ApplyAsync(actor: "test-suite");

        var active = await world.Inner.ActiveStateAsync(TestWorld.TargetName);
        Assert.Equal(
            ChangesetFingerprint.OfArtifact(TestWorld.NewArtifact),
            active.FacetFingerprints["screen-loans"]); // exactly what the reviewed diff described
    }

    [Fact]
    public async Task LayerTwoMismatchRefusesFailClosed()
    {
        var world = new TestWorld();
        // authored against a base the live system never had — baseState entries
        // are correct (drift gate passes), so this isolates the adapter's
        // mandatory layer-2 verification (spec §8)
        var doc = await VerifiedDiffChangesetAsync(world, "export function LoanScreen() { /* stale */ }");

        var session = await world.SimulatedSessionAsync(doc);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyAsync("operator-1"));
        Assert.Contains("layer-2", ex.Message);

        // nothing landed — the active state still fingerprints to the original base
        var active = await world.Inner.ActiveStateAsync(TestWorld.TargetName);
        Assert.Equal(
            ChangesetFingerprint.OfArtifact(TestWorld.BaseArtifact),
            active.FacetFingerprints["screen-loans"]);
    }

    [Fact]
    public async Task VerifiedDiffAgainstUnknownArtifactRefuses()
    {
        var world = new TestWorld();
        var active = await world.Inner.ActiveStateAsync(TestWorld.TargetName);
        var doc = new ChangesetBuilder(
                intent: "Diff against a screen that does not exist",
                producedBy: "test-suite",
                createdAt: "2026-07-19T00:00:00Z",
                baseState: [new BaseStateEntry("ui-artifact", "screen-loans", active.FacetFingerprints["screen-loans"])])
            .AddVerifiedDiffPatch("screen-missing", "old\n", "new\n", "targets an unknown artifact")
            .Finalize();
        doc["approvals"] = new JsonArray(new JsonObject
        {
            ["fingerprint"] = doc["fingerprint"]!.GetValue<string>(),
            ["approvedBy"] = "reviewer-1",
            ["approvedAt"] = "2026-07-19T01:00:00Z",
        });

        var session = await world.SimulatedSessionAsync(doc);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyAsync("operator-1"));
        Assert.Contains("unknown artifact", ex.Message);
    }
}
