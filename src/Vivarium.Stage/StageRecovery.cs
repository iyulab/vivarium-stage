using System.Text.Json.Nodes;
using Vivarium.Stage.Adapters;
using Vivarium.Stage.Ledger;

namespace Vivarium.Stage;

/// <summary>
/// One target's reconciliation verdict, on the two axes the fault-model §3
/// truth table keeps apart. <see cref="PendingOperation"/> is which operation
/// was in flight (<c>apply</c> | <c>rollback</c> — the truth table's row) and
/// <see cref="Resolution"/> is what recovery concluded about it
/// (<c>completed</c> | <c>aborted</c> | <c>unresolved</c>): together they name
/// the entry kind reconciliation appended (<c>rollback</c> + <c>aborted</c> →
/// <c>rollback-aborted</c>), which is why an aborted rollback can never be read
/// as an aborted apply. <see cref="Reason"/> is why, in the same vocabulary —
/// an operator needs to tell "the pointer says something else" from "the
/// pointer could not be read at all", because those call for different
/// interventions.
/// </summary>
/// <remarks>
/// Members are init-only properties rather than positional: this record grows
/// as recovery learns to report more of what the ledger already knows, and with
/// positional members every such addition breaks consumers who deconstruct it.
/// One break (0.4.0) to stop breaking.
/// </remarks>
public sealed record RecoveryOutcome
{
    public required string Target { get; init; }
    public required string ApplyToken { get; init; }
    public required string ChangesetFingerprint { get; init; }

    /// <summary>apply | rollback — the operation the reconciled pending entry started.</summary>
    public required string PendingOperation { get; init; }

    /// <summary>completed | aborted | unresolved</summary>
    public required string Resolution { get; init; }

    /// <summary>
    /// active-matches-new | active-matches-previous | active-matches-neither
    /// | active-state-unreadable | operator-declared
    ///
    /// <para><c>operator-declared</c> is the one verdict the library did not
    /// reach by reading anything: an operator resolved the target out of band
    /// (<see cref="StageRecovery.ResolveAsync"/>). It is kept apart from the
    /// four the active state supports, because a resolution asserted by a
    /// person and one verified against live state are not the same claim, and
    /// an audit that cannot tell them apart is worth less than one that
    /// can.</para>
    /// </summary>
    public required string Reason { get; init; }
}

/// <summary>
/// What one recovery sweep found: whether the ledger it read can be trusted,
/// and what it concluded per target.
///
/// <para>The integrity verdict is on the report rather than on each outcome
/// for a reason that only shows in the quiet case — a ledger can be tampered
/// with and still leave nothing pending, and a per-outcome verdict would
/// vanish exactly then. Recovery reads the ledger as its judgement input, so
/// the state of that input is reported whether or not it had anything to
/// decide.</para>
/// </summary>
public sealed record RecoveryReport
{
    public required LedgerIntegrityReport Integrity { get; init; }

    /// <summary>One verdict per target that had an operation in flight. Empty when none did.</summary>
    public required IReadOnlyList<RecoveryOutcome> Outcomes { get; init; }
}

/// <summary>
/// Post-crash ledger reconciliation (fault-model §3, F5/F6): a started-without-
/// completed entry is resolved by reading which state is actually active —
/// the active state decides, the ledger never guesses. Two cases append
/// nothing and report <c>unresolved</c> instead: the active state is neither
/// the started entry's new nor previous ref (out-of-band change), or the
/// active state cannot be read at all (the adapter does not know the target).
/// Appending either would be a guess forged into an append-only audit trail.
/// Reconciliation appends; it never rewrites.
/// </summary>
public static class StageRecovery
{
    /// <summary>
    /// The actor recovery writes on entries it reconciled by reading live
    /// state. Reserved: <see cref="ResolveAsync"/> refuses it, so an operator's
    /// assertion can never be recorded as the library's verification.
    /// </summary>
    public const string RecoveryActor = "stage-recovery";

    public static async Task<RecoveryReport> RecoverAsync(
        ReleaseLedger ledger, IBackendAdapter adapter, TimeProvider? clock = null,
        StagePolicy? policy = null, CancellationToken ct = default)
    {
        clock ??= TimeProvider.System;
        policy ??= StagePolicy.Default;
        var outcomes = new List<RecoveryOutcome>();
        var entries = await ledger.ReadAllAsync(ct).ConfigureAwait(false);

        // The ledger is recovery's judgement input, so its integrity is read
        // before anything is concluded from it. Reporting rather than refusing
        // is the default on purpose: a damaged ledger is exactly the situation
        // in which a host may most need to recover, and refusing by default
        // would hold availability hostage to a check. A host for whom refusing
        // is the correct answer says so (fault-model §4's shape — consent is
        // explicit, never assumed).
        var integrity = LedgerIntegrity.Verify(entries);
        if (policy.RequireIntactLedger && integrity.Verdict == "broken")
            throw new StageRefusedException(
                RefusalReason.LedgerIntegrityGate,
                "the release ledger does not verify: "
                    + string.Join("; ", integrity.Findings.Select(f => f.Message)),
                new JsonObject
                {
                    ["verdict"] = integrity.Verdict,
                    ["unverifiedPrefix"] = integrity.UnverifiedPrefix,
                    ["findings"] = new JsonArray(integrity.Findings
                        .Select(f => (JsonNode)new JsonObject
                        {
                            ["seq"] = f.Seq,
                            ["kind"] = f.Kind,
                            ["message"] = f.Message,
                        })
                        .ToArray()),
                });

        var projection = LedgerProjection.Replay(entries);

        foreach (var (target, view) in projection)
        {
            if (view.PendingStarted is not { } started) continue;

            // Reading the active pointer is a judgement input, not a fatal step:
            // an adapter that does not know this target is being honest (it will
            // not invent a pointer), and one unreadable target must not abort the
            // sweep for every other one. Cancellation is NOT this case — a
            // cancelled caller must surface as cancellation, not as a verdict.
            ActiveState? active;
            try
            {
                active = await adapter.ActiveStateAsync(target, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                outcomes.Add(Unresolved(target, started, "active-state-unreadable"));
                continue;
            }

            var isRollback = OperationOf(started) == "rollback";
            var (completionKind, reason) =
                active.StateRef == started.NewStateRef
                    ? (isRollback ? "rollback-completed" : "apply-completed", "active-matches-new")
                : active.StateRef == started.PreviousStateRef
                    ? (isRollback ? "rollback-aborted" : "apply-aborted", "active-matches-previous")
                : (null, "active-matches-neither"); // refusing to guess (fixed principle 3)
            if (completionKind is null)
            {
                outcomes.Add(Unresolved(target, started, reason));
                continue;
            }

            // A failed append is NOT a verdict — it means the audit trail itself
            // is broken, so it propagates rather than being reported per-target.
            var now = clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            await ledger.AppendAsync(completionKind, target, started.ChangesetFingerprint,
                started.ApplyToken, RecoveryActor, now,
                previousStateRef: started.PreviousStateRef, newStateRef: started.NewStateRef,
                reconciled: true, ct: ct).ConfigureAwait(false);

            outcomes.Add(Outcome(target, started,
                completionKind.EndsWith("-completed") ? "completed" : "aborted", reason));
        }
        return new RecoveryReport { Integrity = integrity, Outcomes = outcomes };
    }

    /// <summary>
    /// Record an operator's resolution of a target recovery left
    /// <c>unresolved</c> — the intervention point recovery deliberately stops
    /// at when the active state cannot decide the question.
    ///
    /// <para>Until now this was reachable only by calling
    /// <see cref="ReleaseLedger.AppendAsync"/> by hand, which is to say by
    /// writing whatever one likes into an append-only audit trail: a
    /// completion for a target with nothing in flight, under a token no entry
    /// carries, naming a state that was never staged. Every one of those is
    /// permanent. This method admits only a resolution the pending entry can
    /// actually take, and takes the state refs from that entry rather than
    /// from the caller.</para>
    ///
    /// <para>What it does not do is pretend to have checked. The outcome's
    /// reason is <c>operator-declared</c>, and <paramref name="actor"/> may not
    /// be <see cref="RecoveryActor"/> — an operator is entitled to assert what
    /// the library refused to guess, but not to have the assertion recorded as
    /// the library's own verification.</para>
    /// </summary>
    /// <param name="resolution">completed | aborted — what the operator declares happened.</param>
    /// <param name="actor">Who is declaring it. Recorded on the entry; may not be <see cref="RecoveryActor"/>.</param>
    public static async Task<RecoveryOutcome> ResolveAsync(
        ReleaseLedger ledger, string target, string resolution, string actor,
        TimeProvider? clock = null, CancellationToken ct = default)
    {
        if (resolution is not ("completed" or "aborted"))
            throw new ArgumentException(
                $"unknown resolution: {resolution} (expected one of: completed, aborted)", nameof(resolution));
        if (actor == RecoveryActor)
            throw new ArgumentException(
                $"'{RecoveryActor}' is reserved for resolutions the library verified against live state; "
                    + "an operator resolution records who declared it", nameof(actor));
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("an operator resolution must name who declared it", nameof(actor));

        clock ??= TimeProvider.System;
        var projection = LedgerProjection.Replay(await ledger.ReadAllAsync(ct).ConfigureAwait(false));

        // Nothing in flight is not a thing to resolve. Appending anyway would
        // manufacture history — the same refusal recovery makes when the
        // active state matches neither ref.
        if (!projection.TryGetValue(target, out var view) || view.PendingStarted is not { } started)
            throw new StageRefusedException(
                RefusalReason.InvalidStateTransition,
                $"target '{target}' has no operation in flight to resolve",
                new JsonObject { ["target"] = target, ["expected"] = "pending-started", ["actual"] = "none" });

        var isRollback = OperationOf(started) == "rollback";
        var completionKind = (isRollback, resolution) switch
        {
            (true, "completed") => "rollback-completed",
            (true, _) => "rollback-aborted",
            (false, "completed") => "apply-completed",
            _ => "apply-aborted",
        };

        var now = clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        await ledger.AppendAsync(completionKind, target, started.ChangesetFingerprint,
            started.ApplyToken, actor, now,
            previousStateRef: started.PreviousStateRef, newStateRef: started.NewStateRef,
            reconciled: true, ct: ct).ConfigureAwait(false);

        return Outcome(target, started, resolution, "operator-declared");
    }

    private static RecoveryOutcome Unresolved(string target, LedgerEntry started, string reason) =>
        Outcome(target, started, "unresolved", reason);

    private static RecoveryOutcome Outcome(string target, LedgerEntry started, string resolution, string reason) =>
        new()
        {
            Target = target,
            ApplyToken = started.ApplyToken,
            ChangesetFingerprint = started.ChangesetFingerprint,
            PendingOperation = OperationOf(started),
            Resolution = resolution,
            Reason = reason,
        };

    /// <summary>
    /// Total by construction: a projection's pending entry is one of the two
    /// started kinds (<see cref="LedgerProjection"/>), and the ledger's write
    /// door rejects any kind outside that vocabulary — so every outcome,
    /// including the unresolved ones, carries an operation.
    /// </summary>
    private static string OperationOf(LedgerEntry started) =>
        started.Kind == "rollback-started" ? "rollback" : "apply";
}
