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
    public const string ActiveStateReturnsRefAndFingerprints = "adapter-api §3/active-state-returns-ref-and-fingerprints";
    public const string ActiveStateDeterministic = "adapter-api §3/active-state-deterministic";
    public const string UnknownTargetThrows = "adapter-api §Error-taxonomy/unknown-target-throws";
    public const string FlipActivatesStateRef = "adapter-api §3/flip-activates-state-ref";
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
    string TokenPrefix = "conformance");

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
        catch (Exception)
        {
            // Any exception type satisfies this: §Error taxonomy leaves the type
            // unspecified in v0. Asserting a type would narrow the contract to
            // one implementation's choice.
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

        const string prepareFacetTitle = "prepare reports per-facet completion";
        if (report.FacetComplete.Count == 0)
            Fail(ConformanceIds.PrepareReportsPerFacet, prepareFacetTitle,
                "FacetComplete is empty — Stage cannot confirm ALL facets before a flip, which is what makes a half-applied change impossible");
        else
            Pass(ConformanceIds.PrepareReportsPerFacet, prepareFacetTitle);

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
            // the exception TYPE is the adapter's choice (§Error taxonomy is
            // unspecified in v0); refusing at all, with something to read, is not
            Pass(ConformanceIds.PrepareRefusesMalformedDataOp, malformedDataTitle);
            if (string.IsNullOrWhiteSpace(e.Message))
                checks[^1] = new ConformanceCheck(ConformanceIds.PrepareRefusesMalformedDataOp,
                    malformedDataTitle, ConformanceOutcome.Failed,
                    "the refusal carried no message — §Error taxonomy requires a reason, and an empty one is not a reason");
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
            if (string.IsNullOrWhiteSpace(e.Message))
                Fail(ConformanceIds.PrepareRefusesMalformedSchemaOp, malformedSchemaTitle,
                    "the refusal carried no message — §Error taxonomy requires a reason, and an empty one is not a reason");
            else
                Pass(ConformanceIds.PrepareRefusesMalformedSchemaOp, malformedSchemaTitle);
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
        try
        {
            await adapter.FlipAsync(fixture.KnownTarget, branch.BranchRef, token, ct);
            flipped = true;
            var active = await adapter.ActiveStateAsync(fixture.KnownTarget, ct);
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
            catch (Exception)
            {
                // Type unspecified in v0 — throwing at all is the contract.
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

    private static readonly HashSet<string> FidelityModes = ["full", "subset", "stub"];

    private static bool SameFingerprints(ActiveState a, ActiveState b) =>
        a.FacetFingerprints.Count == b.FacetFingerprints.Count &&
        a.FacetFingerprints.All(kv => b.FacetFingerprints.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static bool SameState(ActiveState a, ActiveState b) =>
        a.StateRef == b.StateRef && SameFingerprints(a, b);

    private static bool SameCompletion(PrepareReport a, PrepareReport b) =>
        a.FacetComplete.Count == b.FacetComplete.Count &&
        a.FacetComplete.All(kv => b.FacetComplete.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
