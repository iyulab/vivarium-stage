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
            obj["reconciled"]?.GetValue<bool>() ?? false);
    }
}

/// <summary>Persistence port for the ledger — keeps the core hosting-neutral (ADR-0003).</summary>
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
/// </summary>
public sealed class ReleaseLedger(ILedgerStore store)
{
    private long _seq = -1; // -1 = not yet initialized from the store
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
                // resume numbering after existing history — history is never rewritten
                var existing = await store.ReadAllAsync(ct).ConfigureAwait(false);
                _seq = existing.Count == 0 ? 0 : existing.Max(e => e.Seq);
            }
            var entry = new LedgerEntry(
                ++_seq, kind, target, changesetFingerprint, applyToken, actor, at,
                fidelity, previousStateRef, newStateRef, reconciled);
            await store.AppendAsync(entry, ct).ConfigureAwait(false);
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

    /// <summary>Rehydrate a ledger's entries from an exported JSON array (machine-verifiable round-trip).</summary>
    public static IReadOnlyList<LedgerEntry> ParseExport(string json)
    {
        var arr = JsonNode.Parse(json) as JsonArray ?? throw new JsonException("ledger export must be a JSON array");
        return [.. arr.Select(n => LedgerEntry.FromJson((JsonObject)n!))];
    }
}
