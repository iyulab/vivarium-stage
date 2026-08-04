using Vivarium.Stage.Adapters;
using Vivarium.Stage.Ledger;

namespace Vivarium.Stage.Tests;

/// <summary>
/// The ledger is recovery's judgement input, so recovery says what it thinks
/// of that input — and the host, not the library, decides whether a damaged
/// one is a reason to stop. These tests fix both halves: that the verdict is
/// always reported, and that refusing is opt-in.
/// </summary>
public class LedgerIntegrityInRecoveryTests
{
    private static async Task<(ReleaseLedger Ledger, InMemoryLedgerStore Store)> PendingAsync(string target = "app")
    {
        var store = new InMemoryLedgerStore();
        var ledger = new ReleaseLedger(store);
        await ledger.AppendAsync("apply-started", target, "sha256:aaa", "tok-1", "operator-1",
            "2026-08-04T00:00:00.000Z", previousStateRef: "live-app", newStateRef: "branch-1");
        return (ledger, store);
    }

    /// <summary>A store whose contents can be edited underneath the ledger, which is the whole threat.</summary>
    private static async Task<ReleaseLedger> TamperedLedgerAsync()
    {
        var (ledger, store) = await PendingAsync();
        await ledger.AppendAsync("apply-completed", "app", "sha256:aaa", "tok-1", "operator-1",
            "2026-08-04T00:00:01.000Z", newStateRef: "branch-1");

        var entries = await store.ReadAllAsync();
        var rewritten = new InMemoryLedgerStore();
        foreach (var e in entries)
            await rewritten.AppendAsync(e.Kind == "apply-completed"
                ? e with { ChangesetFingerprint = "sha256:forged" }
                : e);
        return new ReleaseLedger(rewritten);
    }

    [Fact]
    public async Task Recovery_reports_the_integrity_of_the_ledger_it_read()
    {
        var (ledger, _) = await PendingAsync();
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("app");

        var report = await StageRecovery.RecoverAsync(ledger, adapter, new FixedTimeProvider());

        Assert.Equal("intact", report.Integrity.Verdict);
    }

    /// <summary>
    /// The case the shape exists for: a ledger can be tampered with and still
    /// leave nothing in flight. A verdict carried per outcome would disappear
    /// exactly here — which is where an operator most needs to be told.
    /// </summary>
    [Fact]
    public async Task The_verdict_survives_a_sweep_that_had_nothing_to_resolve()
    {
        var ledger = await TamperedLedgerAsync(); // settled: no pending entry
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("app");

        var report = await StageRecovery.RecoverAsync(ledger, adapter, new FixedTimeProvider());

        Assert.Empty(report.Outcomes);
        Assert.Equal("broken", report.Integrity.Verdict);
    }

    /// <summary>
    /// Default is to report and continue. Stopping would hold availability
    /// hostage to a check, in the one situation where recovery is most needed.
    /// </summary>
    [Fact]
    public async Task A_broken_ledger_does_not_stop_recovery_unless_the_host_says_so()
    {
        var (ledger, store) = await PendingAsync();
        await store.AppendAsync(new LedgerEntry(9, "apply-started", "other", "sha256:bbb", "tok-9",
            "operator-1", "2026-08-04T00:00:02.000Z")); // unchained, after the chain began
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("app");

        var report = await StageRecovery.RecoverAsync(ledger, adapter, new FixedTimeProvider());

        Assert.Equal("broken", report.Integrity.Verdict);
        Assert.NotEmpty(report.Outcomes); // …and it still did its work
    }

    [Fact]
    public async Task A_host_that_asks_to_stop_is_refused_with_what_was_found()
    {
        var (ledger, store) = await PendingAsync();
        await store.AppendAsync(new LedgerEntry(9, "apply-started", "other", "sha256:bbb", "tok-9",
            "operator-1", "2026-08-04T00:00:02.000Z"));
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("app");

        var refusal = await Assert.ThrowsAsync<StageRefusedException>(() =>
            StageRecovery.RecoverAsync(ledger, adapter, new FixedTimeProvider(),
                new StagePolicy { RequireIntactLedger = true }));

        Assert.Equal(RefusalReason.LedgerIntegrityGate, refusal.Reason);
        Assert.Equal("broken", refusal.Details?["verdict"]?.GetValue<string>());
        Assert.NotEmpty(refusal.Details?["findings"]?.AsArray() ?? []);
        Assert.Equal(0, (await ledger.ReadAllAsync()).Count(e => e.Reconciled)); // nothing was appended
    }

    /// <summary>
    /// The control on the switch: history written before the ledger chained
    /// must not trip it. Refusing on "unverifiable" would make the switch
    /// unusable for precisely the deployments that have a past.
    /// </summary>
    [Fact]
    public async Task Unchained_history_is_not_treated_as_a_broken_ledger()
    {
        var store = new InMemoryLedgerStore();
        await store.AppendAsync(new LedgerEntry(1, "apply-started", "app", "sha256:aaa", "tok-1",
            "operator-1", "2026-08-04T00:00:00.000Z", PreviousStateRef: "live-app", NewStateRef: "branch-1"));
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("app");

        var report = await StageRecovery.RecoverAsync(new ReleaseLedger(store), adapter,
            new FixedTimeProvider(), new StagePolicy { RequireIntactLedger = true });

        Assert.Equal("unverifiable", report.Integrity.Verdict);
        Assert.Single(report.Outcomes);
    }
}

/// <summary>
/// The operator's half of reconciliation. Recovery stops at
/// <c>unresolved</c> on purpose — it will not guess — and until now the only
/// way past that point was to hand-write entries into an append-only trail.
/// </summary>
public class OperatorResolutionTests
{
    private static async Task<(ReleaseLedger Ledger, InMemoryBackendAdapter Adapter)> UnresolvedAsync()
    {
        var store = new InMemoryLedgerStore();
        var ledger = new ReleaseLedger(store);
        await ledger.AppendAsync("apply-started", "app", "sha256:aaa", "tok-1", "operator-1",
            "2026-08-04T00:00:00.000Z", previousStateRef: "stale-app", newStateRef: "never-staged");
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("app");
        return (ledger, adapter);
    }

    [Fact]
    public async Task Recovery_leaves_it_unresolved_and_the_operator_closes_it()
    {
        var (ledger, adapter) = await UnresolvedAsync();

        var swept = Assert.Single((await StageRecovery.RecoverAsync(ledger, adapter, new FixedTimeProvider())).Outcomes);
        Assert.Equal("unresolved", swept.Resolution);
        Assert.Equal("active-matches-neither", swept.Reason);

        var resolved = await StageRecovery.ResolveAsync(ledger, "app", "aborted", "alice", new FixedTimeProvider());

        Assert.Equal("aborted", resolved.Resolution);
        Assert.Equal("apply", resolved.PendingOperation);
        Assert.Equal("operator-declared", resolved.Reason);

        // …and the target is no longer pending, so a second sweep has nothing to do
        Assert.Empty((await StageRecovery.RecoverAsync(ledger, adapter, new FixedTimeProvider())).Outcomes);
    }

    /// <summary>
    /// The distinction the audit trail is for: who said so, and on what
    /// footing. An operator may assert what the library refused to guess; the
    /// entry records that a person did, and the reserved actor keeps it from
    /// being read later as the library's own verification.
    /// </summary>
    [Fact]
    public async Task An_operator_resolution_is_recorded_as_theirs_and_may_not_impersonate_recovery()
    {
        var (ledger, _) = await UnresolvedAsync();

        await StageRecovery.ResolveAsync(ledger, "app", "completed", "alice", new FixedTimeProvider());

        var appended = (await ledger.ReadAllAsync()).Single(e => e.Reconciled);
        Assert.Equal("alice", appended.Actor);
        Assert.Equal("apply-completed", appended.Kind);
        Assert.Equal("tok-1", appended.ApplyToken);          // taken from the pending entry
        Assert.Equal("never-staged", appended.NewStateRef);  // …not from the caller

        var (other, _) = await UnresolvedAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            StageRecovery.ResolveAsync(other, "app", "completed", StageRecovery.RecoveryActor, new FixedTimeProvider()));
    }

    [Fact]
    public async Task Resolving_a_target_with_nothing_in_flight_is_refused_not_invented()
    {
        var store = new InMemoryLedgerStore();
        var ledger = new ReleaseLedger(store);

        var refusal = await Assert.ThrowsAsync<StageRefusedException>(() =>
            StageRecovery.ResolveAsync(ledger, "app", "completed", "alice", new FixedTimeProvider()));

        Assert.Equal(RefusalReason.InvalidStateTransition, refusal.Reason);
        Assert.Empty(await ledger.ReadAllAsync());
    }

    [Fact]
    public async Task A_rollback_left_in_flight_resolves_as_a_rollback()
    {
        var store = new InMemoryLedgerStore();
        var ledger = new ReleaseLedger(store);
        await ledger.AppendAsync("rollback-started", "app", "sha256:aaa", "tok-2", "operator-1",
            "2026-08-04T00:00:00.000Z", previousStateRef: "branch-1", newStateRef: "live-app");

        var resolved = await StageRecovery.ResolveAsync(ledger, "app", "aborted", "alice", new FixedTimeProvider());

        Assert.Equal("rollback", resolved.PendingOperation);
        Assert.Equal("rollback-aborted", (await ledger.ReadAllAsync()).Single(e => e.Reconciled).Kind);
    }

    [Fact]
    public async Task An_unknown_resolution_is_refused_at_the_door()
    {
        var (ledger, _) = await UnresolvedAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            StageRecovery.ResolveAsync(ledger, "app", "unresolved", "alice", new FixedTimeProvider()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            StageRecovery.ResolveAsync(ledger, "app", "completed", "  ", new FixedTimeProvider()));
    }

    /// <summary>
    /// Whatever the operator declares, the entry stays inside the chain — an
    /// intervention is part of the history, not an exception to it.
    /// </summary>
    [Fact]
    public async Task An_operator_resolution_extends_the_chain_like_any_other_entry()
    {
        var (ledger, _) = await UnresolvedAsync();

        await StageRecovery.ResolveAsync(ledger, "app", "aborted", "alice", new FixedTimeProvider());

        Assert.Equal("intact", (await ledger.VerifyIntegrityAsync()).Verdict);
    }
}
