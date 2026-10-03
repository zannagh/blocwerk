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
/// The audit trail of the automation API's writes, kept in the change journal: every write runs inside a journal batch
/// labelled <c>api:{action} key:{keyId}</c> and scoped to the wall, so the rows it changes (a wall's marker settings)
/// carry that label, and a successful write whose rows are not journalled (a run, a plan revision) still leaves the
/// batch row with the key owner as its actor. Refused or failed writes record nothing and are only logged.
/// </summary>
public sealed class ApiWriteAudit(IChangeJournal journal, ICurrentUserService currentUser, ILogger<ApiWriteAudit> logger)
{
    /// <summary>The label prefix of every automation API batch.</summary>
    public const string LabelPrefix = "api:";

    /// <summary>The batch label for <paramref name="action"/> by the key in <paramref name="user"/>.</summary>
    public static string Label(ClaimsPrincipal user, string action) =>
        $"{LabelPrefix}{action} key:{user.GetApiKeyId()?.ToString() ?? "none"}";

    /// <summary>Runs <paramref name="write"/> on the wall inside the action's journal batch; records it when it answered 2xx.</summary>
    public Task<IActionResult> RunAsync(ClaimsPrincipal user, Guid wallId, string action, Func<Task<IActionResult>> write) =>
        RunAsync(user, action, ChangeJournalScopeKind.Wall, wallId, write, Succeeded);

    /// <summary>Runs <paramref name="write"/> inside the action's journal batch; records it when <paramref name="succeeded"/> says so.</summary>
    /// <typeparam name="T">The write's result.</typeparam>
    public async Task<T> RunAsync<T>(
        ClaimsPrincipal user, string action, ChangeJournalScopeKind scopeKind, Guid? scopeId, Func<Task<T>> write, Func<T, bool> succeeded)
    {
        using var batch = journal.BeginAction(Label(user, action), scopeKind, scopeId);
        var result = await write();
        if (!succeeded(result))
        {
            logger.LogInformation("API write {Action} on {ScopeId} by key {KeyId} refused", action, scopeId, user.GetApiKeyId());
            return result;
        }

        var actor = await currentUser.GetCurrentUserAsync();
        await batch.CompleteAsync(actor.Id.ToString());
        logger.LogInformation(
            "API write {Action} on {ScopeId} by key {KeyId} (user {UserId}), journal batch {BatchId}",
            action, scopeId, user.GetApiKeyId(), actor.Id, batch.BatchId);
        return result;
    }

    private static bool Succeeded(IActionResult result)
    {
        // An ObjectResult without a status code is written as 200.
        var status = (result as IStatusCodeActionResult)?.StatusCode ?? StatusCodes.Status200OK;
        return status is >= 200 and < 300;
    }
}
