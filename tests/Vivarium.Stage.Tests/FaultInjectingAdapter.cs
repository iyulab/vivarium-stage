using Vivarium.Stage.Adapters;

namespace Vivarium.Stage.Tests;

/// <summary>Simulated crash — the exception type recovery paths must survive.</summary>
public sealed class SimulatedCrashException(string point) : Exception($"simulated crash: {point}");

public enum FaultPoint
{
    None,
    DuringBranch,   // F1
    DuringPrepare,  // F2
    BeforeFlip,     // F3 (after prepare confirmed) / F4 where the swap did not land
    AfterFlip,      // F4 where the swap landed / F5 (before ledger completion)
}

/// <summary>
/// Decorator that crashes at a configured point exactly once, then behaves
/// normally — modelling a process crash followed by a recovery attempt.
/// </summary>
public sealed class FaultInjectingAdapter(IBackendAdapter inner) : IBackendAdapter
{
    public FaultPoint Fault { get; set; } = FaultPoint.None;

    private void MaybeCrash(FaultPoint point)
    {
        if (Fault != point) return;
        Fault = FaultPoint.None; // crash once; the "restarted process" proceeds normally
        throw new SimulatedCrashException(point.ToString());
    }

    public CapabilityManifest Capabilities => inner.Capabilities;

    public Task<BranchInfo> BranchAsync(string target, CancellationToken ct = default)
    {
        MaybeCrash(FaultPoint.DuringBranch);
        return inner.BranchAsync(target, ct);
    }

    public Task<PrepareReport> PrepareAsync(string branchRef, PreparedFacets facets, CancellationToken ct = default)
    {
        MaybeCrash(FaultPoint.DuringPrepare);
        return inner.PrepareAsync(branchRef, facets, ct);
    }

    public async Task FlipAsync(string target, string stateRef, string applyToken, CancellationToken ct = default)
    {
        MaybeCrash(FaultPoint.BeforeFlip);
        await inner.FlipAsync(target, stateRef, applyToken, ct);
        MaybeCrash(FaultPoint.AfterFlip);
    }

    public Task<ActiveState> ActiveStateAsync(string target, CancellationToken ct = default) =>
        inner.ActiveStateAsync(target, ct);

    public Task DiscardAsync(string branchRef, CancellationToken ct = default) =>
        inner.DiscardAsync(branchRef, ct);
}
