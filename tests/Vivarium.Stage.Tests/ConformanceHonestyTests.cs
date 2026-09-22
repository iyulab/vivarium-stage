using System.Text.Json.Nodes;
using Vivarium.Stage.Adapters;
using Vivarium.Stage.Conformance;

namespace Vivarium.Stage.Tests;

/// <summary>
/// A suite that judges adapters has to be judged on the same axis it judges them:
/// does its verdict distinguish work done from work not done? Two silences were
/// measurable here — a fixture whose patch set the adapter never reads produced a
/// report identical, byte for byte, to a correct one; and the reference adapter
/// reported every facet complete whether or not the document carried one.
/// Both read as <c>Passed</c>, which is the shape this package refuses elsewhere
/// ("completion for work not done").
/// </summary>
public class ConformanceHonestyTests
{
    private static InMemoryBackendAdapter Seeded(string target = "fixture-app")
    {
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget(target, new JsonObject
        {
            ["schema"] = new JsonObject { ["entities"] = new JsonObject() },
            ["data"] = new JsonObject(),
            ["artifacts"] = new JsonObject { ["screen-main"] = "export default function mount(root) {}" },
        });
        return adapter;
    }

    private static JsonArray UiPatch() => new(new JsonObject
    {
        ["artifactId"] = "screen-main",
        ["profile"] = "whole-artifact@0",
        ["newContent"] = "export default function mount(root) { root.textContent = 'ok'; }",
        ["explanation"] = "Conformance fixture patch.",
    });

    // ---- the door: a patch set the adapter cannot read is refused where it is written ----

    /// <summary>
    /// The exact shape the shipped getting-started example carried: a plausible key
    /// that is not a facet. Nothing downstream can tell it apart from an adapter that
    /// simply staged nothing, so it is refused at the only point that still knows it
    /// was a typo.
    /// </summary>
    [Fact]
    public void A_patch_set_with_no_recognised_facet_key_is_refused()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new ConformanceFixture(
            KnownTarget: "fixture-app",
            UnknownTarget: "no-such-target",
            Patches: new JsonObject { ["uiPatches"] = UiPatch() }));

        Assert.Equal("Patches", refusal.ParamName);
        // Names what was found, not only what was missing: "uiPatches" is one edit away
        // from "ui", and a message that does not quote it back leaves the reader guessing.
        Assert.Contains("uiPatches", refusal.Message);
        Assert.Contains("schema", refusal.Message);
        Assert.Contains("ui", refusal.Message);
        Assert.Contains("data", refusal.Message);
    }

    [Fact]
    public void A_patch_set_whose_facets_are_all_empty_is_refused()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new ConformanceFixture(
            KnownTarget: "fixture-app",
            UnknownTarget: "no-such-target",
            Patches: new JsonObject
            {
                ["schema"] = new JsonArray(),
                ["ui"] = new JsonArray(),
                ["data"] = new JsonArray(),
            }));

        Assert.Equal("Patches", refusal.ParamName);
        Assert.Contains("empty", refusal.Message);
    }

    [Fact]
    public void A_facet_key_that_is_not_an_array_is_refused()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new ConformanceFixture(
            KnownTarget: "fixture-app",
            UnknownTarget: "no-such-target",
            Patches: new JsonObject { ["ui"] = new JsonObject() }));

        Assert.Equal("Patches", refusal.ParamName);
        Assert.Contains("ui", refusal.Message);
    }

    [Fact]
    public void A_patch_set_carrying_one_real_facet_is_accepted()
    {
        var fixture = new ConformanceFixture(
            KnownTarget: "fixture-app",
            UnknownTarget: "no-such-target",
            Patches: new JsonObject { ["ui"] = UiPatch() });

        Assert.Equal(["ui"], fixture.ExercisedFacets);
    }

    [Fact]
    public void Unrecognised_keys_alongside_a_real_facet_are_still_refused()
    {
        // Tolerating the stray key would reproduce the original failure in miniature:
        // the reader believes both facets were exercised and only one was.
        var refusal = Assert.Throws<ArgumentException>(() => new ConformanceFixture(
            KnownTarget: "fixture-app",
            UnknownTarget: "no-such-target",
            Patches: new JsonObject { ["ui"] = UiPatch(), ["schemaOps"] = new JsonArray() }));

        Assert.Contains("schemaOps", refusal.Message);
    }

    // ---- the verdict: completion is reported per facet the document carried ----

    /// <summary>
    /// The reference adapter used to answer this question with a constant. A constant
    /// passes every coverage check that only counts entries, which is why the suite
    /// could not see the difference between staging a facet and not staging it.
    /// </summary>
    [Fact]
    public async Task Prepare_reports_completion_only_for_the_facets_the_document_carried()
    {
        var adapter = Seeded();
        var branch = await adapter.BranchAsync("fixture-app");

        var report = await adapter.PrepareAsync(
            branch.BranchRef,
            new PreparedFacets("sha256:probe", new JsonObject { ["ui"] = UiPatch() }));

        Assert.Equal(["ui"], report.FacetComplete.Keys.Order());
        Assert.True(report.AllComplete);
    }

    [Fact]
    public async Task A_document_carrying_two_facets_is_reported_for_both()
    {
        var adapter = Seeded();
        var branch = await adapter.BranchAsync("fixture-app");

        var report = await adapter.PrepareAsync(
            branch.BranchRef,
            new PreparedFacets("sha256:probe-2", new JsonObject
            {
                ["ui"] = UiPatch(),
                ["schema"] = new JsonArray(new JsonObject
                {
                    ["op"] = "entity.create",
                    ["entity"] = "Probe",
                    ["fields"] = new JsonArray(),
                    ["explanation"] = "Conformance probe entity.",
                }),
            }));

        Assert.Equal(["schema", "ui"], report.FacetComplete.Keys.Order());
    }

    /// <summary>
    /// The suite's own check has to have teeth: an adapter that answers with a facet
    /// the document never carried is claiming work it was not asked to do, and that
    /// is the same defect read from the other side.
    /// </summary>
    [Fact]
    public async Task The_kit_fails_an_adapter_that_reports_a_facet_the_document_did_not_carry()
    {
        var adapter = new OverclaimingAdapter(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, new ConformanceFixture(
            KnownTarget: "fixture-app",
            UnknownTarget: "no-such-target",
            Patches: new JsonObject { ["ui"] = UiPatch() },
            TokenPrefix: "overclaim"));

        var check = report.Checks.Single(c => c.Id == ConformanceIds.PrepareReportsPerFacet);
        Assert.Equal(ConformanceOutcome.Failed, check.Outcome);
        Assert.Contains("schema", check.Detail);
    }

    [Fact]
    public async Task The_reference_adapter_still_passes_every_check()
    {
        var report = await AdapterConformance.RunAsync(Seeded(), new ConformanceFixture(
            KnownTarget: "fixture-app",
            UnknownTarget: "no-such-target",
            Patches: new JsonObject { ["ui"] = UiPatch() },
            TokenPrefix: "reference"));

        Assert.True(report.AllPassed, report.ToString());
        // The verdict now says which facets it was able to speak for.
        var check = report.Checks.Single(c => c.Id == ConformanceIds.PrepareReportsPerFacet);
        Assert.Contains("ui", check.Detail);
    }

    /// <summary>Reports completion for every facet regardless of the document — the shape the kit must catch.</summary>
    private sealed class OverclaimingAdapter(InMemoryBackendAdapter inner) : IBackendAdapter
    {
        public CapabilityManifest Capabilities => inner.Capabilities;

        public Task<ActiveState> ActiveStateAsync(string target, CancellationToken ct = default) =>
            inner.ActiveStateAsync(target, ct);

        public Task<BranchInfo> BranchAsync(string target, CancellationToken ct = default) =>
            inner.BranchAsync(target, ct);

        public async Task<PrepareReport> PrepareAsync(string branchRef, PreparedFacets facets, CancellationToken ct = default)
        {
            await inner.PrepareAsync(branchRef, facets, ct);
            return new PrepareReport(new Dictionary<string, bool>
            {
                ["schema"] = true,
                ["ui"] = true,
                ["data"] = true,
            });
        }

        public Task FlipAsync(string target, string stateRef, string applyToken, CancellationToken ct = default) =>
            inner.FlipAsync(target, stateRef, applyToken, ct);

        public Task DiscardAsync(string branchRef, CancellationToken ct = default) =>
            inner.DiscardAsync(branchRef, ct);
    }
}
