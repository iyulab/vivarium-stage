using Vivarium.Stage.Adapters;
using Vivarium.Stage.Ledger;

namespace Vivarium.Stage;

public sealed record RecoveryOutcome(string Target, string ApplyToken, string Resolution); // completed | aborted | none

/// <summary>
/// Post-crash ledger reconciliation (fault-model §3, F5): a started-without-
/// completed entry is resolved by reading which state is actually active —
/// the active state's fingerprint decides, the ledger never guesses.
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
            var now = clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            var flipHappened = active.StateRef == started.NewStateRef;
            var completionKind = started.Kind == "rollback-started"
                ? (flipHappened ? "rollback-completed" : "apply-aborted")
                : (flipHappened ? "apply-completed" : "apply-aborted");

            await ledger.AppendAsync(completionKind, target, started.ChangesetFingerprint,
                started.ApplyToken, "stage-recovery", now,
                previousStateRef: started.PreviousStateRef, newStateRef: started.NewStateRef,
                reconciled: true, ct: ct).ConfigureAwait(false);

            outcomes.Add(new RecoveryOutcome(target, started.ApplyToken, flipHappened ? "completed" : "aborted"));
        }
        return outcomes;
    }
}
