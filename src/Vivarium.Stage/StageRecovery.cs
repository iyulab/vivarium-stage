using Vivarium.Stage.Adapters;
using Vivarium.Stage.Ledger;

namespace Vivarium.Stage;

/// <summary>
/// One target's reconciliation verdict. <see cref="Resolution"/> is what
/// recovery concluded (<c>completed</c> | <c>aborted</c> | <c>unresolved</c>);
/// <see cref="Reason"/> is why, in the vocabulary of the fault-model §3 truth
/// table — an operator needs to tell "the pointer says something else" from
/// "the pointer could not be read at all", because those call for different
/// interventions.
/// </summary>
public sealed record RecoveryOutcome(
    string Target,
    string ApplyToken,
    string ChangesetFingerprint,
    string Resolution, // completed | aborted | unresolved
    string Reason); // active-matches-new | active-matches-previous | active-matches-neither | active-state-unreadable

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
    public static async Task<IReadOnlyList<RecoveryOutcome>> RecoverAsync(
        ReleaseLedger ledger, IBackendAdapter adapter, TimeProvider? clock = null, CancellationToken ct = default)
    {
        clock ??= TimeProvider.System;
        var outcomes = new List<RecoveryOutcome>();
        var projection = LedgerProjection.Replay(await ledger.ReadAllAsync(ct).ConfigureAwait(false));

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

            var isRollback = started.Kind == "rollback-started";
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
                started.ApplyToken, "stage-recovery", now,
                previousStateRef: started.PreviousStateRef, newStateRef: started.NewStateRef,
                reconciled: true, ct: ct).ConfigureAwait(false);

            outcomes.Add(new RecoveryOutcome(target, started.ApplyToken, started.ChangesetFingerprint,
                completionKind.EndsWith("-completed") ? "completed" : "aborted", reason));
        }
        return outcomes;
    }

    private static RecoveryOutcome Unresolved(string target, LedgerEntry started, string reason) =>
        new(target, started.ApplyToken, started.ChangesetFingerprint, "unresolved", reason);
}
