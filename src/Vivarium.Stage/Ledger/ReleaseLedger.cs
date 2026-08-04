using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vivarium.Stage.Ledger;

/// <summary>
/// One release ledger record. Entries are append-only (fixed principle 6);
/// reconciliation appends, never rewrites (fault-model §3). The schema is
/// exportable and machine-verifiable: <see cref="ToJson"/> / <see cref="FromJson"/>
/// round-trip losslessly.
/// </summary>
public sealed record LedgerEntry(
    long Seq,
    string Kind, // apply-started | apply-completed | rollback-started | rollback-completed | apply-aborted | rollback-aborted
    string Target,
    string ChangesetFingerprint,
    string ApplyToken,
    string Actor,
    string At, // RFC 3339, supplied by Stage's clock
    JsonObject? Fidelity = null,
    string? PreviousStateRef = null,
    string? NewStateRef = null,
    bool Reconciled = false)
{
    public static readonly string[] Kinds =
        ["apply-started", "apply-completed", "rollback-started", "rollback-completed", "apply-aborted", "rollback-aborted"];

    /// <summary>
    /// The hash of the entry before this one, binding the two together
    /// (<see cref="LedgerIntegrity"/>). Null on the entry that begins the
    /// chain — including a ledger whose earlier history predates chaining,
    /// where the null marks the boundary between what can be verified and
    /// what cannot.
    /// </summary>
    /// <remarks>
    /// Init-only rather than positional: this record grows, and every
    /// positional addition breaks callers who deconstruct it — the lesson
    /// <see cref="RecoveryOutcome"/> already paid for.
    /// </remarks>
    public string? PreviousEntryHash { get; init; }

    /// <summary>This entry's own hash. Null on entries written before the ledger chained.</summary>
    public string? EntryHash { get; init; }

    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["seq"] = Seq,
            ["kind"] = Kind,
            ["target"] = Target,
            ["changesetFingerprint"] = ChangesetFingerprint,
            ["applyToken"] = ApplyToken,
            ["actor"] = Actor,
            ["at"] = At,
        };
        if (Fidelity is not null) obj["fidelity"] = Fidelity.DeepClone();
        if (PreviousStateRef is not null) obj["previousStateRef"] = PreviousStateRef;
        if (NewStateRef is not null) obj["newStateRef"] = NewStateRef;
        if (Reconciled) obj["reconciled"] = true;
        if (PreviousEntryHash is not null) obj["previousEntryHash"] = PreviousEntryHash;
        if (EntryHash is not null) obj["entryHash"] = EntryHash;
        return obj;
    }

    public static LedgerEntry FromJson(JsonObject obj)
    {
        var kind = obj["kind"]!.GetValue<string>();
        if (!Kinds.Contains(kind)) throw new JsonException($"unknown ledger entry kind: {kind}");
        return new LedgerEntry(
            obj["seq"]!.GetValue<long>(),
            kind,
            obj["target"]!.GetValue<string>(),
            obj["changesetFingerprint"]!.GetValue<string>(),
            obj["applyToken"]!.GetValue<string>(),
            obj["actor"]!.GetValue<string>(),
            obj["at"]!.GetValue<string>(),
            obj["fidelity"] as JsonObject,
            obj["previousStateRef"]?.GetValue<string>(),
            obj["newStateRef"]?.GetValue<string>(),
            obj["reconciled"]?.GetValue<bool>() ?? false)
        {
            // Absent on history written before the ledger chained: such an
            // entry re-imports as unchained and verification reports it as
            // part of the unverified prefix. No migration is required, and
            // none is offered — re-hashing old history would assert it was
            // never altered rather than verify it.
            PreviousEntryHash = obj["previousEntryHash"]?.GetValue<string>(),
            EntryHash = obj["entryHash"]?.GetValue<string>(),
        };
    }
}

/// <summary>
/// Persistence port for the ledger — keeps the core hosting-neutral (ADR-0003).
///
/// <para><b>An implementation stores entries; it does not edit them.</b>
/// Every member is inside the entry's hash
/// (<see cref="LedgerIntegrity"/>), so a store that normalizes, reorders
/// members, drops one it does not recognize, or otherwise round-trips an
/// entry through a lossy representation will have its own honest history
/// reported as tampered. <see cref="LedgerEntry.ToJson"/> and
/// <see cref="LedgerEntry.FromJson"/> round-trip losslessly and are the
/// intended shape for a store that persists JSON.</para>
///
/// <para>Ordering is the ledger's, not the store's:
/// <see cref="ReadAllAsync"/> may return entries in any order, because every
/// reader in this library sorts by <see cref="LedgerEntry.Seq"/>.</para>
/// </summary>
public interface ILedgerStore
{
    /// <summary>Durably append one entry. MUST be write-ahead capable: the entry is durable when this returns.</summary>
    Task AppendAsync(LedgerEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(CancellationToken ct = default);
}

public sealed class InMemoryLedgerStore : ILedgerStore
{
    private readonly List<LedgerEntry> _entries = [];
    private readonly Lock _lock = new();

    public Task AppendAsync(LedgerEntry entry, CancellationToken ct = default)
    {
        lock (_lock) _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<LedgerEntry>>(_entries.ToList());
    }
}

/// <summary>
/// The append-only release ledger (fixed principle 6): what was applied, when,
/// by whom, from which fingerprint. Two write-ahead records per apply
/// (fault-model §3): <c>apply-started</c> before flip, <c>apply-completed</c>
/// after. There is no update or delete surface, by design.
///
/// <para><b>One ledger per store.</b> An instance reads the store once to
/// pick up where the history left off and then keeps the sequence and the
/// chain in memory, so two instances writing to the same store interleave
/// into duplicated sequence numbers and entries chained onto something that
/// is no longer their predecessor. Serializing writes through one instance is
/// what makes the numbering meaningful, and always was; what changed is that
/// the mistake is now visible — <see cref="VerifyIntegrityAsync"/> reports
/// such a history as broken instead of it passing unremarked. Being visible
/// is not being safe: the entries are still written.</para>
/// </summary>
public sealed class ReleaseLedger(ILedgerStore store)
{
    private long _seq = -1; // -1 = not yet initialized from the store
    private string? _previousEntryHash;
    private readonly SemaphoreSlim _appendLock = new(1, 1);

    public async Task<LedgerEntry> AppendAsync(
        string kind, string target, string changesetFingerprint, string applyToken,
        string actor, string at, JsonObject? fidelity = null,
        string? previousStateRef = null, string? newStateRef = null, bool reconciled = false,
        CancellationToken ct = default)
    {
        // The vocabulary is checked at the door, not only on re-import: the
        // ledger is append-only, so a typo admitted here is permanent — Replay
        // would ignore the entry (leaving a pending that never resolves) and
        // the export would no longer round-trip through FromJson.
        if (!LedgerEntry.Kinds.Contains(kind))
            throw new ArgumentException(
                $"unknown ledger entry kind: {kind} (expected one of: {string.Join(", ", LedgerEntry.Kinds)})",
                nameof(kind));

        await _appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_seq < 0)
            {
                // Resume numbering and the chain after existing history —
                // history is never rewritten. Both resume from the entry with
                // the highest seq rather than the last one the store handed
                // back: a store is free to return its own ordering, and
                // chaining onto the wrong entry would break the chain on the
                // first append after a restart.
                var existing = await store.ReadAllAsync(ct).ConfigureAwait(false);
                var last = existing.MaxBy(e => e.Seq);
                _seq = last?.Seq ?? 0;
                _previousEntryHash = last?.EntryHash;
            }
            var entry = new LedgerEntry(
                ++_seq, kind, target, changesetFingerprint, applyToken, actor, at,
                fidelity, previousStateRef, newStateRef, reconciled)
            {
                PreviousEntryHash = _previousEntryHash,
            };
            entry = entry with { EntryHash = LedgerIntegrity.HashOf(entry) };
            await store.AppendAsync(entry, ct).ConfigureAwait(false);
            // Only after the append is durable: a failed append leaves the
            // chain pointing at the last entry that actually exists.
            _previousEntryHash = entry.EntryHash;
            return entry;
        }
        finally
        {
            _appendLock.Release();
        }
    }

    public Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(CancellationToken ct = default) => store.ReadAllAsync(ct);

    /// <summary>Export the full ledger as a JSON array — the audit trail a runtime-mutable platform owes its operators.</summary>
    public async Task<string> ExportJsonAsync(CancellationToken ct = default)
    {
        var arr = new JsonArray();
        foreach (var e in await ReadAllAsync(ct).ConfigureAwait(false)) arr.Add(e.ToJson());
        return arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Verify that this ledger's history is intact — that no entry was edited,
    /// removed, inserted or reordered since it was written
    /// (<see cref="LedgerIntegrity"/>, which also states what the check cannot
    /// see). Reading and checking are separate on purpose: an exported audit
    /// artifact is verified with <see cref="LedgerIntegrity.Verify"/> by
    /// whoever holds it, with no live store involved.
    /// </summary>
    public async Task<LedgerIntegrityReport> VerifyIntegrityAsync(CancellationToken ct = default) =>
        LedgerIntegrity.Verify(await ReadAllAsync(ct).ConfigureAwait(false));

    /// <summary>Rehydrate a ledger's entries from an exported JSON array (machine-verifiable round-trip).</summary>
    public static IReadOnlyList<LedgerEntry> ParseExport(string json)
    {
        var arr = JsonNode.Parse(json) as JsonArray ?? throw new JsonException("ledger export must be a JSON array");
        return [.. arr.Select(n => LedgerEntry.FromJson((JsonObject)n!))];
    }
}
