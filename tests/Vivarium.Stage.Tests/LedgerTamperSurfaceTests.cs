using System.Text.Json.Nodes;
using Vivarium.Stage.Ledger;

namespace Vivarium.Stage.Tests;

/// <summary>
/// What a dishonest store can do to the ledger today, pinned as executable fact.
///
/// <para>Fixed principle 6 says history is never rewritten. That is a policy the
/// library follows — it exposes no update or delete — but not one it can currently
/// <em>detect</em> the violation of: entries carry no binding to the entry before
/// them, so an <see cref="ILedgerStore"/> that edits its own file is invisible to
/// every reader. These tests pass because the tampering succeeds. They are written
/// to fail the day that stops being true, which is the point.</para>
///
/// <para>Same shape as the refusal gate's assertions for cases where nothing is
/// refused: a gap is worth more written down than remembered.</para>
/// </summary>
public class LedgerTamperSurfaceTests
{
    private static LedgerEntry Entry(long seq, string kind, string fingerprint, string token, string? newRef, string? prevRef = null) =>
        new(seq, kind, "app", fingerprint, token, "operator-1", "2026-08-04T00:00:00.000Z",
            Fidelity: null, PreviousStateRef: prevRef, NewStateRef: newRef);

    /// <summary>One apply, landed and completed — the history the tests below attack.</summary>
    private static List<LedgerEntry> HonestHistory() =>
    [
        Entry(1, "apply-started", "sha256:aaa", "tok-1", "state-2", "state-1"),
        Entry(2, "apply-completed", "sha256:aaa", "tok-1", "state-2"),
    ];

    [Fact]
    public void An_honest_history_replays_to_a_settled_target()
    {
        var view = LedgerProjection.Replay(HonestHistory())["app"];

        Assert.Null(view.PendingStarted);
        Assert.Equal("state-2", view.ActiveStateRef);
        Assert.Equal("sha256:aaa", view.ActiveChangesetFingerprint);
    }

    /// <summary>
    /// Deleting the completion is the most damaging edit available, because it does
    /// not corrupt anything a reader inspects — it makes a settled target look like
    /// an unfinished one. Recovery exists to act on exactly that shape.
    /// </summary>
    [Fact]
    public void Deleting_the_completion_makes_a_settled_target_look_unfinished_and_nothing_notices()
    {
        var tampered = HonestHistory();
        tampered.RemoveAll(e => e.Kind == "apply-completed");

        var view = LedgerProjection.Replay(tampered)["app"];

        Assert.NotNull(view.PendingStarted);          // recovery's trigger, manufactured
        Assert.Null(view.ActiveChangesetFingerprint); // the apply that happened is gone from the account
    }

    /// <summary>
    /// The information needed to catch that deletion is already in the ledger — the
    /// sequence skips. Nothing reads it. This test states the gap as a value, so a
    /// future integrity check has a defined thing to detect.
    /// </summary>
    [Fact]
    public void The_sequence_records_the_deletion_and_no_reader_consults_it()
    {
        var tampered = HonestHistory();
        tampered.RemoveAll(e => e.Seq == 2);
        tampered.Add(Entry(3, "apply-completed", "sha256:bbb", "tok-2", "state-3"));

        var gaps = tampered.OrderBy(e => e.Seq)
            .Zip(tampered.OrderBy(e => e.Seq).Skip(1), (a, b) => b.Seq - a.Seq)
            .Count(step => step != 1);
        Assert.Equal(1, gaps); // observable…

        // …and irrelevant to every reader the library ships: replay orders by Seq and
        // is otherwise total over what it is handed.
        var view = LedgerProjection.Replay(tampered)["app"];
        Assert.Equal("sha256:bbb", view.ActiveChangesetFingerprint);
    }

    /// <summary>
    /// Substitution needs no deletion at all: rewrite the fingerprint in place and the
    /// projection reports a different changeset as the one that produced live state.
    /// </summary>
    [Fact]
    public void Substituting_a_fingerprint_changes_what_the_ledger_says_landed()
    {
        var tampered = HonestHistory()
            .Select(e => e.Kind == "apply-completed" ? e with { ChangesetFingerprint = "sha256:forged" } : e)
            .ToList();

        var view = LedgerProjection.Replay(tampered)["app"];

        Assert.Equal("sha256:forged", view.ActiveChangesetFingerprint);
    }

    /// <summary>
    /// The export is the artifact an operator would hand to an auditor, and it
    /// round-trips a tampered history as readily as an honest one: parsing validates
    /// the entry vocabulary, which says nothing about whether the sequence is intact.
    /// </summary>
    [Fact]
    public async Task A_tampered_export_round_trips_without_complaint()
    {
        var store = new InMemoryLedgerStore();
        var ledger = new ReleaseLedger(store);
        foreach (var e in HonestHistory())
            await ledger.AppendAsync(e.Kind, e.Target, e.ChangesetFingerprint, e.ApplyToken, e.Actor, e.At,
                previousStateRef: e.PreviousStateRef, newStateRef: e.NewStateRef);

        var exported = (JsonArray)JsonNode.Parse(await ledger.ExportJsonAsync())!;
        exported.RemoveAt(1); // drop the completion from the audit artifact

        var reparsed = ReleaseLedger.ParseExport(exported.ToJsonString());

        Assert.Single(reparsed);
        Assert.NotNull(LedgerProjection.Replay(reparsed)["app"].PendingStarted);
    }

    /// <summary>
    /// What the library <em>does</em> refuse, for contrast: an entry kind outside the
    /// vocabulary, at the write door and on re-import. The door is guarded; what comes
    /// through it is not bound to what came before.
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
