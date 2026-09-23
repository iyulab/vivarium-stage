using System.Text.Json.Nodes;
using Vivarium.Stage.Adapters;

namespace Vivarium.Stage.Tests;

/// <summary>
/// adapter-api §6: every refusal the contract names comes out as an
/// <see cref="AdapterRefusedException"/> with its reason, so a host tells "the product
/// said no" from "the backend broke" without reading the message or guessing from which
/// call it came.
/// </summary>
public class AdapterRefusalTests
{
    private static async Task<(InMemoryBackendAdapter Adapter, string Branch)> BranchedAsync()
    {
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("app", new JsonObject
        {
            ["schema"] = new JsonObject
            {
                ["entities"] = new JsonObject
                {
                    ["Item"] = new JsonObject
                    {
                        ["fields"] = new JsonObject { ["sku"] = new JsonObject { ["name"] = "sku", ["type"] = "string" } },
                        ["constraints"] = new JsonArray(),
                    },
                },
            },
            ["data"] = new JsonObject(),
            ["artifacts"] = new JsonObject { ["screen"] = "original" },
        });
        var branch = await adapter.BranchAsync("app");
        return (adapter, branch.BranchRef);
    }

    private static JsonObject Schema(params JsonObject[] ops) => new()
    {
        ["schema"] = new JsonArray(ops.Select(o => (JsonNode)o).ToArray()),
        ["ui"] = new JsonArray(),
        ["data"] = new JsonArray(),
    };

    private static Task<PrepareReport> Prepare(IBackendAdapter adapter, string branchRef, JsonObject patches, string fp = "sha256:probe") =>
        adapter.PrepareAsync(branchRef, new PreparedFacets(fp, patches));

    [Fact]
    public async Task An_unknown_target_is_refused_as_such_by_every_operation_that_takes_one()
    {
        var (adapter, _) = await BranchedAsync();

        foreach (var call in new Func<Task>[]
        {
            () => adapter.ActiveStateAsync("nowhere"),
            () => adapter.BranchAsync("nowhere"),
            () => adapter.FlipAsync("nowhere", "any", "tok"),
        })
        {
            var ex = await Assert.ThrowsAsync<AdapterRefusedException>(call);
            Assert.Equal(AdapterRefusalReason.UnknownTarget, ex.Reason);
            Assert.Equal("nowhere", ex.Details!["target"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_ref_the_adapter_does_not_hold_is_UnknownRef()
    {
        var (adapter, _) = await BranchedAsync();

        var prepare = await Assert.ThrowsAsync<AdapterRefusedException>(() => Prepare(adapter, "branch-999", Schema()));
        Assert.Equal(AdapterRefusalReason.UnknownRef, prepare.Reason);
        Assert.Equal("branch-999", prepare.Details!["ref"]!.GetValue<string>());

        var flip = await Assert.ThrowsAsync<AdapterRefusedException>(() => adapter.FlipAsync("app", "stale-ref", "tok-1"));
        Assert.Equal(AdapterRefusalReason.UnknownRef, flip.Reason);
        Assert.Equal("stale-ref", flip.Details!["ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task Discarding_the_live_state_is_StateIsActive()
    {
        var (adapter, branchRef) = await BranchedAsync();
        await adapter.FlipAsync("app", branchRef, "tok-1");

        var ex = await Assert.ThrowsAsync<AdapterRefusedException>(() => adapter.DiscardAsync(branchRef));

        Assert.Equal(AdapterRefusalReason.StateIsActive, ex.Reason);
        Assert.Equal(branchRef, ex.Details!["ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_malformed_operation_is_a_located_document_refusal()
    {
        var (adapter, branchRef) = await BranchedAsync();

        var ex = await Assert.ThrowsAsync<AdapterRefusedException>(() => Prepare(adapter, branchRef, Schema(
            new JsonObject { ["op"] = "field.add", ["entity"] = "Item", ["field"] = new JsonObject { ["name"] = "a" } },
            new JsonObject { ["op"] = "entity.truncate", ["entity"] = "Item" })));

        Assert.Equal(AdapterRefusalReason.DocumentRefused, ex.Reason);
        var error = Assert.Single(ex.Details!["errors"]!.AsArray())!;
        Assert.Equal("$.patches.schema[1].op", error["path"]!.GetValue<string>());
        Assert.StartsWith("unknown schema operation", error["message"]!.GetValue<string>());
        Assert.Equal($"{error["path"]}: {error["message"]}", ex.Message);
    }

    [Fact]
    public async Task A_well_formed_operation_naming_something_absent_is_located_too()
    {
        // Found only while applying, so the location has to be carried there — the shape
        // check that runs first cannot know the field is missing.
        var (adapter, branchRef) = await BranchedAsync();

        var ex = await Assert.ThrowsAsync<AdapterRefusedException>(() => Prepare(adapter, branchRef, Schema(
            new JsonObject { ["op"] = "field.add", ["entity"] = "Item", ["field"] = new JsonObject { ["name"] = "a" } },
            new JsonObject { ["op"] = "field.remove", ["entity"] = "Item", ["field"] = "no-such-field" })));

        Assert.Equal(AdapterRefusalReason.DocumentRefused, ex.Reason);
        Assert.Equal("$.patches.schema[1].field", ex.Details!["errors"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_refusal_found_while_applying_leaves_no_residue_on_the_branch()
    {
        // The rename lands, then the removal names a field the renamed entity lacks.
        // Staging in place left the rename behind: the corrected document — the retry a
        // host sends the author back to — then failed on "unknown entity: Item", because
        // the branch no longer held what it was branched with (adapter-api §3).
        var (adapter, branchRef) = await BranchedAsync();
        JsonObject Rename() => new() { ["op"] = "entity.rename", ["entity"] = "Item", ["newName"] = "Product" };

        await Assert.ThrowsAsync<AdapterRefusedException>(() => Prepare(adapter, branchRef, Schema(
            Rename(),
            new JsonObject { ["op"] = "field.remove", ["entity"] = "Product", ["field"] = "no-such-field" }), "sha256:first"));

        Assert.Contains("\"Item\"", adapter.WorldCanonical(branchRef));
        Assert.DoesNotContain("\"Product\"", adapter.WorldCanonical(branchRef));

        var report = await Prepare(adapter, branchRef, Schema(
            Rename(),
            new JsonObject { ["op"] = "field.remove", ["entity"] = "Product", ["field"] = "sku" }), "sha256:corrected");
        Assert.True(report.AllComplete);
    }
}
