using System.Text.Json.Nodes;
using Vivarium.Stage.Adapters;
using Vivarium.Stage.Conformance;

namespace Vivarium.Stage.Tests;

/// <summary>
/// The kit's spec §5.4 order probe: a document that adds a field, writes it, and removes
/// it is a no-op on schema and data in spec order, and nothing of the kind otherwise.
/// </summary>
public class ConformanceOrderTests
{
    private static InMemoryBackendAdapter SeededWithRows()
    {
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("app", (JsonObject)JsonNode.Parse("""
            {
              "schema": { "entities": { "Contact": { "fields": {
                  "id":   { "name": "id",   "type": "string" },
                  "name": { "name": "name", "type": "string" }
              }, "constraints": [] } } },
              "data": { "Contact": [
                  { "id": "c1", "name": "Ada" },
                  { "id": "c2", "name": "Borg" }
              ] },
              "artifacts": {
                "screen-main": "export default function mount(root) { root.textContent = 'Home'; }"
              }
            }
            """)!);
        return adapter;
    }

    private static JsonObject UiPatch() => new()
    {
        ["ui"] = new JsonArray(new JsonObject
        {
            ["artifactId"] = "screen-main",
            ["profile"] = "whole-artifact@0",
            ["newContent"] = "export default function mount(root) { root.textContent = 'Conformance'; }",
            ["explanation"] = "Conformance fixture patch.",
        }),
    };

    [Fact]
    public async Task Reference_adapter_passes_the_order_probe_and_every_other_check()
    {
        var report = await AdapterConformance.RunAsync(
            SeededWithRows(), new ConformanceFixture("app", "no-such-target", UiPatch(), OrderProbeEntity: "Contact"));

        Assert.True(report.AllPassed, string.Join(" | ", report.Failures.Select(f => $"{f.Id}: {f.Detail}")));
        var order = Assert.Single(report.Checks, c => c.Id == ConformanceIds.PrepareAppliesSpecOrder);
        Assert.Equal(ConformanceOutcome.Passed, order.Outcome);
    }

    [Fact]
    public async Task Without_a_probe_entity_the_order_check_says_it_was_skipped()
    {
        var report = await AdapterConformance.RunAsync(
            SeededWithRows(), new ConformanceFixture("app", "no-such-target", UiPatch()));

        var order = Assert.Single(report.Checks, c => c.Id == ConformanceIds.PrepareAppliesSpecOrder);
        Assert.Equal(ConformanceOutcome.Skipped, order.Outcome);
        Assert.Contains("OrderProbeEntity", order.Detail);
    }

    [Fact]
    public async Task The_probe_leaves_the_fixture_restored()
    {
        var adapter = SeededWithRows();
        var before = await adapter.ActiveStateAsync("app");

        await AdapterConformance.RunAsync(
            adapter, new ConformanceFixture("app", "no-such-target", UiPatch(), OrderProbeEntity: "Contact"));

        var after = await adapter.ActiveStateAsync("app");
        Assert.Equal(before.StateRef, after.StateRef);
    }

    [Fact]
    public async Task An_adapter_that_applies_every_schema_operation_first_fails_exactly_the_order_check()
    {
        var report = await AdapterConformance.RunAsync(
            new SchemaFirst(SeededWithRows()),
            new ConformanceFixture("app", "no-such-target", UiPatch(), OrderProbeEntity: "Contact"));

        var order = Assert.Single(report.Checks, c => c.Id == ConformanceIds.PrepareAppliesSpecOrder);
        Assert.Equal(ConformanceOutcome.Failed, order.Outcome);
        Assert.Equal([ConformanceIds.PrepareAppliesSpecOrder], report.Failures.Select(f => f.Id));
    }

    [Fact]
    public async Task A_probe_entity_the_target_does_not_have_is_reported_as_a_fixture_problem()
    {
        var report = await AdapterConformance.RunAsync(
            SeededWithRows(), new ConformanceFixture("app", "no-such-target", UiPatch(), OrderProbeEntity: "Nobody"));

        var order = Assert.Single(report.Checks, c => c.Id == ConformanceIds.PrepareAppliesSpecOrder);
        Assert.Equal(ConformanceOutcome.Failed, order.Outcome);
        Assert.Contains("fix the fixture", order.Detail);
    }

    /// <summary>
    /// The order the reference adapter used through 0.7.x: every schema operation, then
    /// the data. It stages the same document in two passes, schema first.
    /// </summary>
    private sealed class SchemaFirst(IBackendAdapter inner) : IBackendAdapter
    {
        public CapabilityManifest Capabilities => inner.Capabilities;
        public Task<BranchInfo> BranchAsync(string t, CancellationToken ct = default) => inner.BranchAsync(t, ct);
        public Task FlipAsync(string t, string s, string tok, CancellationToken ct = default) => inner.FlipAsync(t, s, tok, ct);
        public Task<ActiveState> ActiveStateAsync(string t, CancellationToken ct = default) => inner.ActiveStateAsync(t, ct);
        public Task DiscardAsync(string b, CancellationToken ct = default) => inner.DiscardAsync(b, ct);

        public async Task<PrepareReport> PrepareAsync(string branchRef, PreparedFacets facets, CancellationToken ct = default)
        {
            var schema = facets.Patches["schema"] as JsonArray ?? [];
            var data = facets.Patches["data"] as JsonArray ?? [];
            if (schema.Count == 0 || data.Count == 0) return await inner.PrepareAsync(branchRef, facets, ct);

            var schemaOnly = (JsonObject)facets.Patches.DeepClone();
            schemaOnly["data"] = new JsonArray();
            await inner.PrepareAsync(branchRef, new PreparedFacets(facets.ChangesetFingerprint + "-schema", schemaOnly), ct);
            var rest = (JsonObject)facets.Patches.DeepClone();
            rest["schema"] = new JsonArray();
            return await inner.PrepareAsync(branchRef, new PreparedFacets(facets.ChangesetFingerprint + "-data", rest), ct);
        }
    }
}
