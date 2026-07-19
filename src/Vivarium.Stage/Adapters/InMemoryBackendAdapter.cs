using System.Text.Json;
using System.Text.Json.Nodes;
using Vivarium.Changeset;

namespace Vivarium.Stage.Adapters;

/// <summary>
/// Reference in-memory adapter: the executable specification of the adapter
/// contract, and the family's test double. Worlds are immutable-per-ref;
/// prepare mutates only the branch; flip is a single pointer swap under a
/// lock, idempotent per apply token. Not for production use.
/// </summary>
public sealed class InMemoryBackendAdapter : IBackendAdapter
{
    private sealed class TargetWorld
    {
        public required Dictionary<string, JsonObject> States { get; init; } // stateRef → world
        public required string ActiveRef { get; set; }
        public Dictionary<string, string> FlipTokens { get; } = []; // applyToken → stateRef
        public Dictionary<string, HashSet<string>> Prepared { get; } = []; // branchRef → changeset fingerprints
    }

    private readonly Dictionary<string, TargetWorld> _targets = [];
    private readonly Lock _lock = new();
    private int _branchCounter;

    public CapabilityManifest Capabilities { get; } = new(
        FlipCapability.Atomic,
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["schema"] = ["full"],
            ["data"] = ["full"],
            ["ui"] = ["full"],
        });

    /// <summary>Create a target with an initial live world. World shape: { schema: { entities: {} }, data: {}, artifacts: {} }.</summary>
    public void SeedTarget(string target, JsonObject? initialWorld = null)
    {
        var world = initialWorld is null
            ? new JsonObject
            {
                ["schema"] = new JsonObject { ["entities"] = new JsonObject() },
                ["data"] = new JsonObject(),
                ["artifacts"] = new JsonObject(),
            }
            : (JsonObject)initialWorld.DeepClone();
        // state refs are globally unique — branches use a global counter, and
        // the seed state embeds the target name (WorldCanonical resolves by ref alone)
        var liveRef = $"live-{target}";
        lock (_lock)
            _targets[target] = new TargetWorld { States = new() { [liveRef] = world }, ActiveRef = liveRef };
    }

    /// <summary>Canonical JSON of the active world — lets tests assert "old or new, never mixed" byte-for-byte.</summary>
    public string ActiveWorldCanonical(string target)
    {
        lock (_lock)
        {
            var world = Get(target);
            return JsonCanonicalizer.Canonicalize(world.States[world.ActiveRef].ToJsonString());
        }
    }

    /// <summary>
    /// Canonical JSON of any state (branch or live) — the host-side read
    /// surface for driving simulation against a branch (adapter-api §3:
    /// simulation is host territory; the adapter only exposes the world).
    /// </summary>
    public string WorldCanonical(string stateRef)
    {
        lock (_lock)
        {
            foreach (var world in _targets.Values)
                if (world.States.TryGetValue(stateRef, out var state))
                    return JsonCanonicalizer.Canonicalize(state.ToJsonString());
            throw new InvalidOperationException($"unknown state ref: {stateRef}");
        }
    }

    /// <summary>Mutate the LIVE world directly, bypassing the lifecycle — exists to simulate out-of-band drift in tests.</summary>
    public void MutateLiveOutOfBand(string target, Action<JsonObject> mutate)
    {
        lock (_lock)
        {
            var world = Get(target);
            mutate(world.States[world.ActiveRef]);
        }
    }

    private TargetWorld Get(string target) =>
        _targets.TryGetValue(target, out var w) ? w : throw new InvalidOperationException($"unknown target: {target}");

    public Task<BranchInfo> BranchAsync(string target, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var world = Get(target);
            var branchRef = $"branch-{++_branchCounter}";
            world.States[branchRef] = (JsonObject)world.States[world.ActiveRef].DeepClone();
            var fidelity = new FidelityDeclaration(
                new Dictionary<string, FacetFidelity>
                {
                    ["schema"] = new("full", "cow"),
                    ["data"] = new("full", "cow"),
                    ["ui"] = new("full", "cow"),
                },
                KnownDifferences: []);
            return Task.FromResult(new BranchInfo(branchRef, fidelity));
        }
    }

    public Task<PrepareReport> PrepareAsync(string branchRef, PreparedFacets facets, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var world = _targets.Values.FirstOrDefault(w => w.States.ContainsKey(branchRef))
                ?? throw new InvalidOperationException($"unknown branch: {branchRef}");
            var prepared = world.Prepared.TryGetValue(branchRef, out var set) ? set : world.Prepared[branchRef] = [];
            if (!prepared.Contains(facets.ChangesetFingerprint))
            {
                ApplyPatches(world.States[branchRef], facets.Patches);
                prepared.Add(facets.ChangesetFingerprint); // idempotent per changeset fingerprint
            }
            return Task.FromResult(new PrepareReport(new Dictionary<string, bool>
            {
                ["schema"] = true, ["ui"] = true, ["data"] = true,
            }));
        }
    }

    public Task FlipAsync(string target, string stateRef, string applyToken, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var world = Get(target);
            if (world.FlipTokens.TryGetValue(applyToken, out var already))
            {
                if (already != stateRef)
                    throw new InvalidOperationException($"apply token {applyToken} was already used for a different state ref");
                return Task.CompletedTask; // idempotent re-issue (fault-model F4/F6 recovery)
            }
            if (!world.States.ContainsKey(stateRef))
                throw new InvalidOperationException($"unknown state ref: {stateRef}");
            world.ActiveRef = stateRef; // THE atomic mutation — a single pointer swap
            world.FlipTokens[applyToken] = stateRef;
            return Task.CompletedTask;
        }
    }

    public Task<ActiveState> ActiveStateAsync(string target, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var world = Get(target);
            var state = world.States[world.ActiveRef];
            var fingerprints = new Dictionary<string, string>
            {
                ["schema"] = FingerprintOf(state["schema"]),
                ["data"] = FingerprintOf(state["data"]),
            };
            foreach (var (artifactId, content) in (JsonObject)state["artifacts"]!)
                fingerprints[artifactId] = ChangesetFingerprint.OfArtifact(content!.GetValue<string>());
            return Task.FromResult(new ActiveState(world.ActiveRef, fingerprints));
        }
    }

    public Task DiscardAsync(string branchRef, CancellationToken ct = default)
    {
        lock (_lock)
        {
            foreach (var world in _targets.Values)
            {
                if (world.ActiveRef == branchRef)
                    throw new InvalidOperationException("refusing to discard the active state");
                world.States.Remove(branchRef);
                world.Prepared.Remove(branchRef);
            }
            return Task.CompletedTask;
        }
    }

    private static string FingerprintOf(JsonNode? node) =>
        ChangesetFingerprint.OfArtifact(JsonCanonicalizer.Canonicalize(node?.ToJsonString() ?? "null"));

    // --- minimal honest patch semantics: enough to make staging observable ---

    private static void ApplyPatches(JsonObject world, JsonObject patches)
    {
        foreach (var op in (patches["schema"] as JsonArray ?? []).OfType<JsonObject>())
            ApplySchemaOp((JsonObject)world["schema"]!["entities"]!, op);
        foreach (var patch in (patches["ui"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var artifacts = (JsonObject)world["artifacts"]!;
            var artifactId = patch["artifactId"]!.GetValue<string>();
            artifacts[artifactId] = ResolveUiContent(artifacts, artifactId, patch);
        }
        foreach (var patch in (patches["data"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var op in (patch["operations"] as JsonArray ?? []).OfType<JsonObject>())
                ApplyDataOp((JsonObject)world["data"]!, op);
    }

    /// <summary>
    /// whole-artifact@0 carries the full content; verified-diff@0 (spec 0.2)
    /// is resolved against the branch's live base with mandatory layer-2
    /// verification (spec §8) — fail-closed: any mismatch aborts the whole
    /// staging application, never a partial land.
    /// </summary>
    private static string ResolveUiContent(JsonObject artifacts, string artifactId, JsonObject patch)
    {
        var profile = patch["profile"]?.GetValue<string>();
        if (profile != "verified-diff@0")
            return patch["newContent"]!.GetValue<string>();
        if (artifacts[artifactId] is not JsonValue baseNode || baseNode.GetValue<string>() is not { } baseContent)
            throw new InvalidOperationException(
                $"verified-diff patch targets unknown artifact '{artifactId}' (creation is whole-artifact@0's job)");
        var verdict = VerifiedDiff.VerifyAgainstBase(patch, baseContent);
        if (!verdict.Ok)
            throw new InvalidOperationException(
                $"verified-diff layer-2 verification failed for '{artifactId}': " +
                string.Join("; ", verdict.Errors.Select(e => $"{e.Path}: {e.Message}")));
        return verdict.NewContent!;
    }

    private static void ApplySchemaOp(JsonObject entities, JsonObject op)
    {
        var entity = op["entity"]!.GetValue<string>();
        JsonObject EntityObj() => entities[entity] as JsonObject
            ?? throw new InvalidOperationException($"unknown entity: {entity}");
        switch (op["op"]!.GetValue<string>())
        {
            case "entity.create":
                var fields = new JsonObject();
                foreach (var f in (op["fields"] as JsonArray ?? []).OfType<JsonObject>())
                    fields[f["name"]!.GetValue<string>()] = f.DeepClone();
                entities[entity] = new JsonObject { ["fields"] = fields, ["constraints"] = new JsonArray() };
                break;
            case "entity.rename":
                var renamed = EntityObj();
                entities.Remove(entity);
                entities[op["newName"]!.GetValue<string>()] = renamed.DeepClone();
                break;
            case "entity.remove":
                entities.Remove(entity);
                break;
            case "field.add":
                var field = (JsonObject)op["field"]!;
                ((JsonObject)EntityObj()["fields"]!)[field["name"]!.GetValue<string>()] = field.DeepClone();
                break;
            case "field.rename":
                var fieldsObj = (JsonObject)EntityObj()["fields"]!;
                var oldName = op["field"]!.GetValue<string>();
                var moved = fieldsObj[oldName]?.DeepClone();
                fieldsObj.Remove(oldName);
                fieldsObj[op["newName"]!.GetValue<string>()] = moved;
                break;
            case "field.retype":
                ((JsonObject)((JsonObject)EntityObj()["fields"]!)[op["field"]!.GetValue<string>()]!)["type"] =
                    op["newType"]!.GetValue<string>();
                break;
            case "field.remove":
                ((JsonObject)EntityObj()["fields"]!).Remove(op["field"]!.GetValue<string>());
                break;
            case "constraint.add":
                ((JsonArray)EntityObj()["constraints"]!).Add(op["constraint"]!.DeepClone());
                break;
            case "constraint.remove":
                var constraints = (JsonArray)EntityObj()["constraints"]!;
                var toRemove = JsonCanonicalizer.Canonicalize(op["constraint"]!.ToJsonString());
                for (var i = constraints.Count - 1; i >= 0; i--)
                    if (JsonCanonicalizer.Canonicalize(constraints[i]!.ToJsonString()) == toRemove)
                        constraints.RemoveAt(i);
                break;
        }
    }

    private static void ApplyDataOp(JsonObject data, JsonObject op)
    {
        var entity = op["entity"]!.GetValue<string>();
        var rows = data[entity] as JsonArray ?? (JsonArray)(data[entity] = new JsonArray());
        switch (op["op"]!.GetValue<string>())
        {
            case "insert":
                rows.Add(op["values"]!.DeepClone());
                break;
            case "update":
                foreach (var row in Matching(rows, op))
                    foreach (var (k, v) in (JsonObject)op["set"]!)
                        row[k] = v?.DeepClone();
                break;
            case "delete":
                foreach (var row in Matching(rows, op).ToList())
                    rows.Remove(row);
                break;
        }
    }

    private static IEnumerable<JsonObject> Matching(JsonArray rows, JsonObject op)
    {
        var where = op["where"] as JsonObject;
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (where is null) { yield return row; continue; }
            var field = where["field"]!.GetValue<string>();
            var expected = where["equals"];
            var actual = row[field];
            var equal = (expected is null && actual is null) ||
                (expected is not null && actual is not null &&
                 JsonNode.DeepEquals(expected, actual));
            if (equal) yield return row;
        }
    }
}
