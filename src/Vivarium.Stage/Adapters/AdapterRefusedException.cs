using System.Text.Json.Nodes;

namespace Vivarium.Stage.Adapters;

/// <summary>
/// Which clause of the adapter contract a refusal enforces (adapter-api §6, Error
/// taxonomy). A closed vocabulary — hosts branch on it. Each member names a case the
/// contract already requires an adapter to refuse; nothing here is a new obligation.
/// </summary>
public enum AdapterRefusalReason
{
    /// <summary>
    /// <c>prepare</c> was handed a document it cannot execute honestly: an operation
    /// outside the vocabulary or missing a member it needs, or a well-formed operation
    /// naming something the branch does not hold (adapter-api §3, Operation input).
    /// The document's author fixes this.
    /// </summary>
    DocumentRefused,

    /// <summary>A target the adapter does not know — it throws rather than invent a pointer (§6).</summary>
    UnknownTarget,

    /// <summary>A branch or state ref the adapter does not hold — typically a stale one the caller kept.</summary>
    UnknownRef,

    /// <summary>
    /// A flip re-issued an apply token that was already used for a different state ref
    /// (§3, §6). Same token and same ref is the idempotent recovery no-op, not this.
    /// </summary>
    ApplyTokenConflict,

    /// <summary><c>discard</c> named the state that is live on its target, which is not a staging world any more (§5).</summary>
    StateIsActive,
}

/// <summary>
/// An adapter refusing an operation the contract tells it to refuse — the product
/// working, as opposed to a fault. Any other exception out of an adapter is a fault.
/// </summary>
/// <remarks>
/// <para>
/// Stage does not wrap this in <see cref="StageRefusedException"/>: that type is the
/// verdict of Stage's own gates, this one is the backend's, and a host that shows
/// either to a person needs to know which judged. It surfaces from
/// <see cref="ChangeSession.ApplyAsync"/> as the adapter threw it.
/// </para>
/// <para>
/// <see cref="Details"/> follows the same rules as <see cref="StageRefusedException.Details"/>:
/// a snapshot taken at throw time, members per reason, additive. For
/// <see cref="AdapterRefusalReason.DocumentRefused"/> it is
/// <c>{ "errors": [{ "path", "message" }] }</c> — the same shape Stage uses for a
/// changeset that fails validation, so a host reads both the same way.
/// </para>
/// </remarks>
public sealed class AdapterRefusedException : Exception
{
    public AdapterRefusedException(AdapterRefusalReason reason, string message, JsonObject? details = null)
        : base(message)
    {
        Reason = reason;
        Details = (JsonObject?)details?.DeepClone();
    }

    /// <summary>Which contract clause refused. A closed vocabulary — hosts branch on this.</summary>
    public AdapterRefusalReason Reason { get; }

    /// <summary>What the adapter observed, as a snapshot (null when there is nothing to add). Treat as read-only.</summary>
    public JsonObject? Details { get; }

    /// <summary>
    /// A document refusal located at <paramref name="path"/> (JSON-path-like, rooted at
    /// <c>$</c> for the document's <c>patches</c>). The message is <c>path: message</c>.
    /// </summary>
    public static AdapterRefusedException Document(string path, string message) =>
        new(AdapterRefusalReason.DocumentRefused, $"{path}: {message}", new JsonObject
        {
            ["errors"] = new JsonArray(new JsonObject { ["path"] = path, ["message"] = message }),
        });
}
