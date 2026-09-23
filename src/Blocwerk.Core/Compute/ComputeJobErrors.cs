using System.Net;

namespace Blocwerk.Core.Compute;

/// <summary>Maps HTTP status codes of protocol v1 to admin-friendly failures.</summary>
internal static class ComputeJobErrors
{
    public static ComputeJobException FromStatus(HttpStatusCode status, string action, string? detail, bool jobScoped)
    {
        var reason = ComputeErrorText.Sanitize(detail);
        var suffix = reason is null ? string.Empty : $" ({reason})";
        return (int)status switch
        {
            401 or 403 => new ComputeJobException(
                ComputeFailureKind.Unauthorized,
                "The 3D computation service refused this server's API key. Check GEOMETRYSERVICE__APIKEY."),
            404 when jobScoped => new ComputeJobException(
                    ComputeFailureKind.JobNotFound, "The 3D computation service no longer knows this job (it may have restarted)."),
            429 => new ComputeJobException(
                ComputeFailureKind.Busy, "The 3D computation service is busy with other jobs; it will be retried."),
            413 => new ComputeJobException(ComputeFailureKind.Rejected, $"The photos are too large for the 3D computation service{suffix}."),
            >= 400 and < 500 => new ComputeJobException(
                ComputeFailureKind.Rejected, $"The 3D computation service could not {action}{suffix}."),
            _ => new ComputeJobException(
                ComputeFailureKind.Unavailable, $"The 3D computation service had an internal error trying to {action}."),
        };
    }
}
