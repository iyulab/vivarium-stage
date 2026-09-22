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
}
