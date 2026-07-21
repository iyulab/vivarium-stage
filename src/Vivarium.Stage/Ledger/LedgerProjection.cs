namespace Vivarium.Stage.Ledger;

/// <summary>
/// Replayed view of one target's release history.
/// <see cref="ActiveChangesetFingerprint"/> names the changeset whose apply
/// produced the active state — after a rollback it follows the lineage back
/// (the prior apply's fingerprint, or null when the active state precedes
/// any recorded apply). It never names the rolled-back changeset: a
/// changeset that is no longer live must not be reported as live.
/// </summary>
public sealed record TargetProjection(
    string Target,
    IReadOnlyList<LedgerEntry> AppliedHistory,
    string? ActiveStateRef,
    string? ActiveChangesetFingerprint,
    LedgerEntry? PendingStarted);

/// <summary>
/// Deterministic replay of the ledger (acceptance: state is recoverable from
/// the ledger alone). A started-without-completed apply surfaces as
/// <see cref="TargetProjection.PendingStarted"/> — the input to F5 recovery.
/// </summary>
public static class LedgerProjection
{
    public static IReadOnlyDictionary<string, TargetProjection> Replay(IEnumerable<LedgerEntry> entries)
    {
        var applied = new Dictionary<string, List<LedgerEntry>>();
        var active = new Dictionary<string, (string? StateRef, string? Fingerprint)>();
        var pending = new Dictionary<string, LedgerEntry>();
        var lineage = new Dictionary<string, string>(); // stateRef → fingerprint of the apply that produced it

        foreach (var e in entries.OrderBy(e => e.Seq))
        {
            switch (e.Kind)
            {
                case "apply-started":
                case "rollback-started":
                    pending[e.Target] = e;
                    break;
                case "apply-completed":
                case "rollback-completed":
                    // completion pairs with the started entry via the apply token
                    if (pending.TryGetValue(e.Target, out var started) && started.ApplyToken == e.ApplyToken)
                        pending.Remove(e.Target);
                    (applied.TryGetValue(e.Target, out var list) ? list : applied[e.Target] = []).Add(e);
                    if (e.Kind == "apply-completed")
                    {
                        if (e.NewStateRef is not null) lineage[e.NewStateRef] = e.ChangesetFingerprint;
                        active[e.Target] = (e.NewStateRef, e.ChangesetFingerprint);
                    }
                    else
                    {
                        // a rollback re-activates an earlier state: its fingerprint is
                        // whatever apply produced that state — never the rolled-back one
                        active[e.Target] = (e.NewStateRef,
                            e.NewStateRef is not null && lineage.TryGetValue(e.NewStateRef, out var fp) ? fp : null);
                    }
                    break;
                case "apply-aborted":
                case "rollback-aborted":
                    if (pending.TryGetValue(e.Target, out var aborted) && aborted.ApplyToken == e.ApplyToken)
                        pending.Remove(e.Target);
                    break;
            }
        }

        var targets = applied.Keys.Union(pending.Keys).Union(active.Keys);
        return targets.ToDictionary(t => t, t => new TargetProjection(
            t,
            applied.TryGetValue(t, out var h) ? h : [],
            active.TryGetValue(t, out var a) ? a.StateRef : null,
            active.TryGetValue(t, out var a2) ? a2.Fingerprint : null,
            pending.TryGetValue(t, out var p) ? p : null));
    }
}
