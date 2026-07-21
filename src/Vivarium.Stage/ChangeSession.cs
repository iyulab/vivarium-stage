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
}

/// <summary>A gate refusal. Stage refuses loudly and specifically — it never guesses (fixed principle 3).</summary>
public sealed class StageRefusedException(RefusalReason reason, string message) : Exception(message)
{
    public RefusalReason Reason { get; } = reason;
}

/// <summary>Host policy knobs. Defaults are the safe ones.</summary>
public sealed record StagePolicy
{
    /// <summary>Applying through an adapter without the atomic swap primitive requires explicit consent (fault-model §4).</summary>
    public bool AcceptDegradedAdapter { get; init; }

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
            throw new StageRefusedException(RefusalReason.InvalidChangeset,
                "changeset does not validate: " + string.Join("; ", validation.Errors.Select(e => $"{e.Path}: {e.Message}")));
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
                $"target '{target}' has an unreconciled started entry (token {view.PendingStarted.ApplyToken}) — run recovery before rehydrating");
        var latest = view?.AppliedHistory.LastOrDefault();
        if (latest is null || latest.Kind != "apply-completed" || latest.ChangesetFingerprint != session.Fingerprint)
            throw new StageRefusedException(RefusalReason.InvalidStateTransition,
                $"the ledger's latest completed entry for '{target}' is not an apply of changeset {session.Fingerprint} — nothing to rehydrate");

        var active = await adapter.ActiveStateAsync(target, ct).ConfigureAwait(false);
        if (latest.NewStateRef is null || active.StateRef != latest.NewStateRef)
            throw new StageRefusedException(RefusalReason.DriftGate,
                $"live active state '{active.StateRef}' does not match the ledger's applied state '{latest.NewStateRef}' — refusing, not guessing");

        session.State = SessionState.Applied;
        return session;
    }

    private void RequireState(SessionState expected, string operation)
    {
        if (State != expected)
            throw new StageRefusedException(RefusalReason.InvalidStateTransition,
                $"{operation} requires state {expected}, but session is {State}");
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
    /// </summary>
    public async Task ApplyAsync(string actor, string? applyToken = null, CancellationToken ct = default)
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
        var active = await _adapter.ActiveStateAsync(Target, ct).ConfigureAwait(false);
        var baseState = (JsonArray)_changeset["provenance"]!["baseState"]!;
        foreach (var node in baseState)
        {
            // entry shape and kind vocabulary are spec-validated at admission
            // (spec 0.2 §4 — the ctor's Validate refuses malformed entries)
            var entry = (JsonObject)node!;
            var kind = entry["kind"]!.GetValue<string>();
            if (kind == "changeset") continue; // authoring lineage, not live state
            var reference = entry["ref"]!.GetValue<string>();
            var expected = entry["fingerprint"]!.GetValue<string>();
            if (!active.FacetFingerprints.TryGetValue(reference, out var actual))
                throw new StageRefusedException(RefusalReason.DriftGate,
                    $"base state ref '{reference}' is not present in the live target — refusing, not guessing");
            if (actual != expected)
                throw new StageRefusedException(RefusalReason.DriftGate,
                    $"live state of '{reference}' has drifted from the changeset's base ({actual} != {expected}); re-basing is the author's job");
        }

        // Prepare-all: every facet staged and confirmed before any flip
        // (fixed principle 1). No live effect; failure here is F2 territory.
        var patches = (JsonObject)_changeset["patches"]!.DeepClone();
        var report = await _adapter.PrepareAsync(branch.BranchRef, new PreparedFacets(Fingerprint, patches), ct)
            .ConfigureAwait(false);
        if (!report.AllComplete)
        {
            var missing = string.Join(", ", report.FacetComplete.Where(kv => !kv.Value).Select(kv => kv.Key));
            throw new StageRefusedException(RefusalReason.PrepareIncomplete,
                $"prepare did not confirm all facets (incomplete: {missing}) — refusing to flip");
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
    }

    /// <summary>
    /// applied → rolled back: a re-flip to the pre-apply state through the same
    /// primitive (fixed principle 4 — every apply has a return path).
    /// </summary>
    public async Task RollbackAsync(string actor, string? applyToken = null, CancellationToken ct = default)
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
