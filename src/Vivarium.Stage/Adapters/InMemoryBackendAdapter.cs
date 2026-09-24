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

    /// <inheritdoc/>
    public CapabilityManifest Capabilities { get; } = new(
        FlipCapability.Atomic,
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["schema"] = ["full"],
            ["data"] = ["full"],
            ["ui"] = ["full"],
        });

    /// <summary>
    /// Create a target with an initial live world. World shape:
    /// { schema: { entities: { "&lt;entity&gt;": { fields: {}, constraints: [] } } },
    ///   data: { "&lt;entity&gt;": [] }, artifacts: {} }.
    /// <para>A supplied world must carry that whole shape, <em>including the inner shape of
    /// each entity and of each data container</em>. Every read path downstream —
    /// <see cref="ActiveStateAsync"/> fingerprinting each facet, patch application walking
    /// <c>schema.entities.&lt;entity&gt;.fields</c> / <c>.constraints</c> and the row array at
    /// <c>data.&lt;entity&gt;</c> — addresses those containers directly, so a world missing one
    /// does not fail here, it fails later inside a method the caller never named. The seed
    /// is the last point at which the caller still holds the wrong input, so that is where
    /// it is refused, naming every problem at once rather than one per round-trip.</para>
    /// <para>The entity shape is not a convention: <c>entity.create</c> writes exactly
    /// <c>{ fields: {}, constraints: [] }</c> and nothing else ever produces an entity, so a
    /// seeded one that differs is a world the adapter cannot have built itself.</para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="initialWorld"/> is missing a required container, one of them is not
    /// the JSON type the adapter reads back, or an entity or data container inside them is.
    /// </exception>
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
        if (initialWorld is not null)
            RequireWorldShape(world);
        // state refs are globally unique — branches use a global counter, and
        // the seed state embeds the target name (WorldCanonical resolves by ref alone)
        var liveRef = $"live-{target}";
        lock (_lock)
            _targets[target] = new TargetWorld { States = new() { [liveRef] = world }, ActiveRef = liveRef };
    }

    /// <summary>
    /// The documented world shape, checked rather than assumed. Collects every problem
    /// before throwing: a caller fixing one missing container at a time learns the shape
    /// one crash at a time, which is the failure this refusal exists to end.
    /// </summary>
    private static void RequireWorldShape(JsonObject world)
    {
        List<string> problems = [];
        foreach (var key in (string[])["schema", "data", "artifacts"])
        {
            if (!world.ContainsKey(key))
                problems.Add($"'{key}' is missing");
            else if (world[key] is not JsonObject)
                problems.Add($"'{key}' must be an object");
        }
        // schema carries its entities map; patch application addresses it directly.
        if (world["schema"] is JsonObject schema)
        {
            if (schema["entities"] is not JsonObject entities)
                problems.Add(schema.ContainsKey("entities")
                    ? "'schema.entities' must be an object"
                    : "'schema.entities' is missing");
            else
                // One level further than the containers, and for the same reason. An entity
                // that is present but half-formed is the case the container check cannot see:
                // ApplySchemaOp reaches 'fields' and 'constraints' through the entity, so a
                // seeded entity lacking either ends in a stack trace from an operation the
                // caller did name but a member they never mentioned. An *absent* entity is
                // already handled well ("unknown entity: x"), which is what makes the
                // half-formed one the gap rather than a second instance of the same case.
                foreach (var (name, entity) in entities)
                {
                    if (entity is not JsonObject e)
                    {
                        problems.Add($"'schema.entities.{name}' must be an object");
                        continue;
                    }
                    if (e["fields"] is not JsonObject)
                        problems.Add(e.ContainsKey("fields")
                            ? $"'schema.entities.{name}.fields' must be an object"
                            : $"'schema.entities.{name}.fields' is missing");
                    if (e["constraints"] is not JsonArray)
                        problems.Add(e.ContainsKey("constraints")
                            ? $"'schema.entities.{name}.constraints' must be an array"
                            : $"'schema.entities.{name}.constraints' is missing");
                }
        }
        // Rows are the quiet one. ApplyDataOp resolves an entity's rows as "array, or install
        // an empty array", so a non-array container is discarded without an exception and the
        // facet still reports complete — an approved document applied against contents that
        // vanished on the way in. DropRemovedValues skips it just as silently, leaving spec
        // §5.4's "a removal carries away the values" unperformed with nothing said. Absent is
        // fine and normal (the entity simply has no rows yet); present-and-not-an-array is not.
        if (world["data"] is JsonObject data)
            foreach (var (name, rows) in data)
                if (rows is not JsonArray)
                    problems.Add($"'data.{name}' must be an array of rows");
        // Artifact contents are read straight back as strings by ActiveStateAsync — which is
        // the very method the container guard was written for. A seeded artifact holding null
        // reproduces that original NullReferenceException at the same line, and one holding a
        // number or an object trades it for a framework message naming neither the artifact
        // nor the world. Nothing ever writes a non-string here (ResolveUiContent returns
        // string), so a seeded one cannot have come from this adapter.
        if (world["artifacts"] is JsonObject artifacts)
            foreach (var (id, content) in artifacts)
                if (content is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.String)
                    problems.Add($"'artifacts.{id}' must be a string (the artifact's content)");
        if (problems.Count > 0)
            throw new ArgumentException(
                $"seeded world does not carry the shape this adapter reads back: {string.Join("; ", problems)}. " +
                "Expected { schema: { entities: { \"<entity>\": { fields: {}, constraints: [] } } }, " +
                "data: { \"<entity>\": [] }, artifacts: { \"<id>\": \"<content>\" } } — " +
                "domain contents go inside those.",
                "initialWorld");
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
            throw new AdapterRefusedException(AdapterRefusalReason.UnknownRef, $"unknown state ref: {stateRef}",
                new JsonObject { ["ref"] = stateRef });
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
        _targets.TryGetValue(target, out var w)
            ? w
            : throw new AdapterRefusedException(AdapterRefusalReason.UnknownTarget, $"unknown target: {target}",
                new JsonObject { ["target"] = target });

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task<PrepareReport> PrepareAsync(string branchRef, PreparedFacets facets, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var world = _targets.Values.FirstOrDefault(w => w.States.ContainsKey(branchRef))
                ?? throw new AdapterRefusedException(AdapterRefusalReason.UnknownRef, $"unknown branch: {branchRef}",
                    new JsonObject { ["ref"] = branchRef });
            var prepared = world.Prepared.TryGetValue(branchRef, out var set) ? set : world.Prepared[branchRef] = [];
            if (!prepared.Contains(facets.ChangesetFingerprint))
            {
                // Staged on a copy and swapped in only once every operation has applied.
                // The shape check runs before anything is touched, but an operation can be
                // well formed and still name something absent — found only while applying,
                // after earlier operations have already landed. Applying in place left
                // those behind a refusal, so the branch a host was told to retry on was no
                // longer the branch it prepared on (adapter-api §3, a refusal leaves the
                // branch as preparable as it found it).
                var staged = (JsonObject)world.States[branchRef].DeepClone();
                ApplyPatches(staged, facets.Patches);
                world.States[branchRef] = staged;
                prepared.Add(facets.ChangesetFingerprint); // idempotent per changeset fingerprint
            }
            // Report the facets this document actually carried, not a constant. A
            // constant answers "complete" for a facet the changeset never mentioned,
            // which is the same completion-for-work-not-done this adapter refuses
            // elsewhere — and, because it is the executable specification of the
            // contract, an adapter author copying it would inherit the habit.
            return Task.FromResult(new PrepareReport(
                FacetsCarriedBy(facets.Patches).ToDictionary(f => f, _ => true)));
        }
    }

    /// <inheritdoc/>
    public Task FlipAsync(string target, string stateRef, string applyToken, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var world = Get(target);
            if (world.FlipTokens.TryGetValue(applyToken, out var already))
            {
                if (already != stateRef)
                    throw new AdapterRefusedException(AdapterRefusalReason.ApplyTokenConflict,
                        $"apply token {applyToken} was already used for a different state ref",
                        new JsonObject { ["applyToken"] = applyToken, ["usedFor"] = already, ["requested"] = stateRef });
                return Task.CompletedTask; // idempotent re-issue (fault-model F4/F6 recovery)
            }
            if (!world.States.ContainsKey(stateRef))
                throw new AdapterRefusedException(AdapterRefusalReason.UnknownRef, $"unknown state ref: {stateRef}",
                new JsonObject { ["ref"] = stateRef });
            world.ActiveRef = stateRef; // THE atomic mutation — a single pointer swap
            world.FlipTokens[applyToken] = stateRef;
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task DiscardAsync(string branchRef, CancellationToken ct = default)
    {
        lock (_lock)
        {
            foreach (var world in _targets.Values)
            {
                if (world.ActiveRef == branchRef)
                    throw new AdapterRefusedException(AdapterRefusalReason.StateIsActive, "refusing to discard the active state",
                        new JsonObject { ["ref"] = branchRef });
                world.States.Remove(branchRef);
                world.Prepared.Remove(branchRef);
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Which facets a prepared document carries — an empty array counts as not carried,
    /// because a facet with no patches is not work this prepare completed.
    /// </summary>
    private static IEnumerable<string> FacetsCarriedBy(JsonObject patches) =>
        ((string[])["schema", "ui", "data"])
            .Where(key => patches[key] is JsonArray { Count: > 0 });

    private static string FingerprintOf(JsonNode? node) =>
        ChangesetFingerprint.OfArtifact(JsonCanonicalizer.Canonicalize(node?.ToJsonString() ?? "null"));

    // --- minimal honest patch semantics: enough to make staging observable ---

    private static void ApplyPatches(JsonObject world, JsonObject patches)
    {
        // Total check before any mutation: prepare receives a document authored
        // elsewhere, so its operations are input to be validated, not facts to be
        // trusted. Checking op-by-op inside an apply loop would leave the branch
        // half-staged behind a refusal.
        //
        // All three facets, not just data. The changeset validator does constrain
        // schema and UI patches — but PrepareAsync is public API that a host can
        // call without ever building a ChangeSession, so "the validator checked it"
        // is not a property this method can rely on.
        foreach (var (op, i) in (patches["schema"] as JsonArray ?? []).Select((o, i) => (o, i)))
            RequireWellFormedSchemaOp(op, $"$.patches.schema[{i}]");
        foreach (var (patch, i) in (patches["ui"] as JsonArray ?? []).Select((o, i) => (o, i)))
            RequireWellFormedUiPatch(patch, $"$.patches.ui[{i}]");
        var dataPatches = (patches["data"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        for (var p = 0; p < dataPatches.Length; p++)
            foreach (var (op, i) in (dataPatches[p]["operations"] as JsonArray ?? []).Select((o, i) => (o, i)))
                RequireWellFormedDataOp(op, $"$.patches.data[{p}].operations[{i}]");

        // Spec §5.4 — expand, then move the values, then contract. Adding first is what
        // lets one document create an entity and populate it; removing last is what lets
        // its data operations still read and rewrite the rows of a field on its way out.
        // Applying every schema operation up front (which this did through 0.7.x) makes
        // the second of those unexpressible, and leaves a cleared-then-removed field
        // behind as a member no schema declares.
        var schemaOps = (patches["schema"] as JsonArray ?? [])
            .Select((o, i) => (Op: (JsonObject)o!, Path: $"$.patches.schema[{i}]")).ToArray();
        var entities = (JsonObject)world["schema"]!["entities"]!;

        foreach (var (op, path) in schemaOps.Where(o => !IsRemoval(o.Op)))
            ApplySchemaOp(entities, op, path);

        // UI patches address artifacts, not rows — nothing observable depends on where
        // they fall, so they keep their place between the two schema phases.
        foreach (var (patch, i) in (patches["ui"] as JsonArray ?? []).Select((o, i) => ((JsonObject)o!, i)))
        {
            var artifacts = (JsonObject)world["artifacts"]!;
            var artifactId = patch["artifactId"]!.GetValue<string>();
            artifacts[artifactId] = ResolveUiContent(artifacts, artifactId, patch, $"$.patches.ui[{i}]");
        }

        foreach (var patch in (patches["data"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var op in (patch["operations"] as JsonArray ?? []).OfType<JsonObject>())
                ApplyDataOp((JsonObject)world["data"]!, op);

        foreach (var (op, path) in schemaOps.Where(o => IsRemoval(o.Op)))
        {
            ApplySchemaOp(entities, op, path);
            // §5.4: a removal carries away the values stored under what it removes.
            // Dropping the declaration alone would leave rows holding members nothing
            // in the schema explains — and nothing in the operation vocabulary can
            // address afterwards, since every op names a declared field.
            DropRemovedValues((JsonObject)world["data"]!, op);
        }
    }

    private static bool IsRemoval(JsonObject op) =>
        op["op"]?.GetValue<string>() is "field.remove" or "entity.remove";

    private static void DropRemovedValues(JsonObject data, JsonObject op)
    {
        var entity = op["entity"]!.GetValue<string>();
        if (op["op"]!.GetValue<string>() == "entity.remove")
        {
            data.Remove(entity);
            return;
        }
        if (data[entity] is not JsonArray rows) return;
        var field = op["field"]!.GetValue<string>();
        foreach (var row in rows.OfType<JsonObject>())
            row.Remove(field);
    }

    /// <summary>
    /// whole-artifact@0 carries the full content; verified-diff@0 (spec 0.2)
    /// is resolved against the branch's live base with mandatory layer-2
    /// verification (spec §8) — fail-closed: any mismatch aborts the whole
    /// staging application, never a partial land.
    /// </summary>
    private static string ResolveUiContent(JsonObject artifacts, string artifactId, JsonObject patch, string path)
    {
        var profile = patch["profile"]?.GetValue<string>();
        if (profile != "verified-diff@0")
            return patch["newContent"]!.GetValue<string>();
        if (artifacts[artifactId] is not JsonValue baseNode || baseNode.GetValue<string>() is not { } baseContent)
            throw AdapterRefusedException.Document($"{path}.artifactId",
                $"verified-diff patch targets unknown artifact '{artifactId}' (creation is whole-artifact@0's job)");
        var verdict = VerifiedDiff.VerifyAgainstBase(patch, baseContent);
        if (!verdict.Ok)
            throw new AdapterRefusedException(AdapterRefusalReason.DocumentRefused,
                $"{path}: verified-diff layer-2 verification failed for '{artifactId}': " +
                string.Join("; ", verdict.Errors.Select(e => $"{e.Path}: {e.Message}")),
                new JsonObject
                {
                    ["errors"] = new JsonArray(verdict.Errors
                        // The verifier locates each failure within the patch ($.baseFingerprint,
                        // $.diff.hunk[1]); lifted under the patch's own place in the document.
                        .Select(e => (JsonNode)new JsonObject
                        {
                            ["path"] = e.Path == "$" ? path : path + e.Path[1..],
                            ["message"] = e.Message,
                        })
                        .ToArray()),
                });
        return verdict.NewContent!;
    }

    private static void ApplySchemaOp(JsonObject entities, JsonObject op, string path)
    {
        var entity = op["entity"]!.GetValue<string>();
        JsonObject EntityObj() => entities[entity] as JsonObject
            ?? throw AdapterRefusedException.Document($"{path}.entity", $"unknown entity: {entity}");
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
                // Removal is where a quiet success is most convincing: the target is
                // supposed to end up absent, so an operation naming an entity nobody
                // has looks exactly like one that worked. EntityObj() is the same
                // door the other operations go through.
                EntityObj();
                entities.Remove(entity);
                break;
            case "field.add":
                var field = (JsonObject)op["field"]!;
                ((JsonObject)EntityObj()["fields"]!)[field["name"]!.GetValue<string>()] = field.DeepClone();
                break;
            case "field.rename":
                var fieldsObj = (JsonObject)EntityObj()["fields"]!;
                var oldName = op["field"]!.GetValue<string>();
                // renaming a field that is not there wrote a null under the new name —
                // a declared-but-empty field that no later read distinguishes from a
                // real one. Refuse instead: the entity is known, the field is not.
                var moved = fieldsObj[oldName]?.DeepClone()
                    ?? throw AdapterRefusedException.Document($"{path}.field",
                        $"cannot rename '{oldName}' on entity '{entity}': no such field");
                fieldsObj.Remove(oldName);
                fieldsObj[op["newName"]!.GetValue<string>()] = moved;
                break;
            case "field.retype":
                var retypeName = op["field"]!.GetValue<string>();
                var target = ((JsonObject)EntityObj()["fields"]!)[retypeName] as JsonObject
                    ?? throw AdapterRefusedException.Document($"{path}.field",
                        $"cannot retype '{retypeName}' on entity '{entity}': no such field");
                target["type"] = op["newType"]!.GetValue<string>();
                break;
            case "field.remove":
                var removeName = op["field"]!.GetValue<string>();
                var removeFrom = (JsonObject)EntityObj()["fields"]!;
                // Remove returns false for a key that was never there, and reporting
                // the facet complete on that is completion for work not done: an
                // approved document said this field would go, and nothing went.
                if (!removeFrom.Remove(removeName))
                    throw AdapterRefusedException.Document($"{path}.field",
                        $"cannot remove '{removeName}' on entity '{entity}': no such field");
                break;
            case "constraint.add":
                ((JsonArray)EntityObj()["constraints"]!).Add(op["constraint"]!.DeepClone());
                break;
            case "constraint.remove":
                var constraints = (JsonArray)EntityObj()["constraints"]!;
                var toRemove = JsonCanonicalizer.Canonicalize(op["constraint"]!.ToJsonString());
                // A constraint is addressed by its whole shape rather than by a name,
                // so "remove the ones that match" reads like a filter and matching
                // nothing looks like a legitimate outcome. It is not: the document
                // named a constraint it expected to find.
                var removed = false;
                for (var i = constraints.Count - 1; i >= 0; i--)
                    if (JsonCanonicalizer.Canonicalize(constraints[i]!.ToJsonString()) == toRemove)
                    {
                        constraints.RemoveAt(i);
                        removed = true;
                    }
                if (!removed)
                    throw AdapterRefusedException.Document($"{path}.constraint",
                        $"cannot remove a constraint on entity '{entity}': no such constraint");
                break;
            default:
                // unreachable: the vocabulary is checked before anything is applied.
                // Present so that widening it cannot silently no-op while prepare
                // still reports the schema facet complete.
                throw new InvalidOperationException($"unhandled schema operation: {op["op"]}");
        }
    }

    /// <summary>
    /// Refuse a data operation this adapter cannot execute honestly, naming what is
    /// wrong and where (adapter-api §Operation input). The changeset spec defines the
    /// shape; an adapter checks it anyway, because <c>prepare</c> is the door and a
    /// door that assumes its input has been checked upstream is not a door.
    /// </summary>
    /// <summary>Per-operation members the schema vocabulary defines (changeset spec §5.1).</summary>
    private static readonly Dictionary<string, string[]> SchemaOpMembers = new()
    {
        ["entity.create"] = ["entity", "fields"],
        ["entity.rename"] = ["entity", "newName"],
        ["entity.remove"] = ["entity"],
        ["field.add"] = ["entity", "field"],
        ["field.rename"] = ["entity", "field", "newName"],
        ["field.retype"] = ["entity", "field", "newType"],
        ["field.remove"] = ["entity", "field"],
        ["constraint.add"] = ["entity", "constraint"],
        ["constraint.remove"] = ["entity", "constraint"],
    };

    /// <summary>Refuse a schema operation this adapter cannot execute honestly (adapter-api §Operation input).</summary>
    private static void RequireWellFormedSchemaOp(JsonNode? node, string path)
    {
        if (node is not JsonObject op)
            throw AdapterRefusedException.Document($"{path}", $"schema operation must be a JSON object");

        var kind = (op["op"] as JsonValue)?.TryGetValue(out string? k) == true ? k : null;
        if (kind is null || !SchemaOpMembers.TryGetValue(kind, out var required))
            throw AdapterRefusedException.Document($"{path}.op",
                $"unknown schema operation '{op["op"]?.ToJsonString() ?? "undefined"}' " +
                $"(expected one of: {string.Join(", ", SchemaOpMembers.Keys)})");

        foreach (var member in required)
            if (!op.ContainsKey(member))
                throw AdapterRefusedException.Document($"{path}.{member}", $"required by {kind}");

        if ((op["entity"] as JsonValue)?.TryGetValue(out string? entity) != true || string.IsNullOrEmpty(entity))
            throw AdapterRefusedException.Document($"{path}.entity", $"required non-empty string");

        // `field` is an object for field.add (the declaration) and a name for the
        // operations that address an existing one — the vocabulary's one asymmetry,
        // and the reason a single "is it a string" check would be wrong here.
        if (kind == "field.add")
        {
            if (op["field"] is not JsonObject declaration)
                throw AdapterRefusedException.Document($"{path}.field", $"field.add declares a field, so this must be an object");
            if ((declaration["name"] as JsonValue)?.TryGetValue(out string? fieldName) != true || string.IsNullOrEmpty(fieldName))
                throw AdapterRefusedException.Document($"{path}.field.name", $"required non-empty string");
        }
        else if (required.Contains("field")
            && ((op["field"] as JsonValue)?.TryGetValue(out string? named) != true || string.IsNullOrEmpty(named)))
        {
            throw AdapterRefusedException.Document($"{path}.field", $"required non-empty field name");
        }

        foreach (var member in new[] { "newName", "newType" })
            if (required.Contains(member)
                && ((op[member] as JsonValue)?.TryGetValue(out string? value) != true || string.IsNullOrEmpty(value)))
                throw AdapterRefusedException.Document($"{path}.{member}", $"required non-empty string");

        if (kind == "entity.create" && op["fields"] is not JsonArray)
            throw AdapterRefusedException.Document($"{path}.fields", $"entity.create requires an array of field declarations");
        if (kind == "entity.create")
            foreach (var (f, i) in ((JsonArray)op["fields"]!).Select((f, i) => (f, i)))
                if ((f as JsonObject)?["name"] is not JsonValue name
                    || !name.TryGetValue(out string? n) || string.IsNullOrEmpty(n))
                    throw AdapterRefusedException.Document($"{path}.fields[{i}].name", $"required non-empty string");
    }

    /// <summary>Refuse a UI patch this adapter cannot execute honestly (adapter-api §Operation input).</summary>
    private static void RequireWellFormedUiPatch(JsonNode? node, string path)
    {
        if (node is not JsonObject patch)
            throw AdapterRefusedException.Document($"{path}", $"UI patch must be a JSON object");

        if ((patch["artifactId"] as JsonValue)?.TryGetValue(out string? artifactId) != true || string.IsNullOrEmpty(artifactId))
            throw AdapterRefusedException.Document($"{path}.artifactId", $"required non-empty string");

        var profile = (patch["profile"] as JsonValue)?.TryGetValue(out string? p) == true ? p : null;
        if (profile is not ("whole-artifact@0" or "verified-diff@0"))
            throw AdapterRefusedException.Document($"{path}.profile",
                $"unknown UI patch profile '{patch["profile"]?.ToJsonString() ?? "undefined"}' " +
                "(expected whole-artifact@0 or verified-diff@0)");

        // verified-diff@0's own inputs are already verified where they are resolved
        // against the branch's base (layer 2, §5.2.2) — checking them twice would
        // duplicate a check that has to live there anyway.
        if (profile == "whole-artifact@0"
            && ((patch["newContent"] as JsonValue)?.TryGetValue(out string? _) != true))
            throw AdapterRefusedException.Document($"{path}.newContent", $"whole-artifact@0 carries the full content, so this is required");
    }

    private static void RequireWellFormedDataOp(JsonNode? node, string path)
    {
        if (node is not JsonObject op)
            throw AdapterRefusedException.Document($"{path}", $"data operation must be a JSON object");

        var kind = (op["op"] as JsonValue)?.TryGetValue(out string? k) == true ? k : null;
        if (kind is not ("insert" or "update" or "delete"))
            throw AdapterRefusedException.Document($"{path}.op",
                $"unknown data operation '{op["op"]?.ToJsonString() ?? "undefined"}' " +
                "(expected insert, update, or delete)");

        if ((op["entity"] as JsonValue)?.TryGetValue(out string? entity) != true || string.IsNullOrEmpty(entity))
            throw AdapterRefusedException.Document($"{path}.entity", $"required non-empty string");

        if (kind is "insert" && op["values"] is not JsonObject)
            throw AdapterRefusedException.Document($"{path}.values", $"insert requires an object of field name to value");
        if (kind is "update" && op["set"] is not JsonObject)
            throw AdapterRefusedException.Document($"{path}.set", $"update requires an object of field name to value");

        if (kind is "insert") return;
        if (op["where"] is not JsonObject where)
            throw AdapterRefusedException.Document($"{path}.where",
                $"{kind} requires a predicate object {{ field, equals }}");
        if ((where["field"] as JsonValue)?.TryGetValue(out string? field) != true || string.IsNullOrEmpty(field))
            throw AdapterRefusedException.Document($"{path}.where.field", $"required non-empty string");
        if (!where.ContainsKey("equals"))
            throw AdapterRefusedException.Document($"{path}.where.equals", $"required — a literal to compare against");
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
            default:
                // unreachable: the op vocabulary is checked before anything is applied.
                // Present so that widening the vocabulary cannot silently no-op instead.
                throw new InvalidOperationException($"unhandled data operation: {op["op"]}");
        }
    }

    private static IEnumerable<JsonObject> Matching(JsonArray rows, JsonObject op)
    {
        // No match-all fallback: a predicate that went missing must not quietly select
        // every row. RequireWellFormedDataOp has already refused that document.
        var where = op["where"] as JsonObject
            ?? throw new InvalidOperationException("data operation reached matching without a predicate");
        foreach (var row in rows.OfType<JsonObject>())
        {
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
