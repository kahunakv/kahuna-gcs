using System.Net;
using Google;

namespace Kahuna.Storage.Gcs;

/// <summary>
/// Classifies Google API failures, so "the object is not there" is handled as a fact and every other
/// failure keeps propagating.
/// <para>
/// The distinction matters more than it looks: several operations here are required to be idempotent
/// (delete a manifest, delete a backup's artifacts) and several are required to fail closed (any
/// listing feeding the orphan sweep). Swallowing the wrong status turns a transient outage into
/// "there is nothing here", which is how a sweep reclaims live data.
/// </para>
/// </summary>
internal static class GcsErrors
{
    internal static bool IsNotFound(GoogleApiException exception) =>
        exception.HttpStatusCode == HttpStatusCode.NotFound;

    /// <summary>
    /// True when a write was rejected because the create-if-absent precondition failed — the object
    /// already exists. GCS reports a failed generation precondition as 412 Precondition Failed.
    /// </summary>
    internal static bool IsPreconditionFailed(GoogleApiException exception) =>
        exception.HttpStatusCode == HttpStatusCode.PreconditionFailed;
}
