using Vivarium.Stage.Adapters;

namespace Vivarium.Stage.Conformance;

// Packaging note: the kit ships inside Vivarium.Stage rather than as a separate
// package. Two reasons. (1) It is the contract made executable — an adapter
// author already references this assembly to implement IBackendAdapter, so the
// verifier arrives with the contract and needs no second install. (2) A second
// artifact in this repository would force the release tag scheme to name the
// artifact (v* → prefixed), and coupling two artifacts to one tag makes a
// packaging-only change to one force a no-op re-release of the other. The kit
// takes no test-framework dependency, so it costs consumers nothing at runtime.

/// <summary>
/// Check ids. The id is the clause it enforces, not a serial number — a failing
/// check tells the adapter author exactly what to read, and a reviewer can
/// confirm the kit invents no requirement. A check with no clause is not a check.
/// </summary>
public static class ConformanceIds
{
    public const string DegradationDeclared = "adapter-api §2/degradation-declared";
    public const string ManifestDeclaresFidelityModes = "adapter-api §2/manifest-declares-fidelity-modes";
    public const string BranchReturnsRef = "adapter-api §3/branch-returns-ref";
    public const string BranchDeclaresFidelity = "adapter-api §3/branch-declares-fidelity";
    public const string FidelityModeVocabulary = "adapter-api §4/fidelity-mode-vocabulary";
    public const string SubsetRequiresSelectionRule = "adapter-api §4/subset-requires-selection-rule";
    public const string BranchModesWithinManifest = "adapter-api §2/branch-modes-within-manifest";
    public const string BranchHasNoLiveEffect = "adapter-api §3/branch-has-no-live-effect";
    public const string PrepareReportsPerFacet = "adapter-api §3/prepare-reports-per-facet";
    public const string PrepareIdempotentPerFingerprint = "adapter-api §3/prepare-idempotent-per-fingerprint";
    public const string PrepareHasNoLiveEffect = "adapter-api §3/prepare-has-no-live-effect";
    public const string PrepareRefusesMalformedDataOp = "adapter-api §3/prepare-refuses-malformed-data-operation";
    public const string PrepareRefusesMalformedSchemaOp = "adapter-api §3/prepare-refuses-malformed-schema-operation";
    public const string PrepareRefusesAbsentSchemaTarget = "adapter-api §3/prepare-refuses-absent-schema-target";
    public const string RefusalLeavesBranchPreparable = "adapter-api §3/refusal-leaves-branch-preparable";
    public const string RefusalLeavesNoResidue = "adapter-api §3/refusal-leaves-no-residue";
    public const string ActiveStateReturnsRefAndFingerprints = "adapter-api §3/active-state-returns-ref-and-fingerprints";
    public const string ActiveStateDeterministic = "adapter-api §3/active-state-deterministic";
    public const string UnknownTargetThrows = "adapter-api §Error-taxonomy/unknown-target-throws";
    public const string FlipActivatesStateRef = "adapter-api §3/flip-activates-state-ref";
    public const string FlipLandsPreparedState = "adapter-api §3/flip-lands-prepared-state";
    public const string FlipIdempotentUnderToken = "adapter-api §3/flip-idempotent-under-token";
    public const string TokenReuseDifferentStateThrows = "adapter-api §Error-taxonomy/token-reuse-different-state-throws";
    public const string DiscardHasNoLiveEffect = "adapter-api §3/discard-has-no-live-effect";
    public const string FlipRestoresPreviousState = "adapter-api §3/flip-restores-previous-state";
}

public enum ConformanceOutcome
{
    Passed,
    Failed,

    /// <summary>
    /// The contract does not constrain this case for this adapter, so the check
    /// is unverifiable rather than violated. Skipped is never a failure — an
    /// over-strict kit that fails honest adapters is worse than no kit.
    /// </summary>
    Skipped,
}

/// <summary>One contract clause, checked. <paramref name="Id"/> names the clause.</summary>
public sealed record ConformanceCheck(string Id, string Title, ConformanceOutcome Outcome, string? Detail = null);

/// <summary>
/// The result of a conformance run. Structured per check rather than thrown —
/// the caller's test framework decides what to assert, and reporting never
/// requires parsing a message.
/// </summary>
public sealed record ConformanceReport(IReadOnlyList<ConformanceCheck> Checks)
{
    public bool AllPassed => Checks.All(c => c.Outcome != ConformanceOutcome.Failed);
    public IReadOnlyList<ConformanceCheck> Failures => Checks.Where(c => c.Outcome == ConformanceOutcome.Failed).ToArray();

    /// <summary>Human-readable summary — one line per check, failures first.</summary>
    public override string ToString() => string.Join(
        Environment.NewLine,
        Failures.Concat(Checks.Where(c => c.Outcome != ConformanceOutcome.Failed))
            .Select(c => $"[{c.Outcome,-7}] {c.Id}{(c.Detail is null ? "" : " — " + c.Detail)}"));
}

/// <summary>
/// What the kit needs from the adapter author to exercise the contract.
/// </summary>
/// <param name="KnownTarget">
/// A target the adapter knows, whose live state the run may flip. See the
/// mutation warning on <see cref="AdapterConformance.RunAsync"/>.
/// </param>
/// <param name="UnknownTarget">
/// A target the adapter must NOT know. The kit asserts it throws rather than
/// inventing a pointer.
/// </param>
/// <param name="Patches">
/// A patch set the adapter's <c>PrepareAsync</c> can actually stage for
/// <paramref name="KnownTarget"/> — valid means "this adapter would accept it
/// in a real apply". An empty or unrecognised patch set makes the prepare
/// checks report on the fixture rather than on the adapter.
/// </param>
/// <param name="TokenPrefix">
/// Prefix for the flip tokens this run issues. Tokens are unique per run so a
/// re-run is not mistaken for an idempotent replay; the prefix is what makes
/// them traceable. Supply a build id (or any stable, unique-per-run value) when
/// running in CI against an adapter that persists tokens — a real backend keeps
/// a flip log keyed by token, and rows nobody can correlate to a run are
/// indistinguishable from litter.
/// </param>
public sealed record ConformanceFixture(
    string KnownTarget,
    string UnknownTarget,
    System.Text.Json.Nodes.JsonObject Patches,
    string TokenPrefix = "conformance")
{
    /// <summary>The facet keys a changeset's <c>patches</c> object may carry (spec §5).</summary>
    private static readonly string[] FacetKeys = ["schema", "ui", "data"];

    /// <summary>
    /// The facets <see cref="Patches"/> actually carries content for, in a stable order —
    /// what the prepare checks are able to speak for, and nothing more.
    /// </summary>
    public IReadOnlyList<string> ExercisedFacets { get; } = Validate(Patches);

    /// <summary>
    /// A patch set the adapter will not read is the one input that makes this suite lie:
    /// prepare stages nothing, the adapter reports whatever it reports, and the run comes
    /// back green about work that never happened. Nothing downstream can tell that apart
    /// from a correct run, so it is refused here — the one place that still knows the key
    /// was a typo rather than a decision.
    /// </summary>
    private static string[] Validate(System.Text.Json.Nodes.JsonObject patches)
    {
        ArgumentNullException.ThrowIfNull(patches);
        List<string> problems = [];

        var strays = patches.Select(kv => kv.Key).Where(k => !FacetKeys.Contains(k)).ToArray();
        if (strays.Length > 0)
            problems.Add($"[{string.Join(", ", strays)}] {(strays.Length == 1 ? "is not a facet" : "are not facets")}");

        List<string> exercised = [];
        foreach (var key in FacetKeys)
        {
            if (!patches.ContainsKey(key)) continue;
            if (patches[key] is not System.Text.Json.Nodes.JsonArray array)
            {
                problems.Add($"'{key}' must be an array");
                continue;
            }
            if (array.Count > 0) exercised.Add(key);
        }

        if (problems.Count == 0 && exercised.Count == 0)
            problems.Add("every facet is absent or empty, so prepare would stage nothing");

        if (problems.Count > 0)
            throw new ArgumentException(
                $"this patch set cannot exercise the adapter: {string.Join("; ", problems)}. " +
                $"Expected a changeset's own facet keys — schema, ui, data — with at least one carrying a patch.",
                nameof(Patches));

        return [.. exercised];
    }
}

/// <summary>
/// Executable conformance suite for <see cref="IBackendAdapter"/>
/// implementations — the normative boundary in <c>docs/adapter-api.md</c>,
/// checked rather than read.
///
/// Stage specifies what an adapter must do but cannot see whether a given
/// implementation does it; this closes that gap without Stage learning anything
/// about a specific backend.
/// </summary>
public static class AdapterConformance
{
    /// <summary>
    /// Run every contract check against <paramref name="adapter"/>.
    ///
    /// <para><b>This mutates live state.</b> The run flips
    /// <see cref="ConformanceFixture.KnownTarget"/> to a prepared branch and
    /// then flips it back, so it MUST be pointed at a disposable fixture and
    /// NEVER at production. The restore runs last and is reported as its own
    /// check, so a mid-run failure still leaves a record of whether the fixture
    /// was returned to its original state.</para>
    ///
    /// <para>Never throws for a contract violation — violations are reported as
    /// failed checks. An exception escaping this method means the adapter (or
    /// the fixture) failed in a way the contract does not describe.</para>
    /// </summary>
    public static async Task<ConformanceReport> RunAsync(
        IBackendAdapter adapter,
        ConformanceFixture fixture,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(fixture);

        var checks = new List<ConformanceCheck>();
        void Pass(string id, string title) => checks.Add(new ConformanceCheck(id, title, ConformanceOutcome.Passed));
        void Fail(string id, string title, string detail) => checks.Add(new ConformanceCheck(id, title, ConformanceOutcome.Failed, detail));
        void Skip(string id, string title, string reason) => checks.Add(new ConformanceCheck(id, title, ConformanceOutcome.Skipped, reason));

        // ---- §2 capability manifest ----
        var manifest = adapter.Capabilities;
        const string degradationTitle = "a non-atomic flip declares its non-atomic window";
        if (manifest.Flip.AtomicSwap)
            Pass(ConformanceIds.DegradationDeclared, degradationTitle);
        else if (string.IsNullOrWhiteSpace(manifest.Flip.DegradationDescription))
            Fail(ConformanceIds.DegradationDeclared, degradationTitle,
                "flip is not atomic but DegradationDescription is empty — a degradation that is not described cannot be consented to");
        else
            Pass(ConformanceIds.DegradationDeclared, degradationTitle);

        const string modesTitle = "the manifest declares producible fidelity modes";
        if (manifest.FidelityModesPerFacet.Count == 0)
            Fail(ConformanceIds.ManifestDeclaresFidelityModes, modesTitle,
                "FidelityModesPerFacet is empty — hosts cannot set branching policy against real capabilities");
        else if (manifest.FidelityModesPerFacet.SelectMany(kv => kv.Value).FirstOrDefault(m => !FidelityModes.Contains(m)) is { } bad)
            Fail(ConformanceIds.ManifestDeclaresFidelityModes, modesTitle,
                $"declared mode '{bad}' is outside the vocabulary (full|subset|stub)");
        else
            Pass(ConformanceIds.ManifestDeclaresFidelityModes, modesTitle);

        // ---- §Error taxonomy: unknown targets throw, never invent ----
        // Checked before anything else touches the fixture: an adapter that
        // fabricates pointers makes every later reading meaningless.
        const string unknownTitle = "activeState on an unknown target throws rather than inventing a pointer";
        try
        {
            var invented = await adapter.ActiveStateAsync(fixture.UnknownTarget, ct);
            Fail(ConformanceIds.UnknownTargetThrows, unknownTitle,
                $"returned StateRef '{invented.StateRef}' for unknown target '{fixture.UnknownTarget}' — a fabricated pointer reaches the drift gate and recovery as if it were fact");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // Throwing is half the clause; the other half is saying it was a refusal.
            // A host that cannot tell "no such target" from "the backend broke" has to
            // parse the message or report every absence as a crash (§6).
            if (RefusalMismatch(e, AdapterRefusalReason.UnknownTarget) is { } mismatch)
                Fail(ConformanceIds.UnknownTargetThrows, unknownTitle, mismatch);
            else
                Pass(ConformanceIds.UnknownTargetThrows, unknownTitle);
        }

        // ---- §3 activeState ----
        var original = await adapter.ActiveStateAsync(fixture.KnownTarget, ct);

        const string activeShapeTitle = "activeState returns the pointer value and per-facet fingerprints";
        if (string.IsNullOrWhiteSpace(original.StateRef))
            Fail(ConformanceIds.ActiveStateReturnsRefAndFingerprints, activeShapeTitle,
                "StateRef is empty — rollback has no return path and recovery has no decider");
        else if (original.FacetFingerprints.Count == 0)
            Fail(ConformanceIds.ActiveStateReturnsRefAndFingerprints, activeShapeTitle,
                "FacetFingerprints is empty — the drift gate would compare against nothing");
        else
            Pass(ConformanceIds.ActiveStateReturnsRefAndFingerprints, activeShapeTitle);

        const string determinismTitle = "activeState fingerprints are deterministic between unchanged reads";
        var reread = await adapter.ActiveStateAsync(fixture.KnownTarget, ct);
        if (!SameFingerprints(original, reread))
            Fail(ConformanceIds.ActiveStateDeterministic, determinismTitle,
                "two reads with no intervening change disagree — the drift gate would refuse applies that have not drifted");
        else
            Pass(ConformanceIds.ActiveStateDeterministic, determinismTitle);

        // ---- §3 branch ----
        var branch = await adapter.BranchAsync(fixture.KnownTarget, ct);

        const string branchRefTitle = "branch returns a reference";
        if (string.IsNullOrWhiteSpace(branch.BranchRef))
            Fail(ConformanceIds.BranchReturnsRef, branchRefTitle, "BranchRef is empty");
        else
            Pass(ConformanceIds.BranchReturnsRef, branchRefTitle);

        const string fidelityTitle = "branch carries a fidelity declaration";
        var perFacet = branch.Fidelity?.PerFacet ?? new Dictionary<string, FacetFidelity>();
        if (perFacet.Count == 0)
            Fail(ConformanceIds.BranchDeclaresFidelity, fidelityTitle,
                "no per-facet fidelity declared — a branch without one cannot enter simulation, so its evidence has no interpretation rule");
        else
            Pass(ConformanceIds.BranchDeclaresFidelity, fidelityTitle);

        const string vocabTitle = "declared fidelity modes are within the vocabulary and name a method";
        var vocabProblem = perFacet
            .Select(kv => !FidelityModes.Contains(kv.Value.Mode)
                ? $"facet '{kv.Key}' declares mode '{kv.Value.Mode}' (expected full|subset|stub)"
                : string.IsNullOrWhiteSpace(kv.Value.Method)
                    ? $"facet '{kv.Key}' declares no method tag"
                    : null)
            .FirstOrDefault(p => p is not null);
        if (perFacet.Count == 0) Skip(ConformanceIds.FidelityModeVocabulary, vocabTitle, "no fidelity declaration to inspect");
        else if (vocabProblem is not null) Fail(ConformanceIds.FidelityModeVocabulary, vocabTitle, vocabProblem);
        else Pass(ConformanceIds.FidelityModeVocabulary, vocabTitle);

        const string subsetTitle = "subset fidelity carries its selection rule";
        var subsets = perFacet.Where(kv => kv.Value.Mode == "subset").ToArray();
        if (subsets.Length == 0)
            Skip(ConformanceIds.SubsetRequiresSelectionRule, subsetTitle, "this branch declares no subset facet");
        else if (subsets.FirstOrDefault(kv => string.IsNullOrWhiteSpace(kv.Value.SelectionRule)) is { Key: not null } missing)
            Fail(ConformanceIds.SubsetRequiresSelectionRule, subsetTitle,
                $"facet '{missing.Key}' declares subset fidelity with no selection rule — 'part of the data' is not an interpretable claim without saying which part");
        else
            Pass(ConformanceIds.SubsetRequiresSelectionRule, subsetTitle);

        const string withinTitle = "branch fidelity modes are among those the manifest claims to produce";
        // §2 does not require the manifest to enumerate every facet, so a facet
        // it is silent about is unverifiable, not wrong. Unverifiable facets are
        // named in the detail rather than dropped — a check that quietly covers
        // less than it appears to reads as broader assurance than it gives.
        var verifiable = perFacet.Where(kv => manifest.FidelityModesPerFacet.ContainsKey(kv.Key)).ToArray();
        var unverifiable = perFacet.Keys.Where(f => !manifest.FidelityModesPerFacet.ContainsKey(f)).ToArray();
        var unverifiableNote = unverifiable.Length == 0
            ? null
            : $"not verifiable for [{string.Join(", ", unverifiable)}] — the manifest declares no modes for {(unverifiable.Length == 1 ? "that facet" : "those facets")}";

        if (verifiable.Length == 0)
            Skip(ConformanceIds.BranchModesWithinManifest, withinTitle,
                "the manifest declares no modes for the facets this branch declares");
        else if (verifiable.FirstOrDefault(kv => !manifest.FidelityModesPerFacet[kv.Key].Contains(kv.Value.Mode)) is { Key: not null } outside)
            Fail(ConformanceIds.BranchModesWithinManifest, withinTitle,
                $"facet '{outside.Key}' branched as '{outside.Value.Mode}' but the manifest claims only [{string.Join(", ", manifest.FidelityModesPerFacet[outside.Key])}]");
        else
            checks.Add(new ConformanceCheck(ConformanceIds.BranchModesWithinManifest, withinTitle, ConformanceOutcome.Passed, unverifiableNote));

        const string branchLiveTitle = "branch has no live effect";
        var afterBranch = await adapter.ActiveStateAsync(fixture.KnownTarget, ct);
        if (!SameState(original, afterBranch))
            Fail(ConformanceIds.BranchHasNoLiveEffect, branchLiveTitle,
                "the live target changed while branching — staging must never touch live state (fault-model F1)");
        else
            Pass(ConformanceIds.BranchHasNoLiveEffect, branchLiveTitle);

        // ---- §3 prepare ----
        const string fingerprint = "sha256:conformance-fixture";
        var facets = new PreparedFacets(fingerprint, fixture.Patches);
        var report = await adapter.PrepareAsync(branch.BranchRef, facets, ct);

        // Counting entries is not a check: an adapter that answers with a constant
        // dictionary passes it while staging nothing, which is how this suite came to
        // report the same verdict for a patch set the adapter read and one it did not.
        // The fixture knows which facets the document carries, so the question worth
        // asking is whether the report covers exactly those.
        const string prepareFacetTitle = "prepare reports per-facet completion";
        var exercised = fixture.ExercisedFacets;
        var reported = report.FacetComplete.Keys.ToArray();
        var silent = exercised.Where(f => !reported.Contains(f)).ToArray();
        var overclaimed = reported.Where(f => !exercised.Contains(f)).ToArray();
        var coverageNote = $"the document carried [{string.Join(", ", exercised)}]";
        if (report.FacetComplete.Count == 0)
            Fail(ConformanceIds.PrepareReportsPerFacet, prepareFacetTitle,
                "FacetComplete is empty — Stage cannot confirm ALL facets before a flip, which is what makes a half-applied change impossible");
        else if (silent.Length > 0)
            Fail(ConformanceIds.PrepareReportsPerFacet, prepareFacetTitle,
                $"{coverageNote} but the report says nothing about [{string.Join(", ", silent)}] — Stage cannot confirm a facet the adapter never answered for");
        else if (overclaimed.Length > 0)
            Fail(ConformanceIds.PrepareReportsPerFacet, prepareFacetTitle,
                $"{coverageNote} but the report also claims [{string.Join(", ", overclaimed)}] — reporting completion for a facet the document never carried is completion for work not done");
        else
            checks.Add(new ConformanceCheck(ConformanceIds.PrepareReportsPerFacet, prepareFacetTitle,
                ConformanceOutcome.Passed, coverageNote));

        const string prepareIdemTitle = "prepare is idempotent per changeset fingerprint";
        var second = await adapter.PrepareAsync(branch.BranchRef, facets, ct);
        if (!SameCompletion(report, second))
            Fail(ConformanceIds.PrepareIdempotentPerFingerprint, prepareIdemTitle,
                "re-preparing the same fingerprint reported a different completion — retry after a prepare crash (fault-model F2) would not be safe");
        else
            Pass(ConformanceIds.PrepareIdempotentPerFingerprint, prepareIdemTitle);

        // The document handed to prepare is authored elsewhere, so the adapter must
        // refuse a data operation it cannot execute honestly rather than crash on it
        // or quietly stage less. The predicate below is malformed under the changeset
        // spec's §5.3 shape in the most ordinary way — a key/value map where
        // `{ field, equals }` belongs — which is the shape a producer reaches for first.
        const string malformedDataTitle = "prepare refuses a malformed data operation";
        var malformed = new System.Text.Json.Nodes.JsonObject
        {
            ["schema"] = new System.Text.Json.Nodes.JsonArray(),
            ["ui"] = new System.Text.Json.Nodes.JsonArray(),
            ["data"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "conformance-malformed",
                ["explanation"] = "conformance probe — must be refused",
                ["operations"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["op"] = "update",
                    ["entity"] = "conformance-probe-entity",
                    ["where"] = new System.Text.Json.Nodes.JsonObject { ["someField"] = "someValue" },
                    ["set"] = new System.Text.Json.Nodes.JsonObject { ["someField"] = "other" },
                }),
            }),
        };
        try
        {
            await adapter.PrepareAsync(
                branch.BranchRef,
                new PreparedFacets($"{fingerprint}-malformed", malformed),
                ct);
            Fail(ConformanceIds.PrepareRefusesMalformedDataOp, malformedDataTitle,
                "prepare accepted an operation whose predicate is not { field, equals } — it either staged something it could not have understood or silently staged nothing, and reported completion either way");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is not NullReferenceException)
        {
            if (RefusalMismatch(e, AdapterRefusalReason.DocumentRefused) is { } mismatch)
                Fail(ConformanceIds.PrepareRefusesMalformedDataOp, malformedDataTitle, mismatch);
            else
                Pass(ConformanceIds.PrepareRefusesMalformedDataOp, malformedDataTitle);
        }
        catch (NullReferenceException)
        {
            Fail(ConformanceIds.PrepareRefusesMalformedDataOp, malformedDataTitle,
                "prepare dereferenced an absent member instead of refusing — a null-reference fault is an accident, not a reason (§Error taxonomy)");
        }

        // The clause covers every facet, so the check does too. A schema operation
        // outside the vocabulary is the shape that used to stage nothing while
        // prepare reported the facet complete — completion for work not done.
        const string malformedSchemaTitle = "prepare refuses a malformed schema operation";
        var malformedSchema = new System.Text.Json.Nodes.JsonObject
        {
            ["schema"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["op"] = "entity.truncate",
                ["entity"] = "conformance-probe-entity",
                ["explanation"] = "conformance probe — must be refused",
            }),
            ["ui"] = new System.Text.Json.Nodes.JsonArray(),
            ["data"] = new System.Text.Json.Nodes.JsonArray(),
        };
        try
        {
            await adapter.PrepareAsync(
                branch.BranchRef,
                new PreparedFacets($"{fingerprint}-malformed-schema", malformedSchema),
                ct);
            Fail(ConformanceIds.PrepareRefusesMalformedSchemaOp, malformedSchemaTitle,
                "prepare accepted a schema operation outside the vocabulary — it staged nothing for that facet and reported it complete anyway");
        }
        catch (OperationCanceledException) { throw; }
        catch (NullReferenceException)
        {
            Fail(ConformanceIds.PrepareRefusesMalformedSchemaOp, malformedSchemaTitle,
                "prepare dereferenced an absent member instead of refusing — a null-reference fault is an accident, not a reason (§Error taxonomy)");
        }
        catch (Exception e)
        {
            if (RefusalMismatch(e, AdapterRefusalReason.DocumentRefused) is { } mismatch)
                Fail(ConformanceIds.PrepareRefusesMalformedSchemaOp, malformedSchemaTitle, mismatch);
            else
                Pass(ConformanceIds.PrepareRefusesMalformedSchemaOp, malformedSchemaTitle);
        }

        // The clause does not stop at malformed documents. An operation can be
        // perfectly well formed and still name something that is not there, and the
        // contract answers that case too: refuse, because the operation says what it
        // expected to find. Inventing the target is one failure; reporting the facet
        // complete without touching anything is the other, and removal is where the
        // second one is most convincing — the target is supposed to end up absent, so
        // an operation naming an entity nobody has looks exactly like one that worked.
        const string absentTargetTitle = "prepare refuses a well-formed operation naming an absent target";
        var absentTarget = new System.Text.Json.Nodes.JsonObject
        {
            ["schema"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["op"] = "entity.remove",
                ["entity"] = "conformance-absent-entity",
                ["explanation"] = "conformance probe — the target does not exist and must be refused",
            }),
            ["ui"] = new System.Text.Json.Nodes.JsonArray(),
            ["data"] = new System.Text.Json.Nodes.JsonArray(),
        };
        try
        {
            await adapter.PrepareAsync(
                branch.BranchRef,
                new PreparedFacets($"{fingerprint}-absent-target", absentTarget),
                ct);
            Fail(ConformanceIds.PrepareRefusesAbsentSchemaTarget, absentTargetTitle,
                "prepare accepted a removal of an entity that is not there — it staged nothing and reported the facet complete, so an approved document says a thing was removed that never existed");
        }
        catch (OperationCanceledException) { throw; }
        catch (NullReferenceException)
        {
            Fail(ConformanceIds.PrepareRefusesAbsentSchemaTarget, absentTargetTitle,
                "prepare dereferenced an absent member instead of refusing — a null-reference fault is an accident, not a reason (§Error taxonomy)");
        }
        catch (Exception e)
        {
            if (RefusalMismatch(e, AdapterRefusalReason.DocumentRefused) is { } mismatch)
                Fail(ConformanceIds.PrepareRefusesAbsentSchemaTarget, absentTargetTitle, mismatch);
            else
                // §7: where a check verifies only part of what it appears to, it names
                // the part it could not reach. The probe addresses an entity, because
                // that is the only absent target the suite can name without knowing
                // the fixture's schema — an adapter that refuses this one and still
                // accepts removal of an absent field or constraint passes here.
                checks.Add(new ConformanceCheck(
                    ConformanceIds.PrepareRefusesAbsentSchemaTarget, absentTargetTitle,
                    ConformanceOutcome.Passed,
                    "verified at the entity level only — the suite cannot name a field or constraint the fixture is known to lack"));
        }

        // Three refusals just happened on this branch, and a host acting on a document
        // refusal tells the author to fix the document and retry. That advice is
        // worthless if the refused attempt already spoiled the branch, so the guarantee
        // has to be checked and not merely stated: a good document must still prepare,
        // identically.
        const string preparableTitle = "a refusal leaves the branch as preparable as it was";
        try
        {
            var afterRefusals = await adapter.PrepareAsync(branch.BranchRef, facets, ct);
            if (!SameCompletion(report, afterRefusals))
                Fail(ConformanceIds.RefusalLeavesBranchPreparable, preparableTitle,
                    "a document that prepared cleanly before the refusals reported a different completion after them — the refused attempts left the branch changed, so 'fix it and try again' lands somewhere the first attempt already spoiled");
            else
                Pass(ConformanceIds.RefusalLeavesBranchPreparable, preparableTitle);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Fail(ConformanceIds.RefusalLeavesBranchPreparable, preparableTitle,
                $"a document that prepared cleanly before the refusals now throws — the branch did not survive being refused: {e.Message}");
        }

        // The three refusals above are each refused at their first operation, so none of
        // them ever had anything staged to leave behind. The case the clause is really
        // about is a document refused part-way: a well-formed operation lands, and a later
        // one names something absent — found only while applying. Whatever landed must not
        // stay. Observed through the interface alone: create an entity, refuse on the next
        // operation, then ask to remove that entity — on a clean branch it is absent, so
        // that removal must be refused too. On its own branch, so nothing here can reach
        // the flip below.
        const string residueTitle = "a document refused part-way leaves nothing it asked for on the branch";
        var residueBranch = await adapter.BranchAsync(fixture.KnownTarget, ct);
        try
        {
            var created = new System.Text.Json.Nodes.JsonObject
            {
                ["op"] = "entity.create",
                ["entity"] = "conformance-residue-probe",
                ["fields"] = new System.Text.Json.Nodes.JsonArray(),
                ["explanation"] = "conformance probe — lands, then the document is refused",
            };
            var refusedPartWay = new System.Text.Json.Nodes.JsonObject
            {
                ["schema"] = new System.Text.Json.Nodes.JsonArray(created, new System.Text.Json.Nodes.JsonObject
                {
                    ["op"] = "entity.remove",
                    ["entity"] = "conformance-absent-entity",
                    ["explanation"] = "conformance probe — absent, refuses the document",
                }),
                ["ui"] = new System.Text.Json.Nodes.JsonArray(),
                ["data"] = new System.Text.Json.Nodes.JsonArray(),
            };
            var refused = false;
            try
            {
                await adapter.PrepareAsync(residueBranch.BranchRef,
                    new PreparedFacets($"{fingerprint}-refused-part-way", refusedPartWay), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { refused = true; }

            if (!refused)
                Skip(ConformanceIds.RefusalLeavesNoResidue, residueTitle,
                    "the probe document was not refused, so there is no refusal to leave residue — see prepare-refuses-absent-schema-target");
            else
            {
                var removeCreated = new System.Text.Json.Nodes.JsonObject
                {
                    ["schema"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                    {
                        ["op"] = "entity.remove",
                        ["entity"] = "conformance-residue-probe",
                        ["explanation"] = "conformance probe — absent unless the refused document left it behind",
                    }),
                    ["ui"] = new System.Text.Json.Nodes.JsonArray(),
                    ["data"] = new System.Text.Json.Nodes.JsonArray(),
                };
                try
                {
                    await adapter.PrepareAsync(residueBranch.BranchRef,
                        new PreparedFacets($"{fingerprint}-residue-check", removeCreated), ct);
                    Fail(ConformanceIds.RefusalLeavesNoResidue, residueTitle,
                        "the entity created by the first operation of a refused document was still on the branch — the refusal left what landed before it, so the corrected retry runs against a branch the refused attempt already changed");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    // Removal of an absent entity is refused — exactly what a clean branch does.
                    // Whether that refusal is well formed is prepare-refuses-absent-schema-target's
                    // business; this check is only about what the branch still holds.
                    Pass(ConformanceIds.RefusalLeavesNoResidue, residueTitle);
                }
            }
        }
        finally
        {
            await adapter.DiscardAsync(residueBranch.BranchRef, ct);
        }

        const string prepareLiveTitle = "prepare has no live effect";
        var afterPrepare = await adapter.ActiveStateAsync(fixture.KnownTarget, ct);
        if (!SameState(original, afterPrepare))
            Fail(ConformanceIds.PrepareHasNoLiveEffect, prepareLiveTitle,
                "the live target changed while preparing — prepare must be retryable and discardable with zero live effect (fault-model F2)");
        else
            Pass(ConformanceIds.PrepareHasNoLiveEffect, prepareLiveTitle);

        // ---- §3 flip ----
        var token = $"{fixture.TokenPrefix}-flip-{Guid.NewGuid():n}";
        const string flipTitle = "flip activates the requested state ref";
        var flipped = false;
        ActiveState? landed = null;
        try
        {
            await adapter.FlipAsync(fixture.KnownTarget, branch.BranchRef, token, ct);
            flipped = true;
            var active = landed = await adapter.ActiveStateAsync(fixture.KnownTarget, ct);
            if (active.StateRef != branch.BranchRef)
                Fail(ConformanceIds.FlipActivatesStateRef, flipTitle,
                    $"flip reported success but the active pointer is '{active.StateRef}', not '{branch.BranchRef}'");
            else
                Pass(ConformanceIds.FlipActivatesStateRef, flipTitle);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Fail(ConformanceIds.FlipActivatesStateRef, flipTitle, $"flip threw: {e.Message}");
        }

        // The pointer moved — but to a state holding what the document asked for? Until
        // this check the suite compared what the adapter *said* (its per-facet report)
        // with what the document *carried*, and never observed what the adapter *did*:
        // an adapter reporting completion and staging nothing passed. The observation was
        // available all along. The fixture target is flipped to the prepared branch, and
        // §6 fixes what the fingerprints mean: a UI key is an artifact's drift ref, and
        // the drift gate compares it with the changeset spec's artifact fingerprint
        // (§4, a hash of the content) — so the value a written artifact must hold is known
        // exactly, without reading the world.
        const string landsTitle = "the flipped-to state holds what the document staged";
        if (landed is null)
            Skip(ConformanceIds.FlipLandsPreparedState, landsTitle, "the flip did not succeed");
        else
            CheckLanded(original, landed, fixture.Patches, ConformanceIds.FlipLandsPreparedState, landsTitle, checks);

        const string idempotentTitle = "re-issuing the same token for the same state ref is a no-op, not an error";
        if (!flipped)
            Skip(ConformanceIds.FlipIdempotentUnderToken, idempotentTitle, "the first flip did not succeed");
        else
        {
            try
            {
                await adapter.FlipAsync(fixture.KnownTarget, branch.BranchRef, token, ct);
                Pass(ConformanceIds.FlipIdempotentUnderToken, idempotentTitle);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                Fail(ConformanceIds.FlipIdempotentUnderToken, idempotentTitle,
                    $"re-issuing the recovery no-op threw ({e.Message}) — recovery re-issues flips after a crash during flip (fault-model F4/F6)");
            }
        }

        const string reuseTitle = "the same token bound to a different state ref must throw";
        if (!flipped)
            Skip(ConformanceIds.TokenReuseDifferentStateThrows, reuseTitle, "the first flip did not succeed");
        else
        {
            try
            {
                await adapter.FlipAsync(fixture.KnownTarget, original.StateRef, token, ct);
                Fail(ConformanceIds.TokenReuseDifferentStateThrows, reuseTitle,
                    "a used token was accepted for a different state ref — the token can no longer distinguish an idempotent replay from a different flip");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                if (RefusalMismatch(e, AdapterRefusalReason.ApplyTokenConflict) is { } mismatch)
                    Fail(ConformanceIds.TokenReuseDifferentStateThrows, reuseTitle, mismatch);
                else
                    Pass(ConformanceIds.TokenReuseDifferentStateThrows, reuseTitle);
            }
        }

        // ---- §3 discard ----
        // Discarded on its own branch, never on the one that was flipped: once a
        // branch is flipped it IS the live state, and "discard the active state"
        // is not what §3 means by releasing a staging world. Testing it there
        // would report an adapter that correctly refuses as non-conforming.
        const string discardTitle = "discard releases staging without live effect";
        var beforeDiscard = await adapter.ActiveStateAsync(fixture.KnownTarget, ct);
        try
        {
            var disposable = await adapter.BranchAsync(fixture.KnownTarget, ct);
            await adapter.DiscardAsync(disposable.BranchRef, ct);
            var afterDiscard = await adapter.ActiveStateAsync(fixture.KnownTarget, ct);
            if (!SameState(beforeDiscard, afterDiscard))
                Fail(ConformanceIds.DiscardHasNoLiveEffect, discardTitle, "discarding staging changed the live target");
            else
                Pass(ConformanceIds.DiscardHasNoLiveEffect, discardTitle);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Fail(ConformanceIds.DiscardHasNoLiveEffect, discardTitle, $"discard threw: {e.Message} — discard is always safe");
        }

        // ---- restore: the rollback primitive, and the fixture's way home ----
        // Runs last and always, so a mid-run failure still leaves a record of
        // whether the fixture was returned to its original state.
        const string restoreTitle = "flip returns to a previously active state (the rollback primitive)";
        try
        {
            var now = await adapter.ActiveStateAsync(fixture.KnownTarget, ct);
            if (now.StateRef == original.StateRef)
            {
                Skip(ConformanceIds.FlipRestoresPreviousState, restoreTitle,
                    "the fixture never left its original state, so the rollback path was not exercised");
            }
            else
            {
                await adapter.FlipAsync(fixture.KnownTarget, original.StateRef, $"{fixture.TokenPrefix}-restore-{Guid.NewGuid():n}", ct);
                var restored = await adapter.ActiveStateAsync(fixture.KnownTarget, ct);
                if (!SameState(original, restored))
                    Fail(ConformanceIds.FlipRestoresPreviousState, restoreTitle,
                        $"flip back to '{original.StateRef}' left the target at '{restored.StateRef}' — every apply must have a return path, and THE FIXTURE IS NOT RESTORED");
                else
                    Pass(ConformanceIds.FlipRestoresPreviousState, restoreTitle);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Fail(ConformanceIds.FlipRestoresPreviousState, restoreTitle,
                $"restoring the previous state threw: {e.Message} — THE FIXTURE IS NOT RESTORED");
        }

        return new ConformanceReport(checks);
    }

    /// <summary>
    /// Compare the state before the flip with the state after it, key by key, against what
    /// the document carried. Exact where §6 fixes a key's value (UI artifacts), movement
    /// where it only fixes the facet (<c>schema</c>), and unverifiable — said so — where
    /// neither holds.
    /// </summary>
    private static void CheckLanded(
        ActiveState before, ActiveState after, System.Text.Json.Nodes.JsonObject patches,
        string id, string title, List<ConformanceCheck> checks)
    {
        var problems = new List<string>();
        var unreached = new List<string>();

        // Last write wins when a document patches one artifact twice. The fixture is the
        // author's input, and an adapter can accept a patch the reference adapter would
        // refuse — so a member this needs may be missing. That is reported, never thrown:
        // the suite reports rather than throws (§7), and one unreadable patch must not
        // end the run before the restore check brings the fixture home.
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (patch, i) in (patches["ui"] as System.Text.Json.Nodes.JsonArray ?? [])
                     .Select((p, i) => (p as System.Text.Json.Nodes.JsonObject, i)))
        {
            var artifactId = Text(patch?["artifactId"]);
            var verified = Text(patch?["profile"]) == "verified-diff@0";
            var expected = verified
                ? Text(patch?["newFingerprint"])
                : Text(patch?["newContent"]) is { } content ? Vivarium.Changeset.ChangesetFingerprint.OfArtifact(content) : null;
            if (artifactId is null || expected is null)
                unreached.Add($"ui[{i}]: the fixture patch carries no {(artifactId is null ? "artifactId" : verified ? "newFingerprint" : "newContent")} to derive the expected fingerprint from");
            else
                written[artifactId] = expected;
        }
        foreach (var (artifactId, expected) in written)
        {
            if (!after.FacetFingerprints.TryGetValue(artifactId, out var actual))
                problems.Add($"artifact '{artifactId}' was written by the document but has no fingerprint in the active state");
            else if (actual != expected)
                problems.Add($"artifact '{artifactId}' fingerprints to {actual}; the content the document wrote fingerprints to {expected}");
        }

        foreach (var (key, value) in before.FacetFingerprints)
        {
            if (key is "schema" or "data" || written.ContainsKey(key)) continue;
            if (!after.FacetFingerprints.TryGetValue(key, out var now))
                problems.Add($"'{key}' disappeared although the document did not touch it");
            else if (now != value)
                problems.Add($"'{key}' changed although the document did not touch it");
        }

        var schemaCarried = patches["schema"] is System.Text.Json.Nodes.JsonArray { Count: > 0 };
        if (before.FacetFingerprints.TryGetValue("schema", out var schemaBefore)
            && after.FacetFingerprints.TryGetValue("schema", out var schemaAfter))
        {
            // Every schema operation changes the schema's shape — one that names something
            // absent is refused, not applied — so a carried schema facet must move.
            if (schemaCarried && schemaBefore == schemaAfter)
                problems.Add("the document carried schema operations but the 'schema' fingerprint did not move");
            if (!schemaCarried && schemaBefore != schemaAfter)
                problems.Add("the 'schema' fingerprint moved although the document carried no schema operations");
        }
        else if (schemaCarried)
            unreached.Add("schema: no 'schema' key — §6 lets an adapter key the schema below facet granularity, and the suite cannot attribute those keys");

        var dataCarried = patches["data"] is System.Text.Json.Nodes.JsonArray { Count: > 0 };
        if (dataCarried)
            unreached.Add("data: a predicate that selects no rows has done what it said, so movement cannot be required");
        else if (before.FacetFingerprints.TryGetValue("data", out var dataBefore)
            && after.FacetFingerprints.TryGetValue("data", out var dataAfter) && dataBefore != dataAfter)
            problems.Add("the 'data' fingerprint moved although the document carried no data operations");

        var note = unreached.Count == 0 ? null : "not verified — " + string.Join("; ", unreached);
        checks.Add(problems.Count > 0
            ? new ConformanceCheck(id, title, ConformanceOutcome.Failed,
                string.Join("; ", problems) + (note is null ? "" : $" ({note})"))
            : new ConformanceCheck(id, title, ConformanceOutcome.Passed, note));
    }

    private static string? Text(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    private static readonly HashSet<string> FidelityModes = ["full", "subset", "stub"];

    /// <summary>
    /// Null when <paramref name="e"/> is the refusal a clause names; otherwise what is wrong
    /// with it. A refusal is an <see cref="AdapterRefusedException"/> carrying the clause's
    /// reason and a message (§6); a document refusal also says where (<c>Details.errors</c>),
    /// because "fix the document" with no location is a round trip per error.
    /// </summary>
    private static string? RefusalMismatch(Exception e, AdapterRefusalReason expected) => e switch
    {
        AdapterRefusedException r when r.Reason != expected =>
            $"refused as {r.Reason}, but this clause is {expected}: {r.Message}",
        AdapterRefusedException r when string.IsNullOrWhiteSpace(r.Message) =>
            "the refusal carried no message — §6 requires a reason, and an empty one is not a reason",
        AdapterRefusedException { Reason: AdapterRefusalReason.DocumentRefused } r
            when r.Details?["errors"] is not System.Text.Json.Nodes.JsonArray { Count: > 0 } =>
            "a document refusal must locate what it refused — Details.errors was absent or empty",
        AdapterRefusedException => null,
        _ => $"threw {e.GetType().Name} ({e.Message}) — a refusal this contract names must be an " +
             $"AdapterRefusedException({expected}), or a host cannot tell it from a fault (§6)",
    };

    private static bool SameFingerprints(ActiveState a, ActiveState b) =>
        a.FacetFingerprints.Count == b.FacetFingerprints.Count &&
        a.FacetFingerprints.All(kv => b.FacetFingerprints.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static bool SameState(ActiveState a, ActiveState b) =>
        a.StateRef == b.StateRef && SameFingerprints(a, b);

    private static bool SameCompletion(PrepareReport a, PrepareReport b) =>
        a.FacetComplete.Count == b.FacetComplete.Count &&
        a.FacetComplete.All(kv => b.FacetComplete.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
