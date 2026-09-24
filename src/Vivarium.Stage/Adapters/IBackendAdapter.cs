using System.Text.Json.Nodes;

namespace Vivarium.Stage.Adapters;

/// <summary>
/// Backend adapter boundary (<see href="https://github.com/iyulab/vivarium-stage/blob/main/docs/adapter-api.md">adapter-api</see> v0.1). The adapter owns how
/// branches are made, how facet states are staged, and the atomic flip
/// primitive. It never sees approval semantics, ledger contents, or how
/// changesets were authored — it consumes prepared facet operations only.
///
/// Signatures are v0 — finalized against the first real-backend adapter
/// (consumer-owned) and recorded in <see href="https://github.com/iyulab/vivarium-stage/blob/main/docs/adapter-api.md">adapter-api</see>.
/// </summary>
public interface IBackendAdapter
{
    /// <summary>Machine-readable declaration of flip capability and producible fidelity modes (adapter-api §2).</summary>
    CapabilityManifest Capabilities { get; }

    /// <summary>
    /// Create an isolated staging world from the target's current state.
    /// Returns a branch reference plus a fidelity declaration — a branch
    /// without one cannot enter simulation. No live effect.
    /// </summary>
    Task<BranchInfo> BranchAsync(string target, CancellationToken ct = default);

    /// <summary>
    /// Stage the changeset's facet operations into the branch. Idempotent per
    /// changeset fingerprint. Reports per-facet completion so Stage can
    /// confirm ALL facets before any flip. No live effect.
    /// </summary>
    Task<PrepareReport> PrepareAsync(string branchRef, PreparedFacets facets, CancellationToken ct = default);

    /// <summary>
    /// Atomically activate <paramref name="stateRef"/> (a prepared branch, or
    /// a previously active state for rollback). Succeeds completely or has no
    /// effect; idempotent under <paramref name="applyToken"/> so recovery may
    /// re-issue it (fault-model F4/F6).
    /// </summary>
    Task FlipAsync(string target, string stateRef, string applyToken, CancellationToken ct = default);

    /// <summary>
    /// The currently active state: its state ref (the flip pointer's current
    /// value — the return path for rollback and the decider for post-crash
    /// ledger reconciliation, fault-model F5) plus deterministic per-facet
    /// fingerprints — Stage's input for drift refusal.
    /// </summary>
    Task<ActiveState> ActiveStateAsync(string target, CancellationToken ct = default);

    /// <summary>Release a staging world and its resources. Always safe (staging never touches live state).</summary>
    Task DiscardAsync(string branchRef, CancellationToken ct = default);
}

/// <summary>Facet operations handed to an adapter: the changeset's patches and its fingerprint — nothing about authoring or review.</summary>
public sealed record PreparedFacets(string ChangesetFingerprint, JsonObject Patches);

/// <summary>What <see cref="IBackendAdapter.BranchAsync"/> returns: the new staging world and how faithfully it replicates the target.</summary>
/// <param name="BranchRef">The branch's state ref — what prepare stages into and what a flip later activates.</param>
/// <param name="Fidelity">
/// How the branch replicates each facet of the target. Required: a branch without a
/// fidelity declaration cannot enter simulation.
/// </param>
public sealed record BranchInfo(string BranchRef, FidelityDeclaration Fidelity);

/// <summary>The active pointer's current value plus deterministic fingerprints of the state it points at, keyed by facet/artifact ref.</summary>
public sealed record ActiveState(string StateRef, IReadOnlyDictionary<string, string> FacetFingerprints);

/// <summary>Per-facet staging completion (adapter-api §3): Stage confirms all facets before any flip (fixed principle 1).</summary>
public sealed record PrepareReport(IReadOnlyDictionary<string, bool> FacetComplete)
{
    /// <summary>
    /// True when at least one facet was reported and every reported facet is complete.
    /// An empty report is not complete: it confirms nothing, so it cannot clear a flip.
    /// </summary>
    public bool AllComplete => FacetComplete.Count > 0 && FacetComplete.Values.All(v => v);
}

/// <summary>Flip capability: the atomic swap primitive, or an honest degradation declaration (fault-model §4).</summary>
public sealed record FlipCapability(bool AtomicSwap, string? DegradationDescription = null)
{
    /// <summary>The adapter has the atomic swap primitive: a flip succeeds completely or has no effect.</summary>
    public static FlipCapability Atomic { get; } = new(true);

    /// <summary>
    /// The adapter has no atomic swap. <paramref name="description"/> states the
    /// non-atomic window, so a host can decide whether to consent to it
    /// (<see cref="StagePolicy.AcceptDegradedAdapter"/>).
    /// </summary>
    public static FlipCapability Degraded(string description) => new(false, description);
}

/// <summary>
/// An adapter's machine-readable capability manifest (adapter-api §2): what its flip can
/// guarantee and which fidelity modes it can produce, so a host sets policy against real
/// capabilities rather than assumed ones.
/// </summary>
/// <param name="Flip">Whether the flip is an atomic swap, or the declared degradation if not.</param>
/// <param name="FidelityModesPerFacet">
/// The fidelity modes (<c>full</c> | <c>subset</c> | <c>stub</c>) the adapter can produce,
/// keyed by facet. A branch's declared modes are expected to fall within these.
/// </param>
public sealed record CapabilityManifest(
    FlipCapability Flip,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FidelityModesPerFacet);

/// <summary>
/// Machine-readable fidelity declaration (adapter-api §4): the interpretation
/// rule for simulation evidence, recorded in the ledger with the apply.
/// An empty <see cref="KnownDifferences"/> list is a claim, not an omission.
/// </summary>
public sealed record FidelityDeclaration(
    IReadOnlyDictionary<string, FacetFidelity> PerFacet,
    IReadOnlyList<string> KnownDifferences)
{
    /// <summary>
    /// The declaration as JSON — <c>{ perFacet: { "&lt;facet&gt;": { mode, method, selectionRule } }, knownDifferences: [] }</c> —
    /// the form recorded in the ledger's <c>apply-started</c> entry.
    /// </summary>
    public JsonObject ToJson()
    {
        var perFacet = new JsonObject();
        foreach (var (facet, f) in PerFacet)
            perFacet[facet] = new JsonObject
            {
                ["mode"] = f.Mode,
                ["method"] = f.Method,
                ["selectionRule"] = f.SelectionRule,
            };
        return new JsonObject
        {
            ["perFacet"] = perFacet,
            ["knownDifferences"] = new JsonArray(KnownDifferences.Select(d => (JsonNode)d).ToArray()),
        };
    }
}

/// <summary>Replication mode per facet: <c>full</c>, <c>subset</c> (with selection rule), or <c>stub</c>, plus the method tag (e.g. cow, snapshot, sample).</summary>
public sealed record FacetFidelity(string Mode, string Method, string? SelectionRule = null);
