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
    // The facet key is the changeset's own: "ui". This helper carried "uiPatches" until
    // 2026-09, which the adapter never reads — so every prepare check in this file ran
    // against a document that staged nothing, and passed. That is the exact silence the
    // fixture now refuses at construction.
    private static JsonObject Patches() => new()
    {
        ["ui"] = new JsonArray(new JsonObject
        {
            ["artifactId"] = "screen-main",
            ["profile"] = "whole-artifact@0",
            ["newContent"] = "export default function mount(root) { root.textContent = 'Conformance'; }",
            ["explanation"] = "Conformance fixture patch.",
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
    public async Task Accepting_a_well_formed_operation_on_an_absent_target_fails()
    {
        // Not every false input is malformed. Removal is where the quiet success is
        // most convincing — the target is supposed to end up absent, so an operation
        // naming something nobody has looks exactly like one that worked.
        var adapter = new AcceptsRemovalOfAnAbsentTarget(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        AssertFailedOnly(report, ConformanceIds.PrepareRefusesAbsentSchemaTarget);
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
    public async Task Refusing_correctly_but_spoiling_the_branch_fails()
    {
        // A host that receives a document refusal tells the author to fix the document
        // and retry. This adapter refuses correctly, with a reason, every time; it
        // simply cannot be prepared again afterwards, so the advice the refusal
        // enables goes nowhere.
        var adapter = new SpoilsTheBranchOnRefusal(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        var check = report.Failures.Single(f => f.Id == ConformanceIds.RefusalLeavesBranchPreparable);
        // This adapter's residue makes the retry throw outright; a different one
        // would report a changed completion instead. Both are the same violation and
        // the check names which of the two it saw.
        Assert.Contains("did not survive being refused", check.Detail);

        // The refusal clauses themselves stay green — that is what makes this check
        // load-bearing rather than a second spelling of the ones above.
        Assert.DoesNotContain(report.Failures, f => f.Id == ConformanceIds.PrepareRefusesMalformedDataOp);
        Assert.DoesNotContain(report.Failures, f => f.Id == ConformanceIds.PrepareRefusesMalformedSchemaOp);
        Assert.DoesNotContain(report.Failures, f => f.Id == ConformanceIds.PrepareRefusesAbsentSchemaTarget);
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
    public async Task Throwing_a_foreign_exception_type_fails_the_refusal_clauses()
    {
        // Through 0.8 the type was unspecified and any throw passed. A host then had to
        // tell "no such target" from "the backend broke" by which call it came out of,
        // or by parsing the message. §6 now names the refusal: throwing is half of the
        // clause, and saying it was a refusal is the other half.
        var adapter = new ThrowsCustomExceptionType(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        var failed = report.Failures.Select(f => f.Id).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { ConformanceIds.UnknownTargetThrows, ConformanceIds.TokenReuseDifferentStateThrows }.Order(StringComparer.Ordinal),
            failed);
        Assert.All(report.Failures, f => Assert.Contains("AdapterRefusedException", f.Detail));
        // Everything else passes, including the restore — which is what catches a
        // decorator whose own token guard swallows the restore flip.
        Assert.Equal(ConformanceOutcome.Passed,
            report.Checks.Single(c => c.Id == ConformanceIds.FlipRestoresPreviousState).Outcome);
    }

    [Fact]
    public async Task Refusing_under_the_wrong_reason_fails()
    {
        var adapter = new RefusesUnknownTargetAsUnknownRef(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        var check = report.Failures.Single();
        Assert.Equal(ConformanceIds.UnknownTargetThrows, check.Id);
        Assert.Contains("refused as UnknownRef, but this clause is UnknownTarget", check.Detail);
    }

    [Fact]
    public async Task A_document_refusal_that_does_not_say_where_fails()
    {
        // "Fix the document" with no location costs the author a round trip per error.
        var adapter = new RefusesDocumentsWithoutLocation(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        Assert.Equal(
            new[]
            {
                ConformanceIds.PrepareRefusesAbsentSchemaTarget,
                ConformanceIds.PrepareRefusesMalformedDataOp,
                ConformanceIds.PrepareRefusesMalformedSchemaOp,
            },
            report.Failures.Select(f => f.Id).Order(StringComparer.Ordinal));
        Assert.All(report.Failures, f => Assert.Contains("Details.errors", f.Detail));
    }

    [Fact]
    public async Task Reporting_completion_while_staging_nothing_fails_on_what_landed()
    {
        // The adapter says every carried facet is complete and does nothing. Every
        // check that compares what an adapter says with what the document carried is
        // satisfied by that; only observing the flipped-to state catches it.
        var adapter = new ReportsCompletionStagesNothing(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        AssertFailedOnly(report, ConformanceIds.FlipLandsPreparedState);
        var check = report.Failures.Single();
        Assert.Contains("'screen-main' fingerprints to", check.Detail);
    }

    [Fact]
    public async Task A_schema_carrying_fixture_is_checked_for_movement_and_data_is_named_as_unverified()
    {
        var patches = Patches();
        patches["schema"] = new JsonArray(new JsonObject
        {
            ["op"] = "entity.create",
            ["entity"] = "Order",
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "id", ["type"] = "string" }),
            ["explanation"] = "Conformance fixture entity.",
        });
        patches["data"] = new JsonArray(new JsonObject
        {
            ["id"] = "seed-orders",
            ["explanation"] = "Conformance fixture rows.",
            ["operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "insert", ["entity"] = "Order", ["values"] = new JsonObject { ["id"] = "o-1" },
            }),
        });

        var report = await AdapterConformance.RunAsync(Seeded(), new ConformanceFixture("app", "no-such-target", patches));

        Assert.True(report.AllPassed, report.ToString());
        var check = report.Checks.Single(c => c.Id == ConformanceIds.FlipLandsPreparedState);
        Assert.Equal(ConformanceOutcome.Passed, check.Outcome);
        Assert.Contains("data: a predicate that selects no rows", check.Detail);
        Assert.DoesNotContain("schema:", check.Detail);
    }

    [Fact]
    public async Task A_document_refused_part_way_that_leaves_what_landed_fails()
    {
        // Each of the suite's other refusal probes is refused at its first operation, so
        // an adapter that stages in place passes all of them. This one lands an entity,
        // refuses on the next operation, and keeps the entity.
        var adapter = new KeepsWhatLandedBeforeARefusal(Seeded());

        var report = await AdapterConformance.RunAsync(adapter, Fixture());

        AssertFailedOnly(report, ConformanceIds.RefusalLeavesNoResidue);
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
            catch (AdapterRefusedException) { return new ActiveState("invented", new Dictionary<string, string>()); }
        }
    }

    private sealed class SwallowsMalformedOps(IBackendAdapter inner) : Passthrough(inner)
    {
        public override async Task<PrepareReport> PrepareAsync(
            string branchRef, PreparedFacets facets, CancellationToken ct = default)
        {
            try { return await Inner.PrepareAsync(branchRef, facets, ct); }
            catch (AdapterRefusedException)
            {
                return new PrepareReport(new Dictionary<string, bool>
                {
                    ["schema"] = true, ["ui"] = true, ["data"] = true,
                });
            }
        }
    }

    /// <summary>
    /// The shape this check exists for: the operation is well formed, its target is
    /// not there, and the adapter reports the facet complete anyway. Only removal is
    /// intercepted, so the failure lands on the absent-target clause and nowhere else.
    /// </summary>
    private sealed class AcceptsRemovalOfAnAbsentTarget(IBackendAdapter inner) : Passthrough(inner)
    {
        public override async Task<PrepareReport> PrepareAsync(
            string branchRef, PreparedFacets facets, CancellationToken ct = default)
        {
            var schema = facets.Patches["schema"] as JsonArray ?? [];
            if (schema.OfType<JsonObject>().Any(o => o["op"]?.GetValue<string>() == "entity.remove"))
                return new PrepareReport(new Dictionary<string, bool>
                {
                    ["schema"] = true, ["ui"] = true, ["data"] = true,
                });
            return await Inner.PrepareAsync(branchRef, facets, ct);
        }
    }

    /// <summary>
    /// An adapter that starts staging before it finishes checking: the refusal is
    /// correct and carries a reason, but it leaves residue, so the branch never
    /// prepares again. Every refusal check still passes — which is the point. A
    /// contract that only asks "did it refuse?" cannot see this, and a host acting
    /// on the refusal sends the author back to a branch the first attempt spoiled.
    /// </summary>
    private sealed class SpoilsTheBranchOnRefusal(IBackendAdapter inner) : Passthrough(inner)
    {
        private readonly HashSet<string> spoiled = [];

        public override async Task<PrepareReport> PrepareAsync(
            string branchRef, PreparedFacets facets, CancellationToken ct = default)
        {
            // Refusals still come out as refusals; it is only the good document that
            // lands on the residue — which is the retry a host sends the author back to.
            PrepareReport report;
            try { report = await Inner.PrepareAsync(branchRef, facets, ct); }
            catch (AdapterRefusedException) { spoiled.Add(branchRef); throw; }
            if (spoiled.Contains(branchRef))
                throw new InvalidOperationException(
                    "the branch is half-staged from an earlier refusal and cannot be prepared again");
            return report;
        }
    }

    private sealed class FaultsOnMalformedDataOps(IBackendAdapter inner) : Passthrough(inner)
    {
        public override async Task<PrepareReport> PrepareAsync(
            string branchRef, PreparedFacets facets, CancellationToken ct = default)
        {
            try { return await Inner.PrepareAsync(branchRef, facets, ct); }
            catch (AdapterRefusedException) { throw new NullReferenceException(); }
        }
    }

    private sealed class RefusesUnknownTargetAsUnknownRef(IBackendAdapter inner) : Passthrough(inner)
    {
        public override async Task<ActiveState> ActiveStateAsync(string t, CancellationToken ct = default)
        {
            try { return await Inner.ActiveStateAsync(t, ct); }
            catch (AdapterRefusedException e) when (e.Reason == AdapterRefusalReason.UnknownTarget)
            {
                throw new AdapterRefusedException(AdapterRefusalReason.UnknownRef, e.Message);
            }
        }
    }

    private sealed class RefusesDocumentsWithoutLocation(IBackendAdapter inner) : Passthrough(inner)
    {
        public override async Task<PrepareReport> PrepareAsync(
            string branchRef, PreparedFacets facets, CancellationToken ct = default)
        {
            try { return await Inner.PrepareAsync(branchRef, facets, ct); }
            catch (AdapterRefusedException e) when (e.Reason == AdapterRefusalReason.DocumentRefused)
            {
                throw new AdapterRefusedException(AdapterRefusalReason.DocumentRefused, e.Message);
            }
        }
    }

    /// <summary>
    /// Answers the fixture document with a completion report for exactly the facets it
    /// carried — the report a correct adapter gives — and stages none of it.
    /// </summary>
    private sealed class ReportsCompletionStagesNothing(IBackendAdapter inner) : Passthrough(inner)
    {
        public override Task<PrepareReport> PrepareAsync(string b, PreparedFacets f, CancellationToken ct = default)
        {
            if (f.ChangesetFingerprint != "sha256:conformance-fixture") return Inner.PrepareAsync(b, f, ct);
            var carried = new[] { "schema", "ui", "data" }.Where(k => f.Patches[k] is JsonArray { Count: > 0 });
            return Task.FromResult(new PrepareReport(carried.ToDictionary(k => k, _ => true)));
        }
    }

    /// <summary>
    /// Stands in for an adapter that stages in place: after a refused document created an
    /// entity, that entity is still "there" on the branch, so a later removal of it succeeds.
    /// </summary>
    private sealed class KeepsWhatLandedBeforeARefusal(IBackendAdapter inner) : Passthrough(inner)
    {
        private readonly HashSet<(string Branch, string Entity)> _residue = [];

        public override async Task<PrepareReport> PrepareAsync(string b, PreparedFacets f, CancellationToken ct = default)
        {
            var ops = (f.Patches["schema"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
            if (ops.Length == 1 && ops[0]["op"]?.GetValue<string>() == "entity.remove"
                && _residue.Contains((b, ops[0]["entity"]!.GetValue<string>())))
                return new PrepareReport(new Dictionary<string, bool> { ["schema"] = true });
            try { return await Inner.PrepareAsync(b, f, ct); }
            catch (AdapterRefusedException)
            {
                foreach (var created in ops.TakeWhile(o => o["op"]?.GetValue<string>() == "entity.create"))
                    _residue.Add((b, created["entity"]!.GetValue<string>()));
                throw;
            }
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
            catch (AdapterRefusedException e) { throw new BackendUnreachable(e.Message); }
        }

        public override async Task FlipAsync(string t, string s, string tok, CancellationToken ct = default)
        {
            if (_seen.TryGetValue(tok, out var prior) && prior != s) throw new BackendUnreachable("token bound to another state");
            _seen[tok] = s;
            await Inner.FlipAsync(t, s, tok, ct);
        }
    }
}
