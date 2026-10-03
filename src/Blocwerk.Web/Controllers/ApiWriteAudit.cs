// <copyright file="ApiWriteAudit.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The audit trail of the automation API's writes, kept in the change journal. Before a write runs, its batch row is
/// written as Pending (label <c>api:{action} key:{keyId}</c>, scoped to the wall, the key's owner as actor), so no write
/// happens without one; the write runs with that batch ambient, so the rows it changes that the journal tracks (a
/// wall's marker settings) are recorded in it. A successful write (2xx) completes the row; a refused or failed one
/// removes it (or marks it Failed if it journalled rows). The audit never turns a write that happened into an error.
/// </summary>
public sealed class ApiWriteAudit(IChangeJournal journal, ICurrentUserService currentUser, ILogger<ApiWriteAudit> logger)
{
    /// <summary>The label prefix of every automation API batch.</summary>
    public const string LabelPrefix = "api:";

    /// <summary>The batch label for <paramref name="action"/> by the key in <paramref name="user"/>.</summary>
    public static string Label(ClaimsPrincipal user, string action) =>
        $"{LabelPrefix}{action} key:{user.GetApiKeyId()?.ToString() ?? "none"}";

    /// <summary>Runs <paramref name="write"/> on the wall as an audited action; it succeeded when it answered 2xx.</summary>
    public Task<IActionResult> RunAsync(ClaimsPrincipal user, Guid wallId, string action, Func<Task<IActionResult>> write) =>
        RunAsync(user, action, wallId, write, Succeeded);

    /// <summary>
    /// Runs <paramref name="write"/> on the wall as an audited action; <paramref name="succeeded"/> judges its result.
    /// With <paramref name="append"/> the action's batch is reused across calls (one batch for every file of an upload).
    /// </summary>
    /// <typeparam name="T">The write's result.</typeparam>
    public async Task<T> RunAsync<T>(
        ClaimsPrincipal user, string action, Guid wallId, Func<Task<T>> write, Func<T, bool> succeeded, bool append = false)
    {
        var actor = await currentUser.GetCurrentUserAsync();
        var batch = await journal.StartActionAsync(Label(user, action), ChangeJournalScopeKind.Wall, wallId, actor.Id.ToString(), append);
        T result;
        try
        {
            using (batch.Enter())
            {
                result = await write();
            }
        }
        catch
        {
            await TakeBackAsync(batch, action);
            throw;
        }

        if (!succeeded(result))
        {
            await TakeBackAsync(batch, action);
            logger.LogInformation("API write {Action} on wall {WallId} by key {KeyId} refused", action, wallId, user.GetApiKeyId());
            return result;
        }

        await CompleteAsync(batch, action);
        logger.LogInformation(
            "API write {Action} on wall {WallId} by key {KeyId} (user {UserId}), journal batch {BatchId}",
            action, wallId, user.GetApiKeyId(), actor.Id, batch.BatchId);
        return result;
    }

    private static bool Succeeded(IActionResult result)
    {
        // An ObjectResult without a status code is written as 200.
        var status = (result as IStatusCodeActionResult)?.StatusCode ?? StatusCodes.Status200OK;
        return status is >= 200 and < 300;
    }

    private async Task CompleteAsync(ChangeJournalAction batch, string action)
    {
        try
        {
            await batch.CompleteAsync();
        }
        catch (Exception ex)
        {
            // The write happened: answer it as such. Its audit row stays Pending, which still records the attempt.
            logger.LogError(ex, "API write {Action} succeeded but its journal batch {BatchId} could not be completed", action, batch.BatchId);
        }
    }

    private async Task TakeBackAsync(ChangeJournalAction batch, string action)
    {
        try
        {
            await batch.FailAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "API write {Action} did not happen but its journal batch {BatchId} could not be removed", action, batch.BatchId);
        }
    }
}
