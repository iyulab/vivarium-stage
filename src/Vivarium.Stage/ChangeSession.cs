using System.Text.Json.Nodes;
using Vivarium.Changeset;
using Vivarium.Stage.Adapters;
using Vivarium.Stage.Ledger;

namespace Vivarium.Stage;

public enum SessionState { Proposed, Branched, Simulated, Applied, Discarded, RolledBack }

public enum RefusalReason
{
    InvalidChangeset,
    FingerprintGate,
    DriftGate,
    DegradedAdapter,
    PrepareIncomplete,
    InvalidStateTransition,

    /// <summary>
    /// The release ledger's own history does not verify
    /// (<see cref="Ledger.LedgerIntegrity"/>). Raised only where a host has
    /// asked for it — see <see cref="StagePolicy.RequireIntactLedger"/>. It is
    /// its own reason because the response is unlike every other refusal
    /// here: the others are answered by changing the changeset or the target,
    /// this one by going to look at the store.
    /// </summary>
    LedgerIntegrityGate,
}

/// <summary>
/// A gate refusal. Stage refuses loudly and specifically — it never guesses (fixed principle 3).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Reason"/> says which gate refused; <see cref="Details"/> carries the
/// specifics that gate observed, so a host can state <em>what</em> went wrong without
/// parsing <see cref="Exception.Message"/>. The message stays the human sentence and
/// remains the only place a detail is guaranteed to appear — <c>Details</c> is null
/// wherever the refusal has no fact a caller could act on differently.
/// </para>
/// <para>
/// The member set inside <c>Details</c> is per-gate and additive: a host reads the
/// members it knows and ignores the rest. Adding a member is not a breaking change;
/// removing one is.
/// </para>
/// </remarks>
public sealed class StageRefusedException : Exception
{
    private static JsonObject? Snapshot(JsonObject? details) => (JsonObject?)details?.DeepClone();

    public StageRefusedException(RefusalReason reason, string message, JsonObject? details = null)
        : base(message)
    {
        Reason = reason;
        Details = Snapshot(details);
    }

    /// <summary>Which gate refused. A closed vocabulary — hosts branch on this.</summary>
    public RefusalReason Reason { get; }

    /// <summary>
    /// What that gate observed, as a snapshot taken at throw time (null when the
    /// refusal carries no actionable fact). Treat as read-only.
    /// </summary>
    public JsonObject? Details { get; }
}

/// <summary>
/// What a completed flip did — the facts the operation established, returned rather
/// than left for the caller to reconstruct.
/// </summary>
/// <remarks>
/// <para>
/// This is a record of the <em>past</em>, not a claim about the present. "What did my
/// apply land?" and "what is active now?" are different questions, and they diverge
/// the moment a concurrent flip lands between them — a caller that answers the first
/// by asking the second reports someone else's state as its own. Ask the adapter when
/// you need to know what is live; the verified-never-asserted discipline
/// (<see cref="ChangeSession.RehydrateAppliedAsync"/>) is untouched by this type,
/// because a past fact does not become false with time.
/// </para>
/// <para>
/// Properties are init-only rather than positional: this type is expected to grow as
/// the ledger's facts become useful to callers, and positional records break
/// deconstruction on every addition.
/// </para>
/// </remarks>
public sealed record FlipOutcome
{
    /// <summary><c>apply</c> | <c>rollback</c> — the same axis <c>RecoveryOutcome</c> reports.</summary>
    public required string Operation { get; init; }

    public required string Target { get; init; }

    /// <summary>The changeset this flip carried — the one sealed and approved, not "the latest".</summary>
    public required string ChangesetFingerprint { get; init; }

    /// <summary>The token this flip ran under. Re-issuing it is the idempotent recovery no-op (fault-model F4/F6).</summary>
    public required string ApplyToken { get; init; }

    /// <summary>What was active before. For an apply this is the return path; for a rollback it is what was undone.</summary>
    public required string PreviousStateRef { get; init; }

    /// <summary>What this flip activated.</summary>
    public required string NewStateRef { get; init; }
}

/// <summary>Host policy knobs. Defaults are the safe ones.</summary>
public sealed record StagePolicy
{
    /// <summary>Applying through an adapter without the atomic swap primitive requires explicit consent (fault-model §4).</summary>
    public bool AcceptDegradedAdapter { get; init; }

    /// <summary>
    /// Refuse to recover from a ledger whose history does not verify, instead
    /// of reporting the verdict and continuing.
    ///
    /// <para>Off by default, and that default is the substantive choice. The
    /// ledger is recovery's judgement input, so a damaged one is precisely
    /// when a host may most need to recover — refusing by default would let a
    /// failed integrity check take the recovery path down with it. A host
    /// under an obligation that makes proceeding the wrong answer turns this
    /// on; the library does not weigh that obligation on anyone's behalf.</para>
    ///
    /// <para>Only a <c>broken</c> verdict refuses. History written before the
    /// ledger began chaining verifies as <c>unverifiable</c>, which is a
    /// statement about coverage, not a finding — refusing on it would make
    /// this switch unusable for exactly the deployments that have history.</para>
    /// </summary>
    public bool RequireIntactLedger { get; init; }

    public static StagePolicy Default { get; } = new();
}

/// <summary>
/// The lifecycle state machine (README §The lifecycle):
/// proposed → branched → simulated → applied, with discarded / rolled back exits.
/// One session drives one changeset against one target. v0 serializes applies
/// per target (fault-model §5).
/// </summary>
public sealed class ChangeSession
{
    private readonly JsonObject _changeset;
    private readonly IBackendAdapter _adapter;
    private readonly ReleaseLedger _ledger;
    private readonly StagePolicy _policy;
    private readonly TimeProvider _clock;

    private BranchInfo? _branch;
    private JsonObject? _simulationEvidence;

    public SessionState State { get; private set; }
    public string Target { get; }
    public string Fingerprint { get; }
    public FidelityDeclaration? Fidelity => _branch?.Fidelity;

    /// <summary>Validates and admits a changeset document. Only stamped, spec-valid documents enter the lifecycle.</summary>
    public ChangeSession(
        JsonObject changeset, string target, IBackendAdapter adapter, ReleaseLedger ledger,
        StagePolicy? policy = null, TimeProvider? clock = null)
    {
        var validation = ChangesetValidator.Validate(changeset);
        if (!validation.Valid)
            // the validator already answers "where and why" per error; joining that
            // into one sentence and making the host split it back apart would be
            // throwing away structure Stage was handed.
            throw new StageRefusedException(RefusalReason.InvalidChangeset,
                "changeset does not validate: " + string.Join("; ", validation.Errors.Select(e => $"{e.Path}: {e.Message}")),
                new JsonObject
                {
                    ["errors"] = new JsonArray(validation.Errors
                        .Select(e => (JsonNode)new JsonObject { ["path"] = e.Path, ["message"] = e.Message })
                        .ToArray()),
                });
        if (!ChangesetFingerprint.Verify(changeset))
            throw new StageRefusedException(RefusalReason.FingerprintGate,
                "changeset fingerprint is missing or does not match its content (spec §6)");

        _changeset = (JsonObject)changeset.DeepClone();
        Target = target;
        _adapter = adapter;
        _ledger = ledger;
        _policy = policy ?? StagePolicy.Default;
        _clock = clock ?? TimeProvider.System;
        Fingerprint = _changeset["fingerprint"]!.GetValue<string>();
        State = SessionState.Proposed;
    }

    /// <summary>
    /// Reconstruct an Applied session after a process restart — verified, never
    /// asserted (fault-model §3: the ledger and the active state decide). This
    /// is what keeps "every apply has a return path" (fixed principle 4) true
    /// across process lifetimes: rollback needs an Applied session, and a
    /// restarted host has no other constitutional way to obtain one.
    /// Refuses unless (1) the target has no unreconciled pending entry (run
    /// <see cref="StageRecovery"/> first), (2) the target's latest completed
    /// ledger entry is an <c>apply-completed</c> of exactly this changeset, and
    /// (3) the live active state ref equals that entry's new state ref.
    /// </summary>
    public static async Task<ChangeSession> RehydrateAppliedAsync(
        JsonObject changeset, string target, IBackendAdapter adapter, ReleaseLedger ledger,
        StagePolicy? policy = null, TimeProvider? clock = null, CancellationToken ct = default)
    {
        // the constructor runs the admission gates (spec validity, fingerprint)
        var session = new ChangeSession(changeset, target, adapter, ledger, policy, clock);

        var entries = await ledger.ReadAllAsync(ct).ConfigureAwait(false);
        LedgerProjection.Replay(entries).TryGetValue(target, out var view);
        if (view?.PendingStarted is not null)
            throw new StageRefusedException(RefusalReason.InvalidStateTransition,
                $"target '{target}' has an unreconciled started entry (token {view.PendingStarted.ApplyToken}) — run recovery before rehydrating",
                new JsonObject
                {
                    ["target"] = target,
                    ["unreconciledApplyToken"] = view.PendingStarted.ApplyToken,
                });
        var latest = view?.AppliedHistory.LastOrDefault();
        if (latest is null || latest.Kind != "apply-completed" || latest.ChangesetFingerprint != session.Fingerprint)
            throw new StageRefusedException(RefusalReason.InvalidStateTransition,
                $"the ledger's latest completed entry for '{target}' is not an apply of changeset {session.Fingerprint} — nothing to rehydrate");

        var active = await adapter.ActiveStateAsync(target, ct).ConfigureAwait(false);
        if (latest.NewStateRef is null || active.StateRef != latest.NewStateRef)
            throw new StageRefusedException(RefusalReason.DriftGate,
                $"live active state '{active.StateRef}' does not match the ledger's applied state '{latest.NewStateRef}' — refusing, not guessing",
                new JsonObject
                {
                    ["scope"] = "active-state",
                    ["target"] = target,
                    ["expected"] = latest.NewStateRef,
                    ["actual"] = active.StateRef,
                });

        session.State = SessionState.Applied;
        return session;
    }

    private void RequireState(SessionState expected, string operation)
    {
        if (State != expected)
            throw new StageRefusedException(RefusalReason.InvalidStateTransition,
                $"{operation} requires state {expected}, but session is {State}",
                new JsonObject
                {
                    ["operation"] = operation,
                    ["expectedState"] = expected.ToString(),
                    ["actualState"] = State.ToString(),
                });
    }

    /// <summary>proposed → branched. A branch without a fidelity declaration cannot enter simulation.</summary>
    public async Task<BranchInfo> BranchAsync(CancellationToken ct = default)
    {
        RequireState(SessionState.Proposed, "branch");
        var branch = await _adapter.BranchAsync(Target, ct).ConfigureAwait(false);
        if (branch.Fidelity is null || branch.Fidelity.PerFacet.Count == 0)
            throw new StageRefusedException(RefusalReason.InvalidChangeset,
                "adapter returned a branch without a fidelity declaration (adapter-api §3)");
        _branch = branch;
        State = SessionState.Branched;
        return branch;
    }

    /// <summary>
    /// branched → simulated. Simulation itself is host territory (adapter-api §3);
    /// Stage records that it happened and what was observed. The branch's fidelity
    /// declaration is the interpretation rule for this evidence.
    /// </summary>
    public void RecordSimulation(JsonObject? evidence = null)
    {
        RequireState(SessionState.Branched, "simulate");
        _simulationEvidence = evidence is null ? null : (JsonObject)evidence.DeepClone();
        State = SessionState.Simulated;
    }

    /// <summary>
    /// simulated → applied. Runs every gate, then prepare-all → write-ahead
    /// ledger → atomic flip → completion ledger (fault-model §1, §3).
    /// Retryable after transient failure: prepare is idempotent per fingerprint
    /// and flip is idempotent under the apply token.
    ///
    /// <para>Returns what this apply landed (<see cref="FlipOutcome"/>) — the same
    /// facts it writes to the ledger. Reading them back with
    /// <c>ActiveStateAsync</c> instead answers a different question and races a
    /// concurrent flip.</para>
    /// </summary>
    public async Task<FlipOutcome> ApplyAsync(string actor, string? applyToken = null, CancellationToken ct = default)
    {
        RequireState(SessionState.Simulated, "apply");
        var branch = _branch!;

        // Gate 1 — fingerprint gate (fixed principle 2): exactly a reviewed
        // fingerprint, verified against content and against an approval record.
        if (!ChangesetFingerprint.Verify(_changeset))
            throw new StageRefusedException(RefusalReason.FingerprintGate,
                "changeset fingerprint no longer matches its content");
        var approved = (_changeset["approvals"] as JsonArray)?
            .OfType<JsonObject>()
            .Any(a => a["fingerprint"]?.GetValue<string>() == Fingerprint) ?? false;
        if (!approved)
            throw new StageRefusedException(RefusalReason.FingerprintGate,
                $"no approval record matches fingerprint {Fingerprint} (spec approval gate)");

        // Gate 2 — degraded adapter requires explicit host consent (fault-model §4).
        if (!_adapter.Capabilities.Flip.AtomicSwap && !_policy.AcceptDegradedAdapter)
            throw new StageRefusedException(RefusalReason.DegradedAdapter,
                "adapter declares a non-atomic flip and host policy does not accept the degradation: "
                + _adapter.Capabilities.Flip.DegradationDescription);

        // Gate 3 — drift refusal (fixed principle 3): every state-kind base
        // entry must match the live target exactly; unknown refs refuse too.
        // Every entry is checked before refusing: re-basing is the author's job,
        // and sending them back one drifted ref at a time makes it N round trips
        // to learn what one refusal already knew.
        var active = await _adapter.ActiveStateAsync(Target, ct).ConfigureAwait(false);
        var baseState = (JsonArray)_changeset["provenance"]!["baseState"]!;
        var drifted = new JsonArray();
        var driftSummaries = new List<string>();
        foreach (var node in baseState)
        {
            // entry shape and kind vocabulary are spec-validated at admission
            // (spec §4 — the ctor's Validate refuses malformed entries)
            var entry = (JsonObject)node!;
            var kind = entry["kind"]!.GetValue<string>();
            if (kind == "changeset") continue; // authoring lineage, not live state
            var reference = entry["ref"]!.GetValue<string>();
            var expected = entry["fingerprint"]!.GetValue<string>();
            var present = active.FacetFingerprints.TryGetValue(reference, out var actual);
            if (present && actual == expected) continue;
            drifted.Add(new JsonObject
            {
                ["kind"] = kind,
                ["ref"] = reference,
                ["expected"] = expected,
                // absent is not merely different: an author re-bases a changed ref,
                // but a missing one means the proposal is aimed at something else
                ["actual"] = present ? actual : null,
            });
            driftSummaries.Add(present
                ? $"'{reference}' has drifted ({actual} != {expected})"
                : $"'{reference}' is not present in the live target");
        }
        if (drifted.Count > 0)
            throw new StageRefusedException(RefusalReason.DriftGate,
                $"the changeset's base state does not match the live target — {string.Join("; ", driftSummaries)}; "
                + "re-basing is the author's job",
                new JsonObject
                {
                    ["scope"] = "base-state",
                    ["drifted"] = drifted,
                    ["knownRefs"] = new JsonArray(active.FacetFingerprints.Keys
                        .OrderBy(k => k, StringComparer.Ordinal).Select(k => (JsonNode)k!).ToArray()),
                });

        // Prepare-all: every facet staged and confirmed before any flip
        // (fixed principle 1). No live effect; failure here is F2 territory.
        var patches = (JsonObject)_changeset["patches"]!.DeepClone();
        var report = await _adapter.PrepareAsync(branch.BranchRef, new PreparedFacets(Fingerprint, patches), ct)
            .ConfigureAwait(false);
        if (!report.AllComplete)
        {
            var incomplete = report.FacetComplete.Where(kv => !kv.Value).Select(kv => kv.Key)
                .OrderBy(k => k, StringComparer.Ordinal).ToArray();
            throw new StageRefusedException(RefusalReason.PrepareIncomplete,
                $"prepare did not confirm all facets (incomplete: {string.Join(", ", incomplete)}) — refusing to flip",
                new JsonObject
                {
                    ["incompleteFacets"] = new JsonArray(incomplete.Select(f => (JsonNode)f!).ToArray()),
                });
        }

        // Write-ahead ledger, then the one atomic mutation, then completion (fault-model §3).
        var token = applyToken ?? Guid.NewGuid().ToString("n");
        var previousStateRef = active.StateRef;
        var now = _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        await _ledger.AppendAsync("apply-started", Target, Fingerprint, token, actor, now,
            fidelity: branch.Fidelity.ToJson(), previousStateRef: previousStateRef, newStateRef: branch.BranchRef, ct: ct)
            .ConfigureAwait(false);

        await _adapter.FlipAsync(Target, branch.BranchRef, token, ct).ConfigureAwait(false);

        await _ledger.AppendAsync("apply-completed", Target, Fingerprint, token, actor,
            _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), newStateRef: branch.BranchRef, ct: ct)
            .ConfigureAwait(false);
        State = SessionState.Applied;

        return new FlipOutcome
        {
            Operation = "apply",
            Target = Target,
            ChangesetFingerprint = Fingerprint,
            ApplyToken = token,
            PreviousStateRef = previousStateRef,
            NewStateRef = branch.BranchRef,
        };
    }

    /// <summary>
    /// applied → rolled back: a re-flip to the pre-apply state through the same
    /// primitive (fixed principle 4 — every apply has a return path).
    ///
    /// <para>Returns what this rollback landed (<see cref="FlipOutcome"/>), with the
    /// refs read from the apply it undoes rather than from the live pointer.</para>
    /// </summary>
    public async Task<FlipOutcome> RollbackAsync(string actor, string? applyToken = null, CancellationToken ct = default)
    {
        RequireState(SessionState.Applied, "rollback");
        var entries = await _ledger.ReadAllAsync(ct).ConfigureAwait(false);
        var myApply = entries.LastOrDefault(e =>
            e.Kind == "apply-started" && e.Target == Target && e.ChangesetFingerprint == Fingerprint)
            ?? throw new StageRefusedException(RefusalReason.InvalidStateTransition,
                "no apply-started ledger entry found for this session — cannot derive the return path");
        if (myApply.PreviousStateRef is null)
            throw new StageRefusedException(RefusalReason.InvalidStateTransition,
                "apply recorded no previous state ref — this apply declared no return path");
        if (myApply.NewStateRef is null)
            throw new StageRefusedException(RefusalReason.InvalidStateTransition,
                "apply recorded no new state ref — the entry cannot say what this rollback would undo",
                new JsonObject { ["applyToken"] = myApply.ApplyToken });

        var token = applyToken ?? Guid.NewGuid().ToString("n");
        var now = _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        await _ledger.AppendAsync("rollback-started", Target, Fingerprint, token, actor, now,
            previousStateRef: myApply.NewStateRef, newStateRef: myApply.PreviousStateRef, ct: ct)
            .ConfigureAwait(false);

        await _adapter.FlipAsync(Target, myApply.PreviousStateRef, token, ct).ConfigureAwait(false);

        await _ledger.AppendAsync("rollback-completed", Target, Fingerprint, token, actor,
            _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), newStateRef: myApply.PreviousStateRef, ct: ct)
            .ConfigureAwait(false);
        State = SessionState.RolledBack;

        return new FlipOutcome
        {
            Operation = "rollback",
            Target = Target,
            ChangesetFingerprint = Fingerprint,
            ApplyToken = token,
            // the apply's new ref is what a rollback leaves behind, and its previous
            // ref is where the target returns to — the axis is the operation's, not
            // the ledger entry's
            PreviousStateRef = myApply.NewStateRef,
            NewStateRef = myApply.PreviousStateRef,
        };
    }

    /// <summary>Any pre-apply state → discarded. Staging never touches live state, so this is always safe.</summary>
    public async Task DiscardAsync(CancellationToken ct = default)
    {
        if (State is SessionState.Applied or SessionState.RolledBack or SessionState.Discarded)
            throw new StageRefusedException(RefusalReason.InvalidStateTransition,
                $"discard is a pre-apply exit; session is {State}");
        if (_branch is not null)
            await _adapter.DiscardAsync(_branch.BranchRef, ct).ConfigureAwait(false);
        State = SessionState.Discarded;
    }
}
