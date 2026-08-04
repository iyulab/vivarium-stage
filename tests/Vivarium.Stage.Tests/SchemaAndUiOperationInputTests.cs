using System.Text.Json.Nodes;
using Vivarium.Stage.Adapters;

namespace Vivarium.Stage.Tests;

/// <summary>
/// The same door, the other two facets. <c>PrepareAsync</c> is public API — a host can
/// call it without ever building a <see cref="ChangeSession"/> — so the changeset
/// validator's strictness on schema and UI patches is not a property this method gets
/// to assume (adapter-api §Operation input).
/// </summary>
public class SchemaAndUiOperationInputTests
{
    private static JsonObject SchemaPatches(params JsonObject[] ops) => new()
    {
        ["schema"] = new JsonArray(ops.Select(o => (JsonNode)o).ToArray()),
        ["ui"] = new JsonArray(),
        ["data"] = new JsonArray(),
    };

    private static JsonObject UiPatches(params JsonObject[] patches) => new()
    {
        ["schema"] = new JsonArray(),
        ["ui"] = new JsonArray(patches.Select(o => (JsonNode)o).ToArray()),
        ["data"] = new JsonArray(),
    };

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

    private static Task<PrepareReport> Prepare(InMemoryBackendAdapter adapter, string branchRef, JsonObject patches) =>
        adapter.PrepareAsync(branchRef, new PreparedFacets("sha256:probe", patches));

    public static TheoryData<string, JsonObject> MalformedSchemaOps => new()
    {
        // the one that used to stage nothing while prepare reported the facet complete
        { "operation outside the vocabulary", new JsonObject { ["op"] = "entity.truncate", ["entity"] = "Item" } },
        { "missing op", new JsonObject { ["entity"] = "Item" } },
        { "empty entity", new JsonObject { ["op"] = "entity.remove", ["entity"] = "" } },
        { "field.add without a declaration", new JsonObject { ["op"] = "field.add", ["entity"] = "Item" } },
        { "field.add with a name instead of a declaration", new JsonObject
            { ["op"] = "field.add", ["entity"] = "Item", ["field"] = "qty" } },
        { "field.add declaration without a name", new JsonObject
            { ["op"] = "field.add", ["entity"] = "Item", ["field"] = new JsonObject { ["type"] = "number" } } },
        { "field.rename without newName", new JsonObject
            { ["op"] = "field.rename", ["entity"] = "Item", ["field"] = "sku" } },
        { "field.retype with an empty newType", new JsonObject
            { ["op"] = "field.retype", ["entity"] = "Item", ["field"] = "sku", ["newType"] = "" } },
        { "entity.create with a non-array fields", new JsonObject
            { ["op"] = "entity.create", ["entity"] = "Order", ["fields"] = new JsonObject() } },
        { "entity.create with an unnamed field", new JsonObject
            { ["op"] = "entity.create", ["entity"] = "Order",
              ["fields"] = new JsonArray(new JsonObject { ["type"] = "string" }) } },
    };

    [Theory]
    [MemberData(nameof(MalformedSchemaOps))]
    public async Task PrepareRefusesAMalformedSchemaOperation(string _, JsonObject op)
    {
        var (adapter, branchRef) = await BranchedAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Prepare(adapter, branchRef, SchemaPatches(op)));

        Assert.IsNotType<NullReferenceException>(ex);
        Assert.Contains("patches.schema[0]", ex.Message);
    }

    public static TheoryData<string, JsonObject> MalformedUiPatches => new()
    {
        { "missing artifactId", new JsonObject { ["profile"] = "whole-artifact@0", ["newContent"] = "x" } },
        { "empty artifactId", new JsonObject
            { ["profile"] = "whole-artifact@0", ["artifactId"] = "", ["newContent"] = "x" } },
        { "unknown profile", new JsonObject
            { ["profile"] = "whole-artifact@1", ["artifactId"] = "screen", ["newContent"] = "x" } },
        { "missing profile", new JsonObject { ["artifactId"] = "screen", ["newContent"] = "x" } },
        { "whole-artifact without content", new JsonObject
            { ["profile"] = "whole-artifact@0", ["artifactId"] = "screen" } },
    };

    [Theory]
    [MemberData(nameof(MalformedUiPatches))]
    public async Task PrepareRefusesAMalformedUiPatch(string _, JsonObject patch)
    {
        var (adapter, branchRef) = await BranchedAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Prepare(adapter, branchRef, UiPatches(patch)));

        Assert.IsNotType<NullReferenceException>(ex);
        Assert.Contains("patches.ui[0]", ex.Message);
    }

    /// <summary>
    /// Operating on a field that is not there used to succeed quietly — rename wrote
    /// a null under the new name, producing a declared field that no later read tells
    /// apart from a real one, and remove reported success for a field it never found.
    /// The clause names no operations, so neither does this theory: every schema
    /// operation that addresses a field belongs here, and widening the vocabulary
    /// must widen the row set rather than leave the new operation unexamined.
    /// </summary>
    [Theory]
    [InlineData("field.rename")]
    [InlineData("field.retype")]
    [InlineData("field.remove")]
    public async Task OperatingOnAFieldThatIsNotThereIsRefusedRatherThanInvented(string op)
    {
        var (adapter, branchRef) = await BranchedAsync();
        var patch = new JsonObject { ["op"] = op, ["entity"] = "Item", ["field"] = "no-such-field" };
        if (op == "field.rename") patch["newName"] = "whatever";
        if (op == "field.retype") patch["newType"] = "whatever";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Prepare(adapter, branchRef, SchemaPatches(patch)));

        Assert.IsNotType<NullReferenceException>(ex);
        Assert.Contains("no such field", ex.Message);
        Assert.DoesNotContain("no-such-field", adapter.WorldCanonical(branchRef));
    }

    /// <summary>
    /// Removing an entity that is not there is the same defect one level up, and the
    /// one where a quiet success is most convincing: removal's whole purpose is that
    /// the target ends up absent, so an operation naming an entity nobody has looks
    /// exactly like one that worked. The document said what it expected to find.
    /// </summary>
    [Fact]
    public async Task RemovingAnEntityThatIsNotThereIsRefusedRatherThanReportedDone()
    {
        var (adapter, branchRef) = await BranchedAsync();
        var patch = new JsonObject { ["op"] = "entity.remove", ["entity"] = "NoSuchEntity" };

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Prepare(adapter, branchRef, SchemaPatches(patch)));

        Assert.IsNotType<NullReferenceException>(ex);
        Assert.Contains("NoSuchEntity", ex.Message);
    }

    /// <summary>
    /// The third removal in the vocabulary, and the one easiest to miss: a constraint
    /// is addressed by its whole shape rather than by a name, so "remove the ones that
    /// match" reads like a filter and quietly matches nothing. The document still
    /// named a constraint it expected to find.
    /// </summary>
    [Fact]
    public async Task RemovingAConstraintThatIsNotThereIsRefusedRatherThanReportedDone()
    {
        var (adapter, branchRef) = await BranchedAsync();
        var patch = new JsonObject
        {
            ["op"] = "constraint.remove",
            ["entity"] = "Item",
            ["constraint"] = new JsonObject
            {
                ["kind"] = "unique",
                ["fields"] = new JsonArray("no-such-field"),
            },
        };

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Prepare(adapter, branchRef, SchemaPatches(patch)));

        Assert.IsNotType<NullReferenceException>(ex);
        Assert.Contains("no such constraint", ex.Message);
    }

    [Fact]
    public async Task ARefusedDocumentStagesNothingAcrossFacets()
    {
        var (adapter, branchRef) = await BranchedAsync();
        var before = adapter.WorldCanonical(branchRef);

        await Assert.ThrowsAnyAsync<Exception>(() => Prepare(adapter, branchRef, new JsonObject
        {
            // valid, and listed first
            ["schema"] = new JsonArray(new JsonObject
            {
                ["op"] = "field.add",
                ["entity"] = "Item",
                ["field"] = new JsonObject { ["name"] = "qty", ["type"] = "number" },
            }),
            ["ui"] = new JsonArray(new JsonObject
            {
                ["profile"] = "whole-artifact@0",
                ["artifactId"] = "screen",
                ["newContent"] = "changed",
            }),
            // malformed, and listed last
            ["data"] = new JsonArray(new JsonObject
            {
                ["id"] = "p",
                ["explanation"] = "e",
                ["operations"] = new JsonArray(new JsonObject { ["op"] = "truncate", ["entity"] = "Item" }),
            }),
        }));

        Assert.Equal(before, adapter.WorldCanonical(branchRef));
    }

    [Fact]
    public async Task AWellFormedSchemaAndUiDocumentStillStages()
    {
        var (adapter, branchRef) = await BranchedAsync();

        await Prepare(adapter, branchRef, new JsonObject
        {
            ["schema"] = new JsonArray(new JsonObject
            {
                ["op"] = "field.add",
                ["entity"] = "Item",
                ["field"] = new JsonObject { ["name"] = "qty", ["type"] = "number" },
            }),
            ["ui"] = new JsonArray(new JsonObject
            {
                ["profile"] = "whole-artifact@0",
                ["artifactId"] = "screen",
                ["newContent"] = "changed",
            }),
            ["data"] = new JsonArray(),
        });

        var world = adapter.WorldCanonical(branchRef);
        Assert.Contains("qty", world);
        Assert.Contains("changed", world);
    }
}
