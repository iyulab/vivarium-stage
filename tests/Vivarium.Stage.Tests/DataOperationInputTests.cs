using System.Text.Json.Nodes;
using Vivarium.Stage.Adapters;

namespace Vivarium.Stage.Tests;

/// <summary>
/// The reference adapter is what adapter authors copy, so a hole here is copied
/// into every real backend. `prepare` is the door: the document it receives was
/// authored elsewhere (adapter-api §3 — Data operations).
/// </summary>
public class DataOperationInputTests
{
    private static JsonObject Patches(params JsonObject[] operations) => new()
    {
        ["schema"] = new JsonArray(),
        ["ui"] = new JsonArray(),
        ["data"] = new JsonArray(new JsonObject
        {
            ["id"] = "probe",
            ["explanation"] = "test payload",
            ["operations"] = new JsonArray(operations.Select(o => (JsonNode)o).ToArray()),
        }),
    };

    private static async Task<(InMemoryBackendAdapter Adapter, string Branch)> BranchedAsync()
    {
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("app", new JsonObject
        {
            ["schema"] = new JsonObject { ["entities"] = new JsonObject() },
            ["data"] = new JsonObject { ["Item"] = new JsonArray(
                new JsonObject { ["sku"] = "SKU-1", ["qty"] = 1 },
                new JsonObject { ["sku"] = "SKU-2", ["qty"] = 2 }) },
            ["artifacts"] = new JsonObject(),
        });
        var branch = await adapter.BranchAsync("app");
        return (adapter, branch.BranchRef);
    }

    public static TheoryData<string, JsonObject> Malformed => new()
    {
        // the shape a producer reaches for first — and the one that used to fault
        { "where as a key/value map", new JsonObject
            { ["op"] = "update", ["entity"] = "Item",
              ["where"] = new JsonObject { ["sku"] = "SKU-1" },
              ["set"] = new JsonObject { ["qty"] = 9 } } },
        { "no predicate at all", new JsonObject
            { ["op"] = "delete", ["entity"] = "Item" } },
        { "predicate without equals", new JsonObject
            { ["op"] = "delete", ["entity"] = "Item",
              ["where"] = new JsonObject { ["field"] = "sku" } } },
        { "empty entity", new JsonObject
            { ["op"] = "insert", ["entity"] = "", ["values"] = new JsonObject() } },
        { "insert without values", new JsonObject
            { ["op"] = "insert", ["entity"] = "Item" } },
        { "update without set", new JsonObject
            { ["op"] = "update", ["entity"] = "Item",
              ["where"] = new JsonObject { ["field"] = "sku", ["equals"] = "SKU-1" } } },
        { "operation outside the vocabulary", new JsonObject
            { ["op"] = "truncate", ["entity"] = "Item" } },
        { "missing op", new JsonObject { ["entity"] = "Item" } },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task PrepareRefusesAMalformedDataOperationWithSomethingToRead(string _, JsonObject operation)
    {
        var (adapter, branchRef) = await BranchedAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            adapter.PrepareAsync(branchRef, new PreparedFacets("sha256:probe", Patches(operation))));

        Assert.IsNotType<NullReferenceException>(ex);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        // the message names where, so a consumer is not left grepping their document
        Assert.Contains("patches.data[0].operations[0]", ex.Message);
    }

    /// <summary>
    /// The check is total and runs before any mutation, so a refusal does not leave
    /// a branch holding some of a document it rejected.
    /// </summary>
    [Fact]
    public async Task ARefusedDocumentStagesNothingAtAll()
    {
        var (adapter, branchRef) = await BranchedAsync();
        var before = adapter.WorldCanonical(branchRef);

        await Assert.ThrowsAnyAsync<Exception>(() => adapter.PrepareAsync(branchRef,
            new PreparedFacets("sha256:probe", Patches(
                new JsonObject // valid, and listed first
                {
                    ["op"] = "delete", ["entity"] = "Item",
                    ["where"] = new JsonObject { ["field"] = "sku", ["equals"] = "SKU-2" },
                },
                new JsonObject // malformed, and listed second
                {
                    ["op"] = "update", ["entity"] = "Item",
                    ["where"] = new JsonObject { ["sku"] = "SKU-1" },
                    ["set"] = new JsonObject { ["qty"] = 9 },
                }))));

        Assert.Equal(before, adapter.WorldCanonical(branchRef));
    }

    [Fact]
    public async Task AWellFormedDocumentStillStages()
    {
        var (adapter, branchRef) = await BranchedAsync();

        await adapter.PrepareAsync(branchRef, new PreparedFacets("sha256:probe", Patches(
            new JsonObject
            {
                ["op"] = "update", ["entity"] = "Item",
                ["where"] = new JsonObject { ["field"] = "sku", ["equals"] = "SKU-1" },
                ["set"] = new JsonObject { ["qty"] = 9 },
            })));

        Assert.Contains("\"qty\":9", adapter.WorldCanonical(branchRef));
    }
}
