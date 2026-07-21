using Vivarium.Stage.Adapters;
using Vivarium.Stage.Ledger;

namespace Vivarium.Stage;

public sealed record RecoveryOutcome(string Target, string ApplyToken, string Resolution); // completed | aborted | unresolved

/// <summary>
/// Post-crash ledger reconciliation (fault-model §3, F5/F6): a started-without-
/// completed entry is resolved by reading which state is actually active —
/// the active state decides, the ledger never guesses. When the active state
/// is neither the started entry's new nor previous ref (out-of-band change),
/// recovery reports <c>unresolved</c> and appends nothing: an append here
/// would be a guess forged into an append-only audit trail.
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

            var active = await adapter.ActiveStateAsync(target, ct).ConfigureAwait(false);
            var isRollback = started.Kind == "rollback-started";
            var completionKind =
                active.StateRef == started.NewStateRef
                    ? (isRollback ? "rollback-completed" : "apply-completed")
                : active.StateRef == started.PreviousStateRef
                    ? (isRollback ? "rollback-aborted" : "apply-aborted")
                : null; // neither old nor new — refusing to guess (fixed principle 3)
            if (completionKind is null)
            {
                outcomes.Add(new RecoveryOutcome(target, started.ApplyToken, "unresolved"));
                continue;
            }

            var now = clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            await ledger.AppendAsync(completionKind, target, started.ChangesetFingerprint,
                started.ApplyToken, "stage-recovery", now,
                previousStateRef: started.PreviousStateRef, newStateRef: started.NewStateRef,
                reconciled: true, ct: ct).ConfigureAwait(false);

            outcomes.Add(new RecoveryOutcome(target, started.ApplyToken,
                completionKind.EndsWith("-completed") ? "completed" : "aborted"));
        }
        return outcomes;
    }
}
