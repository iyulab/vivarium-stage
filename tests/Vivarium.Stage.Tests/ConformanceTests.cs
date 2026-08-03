using System.Text.Json.Nodes;
using Vivarium.Stage.Adapters;
using Vivarium.Stage.Conformance;

namespace Vivarium.Stage.Tests;

/// <summary>
/// The conformance kit's own tests. A kit that only proves the reference
/// adapter passes proves nothing — every check gets a deliberately violating
/// adapter that must fail exactly that check and nothing else.
/// </summary>
public class ConformanceTests
{
    private static JsonObject Patches() => new()
    {
        ["uiPatches"] = new JsonArray(new JsonObject
        {
            ["artifactId"] = "screen-main",
            ["profile"] = "whole-artifact@0",
            ["newContent"] = "export default function mount(root) { root.textContent = 'Conformance'; }",
        }),
    };

    private static InMemoryBackendAdapter Seeded(string target = "app")
    {
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget(target, new JsonObject
        {
            ["schema"] = new JsonObject { ["entities"] = new JsonObject() },
            ["data"] = new JsonObject(),
            ["artifacts"] = new JsonObject
            {
                ["screen-main"] = "export default function mount(root) { root.textContent = 'Home'; }",
            },
        });
        return adapter;
    }

    private static ConformanceFixture Fixture(string target = "app") =>
        new(target, "no-such-target", Patches());

    // ---- the reference adapter is the first thing that must pass ----

    [Fact]
    public async Task Reference_adapter_passes_every_check()
    {
        var report = await AdapterConformance.RunAsync(Seeded(), Fixture());

        Assert.True(
            report.AllPassed,
            "reference adapter failed: " + string.Join(" | ", report.Failures.Select(f => $"{f.Id}: {f.Detail}")));
        Assert.Empty(report.Failures);
        Assert.All(report.Checks, c => Assert.False(string.IsNullOrWhiteSpace(c.Id)));
    }

    [Fact]
    public async Task Every_check_cites_the_clause_it_enforces()
    {
        var report = await AdapterConformance.RunAsync(Seeded(), Fixture());

        // The id IS the traceability: a check with no clause is not a check.
        Assert.All(report.Checks, c => Assert.Contains("§", c.Id));
        Assert.Equal(report.Checks.Select(c => c.Id).Distinct().Count(), report.Checks.Count);
    }

    [Fact]
    public async Task Restore_check_runs_last_and_returns_the_fixture_to_its_original_state()
    {
        var adapter = Seeded();
        var before = (await adapter.ActiveStateAsync("app")).StateRef;

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        Assert.Equal(ConformanceIds.FlipRestoresPreviousState, report.Checks[^1].Id);
        Assert.Equal(before, (await adapter.ActiveStateAsync("app")).StateRef);
    }

    // ---- violations: each must fail its own check ----

    [Fact]
    public async Task Fabricating_an_active_state_for_an_unknown_target_fails()
    {
        // The contract's most emphasized MUST — "throw, never invent". Returning
        // a fabricated pointer is what lets a guess reach the gates.
        var adapter = new FabricatesUnknownTarget(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        AssertFailedOnly(report, ConformanceIds.UnknownTargetThrows);
    }

    [Fact]
    public async Task Accepting_a_malformed_data_operation_fails()
    {
        // Silently staging less is the failure the check is really after: the
        // adapter reports completion for work it did not do, and a false input
        // goes under the flip.
        var adapter = new SwallowsMalformedOps(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        Assert.Contains(report.Failures, f => f.Id == ConformanceIds.PrepareRefusesMalformedDataOp);
    }

    [Fact]
    public async Task Accepting_a_malformed_schema_operation_fails()
    {
        // The clause covers every facet; so must the kit. An operation outside the
        // vocabulary that stages nothing while prepare reports completion is the
        // same silence, one facet over.
        var adapter = new SwallowsMalformedOps(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        Assert.Contains(report.Failures, f => f.Id == ConformanceIds.PrepareRefusesMalformedSchemaOp);
    }

    [Fact]
    public async Task Faulting_on_a_malformed_data_operation_fails_too()
    {
        // Throwing is necessary but not sufficient — a null-reference fault is an
        // accident, and §Error taxonomy asks for a reason.
        var adapter = new FaultsOnMalformedDataOps(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        var check = report.Failures.Single(f => f.Id == ConformanceIds.PrepareRefusesMalformedDataOp);
        Assert.Contains("accident", check.Detail);
    }

    [Fact]
    public async Task Branch_without_a_fidelity_declaration_fails()
    {
        var adapter = new EmptyFidelity(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        Assert.Contains(report.Failures, f => f.Id == ConformanceIds.BranchDeclaresFidelity);
    }

    [Fact]
    public async Task Subset_fidelity_without_a_selection_rule_fails()
    {
        var adapter = new SubsetWithoutRule(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        Assert.Contains(report.Failures, f => f.Id == ConformanceIds.SubsetRequiresSelectionRule);
    }

    [Fact]
    public async Task Reusing_a_flip_token_for_a_different_state_ref_must_throw()
    {
        var adapter = new TokenReuseSucceeds(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        AssertFailedOnly(report, ConformanceIds.TokenReuseDifferentStateThrows);
    }

    [Fact]
    public async Task Non_idempotent_flip_under_the_same_token_fails()
    {
        var adapter = new ReflipThrows(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        Assert.Contains(report.Failures, f => f.Id == ConformanceIds.FlipIdempotentUnderToken);
    }

    [Fact]
    public async Task Branch_with_a_live_effect_fails()
    {
        var adapter = new BranchMutatesLive(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        Assert.Contains(report.Failures, f => f.Id == ConformanceIds.BranchHasNoLiveEffect);
    }

    [Fact]
    public async Task Non_deterministic_active_state_fingerprints_fail()
    {
        var adapter = new DriftingFingerprints(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        Assert.Contains(report.Failures, f => f.Id == ConformanceIds.ActiveStateDeterministic);
    }

    [Fact]
    public async Task Degraded_flip_without_a_degradation_description_fails()
    {
        var adapter = new DishonestDegradation(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        AssertFailedOnly(report, ConformanceIds.DegradationDeclared);
    }

    [Fact]
    public async Task Prepare_that_reports_no_facets_fails()
    {
        var adapter = new EmptyPrepareReport(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        Assert.Contains(report.Failures, f => f.Id == ConformanceIds.PrepareReportsPerFacet);
    }

    // ---- honest adapters must not be failed by over-strict checks ----

    [Fact]
    public async Task Manifest_that_omits_a_facet_is_not_failed_and_says_what_it_could_not_verify()
    {
        // adapter-api §2 does not require the manifest to enumerate every facet,
        // so a facet the manifest is silent about is unverifiable, not wrong.
        // The facets it CAN check still pass — but the check must name what it
        // skipped rather than reporting broader assurance than it gave.
        var adapter = new ManifestOmitsUiFacet(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        var check = report.Checks.Single(c => c.Id == ConformanceIds.BranchModesWithinManifest);
        Assert.Equal(ConformanceOutcome.Passed, check.Outcome);
        Assert.Contains("ui", check.Detail);
        Assert.True(report.AllPassed, report.ToString());
    }

    [Fact]
    public async Task Discard_is_checked_on_a_branch_that_was_never_flipped()
    {
        // A flipped branch IS the live state; refusing to discard it is correct
        // adapter behaviour, so checking discard there would fail honest adapters.
        var report = await AdapterConformance.RunAsync(Seeded(), Fixture());

        var check = report.Checks.Single(c => c.Id == ConformanceIds.DiscardHasNoLiveEffect);
        Assert.Equal(ConformanceOutcome.Passed, check.Outcome);
    }

    [Fact]
    public async Task Throwing_any_exception_type_satisfies_the_unspecified_error_taxonomy()
    {
        // §Error taxonomy: "The exception type is not specified in v0."
        // Asserting a type would narrow the contract to the reference adapter.
        var adapter = new ThrowsCustomExceptionType(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        Assert.DoesNotContain(report.Failures, f => f.Id == ConformanceIds.UnknownTargetThrows);
        Assert.DoesNotContain(report.Failures, f => f.Id == ConformanceIds.TokenReuseDifferentStateThrows);
        // Asserting the whole run passes is what catches a decorator whose own
        // token guard swallows the restore flip — absence from Failures on two
        // ids would not notice the fixture failing to come home.
        Assert.True(report.AllPassed, report.ToString());
    }

    private static void AssertFailedOnly(ConformanceReport report, string expectedId)
    {
        Assert.Contains(report.Failures, f => f.Id == expectedId);
        var collateral = report.Failures.Where(f => f.Id != expectedId).Select(f => f.Id).ToArray();
        Assert.True(collateral.Length == 0, "unexpected collateral failures: " + string.Join(", ", collateral));
    }

    // ---- violating adapters (decorators over the reference implementation) ----

    private class Passthrough(IBackendAdapter inner) : IBackendAdapter
    {
        protected readonly IBackendAdapter Inner = inner;
        public virtual CapabilityManifest Capabilities => Inner.Capabilities;
        public virtual Task<BranchInfo> BranchAsync(string t, CancellationToken ct = default) => Inner.BranchAsync(t, ct);
        public virtual Task<PrepareReport> PrepareAsync(string b, PreparedFacets f, CancellationToken ct = default) => Inner.PrepareAsync(b, f, ct);
        public virtual Task FlipAsync(string t, string s, string tok, CancellationToken ct = default) => Inner.FlipAsync(t, s, tok, ct);
        public virtual Task<ActiveState> ActiveStateAsync(string t, CancellationToken ct = default) => Inner.ActiveStateAsync(t, ct);
        public virtual Task DiscardAsync(string b, CancellationToken ct = default) => Inner.DiscardAsync(b, ct);
    }

    private sealed class FabricatesUnknownTarget(IBackendAdapter inner) : Passthrough(inner)
    {
        public override async Task<ActiveState> ActiveStateAsync(string t, CancellationToken ct = default)
        {
            try { return await Inner.ActiveStateAsync(t, ct); }
            catch (InvalidOperationException) { return new ActiveState("invented", new Dictionary<string, string>()); }
        }
    }

    private sealed class SwallowsMalformedOps(IBackendAdapter inner) : Passthrough(inner)
    {
        public override async Task<PrepareReport> PrepareAsync(
            string branchRef, PreparedFacets facets, CancellationToken ct = default)
        {
            try { return await Inner.PrepareAsync(branchRef, facets, ct); }
            catch (InvalidOperationException)
            {
                return new PrepareReport(new Dictionary<string, bool>
                {
                    ["schema"] = true, ["ui"] = true, ["data"] = true,
                });
            }
        }
    }

    private sealed class FaultsOnMalformedDataOps(IBackendAdapter inner) : Passthrough(inner)
    {
        public override async Task<PrepareReport> PrepareAsync(
            string branchRef, PreparedFacets facets, CancellationToken ct = default)
        {
            try { return await Inner.PrepareAsync(branchRef, facets, ct); }
            catch (InvalidOperationException) { throw new NullReferenceException(); }
        }
    }

    private sealed class EmptyFidelity(IBackendAdapter inner) : Passthrough(inner)
    {
        public override async Task<BranchInfo> BranchAsync(string t, CancellationToken ct = default)
        {
            var info = await Inner.BranchAsync(t, ct);
            return info with { Fidelity = new FidelityDeclaration(new Dictionary<string, FacetFidelity>(), []) };
        }
    }

    private sealed class SubsetWithoutRule(IBackendAdapter inner) : Passthrough(inner)
    {
        public override CapabilityManifest Capabilities => new(
            FlipCapability.Atomic,
            new Dictionary<string, IReadOnlyList<string>> { ["schema"] = ["subset"], ["data"] = ["subset"], ["ui"] = ["subset"] });

        public override async Task<BranchInfo> BranchAsync(string t, CancellationToken ct = default)
        {
            var info = await Inner.BranchAsync(t, ct);
            var perFacet = info.Fidelity.PerFacet.ToDictionary(
                kv => kv.Key,
                kv => new FacetFidelity("subset", kv.Value.Method)); // SelectionRule omitted
            return info with { Fidelity = new FidelityDeclaration(perFacet, info.Fidelity.KnownDifferences) };
        }
    }

    private sealed class TokenReuseSucceeds(IBackendAdapter inner) : Passthrough(inner)
    {
        private readonly Dictionary<string, string> _seen = [];
        public override async Task FlipAsync(string t, string s, string tok, CancellationToken ct = default)
        {
            if (_seen.TryGetValue(tok, out var prior) && prior != s) return; // silently accepts — the violation
            _seen[tok] = s;
            await Inner.FlipAsync(t, s, tok, ct);
        }
    }

    private sealed class ReflipThrows(IBackendAdapter inner) : Passthrough(inner)
    {
        private readonly HashSet<string> _used = [];
        public override async Task FlipAsync(string t, string s, string tok, CancellationToken ct = default)
        {
            if (!_used.Add(tok)) throw new InvalidOperationException("token already used");
            await Inner.FlipAsync(t, s, tok, ct);
        }
    }

    private sealed class BranchMutatesLive(InMemoryBackendAdapter inner) : Passthrough(inner)
    {
        private readonly InMemoryBackendAdapter _real = inner;
        public override async Task<BranchInfo> BranchAsync(string t, CancellationToken ct = default)
        {
            var info = await Inner.BranchAsync(t, ct);
            _real.MutateLiveOutOfBand(t, w => w["artifacts"]!["screen-main"] = "leaked during branch");
            return info;
        }
    }

    private sealed class DriftingFingerprints(IBackendAdapter inner) : Passthrough(inner)
    {
        private int _n;
        public override async Task<ActiveState> ActiveStateAsync(string t, CancellationToken ct = default)
        {
            var state = await Inner.ActiveStateAsync(t, ct);
            var drifted = state.FacetFingerprints.ToDictionary(kv => kv.Key, kv => kv.Value + "-" + _n++);
            return state with { FacetFingerprints = drifted };
        }
    }

    private sealed class DishonestDegradation(IBackendAdapter inner) : Passthrough(inner)
    {
        public override CapabilityManifest Capabilities =>
            new(new FlipCapability(false, null), Inner.Capabilities.FidelityModesPerFacet);
    }

    private sealed class EmptyPrepareReport(IBackendAdapter inner) : Passthrough(inner)
    {
        public override async Task<PrepareReport> PrepareAsync(string b, PreparedFacets f, CancellationToken ct = default)
        {
            await Inner.PrepareAsync(b, f, ct);
            return new PrepareReport(new Dictionary<string, bool>());
        }
    }

    private sealed class ManifestOmitsUiFacet(IBackendAdapter inner) : Passthrough(inner)
    {
        public override CapabilityManifest Capabilities => new(
            Inner.Capabilities.Flip,
            new Dictionary<string, IReadOnlyList<string>> { ["schema"] = ["full"], ["data"] = ["full"] });
    }

    private sealed class ThrowsCustomExceptionType(IBackendAdapter inner) : Passthrough(inner)
    {
        private sealed class BackendUnreachable(string m) : Exception(m);
        private readonly Dictionary<string, string> _seen = [];

        public override async Task<ActiveState> ActiveStateAsync(string t, CancellationToken ct = default)
        {
            try { return await Inner.ActiveStateAsync(t, ct); }
            catch (InvalidOperationException e) { throw new BackendUnreachable(e.Message); }
        }

        public override async Task FlipAsync(string t, string s, string tok, CancellationToken ct = default)
        {
            if (_seen.TryGetValue(tok, out var prior) && prior != s) throw new BackendUnreachable("token bound to another state");
            _seen[tok] = s;
            await Inner.FlipAsync(t, s, tok, ct);
        }
    }
}
