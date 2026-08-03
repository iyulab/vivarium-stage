using System.Text.Json.Nodes;

namespace Vivarium.Stage.Tests;

/// <summary>
/// The family claim under test: <em>a host can state why without parsing the message.</em>
/// Stage had the vocabulary (<see cref="RefusalReason"/>) but not the facts — the
/// mismatched ref, the expectation, and the observation lived only inside the sentence.
/// These tests pin the facts, not the prose.
/// </summary>
public class RefusalDetailTests
{
    private static string? Str(JsonObject? details, string member) =>
        details?[member] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    [Fact]
    public async Task DriftRefusalCarriesRefExpectedAndActual()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync();
        world.Inner.MutateLiveOutOfBand(TestWorld.TargetName, w =>
            ((JsonObject)w["artifacts"]!)["screen-loans"] = "someone edited this live");

        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => session.ApplyAsync("operator-1"));

        Assert.NotNull(ex.Details);
        Assert.Equal("base-state", Str(ex.Details, "scope"));
        Assert.Equal("ui-artifact", Str(ex.Details, "kind"));
        Assert.Equal("screen-loans", Str(ex.Details, "ref"));
        var expected = Str(ex.Details, "expected");
        var actual = Str(ex.Details, "actual");
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.NotEqual(expected, actual);
        // the two fingerprints are the whole reason for the refusal, so they must be
        // readable as values — reading them out of the sentence is what this replaces
        Assert.Contains(expected, ex.Message);
        Assert.Contains(actual, ex.Message);
    }

    [Fact]
    public async Task AbsentBaseRefIsDistinguishableFromADifferentOne()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync(
            await ChangesetWithBaseRefAsync(world, "no-such-ref"));

        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => session.ApplyAsync("operator-1"));

        Assert.Equal(RefusalReason.DriftGate, ex.Reason);
        Assert.Equal("no-such-ref", Str(ex.Details, "ref"));
        // absent, not merely different — a host offering "re-base" must tell these apart
        Assert.Null(ex.Details!["actual"]);
        Assert.NotNull(ex.Details["knownRefs"] as JsonArray);
        Assert.NotEmpty((JsonArray)ex.Details["knownRefs"]!);
    }

    [Fact]
    public async Task InvalidChangesetRefusalKeepsTheValidatorsPerErrorStructure()
    {
        var world = new TestWorld();
        var doc = await world.ApprovedChangesetAsync(approved: false);
        var broken = (JsonObject)doc.DeepClone();
        broken.Remove("fingerprint");
        broken.Remove("approvals");
        ((JsonObject)broken["provenance"]!)["baseState"] = new JsonArray(
            new JsonObject { ["kind"] = "schema" }); // missing ref + fingerprint

        var ex = await Assert.ThrowsAsync<StageRefusedException>(
            () => world.SimulatedSessionAsync(Vivarium.Changeset.ChangesetFingerprint.Stamp(broken)));

        Assert.Equal(RefusalReason.InvalidChangeset, ex.Reason);
        var errors = ex.Details?["errors"] as JsonArray;
        Assert.NotNull(errors);
        Assert.NotEmpty(errors);
        // each error keeps its own path — the join into one sentence is presentation,
        // not the payload
        Assert.All(errors, e =>
        {
            Assert.False(string.IsNullOrEmpty(Str((JsonObject)e!, "path")));
            Assert.False(string.IsNullOrEmpty(Str((JsonObject)e!, "message")));
        });
        Assert.Contains(errors, e => Str((JsonObject)e!, "path")!.StartsWith("$.provenance.baseState[0]"));
    }

    [Fact]
    public async Task StateTransitionRefusalCarriesExpectedAndActualState()
    {
        var world = new TestWorld();
        var session = await world.SessionAsync();

        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => session.ApplyAsync("operator-1"));

        Assert.Equal(RefusalReason.InvalidStateTransition, ex.Reason);
        Assert.Equal("apply", Str(ex.Details, "operation"));
        Assert.Equal(nameof(SessionState.Simulated), Str(ex.Details, "expectedState"));
        Assert.Equal(nameof(SessionState.Proposed), Str(ex.Details, "actualState"));
    }

    /// <summary>
    /// Restraint is part of the design: a refusal whose only fact the caller already
    /// holds gets no payload. The fingerprint gate refuses a fingerprint the caller
    /// just submitted — repeating it back would inflate the surface without informing
    /// anyone.
    /// </summary>
    [Fact]
    public async Task RefusalsWithNoActionableFactCarryNoDetails()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync(await world.ApprovedChangesetAsync(approved: false));

        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => session.ApplyAsync("operator-1"));

        Assert.Equal(RefusalReason.FingerprintGate, ex.Reason);
        Assert.Null(ex.Details);
    }

    [Fact]
    public async Task DetailsAreASnapshotNotALiveHandle()
    {
        var payload = new JsonObject { ["ref"] = "screen-loans" };
        var ex = new StageRefusedException(RefusalReason.DriftGate, "refused", payload);

        payload["ref"] = "mutated-after-throw";

        Assert.Equal("screen-loans", Str(ex.Details, "ref"));
        await Task.CompletedTask;
    }

    private static async Task<JsonObject> ChangesetWithBaseRefAsync(TestWorld world, string baseRef)
    {
        var doc = await world.ApprovedChangesetAsync(approved: false);
        var rewritten = (JsonObject)doc.DeepClone();
        rewritten.Remove("fingerprint");
        rewritten.Remove("approvals");
        ((JsonObject)rewritten["provenance"]!)["baseState"] = new JsonArray(new JsonObject
        {
            ["kind"] = "schema",
            ["ref"] = baseRef,
            ["fingerprint"] = "sha256:" + new string('a', 64),
        });
        var restamped = Vivarium.Changeset.ChangesetFingerprint.Stamp(rewritten);
        restamped["approvals"] = new JsonArray(new JsonObject
        {
            ["fingerprint"] = restamped["fingerprint"]!.GetValue<string>(),
            ["approvedBy"] = "reviewer-1",
            ["approvedAt"] = "2026-07-16T01:00:00Z",
        });
        return restamped;
    }
}
