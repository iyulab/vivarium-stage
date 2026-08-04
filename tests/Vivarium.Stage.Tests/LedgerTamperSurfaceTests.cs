using System.Text.Json.Nodes;
using Vivarium.Stage.Ledger;

namespace Vivarium.Stage.Tests;

/// <summary>
/// What a dishonest store can do to the ledger, and what the ledger now says
/// about it.
///
/// <para>Fixed principle 6 says history is never rewritten. That used to be a
/// policy the library followed — it exposes no update or delete — without
/// being one it could <em>detect</em> the violation of: entries carried no
/// binding to the entry before them, so a store that edited its own file was
/// invisible to every reader. These tests were written asserting that the
/// tampering succeeded, to fail the day that stopped being true. It has
/// stopped being true, and they now assert what verification reports
/// instead.</para>
///
/// <para>Two of them are unchanged, and deliberately so: an honest history
/// must verify, and a check that only ever answers "broken" would pass every
/// tampering test in this file while being worthless. The control cases are
/// what make the rest mean something.</para>
/// </summary>
public class LedgerTamperSurfaceTests
{
    private static LedgerEntry Entry(long seq, string kind, string fingerprint, string token, string? newRef, string? prevRef = null) =>
        new(seq, kind, "app", fingerprint, token, "operator-1", "2026-08-04T00:00:00.000Z",
            Fidelity: null, PreviousStateRef: prevRef, NewStateRef: newRef);

    /// <summary>
    /// One apply, landed and completed — written through the ledger so the
    /// entries carry the chain, which is the history the tests below attack.
    /// </summary>
    private static async Task<List<LedgerEntry>> HonestHistoryAsync()
    {
        var ledger = new ReleaseLedger(new InMemoryLedgerStore());
        await ledger.AppendAsync("apply-started", "app", "sha256:aaa", "tok-1", "operator-1",
            "2026-08-04T00:00:00.000Z", previousStateRef: "state-1", newStateRef: "state-2");
        await ledger.AppendAsync("apply-completed", "app", "sha256:aaa", "tok-1", "operator-1",
            "2026-08-04T00:00:00.000Z", newStateRef: "state-2");
        return [.. await ledger.ReadAllAsync()];
    }

    [Fact]
    public async Task An_honest_history_replays_to_a_settled_target()
    {
        var view = LedgerProjection.Replay(await HonestHistoryAsync())["app"];

        Assert.Null(view.PendingStarted);
        Assert.Equal("state-2", view.ActiveStateRef);
        Assert.Equal("sha256:aaa", view.ActiveChangesetFingerprint);
    }

    /// <summary>
    /// The control the rest of this file leans on: an untouched history
    /// verifies, and says so with a verdict distinct from "nothing to check".
    /// A verifier that answered "broken" unconditionally would satisfy every
    /// tampering assertion below.
    /// </summary>
    [Fact]
    public async Task An_untouched_history_verifies_intact()
    {
        var report = LedgerIntegrity.Verify(await HonestHistoryAsync());

        Assert.Equal("intact", report.Verdict);
        Assert.Empty(report.Findings);
        Assert.Equal(0, report.UnverifiedPrefix);
        Assert.Equal(1, report.ChainStartSeq);
    }

    /// <summary>
    /// The other control: history written before the ledger chained cannot be
    /// verified, and that is a third answer, not a green one. Re-hashing it at
    /// import would assert it was never altered — the recovery path refuses to
    /// assert what it has not verified, and so does this.
    /// </summary>
    [Fact]
    public void History_written_before_the_chain_is_reported_as_unverifiable_not_as_intact()
    {
        var legacy = new List<LedgerEntry>
        {
            Entry(1, "apply-started", "sha256:aaa", "tok-1", "state-2", "state-1"),
            Entry(2, "apply-completed", "sha256:aaa", "tok-1", "state-2"),
        };

        var report = LedgerIntegrity.Verify(legacy);

        Assert.Equal("unverifiable", report.Verdict);
        Assert.Equal(2, report.UnverifiedPrefix);
        Assert.Null(report.ChainStartSeq);
        Assert.Empty(report.Findings); // unchained is not the same as tampered
    }

    /// <summary>
    /// A ledger that chained partway through reports both halves for what they
    /// are: the older entries unverified, the newer ones verified. This is the
    /// shape any existing deployment takes the first time it chains, and the
    /// boundary is the first chained entry claiming no predecessor.
    /// </summary>
    [Fact]
    public async Task A_ledger_that_began_chaining_partway_reports_the_boundary()
    {
        var store = new InMemoryLedgerStore();
        await store.AppendAsync(Entry(1, "apply-started", "sha256:old", "tok-0", "state-1"));
        await store.AppendAsync(Entry(2, "apply-completed", "sha256:old", "tok-0", "state-1"));
        var ledger = new ReleaseLedger(store);
        await ledger.AppendAsync("apply-started", "app", "sha256:aaa", "tok-1", "operator-1",
            "2026-08-04T00:00:00.000Z", previousStateRef: "state-1", newStateRef: "state-2");

        var report = await ledger.VerifyIntegrityAsync();

        Assert.Equal("intact", report.Verdict);
        Assert.Equal(2, report.UnverifiedPrefix);
        Assert.Equal(3, report.ChainStartSeq);
    }

    /// <summary>
    /// Deleting the completion is the most damaging edit available, because it
    /// does not corrupt anything a reader inspects — it makes a settled target
    /// look like an unfinished one, which is the exact shape recovery exists to
    /// act on. The projection still reports that shape; what changed is that
    /// the ledger is no longer silent about how it came to be.
    /// </summary>
    [Fact]
    public async Task Deleting_the_completion_manufactures_a_pending_and_the_chain_says_so()
    {
        var tampered = await HonestHistoryAsync();
        tampered.RemoveAll(e => e.Kind == "apply-completed");
        tampered.Add(Entry(3, "apply-completed", "sha256:bbb", "tok-2", "state-3"));

        // Replay is unchanged: it is total over what it is handed, and reading
        // integrity into it would make one function answer two questions.
        var view = LedgerProjection.Replay(tampered)["app"];
        Assert.Equal("sha256:bbb", view.ActiveChangesetFingerprint);

        var report = LedgerIntegrity.Verify(tampered);
        Assert.Equal("broken", report.Verdict);
        Assert.Contains(report.Findings, f => f.Kind == "unchained-after-chain-start" && f.Seq == 3);
    }

    /// <summary>
    /// Removing an entry from the middle of a chained history breaks the link
    /// at the entry that followed it, whether or not the sequence numbers are
    /// made to look continuous — the binding is to the previous entry's hash,
    /// not to its number.
    /// </summary>
    [Fact]
    public async Task Removing_a_chained_entry_breaks_the_link_at_the_one_that_followed_it()
    {
        var ledger = new ReleaseLedger(new InMemoryLedgerStore());
        foreach (var (kind, token) in new[] { ("apply-started", "tok-1"), ("apply-completed", "tok-1"), ("rollback-started", "tok-2") })
            await ledger.AppendAsync(kind, "app", "sha256:aaa", token, "operator-1",
                "2026-08-04T00:00:00.000Z", newStateRef: "state-2");

        var tampered = (await ledger.ReadAllAsync()).Where(e => e.Seq != 2).ToList();

        var report = LedgerIntegrity.Verify(tampered);
        Assert.Equal("broken", report.Verdict);
        Assert.Contains(report.Findings, f => f.Kind == "broken-link" && f.Seq == 3);
    }

    /// <summary>
    /// Substitution needs no deletion at all: rewriting the fingerprint in
    /// place still changes what the projection reports as live, and now the
    /// entry no longer hashes to the value it carries.
    /// </summary>
    [Fact]
    public async Task Substituting_a_fingerprint_leaves_the_entry_no_longer_matching_its_own_hash()
    {
        var tampered = (await HonestHistoryAsync())
            .Select(e => e.Kind == "apply-completed" ? e with { ChangesetFingerprint = "sha256:forged" } : e)
            .ToList();

        Assert.Equal("sha256:forged", LedgerProjection.Replay(tampered)["app"].ActiveChangesetFingerprint);

        var report = LedgerIntegrity.Verify(tampered);
        Assert.Equal("broken", report.Verdict);
        Assert.Contains(report.Findings, f => f.Kind == "entry-hash-mismatch" && f.Seq == 2);
    }

    /// <summary>
    /// A forger who recomputes the hash of the entry they edited does not get
    /// away with it either: the entry after it still names the hash it had
    /// before. One edit costs the whole remaining chain.
    /// </summary>
    [Fact]
    public async Task Recomputing_the_hash_of_an_edited_entry_breaks_the_entry_after_it()
    {
        var honest = await HonestHistoryAsync();
        var forged = honest[0] with { ChangesetFingerprint = "sha256:forged", EntryHash = null };
        forged = forged with { EntryHash = LedgerIntegrity.HashOf(forged) };
        var tampered = new List<LedgerEntry> { forged, honest[1] };

        var report = LedgerIntegrity.Verify(tampered);

        Assert.Equal("broken", report.Verdict);
        Assert.DoesNotContain(report.Findings, f => f.Kind == "entry-hash-mismatch"); // the edit itself is consistent
        Assert.Contains(report.Findings, f => f.Kind == "broken-link" && f.Seq == 2);
    }

    /// <summary>
    /// The export is the artifact an operator hands to an auditor, and it used
    /// to round-trip a tampered history as readily as an honest one. The
    /// auditor now verifies it holding nothing but the file.
    /// </summary>
    [Fact]
    public async Task A_tampered_export_no_longer_round_trips_without_complaint()
    {
        var ledger = new ReleaseLedger(new InMemoryLedgerStore());
        foreach (var (kind, token) in new[] { ("apply-started", "tok-1"), ("apply-completed", "tok-1"), ("rollback-started", "tok-2") })
            await ledger.AppendAsync(kind, "app", "sha256:aaa", token, "operator-1",
                "2026-08-04T00:00:00.000Z", newStateRef: "state-2");

        var honestExport = await ledger.ExportJsonAsync();
        Assert.Equal("intact", LedgerIntegrity.Verify(ReleaseLedger.ParseExport(honestExport)).Verdict);

        var exported = (JsonArray)JsonNode.Parse(honestExport)!;
        exported.RemoveAt(1); // drop the completion from the audit artifact

        var report = LedgerIntegrity.Verify(ReleaseLedger.ParseExport(exported.ToJsonString()));

        Assert.Equal("broken", report.Verdict);
        Assert.Contains(report.Findings, f => f.Kind == "broken-link");
    }

    /// <summary>
    /// The gap that remains, written down rather than remembered — the same
    /// form the assertions in this file took before the chain existed.
    ///
    /// <para>Dropping the <em>newest</em> entries leaves a chain that is
    /// internally consistent, because nothing inside a ledger says how far it
    /// should reach. That is not an oversight in the chain: detecting it needs
    /// a fixed point held where the store cannot reach it, and this version has
    /// none. It matters more than it sounds — the most damaging single edit is
    /// removing an <c>apply-completed</c>, which manufactures the unfinished
    /// shape recovery acts on, and that entry is the newest one for as long as
    /// nothing else is written.</para>
    ///
    /// <para>What the chain did change is the cost: the truncation has to take
    /// every entry after the target as well. What it does not do is expose the
    /// truncation later — a ledger resumed against the shortened history
    /// chains onto the entry that survived, so its own future writes close
    /// over the gap rather than revealing it. Only a fixed point kept outside
    /// the store can say how far the history should have reached.</para>
    /// </summary>
    [Fact]
    public async Task Dropping_the_newest_entry_is_not_detectable_from_the_ledger_alone()
    {
        var truncated = await HonestHistoryAsync();
        truncated.RemoveAll(e => e.Kind == "apply-completed"); // the last entry written

        Assert.Equal("intact", LedgerIntegrity.Verify(truncated).Verdict);
        // …while the settled target now reads as unfinished, which is the shape
        // recovery acts on. This is the residual risk, stated as a value.
        Assert.NotNull(LedgerProjection.Replay(truncated)["app"].PendingStarted);

        // And appending afterwards does not surface it: the resumed ledger
        // chains onto what it finds, which is a coherent shorter history.
        var store = new InMemoryLedgerStore();
        foreach (var e in truncated) await store.AppendAsync(e);
        await new ReleaseLedger(store).AppendAsync("rollback-started", "app", "sha256:aaa", "tok-2",
            "operator-1", "2026-08-04T00:00:00.000Z", newStateRef: "state-1");

        Assert.Equal("intact", LedgerIntegrity.Verify(await store.ReadAllAsync()).Verdict);
    }

    /// <summary>
    /// Not tampering, but the chain reports it: two ledgers writing to one
    /// store. Each keeps the sequence and the chain in memory after reading
    /// the store once, so interleaved writes duplicate a sequence number and
    /// chain an entry onto something that is no longer its predecessor.
    ///
    /// <para>The hazard predates the chain — the duplicated numbering was
    /// always wrong and always silent. Recording it here says which way this
    /// cuts: a broken verdict means the history is not accountable, and
    /// misuse is one of the ways to get there. The chain makes it visible, not
    /// safe.</para>
    /// </summary>
    [Fact]
    public async Task Two_ledgers_writing_to_one_store_produce_a_history_that_does_not_verify()
    {
        var store = new InMemoryLedgerStore();
        var one = new ReleaseLedger(store);
        var other = new ReleaseLedger(store);

        await one.AppendAsync("apply-started", "app", "sha256:aaa", "tok-1", "operator-1", "2026-08-04T00:00:00.000Z");
        await other.AppendAsync("apply-completed", "app", "sha256:aaa", "tok-1", "operator-1", "2026-08-04T00:00:01.000Z");
        await one.AppendAsync("rollback-started", "app", "sha256:aaa", "tok-2", "operator-1", "2026-08-04T00:00:02.000Z");

        var entries = await store.ReadAllAsync();
        Assert.Equal([1L, 2L, 2L], entries.Select(e => e.Seq)); // the duplication, which was always silent

        var report = LedgerIntegrity.Verify(entries);
        Assert.Equal("broken", report.Verdict);
        Assert.Contains(report.Findings, f => f.Kind == "broken-link");
    }

    /// <summary>
    /// What the library refuses at the door, unchanged: an entry kind outside
    /// the vocabulary, on write and on re-import. The chain binds what comes
    /// through that door; it does not replace the door.
    /// </summary>
    [Fact]
    public async Task The_vocabulary_is_guarded_at_both_doors()
    {
        var ledger = new ReleaseLedger(new InMemoryLedgerStore());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ledger.AppendAsync("apply-maybe", "app", "sha256:aaa", "tok", "operator-1", "2026-08-04T00:00:00.000Z"));

        Assert.ThrowsAny<Exception>(() => ReleaseLedger.ParseExport(
            """[{"seq":1,"kind":"apply-maybe","target":"app","changesetFingerprint":"sha256:aaa","applyToken":"t","actor":"a","at":"2026-08-04T00:00:00.000Z"}]"""));
    }
}
