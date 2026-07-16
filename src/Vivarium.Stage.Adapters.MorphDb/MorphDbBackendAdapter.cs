using System.Text.Json.Nodes;
using Vivarium.Changeset;
using Vivarium.Stage.Adapters;

namespace Vivarium.Stage.Adapters.MorphDb;

/// <summary>
/// First real backend adapter (Phase 4.b): MorphDB over REST.
///
/// World model: each stage "state" is a MorphDB *project* (an isolated pair of
/// PostgreSQL schemas). Branching creates a new project and copies schema,
/// data, and UI artifacts into it (snapshot copy — MorphDB has no native CoW).
/// The atomic flip is a pointer swap in a stage-owned control project: one
/// MorphDB transaction inserts the flip token (unique) and repoints the
/// target's active-project row — PostgreSQL transactionality makes the swap
/// atomic and durable, and the unique token makes it idempotent under
/// recovery re-issue (fault-model §4).
///
/// UI artifacts live in a per-project <c>vivarium_artifacts</c> table so that
/// schema, data, and UI flip together — the fixed principle 1 atomicity
/// boundary is the project pointer, not three separate deploys.
/// </summary>
public sealed class MorphDbBackendAdapter(MorphDbAdapterOptions options) : IBackendAdapter
{
    private const string TargetsTable = "vivarium_targets";
    private const string FlipsTable = "vivarium_flips";
    private const string ArtifactsTable = "vivarium_artifacts";
    private const string PreparedTable = "vivarium_prepared";
    private static readonly string[] AdapterTables = [ArtifactsTable, PreparedTable];

    private Guid? _controlProjectId;

    public CapabilityManifest Capabilities { get; } = new(
        // one control-row transaction on PostgreSQL: atomic, durable, and
        // idempotent under the unique flip token (fault-model §4)
        FlipCapability.Atomic,
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["schema"] = ["full"],
            ["data"] = ["full"],
            ["ui"] = ["full"],
        });

    /// <summary>REST client scoped to one project (tenant). Public for host-side seeding.</summary>
    public MorphDbRestClient ClientFor(Guid projectId) => new(options.BaseUrl, options.ApiKey, projectId);

    private MorphDbRestClient Admin() => new(options.BaseUrl, options.ApiKey);

    // ---- control project: target pointers + flip log ----

    private async Task<Guid> ControlAsync(CancellationToken ct)
    {
        if (_controlProjectId is { } cached) return cached;
        using var admin = Admin();
        var id = await admin.FindProjectAsync(options.ControlProjectName, ct).ConfigureAwait(false)
            ?? await admin.CreateProjectAsync(options.ControlProjectName, ct).ConfigureAwait(false);
        using var control = ClientFor(id);
        if (await control.GetTableAsync(TargetsTable, ct).ConfigureAwait(false) is null)
            await control.CreateTableAsync(TargetsTable, new JsonArray(
                Column("target", "text", nullable: false, unique: true),
                Column("active_project", "text", nullable: false)), ct).ConfigureAwait(false);
        if (await control.GetTableAsync(FlipsTable, ct).ConfigureAwait(false) is null)
            await control.CreateTableAsync(FlipsTable, new JsonArray(
                Column("flip_token", "text", nullable: false, unique: true),
                Column("target", "text", nullable: false),
                Column("state_ref", "text", nullable: false)), ct).ConfigureAwait(false);
        _controlProjectId = id;
        return id;
    }

    private static JsonObject Column(string name, string type, bool nullable = true, bool unique = false) => new()
    {
        ["name"] = name,
        ["type"] = type,
        ["nullable"] = nullable,
        ["unique"] = unique,
    };

    private static async Task<MorphDbRestClient.Row?> TargetRowAsync(MorphDbRestClient control, string target, CancellationToken ct)
    {
        var (rows, _) = await control.QueryAsync(TargetsTable, "target", target, pageSize: 1, ct: ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Bind a target name to its live MorphDB project. Creates the pointer row; refuses to overwrite one.</summary>
    public async Task RegisterTargetAsync(string target, Guid liveProjectId, CancellationToken ct = default)
    {
        var controlId = await ControlAsync(ct).ConfigureAwait(false);
        using var control = ClientFor(controlId);
        if (await TargetRowAsync(control, target, ct).ConfigureAwait(false) is not null)
            throw new InvalidOperationException($"target '{target}' is already registered — the pointer is flipped, never re-registered");
        await control.InsertAsync(TargetsTable, new JsonObject
        {
            ["target"] = target,
            ["active_project"] = liveProjectId.ToString(),
        }, ct).ConfigureAwait(false);
    }

    private async Task<(Guid ProjectId, Guid RowId)> ActiveProjectAsync(string target, CancellationToken ct)
    {
        var controlId = await ControlAsync(ct).ConfigureAwait(false);
        using var control = ClientFor(controlId);
        var row = await TargetRowAsync(control, target, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"unknown target: {target} (register it first)");
        return (Guid.Parse(row.Data["active_project"]!.GetValue<string>()), row.Id);
    }

    // ---- IBackendAdapter ----

    public async Task<BranchInfo> BranchAsync(string target, CancellationToken ct = default)
    {
        var (sourceProject, _) = await ActiveProjectAsync(target, ct).ConfigureAwait(false);
        using var admin = Admin();
        var branchProject = await admin.CreateProjectAsync(
            $"vivarium-branch-{Guid.NewGuid():n}"[..40], ct).ConfigureAwait(false);

        using var source = ClientFor(sourceProject);
        using var branch = ClientFor(branchProject);

        foreach (var table in (await source.GetTablesAsync(ct).ConfigureAwait(false)).OfType<JsonObject>())
        {
            var name = table["name"]!.GetValue<string>();
            var columns = new JsonArray(((JsonArray)table["columns"]!)
                .OfType<JsonObject>()
                .Where(c => !c["primaryKey"]!.GetValue<bool>()
                    && !c["name"]!.GetValue<string>().StartsWith('_')
                    && c["name"]!.GetValue<string>() != "tenant_id")
                .Select(c => (JsonNode)Column(
                    c["name"]!.GetValue<string>(),
                    c["type"]!.GetValue<string>(),
                    c["nullable"]!.GetValue<bool>(),
                    c["unique"]!.GetValue<bool>()))
                .ToArray());
            await branch.CreateTableAsync(name, columns, ct).ConfigureAwait(false);

            // copy data (a fresh branch starts with an empty prepare log)
            if (name == PreparedTable) continue;
            foreach (var row in await source.QueryAllAsync(name, pageSize: options.CopyPageSize, ct: ct).ConfigureAwait(false))
            {
                var values = new JsonObject();
                foreach (var (k, v) in row.Data)
                    if (!k.StartsWith('_') && k != "tenant_id")
                        values[k] = v?.DeepClone();
                await branch.InsertAsync(name, values, ct).ConfigureAwait(false);
            }
        }

        var fidelity = new FidelityDeclaration(
            new Dictionary<string, FacetFidelity>
            {
                ["schema"] = new("full", "snapshot"),
                ["data"] = new("full", "snapshot"),
                ["ui"] = new("full", "snapshot"),
            },
            KnownDifferences:
            [
                "copied rows receive new system ids and created/updated timestamps",
                "physical PostgreSQL names differ (logical names are identical)",
            ]);
        return new BranchInfo(branchProject.ToString(), fidelity);
    }

    public async Task<PrepareReport> PrepareAsync(string branchRef, PreparedFacets facets, CancellationToken ct = default)
    {
        var projectId = Guid.Parse(branchRef);
        using var client = ClientFor(projectId);

        // idempotency per changeset fingerprint (adapter-api §3)
        await EnsureAdapterTablesAsync(client, ct).ConfigureAwait(false);
        var (seen, _) = await client.QueryAsync(PreparedTable, "fingerprint", facets.ChangesetFingerprint, pageSize: 1, ct: ct).ConfigureAwait(false);
        if (seen.Count > 0) return CompleteReport();

        foreach (var op in (facets.Patches["schema"] as JsonArray ?? []).OfType<JsonObject>())
            await ApplySchemaOpAsync(client, op, ct).ConfigureAwait(false);

        foreach (var patch in (facets.Patches["ui"] as JsonArray ?? []).OfType<JsonObject>())
            await UpsertArtifactAsync(client,
                patch["artifactId"]!.GetValue<string>(),
                patch["newContent"]!.GetValue<string>(), ct).ConfigureAwait(false);

        foreach (var patch in (facets.Patches["data"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var op in (patch["operations"] as JsonArray ?? []).OfType<JsonObject>())
                await ApplyDataOpAsync(client, op, ct).ConfigureAwait(false);

        await client.InsertAsync(PreparedTable, new JsonObject
        {
            ["fingerprint"] = facets.ChangesetFingerprint,
        }, ct).ConfigureAwait(false);
        return CompleteReport();

        static PrepareReport CompleteReport() => new(new Dictionary<string, bool>
        {
            ["schema"] = true, ["ui"] = true, ["data"] = true,
        });
    }

    public async Task FlipAsync(string target, string stateRef, string applyToken, CancellationToken ct = default)
    {
        var controlId = await ControlAsync(ct).ConfigureAwait(false);
        using var control = ClientFor(controlId);
        var row = await TargetRowAsync(control, target, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"unknown target: {target}");

        try
        {
            // ONE transaction: claim the token (unique) + repoint the target.
            // PostgreSQL atomicity underneath makes this the swap primitive.
            await control.TransactionAsync(new JsonArray(
                new JsonObject
                {
                    ["method"] = "INSERT",
                    ["table"] = FlipsTable,
                    ["data"] = new JsonObject
                    {
                        ["flip_token"] = applyToken,
                        ["target"] = target,
                        ["state_ref"] = stateRef,
                    },
                },
                new JsonObject
                {
                    ["method"] = "UPDATE",
                    ["table"] = TargetsTable,
                    ["id"] = row.Id.ToString(),
                    ["data"] = new JsonObject { ["active_project"] = stateRef },
                }), ct).ConfigureAwait(false);
        }
        catch (MorphDbApiException e)
        {
            // idempotent re-issue: acceptable iff this token already flipped
            // to this exact state ref (fault-model F4/F6 recovery)
            var (existing, _) = await control.QueryAsync(FlipsTable, "flip_token", applyToken, pageSize: 1, ct: ct).ConfigureAwait(false);
            if (existing.Count == 0) throw;
            var landed = existing[0].Data["state_ref"]!.GetValue<string>();
            if (landed != stateRef)
                throw new InvalidOperationException(
                    $"apply token {applyToken} was already used for a different state ref ({landed})", e);
        }
    }

    public async Task<ActiveState> ActiveStateAsync(string target, CancellationToken ct = default)
    {
        var (projectId, _) = await ActiveProjectAsync(target, ct).ConfigureAwait(false);
        using var client = ClientFor(projectId);

        // deterministic schema fingerprint: canonical JSON of the logical structure
        var entities = new JsonObject();
        var hasArtifacts = false;
        foreach (var table in (await client.GetTablesAsync(ct).ConfigureAwait(false)).OfType<JsonObject>())
        {
            var name = table["name"]!.GetValue<string>();
            if (name == ArtifactsTable) hasArtifacts = true;
            if (AdapterTables.Contains(name)) continue; // adapter bookkeeping is not world schema
            var columns = new JsonObject();
            foreach (var c in ((JsonArray)table["columns"]!).OfType<JsonObject>())
            {
                var columnName = c["name"]!.GetValue<string>();
                if (c["primaryKey"]!.GetValue<bool>() || columnName.StartsWith('_') || columnName == "tenant_id") continue;
                columns[columnName] = new JsonObject
                {
                    ["type"] = c["type"]!.GetValue<string>(),
                    ["nullable"] = c["nullable"]!.GetValue<bool>(),
                    ["unique"] = c["unique"]!.GetValue<bool>(),
                };
            }
            entities[name] = columns;
        }
        var fingerprints = new Dictionary<string, string>
        {
            ["schema"] = ChangesetFingerprint.OfArtifact(
                JsonCanonicalizer.Canonicalize(entities.ToJsonString())),
        };

        // per-artifact content fingerprints (spec §4: raw UTF-8 bytes, no JCS)
        if (hasArtifacts)
            foreach (var row in await client.QueryAllAsync(ArtifactsTable, pageSize: options.CopyPageSize, ct: ct).ConfigureAwait(false))
                fingerprints[row.Data["artifact_id"]!.GetValue<string>()] =
                    ChangesetFingerprint.OfArtifact(row.Data["content"]!.GetValue<string>());

        return new ActiveState(projectId.ToString(), fingerprints);
    }

    public async Task DiscardAsync(string branchRef, CancellationToken ct = default)
    {
        var controlId = await ControlAsync(ct).ConfigureAwait(false);
        using var control = ClientFor(controlId);
        var (pointers, _) = await control.QueryAsync(TargetsTable, "active_project", branchRef, pageSize: 1, ct: ct).ConfigureAwait(false);
        if (pointers.Count > 0)
            throw new InvalidOperationException("refusing to discard the active state");
        using var admin = Admin();
        await admin.DeleteProjectAsync(Guid.Parse(branchRef), ct).ConfigureAwait(false);
    }

    // ---- facet operation mapping ----

    /// <summary>Changeset logical types (spec ADR-0005) → MorphDB abstract types.</summary>
    private static string MapType(string logicalType) => logicalType switch
    {
        "string" => "text",
        "number" => "decimal",
        "boolean" => "boolean",
        "date" => "date",
        "datetime" => "datetime",
        "reference" => "uuid",
        "json" => "json",
        _ => throw new NotSupportedException($"unknown logical type: {logicalType}"),
    };

    private static JsonObject FieldToColumn(JsonObject f) => Column(
        f["name"]!.GetValue<string>(),
        MapType(f["type"]!.GetValue<string>()),
        nullable: !(f["required"]?.GetValue<bool>() ?? false));

    private static async Task<(Guid ColumnId, int TableVersion)> FindColumnAsync(
        MorphDbRestClient client, string entity, string column, CancellationToken ct)
    {
        var table = await client.GetTableAsync(entity, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"unknown entity: {entity}");
        var col = ((JsonArray)table["columns"]!).OfType<JsonObject>()
            .FirstOrDefault(c => c["name"]!.GetValue<string>() == column)
            ?? throw new InvalidOperationException($"unknown field: {entity}.{column}");
        return (Guid.Parse(col["id"]!.GetValue<string>()), table["version"]!.GetValue<int>());
    }

    private static async Task ApplySchemaOpAsync(MorphDbRestClient client, JsonObject op, CancellationToken ct)
    {
        var entity = op["entity"]!.GetValue<string>();
        switch (op["op"]!.GetValue<string>())
        {
            case "entity.create":
                await client.CreateTableAsync(entity, new JsonArray(
                    ((JsonArray)op["fields"]!).OfType<JsonObject>().Select(f => (JsonNode)FieldToColumn(f)).ToArray()),
                    ct).ConfigureAwait(false);
                break;
            case "entity.rename":
            {
                var table = await client.GetTableAsync(entity, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"unknown entity: {entity}");
                await client.RenameTableAsync(entity, op["newName"]!.GetValue<string>(),
                    table["version"]!.GetValue<int>(), ct).ConfigureAwait(false);
                break;
            }
            case "entity.remove":
                await client.DropTableAsync(entity, ct).ConfigureAwait(false);
                break;
            case "field.add":
                await client.AddColumnAsync(entity, FieldToColumn((JsonObject)op["field"]!), ct).ConfigureAwait(false);
                break;
            case "field.rename":
            {
                var (columnId, version) = await FindColumnAsync(client, entity, op["field"]!.GetValue<string>(), ct).ConfigureAwait(false);
                await client.UpdateColumnAsync(columnId, new JsonObject
                {
                    ["name"] = op["newName"]!.GetValue<string>(),
                    ["version"] = version,
                }, ct).ConfigureAwait(false);
                break;
            }
            case "field.retype":
            {
                var (columnId, version) = await FindColumnAsync(client, entity, op["field"]!.GetValue<string>(), ct).ConfigureAwait(false);
                await client.UpdateColumnAsync(columnId, new JsonObject
                {
                    ["type"] = MapType(op["newType"]!.GetValue<string>()),
                    ["version"] = version,
                }, ct).ConfigureAwait(false);
                break;
            }
            case "field.remove":
            {
                var (columnId, _) = await FindColumnAsync(client, entity, op["field"]!.GetValue<string>(), ct).ConfigureAwait(false);
                await client.DropColumnAsync(columnId, ct).ConfigureAwait(false);
                break;
            }
            // The changeset constraint vocabulary has no MorphDB counterpart
            // pinned yet — an honest prepare failure (F2: staging only, retry
            // or discard) rather than a silent approximation.
            case "constraint.add":
            case "constraint.remove":
                throw new NotSupportedException(
                    $"schema op '{op["op"]}' is not supported by the MorphDB adapter v0");
            default:
                throw new NotSupportedException($"unknown schema op: {op["op"]}");
        }
    }

    private async Task ApplyDataOpAsync(MorphDbRestClient client, JsonObject op, CancellationToken ct)
    {
        var entity = op["entity"]!.GetValue<string>();
        var where = op["where"] as JsonObject;
        switch (op["op"]!.GetValue<string>())
        {
            case "insert":
                await client.InsertAsync(entity, (JsonObject)op["values"]!.DeepClone(), ct).ConfigureAwait(false);
                break;
            case "update":
                foreach (var row in await MatchingRowsAsync(client, entity, where, ct).ConfigureAwait(false))
                    await client.UpdateRowAsync(entity, row.Id, (JsonObject)op["set"]!.DeepClone(), ct).ConfigureAwait(false);
                break;
            case "delete":
                foreach (var row in await MatchingRowsAsync(client, entity, where, ct).ConfigureAwait(false))
                    await client.DeleteRowAsync(entity, row.Id, ct).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"unknown data op: {op["op"]}");
        }
    }

    private Task<IReadOnlyList<MorphDbRestClient.Row>> MatchingRowsAsync(
        MorphDbRestClient client, string entity, JsonObject? where, CancellationToken ct) =>
        client.QueryAllAsync(entity,
            where?["field"]!.GetValue<string>(),
            where?["equals"],
            options.CopyPageSize, ct);

    private static async Task UpsertArtifactAsync(MorphDbRestClient client, string artifactId, string content, CancellationToken ct)
    {
        var (existing, _) = await client.QueryAsync(ArtifactsTable, "artifact_id", artifactId, pageSize: 1, ct: ct).ConfigureAwait(false);
        if (existing.Count > 0)
            await client.UpdateRowAsync(ArtifactsTable, existing[0].Id,
                new JsonObject { ["content"] = content }, ct).ConfigureAwait(false);
        else
            await client.InsertAsync(ArtifactsTable,
                new JsonObject { ["artifact_id"] = artifactId, ["content"] = content }, ct).ConfigureAwait(false);
    }

    /// <summary>Ensure the per-project adapter tables exist (idempotent; also used to seed a fresh live project).</summary>
    public async Task EnsureAdapterTablesAsync(MorphDbRestClient client, CancellationToken ct = default)
    {
        if (await client.GetTableAsync(ArtifactsTable, ct).ConfigureAwait(false) is null)
            await client.CreateTableAsync(ArtifactsTable, new JsonArray(
                Column("artifact_id", "text", nullable: false, unique: true),
                Column("content", "text", nullable: false)), ct).ConfigureAwait(false);
        if (await client.GetTableAsync(PreparedTable, ct).ConfigureAwait(false) is null)
            await client.CreateTableAsync(PreparedTable, new JsonArray(
                Column("fingerprint", "text", nullable: false, unique: true)), ct).ConfigureAwait(false);
    }

    /// <summary>Convenience for hosts/tests: create a live project for a target, seed adapter tables, register the pointer.</summary>
    public async Task<Guid> ProvisionTargetAsync(string target, CancellationToken ct = default)
    {
        using var admin = Admin();
        var projectId = await admin.CreateProjectAsync($"vivarium-live-{Guid.NewGuid():n}"[..40], ct).ConfigureAwait(false);
        using var client = ClientFor(projectId);
        await EnsureAdapterTablesAsync(client, ct).ConfigureAwait(false);
        await RegisterTargetAsync(target, projectId, ct).ConfigureAwait(false);
        return projectId;
    }
}
