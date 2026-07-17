using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Vivarium.Stage.Adapters.MorphDb;

/// <summary>
/// Minimal MorphDB REST client for the adapter's needs, bound to the service's
/// current wire format. Deliberately package-free: the adapter stays decoupled
/// from MorphDB.Client release cadence (0.4.0 shipped with a wire mismatch,
/// fixed in 0.5.0 via breaking renames), and JsonNode-based access keeps this
/// resilient to additive server changes.
/// </summary>
public sealed class MorphDbRestClient : IDisposable
{
    private readonly HttpClient _http;

    public MorphDbRestClient(string baseUrl, string apiKey, Guid? tenantId = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        if (tenantId is { } t) _http.DefaultRequestHeaders.Add("X-Tenant-Id", t.ToString());
    }

    public void Dispose() => _http.Dispose();

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new MorphDbApiException((int)response.StatusCode, text);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync(response, ct).ConfigureAwait(false);
    }

    // ---- projects (no tenant header needed) ----

    public async Task<Guid> CreateProjectAsync(string name, CancellationToken ct = default)
    {
        var body = await SendAsync(HttpMethod.Post, "/api/projects", new JsonObject { ["name"] = name, ["slug"] = name }, ct).ConfigureAwait(false);
        return Guid.Parse(body!["id"]!.GetValue<string>());
    }

    public async Task<Guid?> FindProjectAsync(string nameOrSlug, CancellationToken ct = default)
    {
        var body = await SendAsync(HttpMethod.Get, "/api/projects?pageSize=200", null, ct).ConfigureAwait(false);
        foreach (var p in (JsonArray)body!["data"]!)
            if (p!["slug"]!.GetValue<string>() == nameOrSlug || p["name"]!.GetValue<string>() == nameOrSlug)
                return Guid.Parse(p["id"]!.GetValue<string>());
        return null;
    }

    public Task DeleteProjectAsync(Guid projectId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/api/projects/{projectId}", null, ct);

    // ---- schema ----

    /// <summary>All tables including their column definitions (single call).</summary>
    public async Task<JsonArray> GetTablesAsync(CancellationToken ct = default) =>
        (JsonArray)(await SendAsync(HttpMethod.Get, "/api/schema/tables", null, ct).ConfigureAwait(false))!;

    public async Task<JsonObject?> GetTableAsync(string name, CancellationToken ct = default)
    {
        try
        {
            return (JsonObject?)await SendAsync(HttpMethod.Get, $"/api/schema/tables/{Uri.EscapeDataString(name)}", null, ct).ConfigureAwait(false);
        }
        catch (MorphDbApiException e) when (e.StatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>columns: [{ name, type, nullable?, unique?, indexed? }]</summary>
    public Task CreateTableAsync(string name, JsonArray columns, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/api/schema/tables", new JsonObject { ["name"] = name, ["columns"] = columns }, ct);

    public Task RenameTableAsync(string name, string newName, int version, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, $"/api/schema/tables/{Uri.EscapeDataString(name)}",
            new JsonObject { ["name"] = newName, ["version"] = version }, ct);

    public Task DropTableAsync(string name, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/api/schema/tables/{Uri.EscapeDataString(name)}", null, ct);

    public Task AddColumnAsync(string table, JsonObject column, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/api/schema/tables/{Uri.EscapeDataString(table)}/columns", column, ct);

    public Task UpdateColumnAsync(Guid columnId, JsonObject changes, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, $"/api/schema/columns/{columnId}", changes, ct);

    public Task DropColumnAsync(Guid columnId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/api/schema/columns/{columnId}", null, ct);

    // ---- data ----

    public async Task<JsonObject> InsertAsync(string table, JsonObject values, CancellationToken ct = default) =>
        (JsonObject)(await SendAsync(HttpMethod.Post, $"/api/data/{Uri.EscapeDataString(table)}", values, ct).ConfigureAwait(false))!;

    public sealed record Row(Guid Id, JsonObject Data);

    /// <summary>Equality-filtered page query. Pass a null column for an unfiltered scan.</summary>
    public async Task<(IReadOnlyList<Row> Rows, bool HasNext)> QueryAsync(
        string table, string? column = null, JsonNode? equals = null, int page = 1, int pageSize = 100,
        CancellationToken ct = default)
    {
        var request = new JsonObject { ["page"] = page, ["pageSize"] = pageSize };
        if (column is not null)
            request["filter"] = new JsonObject
            {
                ["$type"] = "condition",
                ["column"] = column,
                ["operator"] = "eq",
                ["value"] = equals?.DeepClone(),
            };
        var body = (JsonObject)(await SendAsync(HttpMethod.Post, $"/api/data/{Uri.EscapeDataString(table)}/query", request, ct).ConfigureAwait(false))!;
        var rows = ((JsonArray)body["data"]!)
            .Select(r => new Row(Guid.Parse(r!["id"]!.GetValue<string>()), (JsonObject)r["data"]!))
            .ToList();
        return (rows, body["pagination"]!["hasNext"]!.GetValue<bool>());
    }

    /// <summary>All rows matching an equality filter, across pages.</summary>
    public async Task<IReadOnlyList<Row>> QueryAllAsync(
        string table, string? column = null, JsonNode? equals = null, int pageSize = 100, CancellationToken ct = default)
    {
        var all = new List<Row>();
        var page = 1;
        while (true)
        {
            var (rows, hasNext) = await QueryAsync(table, column, equals, page, pageSize, ct).ConfigureAwait(false);
            all.AddRange(rows);
            if (!hasNext) break;
            page++;
        }
        return all;
    }

    public Task UpdateRowAsync(string table, Guid id, JsonObject set, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, $"/api/data/{Uri.EscapeDataString(table)}/{id}", set, ct);

    public Task DeleteRowAsync(string table, Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/api/data/{Uri.EscapeDataString(table)}/{id}", null, ct);

    // ---- transactions ----

    /// <summary>Execute operations atomically (all or nothing). Throws MorphDbApiException on failure.</summary>
    public Task TransactionAsync(JsonArray operations, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/api/batch/transaction", new JsonObject { ["operations"] = operations }, ct);
}

public sealed class MorphDbApiException(int statusCode, string body)
    : Exception($"MorphDB API error {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
    public string Body { get; } = body;
}
