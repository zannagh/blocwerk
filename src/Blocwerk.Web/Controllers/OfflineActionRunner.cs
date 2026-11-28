// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Runs one replayable mark write (attempt, rating, favourite, project, comment) and maps the Core services' domain
/// exceptions onto the status codes and <see cref="OfflineActionError"/> envelope a client queue branches on. Shared by the
/// browser's offline queue (<see cref="OfflineActionsController"/>) and the companion app (<see cref="MeBoulderMarksController"/>),
/// so both answer a write the same way.
/// </summary>
public static class OfflineActionRunner
{
    /// <summary>The longest comment the database stores (<see cref="BoulderComment.Text"/>).</summary>
    public const int MaxCommentLength = 1024;

    /// <summary>Verbatim messages the Core services throw for their permanent domain guards.</summary>
    private const string BoulderNotFound = "Boulder not found";

    private const string CommentNotFound = "Comment not found";

    private const string NotAWallMember = "Only wall members can do this";

    /// <summary>The validation error for a comment's text, or null when it may be posted.</summary>
    public static string? CommentTextError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Comment text must not be empty.";
        }

        return text.Length > MaxCommentLength
            ? $"Comment text must be at most {MaxCommentLength} characters."
            : null;
    }

    /// <summary>
    /// Runs <paramref name="write"/>. A replay of an action already applied reaches the success path (the services return the
    /// stored row rather than throwing), so the caller answers 200 and the queue clears the entry.
    /// </summary>
    /// <param name="subjectId">The boulder or comment the write is about, for the log line of an unexpected failure.</param>
    /// <param name="write">The write and its own answer.</param>
    public static async Task<IActionResult> RunAsync(Guid subjectId, Func<Task<IActionResult>> write)
    {
        try
        {
            return await write();
        }
        catch (UnauthorizedAccessException)
        {
            // Session gone. Retryable in principle: the queue pauses and prompts re-login.
            return Error(StatusCodes.Status401Unauthorized, "Your session has expired. Sign in to sync.", permanent: false);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Permanent(ex.Message);
        }
        catch (InvalidOperationException ex) when (IsDomainGuard(ex))
        {
            // Only the known domain guards are permanent. EF Core and Npgsql also raise InvalidOperationException for
            // transient connection failures, and calling one of those permanent would drop a valid queued action.
            var notFound = ex.Message is BoulderNotFound or CommentNotFound;
            return Error(notFound ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden, ex.Message, permanent: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Offline action failed for {SubjectId}", subjectId);
            return Error(StatusCodes.Status500InternalServerError, "The action could not be saved. It stays queued.", permanent: false);
        }
    }

    /// <summary>A 400 the queue drops and shows to the person: retrying can never succeed.</summary>
    public static ObjectResult Permanent(string message) =>
        Error(StatusCodes.Status400BadRequest, message, permanent: true);

    /// <summary>The success envelope: applied now or by an earlier replay of the same client request id.</summary>
    public static OkObjectResult Applied(Guid? clientRequestId, object? result) =>
        new(new OfflineActionResponse(true, clientRequestId, result));

    private static ObjectResult Error(int status, string message, bool permanent) =>
        new(new OfflineActionError(message, permanent)) { StatusCode = status };

    private static bool IsDomainGuard(InvalidOperationException ex) =>
        ex.Message is BoulderNotFound or CommentNotFound
        || ex.Message.Equals(NotAWallMember, StringComparison.Ordinal)
        || ex.Message.Equals(AttemptService.DraftNotLoggableMessage, StringComparison.Ordinal);
}
