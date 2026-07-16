namespace Vivarium.Stage.Adapters.MorphDb;

public sealed record MorphDbAdapterOptions
{
    /// <summary>MorphDB service base URL, e.g. http://localhost:5400.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>API key with rights to manage projects, schemas, and data.</summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Name/slug of the control project that holds the target pointer and flip
    /// log tables. Created on first use if missing.
    /// </summary>
    public string ControlProjectName { get; init; } = "vivarium-stage-control";

    /// <summary>Page size for data copy during branching.</summary>
    public int CopyPageSize { get; init; } = 500;
}
