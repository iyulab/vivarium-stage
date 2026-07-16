namespace Vivarium.Stage.Ledger;

/// <summary>Replayed view of one target's release history.</summary>
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
                    active[e.Target] = (e.NewStateRef, e.ChangesetFingerprint);
                    break;
                case "apply-aborted":
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
