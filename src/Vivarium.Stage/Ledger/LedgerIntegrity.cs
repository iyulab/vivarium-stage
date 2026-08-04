using System.Security.Cryptography;
using System.Text.Json;
using Vivarium.Changeset;

namespace Vivarium.Stage.Ledger;

/// <summary>
/// One thing the chain found wrong, at the entry where it was found.
/// <see cref="Kind"/> is a closed vocabulary because an operator acts
/// differently on each: content that no longer matches its own hash was
/// edited in place, a broken link means an entry was removed, inserted or
/// reordered, and an unchained entry after the chain began means the store
/// stopped writing the chain at all.
/// </summary>
public sealed record LedgerIntegrityFinding
{
    public static readonly string[] Kinds = ["entry-hash-mismatch", "broken-link", "unchained-after-chain-start"];

    /// <summary>The sequence number of the entry the finding is about.</summary>
    public required long Seq { get; init; }

    /// <summary>entry-hash-mismatch | broken-link | unchained-after-chain-start</summary>
    public required string Kind { get; init; }

    public required string Message { get; init; }
}

/// <summary>
/// What verification could and could not establish about a ledger.
///
/// <para><see cref="Verdict"/> separates three states an operator must not
/// confuse. <c>intact</c> means the chained entries verify; <c>broken</c>
/// means at least one does not; <c>unverifiable</c> means no entry carries a
/// chain, so nothing was checked. Collapsing the third into the first would
/// make "green" mean either *verified* or *nobody looked*.</para>
///
/// <para><see cref="UnverifiedPrefix"/> is the count of entries preceding the
/// chain — history written before this ledger began chaining. Those entries
/// are reported as unverifiable rather than re-hashed on the spot: re-hashing
/// old history asserts it was never altered instead of verifying it, and this
/// library verifies rather than asserts.</para>
/// </summary>
public sealed record LedgerIntegrityReport
{
    /// <summary>intact | broken | unverifiable</summary>
    public required string Verdict { get; init; }

    /// <summary>Entries before the chain begins — history this check cannot speak for.</summary>
    public required int UnverifiedPrefix { get; init; }

    /// <summary>Sequence number where the chain begins, or null when there is no chain.</summary>
    public long? ChainStartSeq { get; init; }

    public required IReadOnlyList<LedgerIntegrityFinding> Findings { get; init; }
}

/// <summary>
/// Binds each ledger entry to the one before it, so that a store which edits
/// its own history stops being invisible to every reader.
///
/// <para>An entry's hash is SHA-256 over the JCS canonical bytes of the
/// entry's JSON with <c>entryHash</c> removed — the same canonicalization and
/// the same <c>sha256:</c> prefix the family already uses to seal changesets,
/// so an operator reading a ledger meets no new vocabulary. Because
/// <c>previousEntryHash</c> is inside the hashed content and <c>seq</c> is
/// too, altering any entry invalidates every entry after it: tampering becomes
/// rewrite-everything-or-nothing rather than edit-one-line.</para>
///
/// <para><b>What this does not detect, stated plainly.</b> A store that
/// rewrites the entire chain from the tampered point forward produces a
/// self-consistent history, and so does one that simply drops the newest
/// entries — a shorter history is a valid one, because nothing inside a
/// ledger says how far it should reach. Neither is visible from a single
/// ledger, and appending afterwards does not surface the second: a ledger
/// resumed against a truncated history chains onto the entry that survived,
/// closing over the gap rather than exposing it. Both need a fixed point kept
/// where the store cannot reach it (an external timestamp, a second store, an
/// operator's retained copy), and this version has none.</para>
///
/// <para>That limit deserves its weight: the single most damaging edit is
/// removing an <c>apply-completed</c>, which makes a settled target read as
/// unfinished, and that entry is the newest one until something else is
/// written. What the chain buys is that edits, insertions, reordering, and
/// deletions from anywhere but the end all become visible, and that a
/// convincing forgery costs the whole ledger from the tampered point rather
/// than a single line.</para>
/// </summary>
public static class LedgerIntegrity
{
    /// <summary>
    /// The entry's own hash: SHA-256 over its JCS canonical bytes with
    /// <c>entryHash</c> removed. <c>previousEntryHash</c> is included, which
    /// is what makes the entries a chain rather than a list of checksums.
    /// </summary>
    public static string HashOf(LedgerEntry entry)
    {
        var content = entry.ToJson();
        content.Remove("entryHash");
        using var doc = JsonDocument.Parse(content.ToJsonString());
        return ChangesetFingerprint.Prefix
            + Convert.ToHexStringLower(SHA256.HashData(JsonCanonicalizer.CanonicalBytes(doc.RootElement)));
    }

    /// <summary>
    /// Verify a ledger's entries. Takes the entries rather than a ledger so
    /// that an exported audit artifact can be checked by whoever holds it,
    /// with no live store in hand — which is the situation an audit is.
    ///
    /// <para>Entries are walked in <c>seq</c> order, not in the order the
    /// store returned them: a store is free to hand back its own ordering,
    /// and the chain is defined over the sequence.</para>
    /// </summary>
    public static LedgerIntegrityReport Verify(IEnumerable<LedgerEntry> entries)
    {
        var findings = new List<LedgerIntegrityFinding>();
        var unverifiedPrefix = 0;
        long? chainStartSeq = null;
        string? expectedPrevious = null;

        foreach (var entry in entries.OrderBy(e => e.Seq))
        {
            if (entry.EntryHash is null)
            {
                if (chainStartSeq is null)
                {
                    unverifiedPrefix++;
                    continue;
                }
                findings.Add(new LedgerIntegrityFinding
                {
                    Seq = entry.Seq,
                    Kind = "unchained-after-chain-start",
                    Message = $"entry {entry.Seq} carries no hash, but the chain began at {chainStartSeq}",
                });
                continue;
            }

            var computed = HashOf(entry);
            if (computed != entry.EntryHash)
            {
                findings.Add(new LedgerIntegrityFinding
                {
                    Seq = entry.Seq,
                    Kind = "entry-hash-mismatch",
                    Message = $"entry {entry.Seq} does not hash to the value it carries (content was altered)",
                });
            }

            if (chainStartSeq is null)
            {
                chainStartSeq = entry.Seq;
                // The first chained entry closes the boundary with the
                // unchained past, so it claims no predecessor. One that does
                // is telling us the entries it pointed at are gone.
                if (entry.PreviousEntryHash is not null)
                {
                    findings.Add(new LedgerIntegrityFinding
                    {
                        Seq = entry.Seq,
                        Kind = "broken-link",
                        Message = $"entry {entry.Seq} begins the chain yet names a predecessor, which is not present",
                    });
                }
            }
            else if (entry.PreviousEntryHash != expectedPrevious)
            {
                findings.Add(new LedgerIntegrityFinding
                {
                    Seq = entry.Seq,
                    Kind = "broken-link",
                    Message = $"entry {entry.Seq} does not follow the entry before it (one was removed, inserted or reordered)",
                });
            }

            // The carried hash, not the computed one: an altered entry is
            // already reported once above, and its successors legitimately
            // point at what it carried. Reporting them again would turn one
            // edit into a cascade of findings that name innocent entries.
            expectedPrevious = entry.EntryHash;
        }

        var verdict = findings.Count > 0 ? "broken"
            : chainStartSeq is null ? "unverifiable"
            : "intact";

        return new LedgerIntegrityReport
        {
            Verdict = verdict,
            UnverifiedPrefix = unverifiedPrefix,
            ChainStartSeq = chainStartSeq,
            Findings = findings,
        };
    }
}
