using System.Text.Json.Nodes;
using Vivarium.Stage.Adapters;

namespace Vivarium.Stage.Tests;

/// <summary>
/// Changeset spec §5.4: a document's additive schema operations apply before its data
/// operations and its removing ones after them, and a removal carries away the values
/// stored under what it removes. Until 0.4.0 the spec said neither, and this adapter
/// happened to apply all schema operations first and leave removed values in place —
/// so "retire this field and clear it everywhere" left rows carrying a member no
/// schema declared, addressable by nothing in the operation vocabulary.
/// </summary>
public class FacetApplicationOrderTests
{
    private const string Entity = "Contact";

    private static InMemoryBackendAdapter Seeded()
    {
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("app", (JsonObject)JsonNode.Parse("""
            {
              "schema": { "entities": { "Contact": { "fields": {
                  "id":   { "name": "id",   "type": "string" },
                  "name": { "name": "name", "type": "string" },
                  "fax":  { "name": "fax",  "type": "string" }
              }, "constraints": [] } } },
              "data": { "Contact": [
                  { "id": "c1", "name": "Ada",  "fax": "555-0100" },
                  { "id": "c2", "name": "Borg", "fax": "555-0101" }
              ] },
              "artifacts": {}
            }
            """)!);
        return adapter;
    }

    private static JsonObject Patches(JsonArray? schema = null, JsonArray? data = null) => new()
    {
        ["schema"] = schema ?? new JsonArray(),
        ["ui"] = new JsonArray(),
        ["data"] = data ?? new JsonArray(),
    };

    private static JsonObject RemoveFax() => new()
    {
        ["op"] = "field.remove",
        ["entity"] = Entity,
        ["field"] = "fax",
        ["explanation"] = "Retire the fax number.",
    };

    private static JsonObject ClearFax(string id) => new()
    {
        ["op"] = "update",
        ["entity"] = Entity,
        ["where"] = new JsonObject { ["field"] = "id", ["equals"] = id },
        ["set"] = new JsonObject { ["fax"] = null },
    };

    private static JsonObject DataPatch(string id, params JsonObject[] ops) => new()
    {
        ["id"] = id,
        ["explanation"] = "Test patch.",
        ["operations"] = new JsonArray(ops.Select(o => (JsonNode)o).ToArray()),
    };

    private static async Task<JsonNode> BranchWorldAfterAsync(InMemoryBackendAdapter adapter, JsonObject patches)
    {
        var branch = await adapter.BranchAsync("app");
        await adapter.PrepareAsync(branch.BranchRef, new PreparedFacets("sha256:order-probe", patches));
        return JsonNode.Parse(adapter.WorldCanonical(branch.BranchRef))!;
    }

    [Fact]
    public async Task Removing_a_field_takes_its_values_with_it()
    {
        var adapter = Seeded();

        var world = await BranchWorldAfterAsync(adapter, Patches(schema: new JsonArray(RemoveFax())));

        Assert.Null(world["schema"]!["entities"]![Entity]!["fields"]!["fax"]);
        foreach (var row in (JsonArray)world["data"]![Entity]!)
            Assert.False(((JsonObject)row!).ContainsKey("fax"), $"row {row!["id"]} still carries 'fax'");
    }

    /// <summary>
    /// The turn that motivated the clause. Clearing then removing must not reintroduce
    /// the member as null: under §5.4 the writes run while the field is still declared,
    /// and the removal afterwards takes the member away whatever its value.
    /// </summary>
    [Fact]
    public async Task Clearing_a_field_and_removing_it_in_one_document_leaves_no_residue()
    {
        var adapter = Seeded();

        var world = await BranchWorldAfterAsync(adapter, Patches(
            schema: new JsonArray(RemoveFax()),
            data: new JsonArray(DataPatch("clear-fax", ClearFax("c1"), ClearFax("c2")))));

        Assert.Null(world["schema"]!["entities"]![Entity]!["fields"]!["fax"]);
        foreach (var row in (JsonArray)world["data"]![Entity]!)
            Assert.False(((JsonObject)row!).ContainsKey("fax"), $"row {row!["id"]} still carries 'fax'");
    }

    /// <summary>
    /// The other half of the order, and the reason removals cannot simply run first:
    /// data operations must be able to address a field on its way out. Running the
    /// removal first would make this write land on an undeclared name.
    /// </summary>
    [Fact]
    public async Task A_data_operation_may_still_address_a_field_this_document_removes()
    {
        var adapter = Seeded();

        var world = await BranchWorldAfterAsync(adapter, Patches(
            schema: new JsonArray(RemoveFax()),
            data: new JsonArray(DataPatch("archive-then-retire", new JsonObject
            {
                ["op"] = "delete",
                ["entity"] = Entity,
                ["where"] = new JsonObject { ["field"] = "fax", ["equals"] = "555-0101" },
            }))));

        var rows = (JsonArray)world["data"]![Entity]!;
        // The row was selected by the field being removed — which only works if the
        // delete ran before the removal.
        Assert.Single(rows);
        Assert.Equal("c1", (string)rows[0]!["id"]!);
    }

    /// <summary>Additive operations keep their place ahead of data: create, then populate.</summary>
    [Fact]
    public async Task An_added_field_can_be_backfilled_by_the_same_document()
    {
        var adapter = Seeded();

        var world = await BranchWorldAfterAsync(adapter, Patches(
            schema: new JsonArray(new JsonObject
            {
                ["op"] = "field.add",
                ["entity"] = Entity,
                ["field"] = new JsonObject { ["name"] = "email", ["type"] = "string" },
                ["explanation"] = "Add an email address.",
            }),
            data: new JsonArray(DataPatch("backfill-email", new JsonObject
            {
                ["op"] = "update",
                ["entity"] = Entity,
                ["where"] = new JsonObject { ["field"] = "id", ["equals"] = "c1" },
                ["set"] = new JsonObject { ["email"] = "ada@example.invalid" },
            }))));

        Assert.NotNull(world["schema"]!["entities"]![Entity]!["fields"]!["email"]);
        Assert.Equal("ada@example.invalid", (string)((JsonArray)world["data"]![Entity]!)[0]!["email"]!);
    }

    /// <summary>
    /// One document doing both halves: widen, move the values across, drop the old
    /// column. This is the shape the ordering exists for, and it is unexpressible if
    /// either half runs on the wrong side of the data operations.
    /// </summary>
    [Fact]
    public async Task One_document_can_add_backfill_and_remove()
    {
        var adapter = Seeded();

        var world = await BranchWorldAfterAsync(adapter, Patches(
            schema: new JsonArray(
                new JsonObject
                {
                    ["op"] = "field.add",
                    ["entity"] = Entity,
                    ["field"] = new JsonObject { ["name"] = "contactNumber", ["type"] = "string" },
                    ["explanation"] = "Replace fax with a general contact number.",
                },
                RemoveFax()),
            data: new JsonArray(DataPatch("move-fax", new JsonObject
            {
                ["op"] = "update",
                ["entity"] = Entity,
                ["where"] = new JsonObject { ["field"] = "fax", ["equals"] = "555-0100" },
                ["set"] = new JsonObject { ["contactNumber"] = "555-0100" },
            }))));

        var fields = (JsonObject)world["schema"]!["entities"]![Entity]!["fields"]!;
        Assert.NotNull(fields["contactNumber"]);
        Assert.Null(fields["fax"]);

        var ada = (JsonObject)((JsonArray)world["data"]![Entity]!)[0]!;
        Assert.Equal("555-0100", (string)ada["contactNumber"]!);
        Assert.False(ada.ContainsKey("fax"));
    }

    [Fact]
    public async Task Removing_an_entity_takes_its_rows_with_it()
    {
        var adapter = Seeded();

        var world = await BranchWorldAfterAsync(adapter, Patches(schema: new JsonArray(new JsonObject
        {
            ["op"] = "entity.remove",
            ["entity"] = Entity,
            ["explanation"] = "Retire contacts entirely.",
        })));

        Assert.Null(world["schema"]!["entities"]![Entity]);
        Assert.Null(world["data"]![Entity]);
    }
}
