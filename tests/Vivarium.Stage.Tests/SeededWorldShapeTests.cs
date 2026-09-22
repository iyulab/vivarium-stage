using System.Text.Json.Nodes;
using Vivarium.Stage.Adapters;

namespace Vivarium.Stage.Tests;

/// <summary>
/// <see cref="InMemoryBackendAdapter.SeedTarget"/> documents a world shape but accepts
/// any object, and every other suite here seeds the full shape — so the reference
/// adapter has never been read back after a partial seed. It is the first thing a new
/// adapter author does: seed something plausible, then call the kit. A world missing a
/// facet is either refused at the door with a reason, or it reads back; a
/// <see cref="NullReferenceException"/> raised three calls later says nothing about
/// which key was wrong.
/// </summary>
public class SeededWorldShapeTests
{
    public static TheoryData<string> PartialWorlds() =>
    [
        """{ "loans": [] }""",
        "{}",
        """{ "facets": { "ui": {} } }""",
        """{ "ui": {}, "schema": {}, "data": {} }""",
    ];

    [Theory]
    [MemberData(nameof(PartialWorlds))]
    public void Seeding_a_world_without_artifacts_is_refused_with_a_reason(string world)
    {
        var adapter = new InMemoryBackendAdapter();

        var refusal = Assert.Throws<ArgumentException>(
            () => adapter.SeedTarget("known", (JsonObject)JsonNode.Parse(world)!));

        // The reason names the missing key, so the caller can fix the seed rather than
        // read a stack trace from a method they did not call.
        Assert.Contains("artifacts", refusal.Message);
        Assert.Equal("initialWorld", refusal.ParamName);
    }

    /// <summary>
    /// The three-container world is the near miss: it clears a naive "are the facet keys
    /// there" check and still has nowhere to put an entity, so patch application — not the
    /// read — is where it would have failed. The door has to check the shape the code
    /// addresses, not the shape the key names suggest.
    /// </summary>
    [Fact]
    public void A_schema_container_without_entities_is_refused_too()
    {
        var adapter = new InMemoryBackendAdapter();

        var refusal = Assert.Throws<ArgumentException>(() => adapter.SeedTarget("known", new JsonObject
        {
            ["ui"] = new JsonObject(),
            ["schema"] = new JsonObject(),
            ["data"] = new JsonObject(),
        }));

        Assert.Contains("schema.entities", refusal.Message);
    }

    /// <summary>One refusal names every problem — fixing a seed should cost one round-trip.</summary>
    [Fact]
    public void One_refusal_names_every_missing_container()
    {
        var adapter = new InMemoryBackendAdapter();

        var refusal = Assert.Throws<ArgumentException>(
            () => adapter.SeedTarget("known", new JsonObject { ["loans"] = new JsonArray() }));

        Assert.Contains("schema", refusal.Message);
        Assert.Contains("data", refusal.Message);
        Assert.Contains("artifacts", refusal.Message);
    }

    [Fact]
    public void A_container_of_the_wrong_type_is_refused_as_such()
    {
        var adapter = new InMemoryBackendAdapter();

        var refusal = Assert.Throws<ArgumentException>(() => adapter.SeedTarget("known", new JsonObject
        {
            ["schema"] = new JsonObject { ["entities"] = new JsonObject() },
            ["data"] = new JsonArray(),
            ["artifacts"] = new JsonObject(),
        }));

        Assert.Contains("'data' must be an object", refusal.Message);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("data")]
    [InlineData("artifacts")]
    public void Every_facet_the_active_state_reads_is_required_at_the_door(string missing)
    {
        var adapter = new InMemoryBackendAdapter();
        var world = new JsonObject
        {
            ["schema"] = new JsonObject { ["entities"] = new JsonObject() },
            ["data"] = new JsonObject(),
            ["artifacts"] = new JsonObject(),
        };
        world.Remove(missing);

        var refusal = Assert.Throws<ArgumentException>(() => adapter.SeedTarget("known", world));
        Assert.Contains(missing, refusal.Message);
    }

    [Fact]
    public async Task A_world_carrying_every_facet_reads_back_after_seeding()
    {
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("known", new JsonObject
        {
            ["schema"] = new JsonObject { ["entities"] = new JsonObject() },
            ["data"] = new JsonObject { ["loans"] = new JsonArray() },
            ["artifacts"] = new JsonObject(),
        });

        var active = await adapter.ActiveStateAsync("known");

        Assert.Equal("live-known", active.StateRef);
        Assert.Contains("schema", active.FacetFingerprints.Keys);
        Assert.Contains("data", active.FacetFingerprints.Keys);
    }

    [Fact]
    public async Task The_default_seed_reads_back_unchanged()
    {
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("known");

        var active = await adapter.ActiveStateAsync("known");

        Assert.Equal("live-known", active.StateRef);
    }

    /// <summary>
    /// The path a consumer actually walks: seed, then hand the adapter to the kit. The
    /// kit must reach a verdict rather than propagate an adapter exception — a suite
    /// that cannot report on the reference adapter cannot report on anyone else's.
    /// </summary>
    [Fact]
    public async Task The_conformance_kit_reaches_a_verdict_on_a_minimally_seeded_adapter()
    {
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("known", new JsonObject
        {
            ["schema"] = new JsonObject { ["entities"] = new JsonObject() },
            ["data"] = new JsonObject { ["loans"] = new JsonArray() },
            ["artifacts"] = new JsonObject(),
        });

        var report = await Vivarium.Stage.Conformance.AdapterConformance.RunAsync(
            adapter,
            new Vivarium.Stage.Conformance.ConformanceFixture(
                KnownTarget: "known",
                UnknownTarget: "nope",
                Patches: new JsonObject
                {
                    ["ui"] = new JsonArray(new JsonObject
                    {
                        ["artifactId"] = "screen-main",
                        ["profile"] = "whole-artifact@0",
                        ["newContent"] = "export default function mount(root) { root.textContent = 'probe'; }",
                        ["explanation"] = "Conformance fixture patch.",
                    }),
                },
                TokenPrefix: "probe"));

        Assert.NotEmpty(report.Checks);
    }

    // ---- The same gap, one level down (reported against 0.8.0-unreleased) ----
    //
    // The container guard above leaves the *inside* of an entity and of a data container
    // unchecked, and the code addresses both. A consumer following the documented shape
    // exactly still had to discover 'fields' by crashing.

    private static JsonObject WorldWithEntity(JsonNode? entity) => new()
    {
        ["schema"] = new JsonObject { ["entities"] = new JsonObject { ["loan"] = entity } },
        ["data"] = new JsonObject(),
        ["artifacts"] = new JsonObject(),
    };

    /// <summary>
    /// The reported case. An entity present but without <c>fields</c> reached
    /// <c>ApplySchemaOp</c>, where the container is dereferenced without a check — so
    /// <c>field.add</c> ended in a raw NullReferenceException from a member the operation
    /// never named. An <em>absent</em> entity was always refused with a reason
    /// ("unknown entity"), which is what made the half-formed one the gap.
    /// </summary>
    [Fact]
    public void An_entity_without_its_fields_container_is_refused_at_the_door()
    {
        var adapter = new InMemoryBackendAdapter();

        var refusal = Assert.Throws<ArgumentException>(
            () => adapter.SeedTarget("known", WorldWithEntity(new JsonObject { ["attributes"] = new JsonObject() })));

        Assert.Contains("'schema.entities.loan.fields' is missing", refusal.Message);
        Assert.Equal("initialWorld", refusal.ParamName);
    }

    /// <summary>
    /// Not reported, found by sweeping the same surface: <c>constraint.add</c> and
    /// <c>constraint.remove</c> dereference the entity's constraints array the same way.
    /// An entity carrying <c>fields</c> but no <c>constraints</c> is the reported case
    /// with a different operation.
    /// </summary>
    [Fact]
    public void An_entity_without_its_constraints_array_is_refused_too()
    {
        var adapter = new InMemoryBackendAdapter();

        var refusal = Assert.Throws<ArgumentException>(
            () => adapter.SeedTarget("known", WorldWithEntity(new JsonObject { ["fields"] = new JsonObject() })));

        Assert.Contains("'schema.entities.loan.constraints' is missing", refusal.Message);
    }

    [Theory]
    [InlineData("fields")]
    [InlineData("constraints")]
    public void An_entity_member_of_the_wrong_type_is_refused_as_such(string member)
    {
        var adapter = new InMemoryBackendAdapter();
        var entity = new JsonObject { ["fields"] = new JsonObject(), ["constraints"] = new JsonArray() };
        // swap the member for the other one's type: fields->array, constraints->object
        entity[member] = member == "fields" ? new JsonArray() : new JsonObject();

        var refusal = Assert.Throws<ArgumentException>(() => adapter.SeedTarget("known", WorldWithEntity(entity)));

        Assert.Contains(
            member == "fields"
                ? "'schema.entities.loan.fields' must be an object"
                : "'schema.entities.loan.constraints' must be an array",
            refusal.Message);
    }

    [Fact]
    public void An_entity_that_is_not_an_object_is_refused_before_its_members_are_read()
    {
        var adapter = new InMemoryBackendAdapter();

        var refusal = Assert.Throws<ArgumentException>(() => adapter.SeedTarget("known", WorldWithEntity(new JsonArray())));

        Assert.Contains("'schema.entities.loan' must be an object", refusal.Message);
        // One problem, not three: the members of a non-object are not worth reporting on.
        Assert.DoesNotContain("loan.fields", refusal.Message);
    }

    /// <summary>
    /// The quiet one, and the reason this guard covers data as well. <c>ApplyDataOp</c>
    /// resolves rows as "array, or install an empty array", so a non-array container was
    /// discarded with no exception and the facet still reported complete — an approved
    /// document applied against contents that vanished on the way in. A crash can be read;
    /// this could not.
    /// </summary>
    [Fact]
    public void A_data_container_that_is_not_an_array_is_refused_rather_than_silently_replaced()
    {
        var adapter = new InMemoryBackendAdapter();

        var refusal = Assert.Throws<ArgumentException>(() => adapter.SeedTarget("known", new JsonObject
        {
            ["schema"] = new JsonObject { ["entities"] = new JsonObject() },
            ["data"] = new JsonObject { ["loan"] = new JsonObject { ["id"] = 1 } },
            ["artifacts"] = new JsonObject(),
        }));

        Assert.Contains("'data.loan' must be an array of rows", refusal.Message);
    }

    /// <summary>Still one round-trip: the inner problems collect like the outer ones.</summary>
    [Fact]
    public void One_refusal_names_every_inner_problem_too()
    {
        var adapter = new InMemoryBackendAdapter();

        var refusal = Assert.Throws<ArgumentException>(() => adapter.SeedTarget("known", new JsonObject
        {
            ["schema"] = new JsonObject
            {
                ["entities"] = new JsonObject
                {
                    ["loan"] = new JsonObject { ["fields"] = new JsonObject() },
                    ["customer"] = new JsonObject { ["constraints"] = new JsonArray() },
                },
            },
            ["data"] = new JsonObject { ["loan"] = new JsonObject() },
            ["artifacts"] = new JsonObject(),
        }));

        Assert.Contains("loan.constraints", refusal.Message);
        Assert.Contains("customer.fields", refusal.Message);
        Assert.Contains("'data.loan'", refusal.Message);
    }

    /// <summary>
    /// The control that keeps the guard honest: the shape <c>entity.create</c> itself
    /// writes must pass, and must still read back and apply.
    /// </summary>
    [Fact]
    public async Task The_shape_entity_create_writes_is_accepted_and_reads_back()
    {
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("known", new JsonObject
        {
            ["schema"] = new JsonObject
            {
                ["entities"] = new JsonObject
                {
                    ["loan"] = new JsonObject
                    {
                        ["fields"] = new JsonObject { ["id"] = new JsonObject { ["name"] = "id", ["type"] = "string" } },
                        ["constraints"] = new JsonArray(),
                    },
                },
            },
            ["data"] = new JsonObject { ["loan"] = new JsonArray() },
            ["artifacts"] = new JsonObject(),
        });

        var active = await adapter.ActiveStateAsync("known");

        Assert.Equal("live-known", active.StateRef);
        Assert.Contains("schema", active.FacetFingerprints.Keys);
    }

    /// <summary>
    /// Found by sweeping rather than reported, and it lands on the original method:
    /// <c>ActiveStateAsync</c> reads each artifact's content back as a string, so a seeded
    /// null reproduces the very NullReferenceException the container guard was written to
    /// end — at the same line, in the same method.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    public void An_artifact_whose_content_is_not_a_string_is_refused_at_the_door(int? notAString)
    {
        var adapter = new InMemoryBackendAdapter();
        var world = new JsonObject
        {
            ["schema"] = new JsonObject { ["entities"] = new JsonObject() },
            ["data"] = new JsonObject(),
            ["artifacts"] = new JsonObject { ["screen-main"] = notAString is null ? null : JsonValue.Create(notAString.Value) },
        };

        var refusal = Assert.Throws<ArgumentException>(() => adapter.SeedTarget("known", world));

        Assert.Contains("'artifacts.screen-main' must be a string", refusal.Message);
    }

    [Fact]
    public async Task A_string_artifact_is_accepted_and_fingerprinted()
    {
        var adapter = new InMemoryBackendAdapter();
        adapter.SeedTarget("known", new JsonObject
        {
            ["schema"] = new JsonObject { ["entities"] = new JsonObject() },
            ["data"] = new JsonObject(),
            ["artifacts"] = new JsonObject { ["screen-main"] = "export default function mount(root) {}" },
        });

        var active = await adapter.ActiveStateAsync("known");

        Assert.Contains("screen-main", active.FacetFingerprints.Keys);
    }
}
