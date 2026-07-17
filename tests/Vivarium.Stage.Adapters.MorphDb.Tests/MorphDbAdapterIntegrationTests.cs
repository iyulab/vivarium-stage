using System.Text.Json.Nodes;
using Vivarium.Changeset;
using Vivarium.Stage;
using Vivarium.Stage.Adapters;
using Vivarium.Stage.Ledger;
using Xunit;

namespace Vivarium.Stage.Adapters.MorphDb.Tests;

/// <summary>
/// Integration tests against a LIVE MorphDB service. Gated on MORPHDB_URL +
/// MORPHDB_API_KEY (skipped otherwise) — locally: run the service; in CI:
/// postgres + morphdb containers. These are the acceptance tests for the
/// first real adapter: the same lifecycle the in-memory adapter passes, but
/// with PostgreSQL underneath.
/// </summary>
public class MorphDbAdapterIntegrationTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("MORPHDB_URL");
    private static readonly string? ApiKey = Environment.GetEnvironmentVariable("MORPHDB_API_KEY");

    private const string BaseArtifact = "export function LoanScreen() {\n  return <Form fields={[amount]} />;\n}";
    private const string NewArtifact = "export function LoanScreen() {\n  return <Form fields={[amount, dueDate]} />;\n}";

    private static MorphDbBackendAdapter Adapter() => new(new MorphDbAdapterOptions
    {
        BaseUrl = Url!,
        ApiKey = ApiKey!,
    });

    private static void RequireLiveService() =>
        Skip.If(string.IsNullOrEmpty(Url) || string.IsNullOrEmpty(ApiKey),
            "set MORPHDB_URL and MORPHDB_API_KEY to run MorphDB integration tests");

    /// <summary>Provision a target with a loan table, one row, and the base artifact.</summary>
    private static async Task<(MorphDbBackendAdapter Adapter, string Target)> SeedAsync()
    {
        var adapter = Adapter();
        var target = $"app-{Guid.NewGuid():n}"[..12];
        var projectId = await adapter.ProvisionTargetAsync(target);
        using var client = adapter.ClientFor(projectId);
        await client.CreateTableAsync("loan", new JsonArray(new JsonObject
        {
            ["name"] = "amount", ["type"] = "decimal",
        }));
        await client.InsertAsync("loan", new JsonObject { ["amount"] = 100 });
        await client.InsertAsync("vivarium_artifacts", new JsonObject
        {
            ["artifact_id"] = "screen-loans",
            ["content"] = BaseArtifact,
        });
        return (adapter, target);
    }

    private static async Task<JsonObject> ApprovedChangesetAsync(MorphDbBackendAdapter adapter, string target)
    {
        var active = await adapter.ActiveStateAsync(target);
        var doc = new ChangesetBuilder(
                intent: "Add a due-date to the loan screen",
                producedBy: "morphdb-integration-test",
                createdAt: "2026-07-16T00:00:00Z",
                baseState:
                [
                    new BaseStateEntry("schema", "schema", active.FacetFingerprints["schema"]),
                    new BaseStateEntry("ui-artifact", "screen-loans", active.FacetFingerprints["screen-loans"]),
                ])
            .AddSchemaOp((JsonObject)JsonNode.Parse("""
                { "op": "field.add", "entity": "loan",
                  "field": { "name": "dueDate", "type": "date" },
                  "explanation": "Stores the loan's due date" }
                """)!)
            .AddUiPatch("screen-loans", BaseArtifact, NewArtifact, "Renders the due-date field")
            .AddDataPatch("backfill", "Seed a default due date", [(JsonObject)JsonNode.Parse("""
                { "op": "update", "entity": "loan", "where": { "field": "amount", "equals": 100 }, "set": { "dueDate": "2026-08-01" } }
                """)!])
            .Finalize();
        doc["approvals"] = new JsonArray(new JsonObject
        {
            ["fingerprint"] = doc["fingerprint"]!.GetValue<string>(),
            ["approvedBy"] = "reviewer-1",
            ["approvedAt"] = "2026-07-16T01:00:00Z",
        });
        return doc;
    }

    [SkippableFact]
    public async Task FullLifecycle_BranchSimulateApplyRollback_AgainstLiveMorphDb()
    {
        RequireLiveService();
        var (adapter, target) = await SeedAsync();
        var ledger = new ReleaseLedger(new InMemoryLedgerStore());
        var changeset = await ApprovedChangesetAsync(adapter, target);
        var preApply = await adapter.ActiveStateAsync(target);

        var session = new ChangeSession(changeset, target, adapter, ledger);
        var branch = await session.BranchAsync();

        // fidelity is declared, honestly: snapshot copy with known differences
        Assert.Equal("full", branch.Fidelity.PerFacet["schema"].Mode);
        Assert.Equal("snapshot", branch.Fidelity.PerFacet["schema"].Method);
        Assert.NotEmpty(branch.Fidelity.KnownDifferences);

        session.RecordSimulation();
        await session.ApplyAsync("operator-1", applyToken: $"itok-{Guid.NewGuid():n}");
        Assert.Equal(SessionState.Applied, session.State);

        // the live world now IS the branch project, with all three facets landed
        var postApply = await adapter.ActiveStateAsync(target);
        Assert.Equal(branch.BranchRef, postApply.StateRef);
        Assert.NotEqual(preApply.FacetFingerprints["schema"], postApply.FacetFingerprints["schema"]);
        Assert.Equal(ChangesetFingerprint.OfArtifact(NewArtifact), postApply.FacetFingerprints["screen-loans"]);

        using var live = adapter.ClientFor(Guid.Parse(postApply.StateRef));
        var loans = await live.QueryAllAsync("loan");
        var row = Assert.Single(loans);
        Assert.StartsWith("2026-08-01", row.Data["dueDate"]?.GetValue<string>());

        // the write-ahead pair is in the ledger, in order, with the fidelity declaration
        var entries = await ledger.ReadAllAsync();
        Assert.Equal(["apply-started", "apply-completed"], entries.Select(e => e.Kind));
        Assert.NotNull(entries[0].Fidelity);

        // rollback returns to the pre-apply project
        await session.RollbackAsync("operator-1");
        var postRollback = await adapter.ActiveStateAsync(target);
        Assert.Equal(preApply.StateRef, postRollback.StateRef);
        Assert.Equal(preApply.FacetFingerprints["schema"], postRollback.FacetFingerprints["schema"]);
        Assert.Equal(SessionState.RolledBack, session.State);
    }

    [SkippableFact]
    public async Task DriftedLiveStateIsRefused_AgainstLiveMorphDb()
    {
        RequireLiveService();
        var (adapter, target) = await SeedAsync();
        var ledger = new ReleaseLedger(new InMemoryLedgerStore());
        var changeset = await ApprovedChangesetAsync(adapter, target);

        var session = new ChangeSession(changeset, target, adapter, ledger);
        await session.BranchAsync();
        session.RecordSimulation();

        // out-of-band live mutation after the changeset recorded its base
        var active = await adapter.ActiveStateAsync(target);
        using var live = adapter.ClientFor(Guid.Parse(active.StateRef));
        await live.AddColumnAsync("loan", new JsonObject { ["name"] = "sneaky", ["type"] = "text" });

        var ex = await Assert.ThrowsAsync<StageRefusedException>(() => session.ApplyAsync("operator-1"));
        Assert.Equal(RefusalReason.DriftGate, ex.Reason);
        Assert.Empty(await ledger.ReadAllAsync()); // refused before the write-ahead entry
    }

    [SkippableFact]
    public async Task FlipIsIdempotentUnderToken_AndRefusesTokenReuseForDifferentState()
    {
        RequireLiveService();
        var (adapter, target) = await SeedAsync();
        var before = await adapter.ActiveStateAsync(target);
        var branch = await adapter.BranchAsync(target);
        var token = $"itok-{Guid.NewGuid():n}";

        await adapter.FlipAsync(target, branch.BranchRef, token);
        Assert.Equal(branch.BranchRef, (await adapter.ActiveStateAsync(target)).StateRef);

        // recovery-style re-issue: same token, same state ref → no-op
        await adapter.FlipAsync(target, branch.BranchRef, token);
        Assert.Equal(branch.BranchRef, (await adapter.ActiveStateAsync(target)).StateRef);

        // same token, DIFFERENT state ref → contract violation, pointer unmoved
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.FlipAsync(target, before.StateRef, token));
        Assert.Equal(branch.BranchRef, (await adapter.ActiveStateAsync(target)).StateRef);
    }

    [SkippableFact]
    public async Task PrepareIsIdempotentPerFingerprint()
    {
        RequireLiveService();
        var (adapter, target) = await SeedAsync();
        var changeset = await ApprovedChangesetAsync(adapter, target);
        var branch = await adapter.BranchAsync(target);
        var facets = new PreparedFacets(
            changeset["fingerprint"]!.GetValue<string>(),
            (JsonObject)changeset["patches"]!.DeepClone());

        var first = await adapter.PrepareAsync(branch.BranchRef, facets);
        Assert.True(first.AllComplete);
        // second prepare with the same fingerprint must not double-apply
        var second = await adapter.PrepareAsync(branch.BranchRef, facets);
        Assert.True(second.AllComplete);

        using var client = adapter.ClientFor(Guid.Parse(branch.BranchRef));
        Assert.Single(await client.QueryAllAsync("loan")); // update ran once, no phantom rows

        var table = await client.GetTableAsync("loan");
        Assert.Single(((JsonArray)table!["columns"]!).OfType<JsonObject>(),
            c => c["name"]!.GetValue<string>() == "dueDate"); // column added once
    }

    [SkippableFact]
    public async Task SchemaOpsRoundTrip_RenameRetypeRemove()
    {
        RequireLiveService();
        var (adapter, target) = await SeedAsync();
        var branch = await adapter.BranchAsync(target);
        var patches = (JsonObject)JsonNode.Parse("""
            {
              "schema": [
                { "op": "entity.create", "entity": "member",
                  "fields": [ { "name": "email", "type": "string" }, { "name": "age", "type": "number" } ],
                  "explanation": "membership entity" },
                { "op": "field.rename", "entity": "member", "field": "email", "newName": "contact",
                  "explanation": "clearer name" },
                { "op": "field.remove", "entity": "member", "field": "age", "explanation": "unused" },
                { "op": "entity.rename", "entity": "member", "newName": "person", "explanation": "broader concept" }
              ],
              "ui": [], "data": []
            }
            """)!;

        await adapter.PrepareAsync(branch.BranchRef, new PreparedFacets("sha256:" + new string('c', 64), patches));

        using var client = adapter.ClientFor(Guid.Parse(branch.BranchRef));
        Assert.Null(await client.GetTableAsync("member"));
        var person = await client.GetTableAsync("person");
        Assert.NotNull(person);
        var names = ((JsonArray)person!["columns"]!).OfType<JsonObject>()
            .Select(c => c["name"]!.GetValue<string>()).ToList();
        Assert.Contains("contact", names);
        Assert.DoesNotContain("email", names);
        Assert.DoesNotContain("age", names);
    }

    [SkippableFact]
    public async Task DiscardRefusesTheActiveState()
    {
        RequireLiveService();
        var (adapter, target) = await SeedAsync();
        var active = await adapter.ActiveStateAsync(target);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.DiscardAsync(active.StateRef));

        // a non-active branch discards fine
        var branch = await adapter.BranchAsync(target);
        await adapter.DiscardAsync(branch.BranchRef);
    }
}
