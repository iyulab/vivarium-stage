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
        var entry = Drifted(ex, 0);
        Assert.Equal("ui-artifact", Str(entry, "kind"));
        Assert.Equal("screen-loans", Str(entry, "ref"));
        var expected = Str(entry, "expected");
        var actual = Str(entry, "actual");
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
            await ChangesetWithBaseRefsAsync(world, ("schema", "no-such-ref")));

        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => session.ApplyAsync("operator-1"));

        Assert.Equal(RefusalReason.DriftGate, ex.Reason);
        var entry = Drifted(ex, 0);
        Assert.Equal("no-such-ref", Str(entry, "ref"));
        // absent, not merely different — a host offering "re-base" must tell these apart
        Assert.Null(entry["actual"]);
        Assert.NotEmpty((JsonArray)ex.Details!["knownRefs"]!);
    }

    /// <summary>
    /// Re-basing is the author's job, and refusing one drifted ref at a time would
    /// make learning the full picture N round trips through the whole gate.
    /// </summary>
    [Fact]
    public async Task EveryDriftedRefIsReportedInOneRefusal()
    {
        var world = new TestWorld();
        var session = await world.SimulatedSessionAsync(await ChangesetWithBaseRefsAsync(
            world, ("schema", "no-such-ref"), ("ui-artifact", "also-missing")));

        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => session.ApplyAsync("operator-1"));

        var entries = (JsonArray)ex.Details!["drifted"]!;
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => Str((JsonObject)e!, "ref") == "no-such-ref");
        Assert.Contains(entries, e => Str((JsonObject)e!, "ref") == "also-missing");
        // the sentence stays a complete account too, not a sample of one
        Assert.Contains("no-such-ref", ex.Message);
        Assert.Contains("also-missing", ex.Message);
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

    private static JsonObject Drifted(StageRefusedException ex, int index) =>
        (JsonObject)((JsonArray)ex.Details!["drifted"]!)[index]!;

    private static async Task<JsonObject> ChangesetWithBaseRefsAsync(
        TestWorld world, params (string Kind, string Ref)[] refs)
    {
        var doc = await world.ApprovedChangesetAsync(approved: false);
        var rewritten = (JsonObject)doc.DeepClone();
        rewritten.Remove("fingerprint");
        rewritten.Remove("approvals");
        ((JsonObject)rewritten["provenance"]!)["baseState"] = new JsonArray(refs
            .Select(r => (JsonNode)new JsonObject
            {
                ["kind"] = r.Kind,
                ["ref"] = r.Ref,
                ["fingerprint"] = "sha256:" + new string('a', 64),
            }).ToArray());
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
