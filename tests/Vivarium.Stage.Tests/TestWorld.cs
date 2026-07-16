using System.Text.Json.Nodes;
using Vivarium.Changeset;
using Vivarium.Stage.Adapters;
using Vivarium.Stage.Ledger;

namespace Vivarium.Stage.Tests;

public sealed class FixedTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>One seeded target plus a valid, approved changeset against it.</summary>
public sealed class TestWorld
{
    public const string TargetName = "app";
    public const string BaseArtifact = "export function LoanScreen() {\n  return <Form fields={[amount]} />;\n}";
    public const string NewArtifact = "export function LoanScreen() {\n  return <Form fields={[amount, dueDate]} />;\n}";

    public InMemoryBackendAdapter Inner { get; } = new();
    public FaultInjectingAdapter Adapter { get; }
    public InMemoryLedgerStore Store { get; } = new();
    public ReleaseLedger Ledger { get; }
    public FixedTimeProvider Clock { get; } = new();

    public TestWorld()
    {
        Adapter = new FaultInjectingAdapter(Inner);
        Ledger = new ReleaseLedger(Store);
        Inner.SeedTarget(TargetName, (JsonObject)JsonNode.Parse($$"""
            {
              "schema": { "entities": { "loan": { "fields": { "amount": { "name": "amount", "type": "number" } }, "constraints": [] } } },
              "data": { "loan": [ { "amount": 100 } ] },
              "artifacts": { "screen-loans": {{JsonValue.Create(BaseArtifact).ToJsonString()}} }
            }
            """)!);
    }

    /// <summary>Build a valid changeset whose baseState matches the live target, finalize, and approve it.</summary>
    public async Task<JsonObject> ApprovedChangesetAsync(bool approved = true)
    {
        var active = await Inner.ActiveStateAsync(TargetName);
        var doc = new ChangesetBuilder(
                intent: "Add a due-date to the loan screen",
                producedBy: "test-suite",
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
        if (approved)
            doc["approvals"] = new JsonArray(new JsonObject
            {
                ["fingerprint"] = doc["fingerprint"]!.GetValue<string>(),
                ["approvedBy"] = "reviewer-1",
                ["approvedAt"] = "2026-07-16T01:00:00Z",
            });
        return doc;
    }

    public async Task<ChangeSession> SessionAsync(JsonObject? changeset = null, StagePolicy? policy = null)
    {
        changeset ??= await ApprovedChangesetAsync();
        return new ChangeSession(changeset, TargetName, Adapter, Ledger, policy, Clock);
    }

    /// <summary>Drive a session to the Simulated state (ready to apply).</summary>
    public async Task<ChangeSession> SimulatedSessionAsync(JsonObject? changeset = null, StagePolicy? policy = null)
    {
        var session = await SessionAsync(changeset, policy);
        await session.BranchAsync();
        session.RecordSimulation(new JsonObject { ["observed"] = "due-date renders" });
        return session;
    }
}
