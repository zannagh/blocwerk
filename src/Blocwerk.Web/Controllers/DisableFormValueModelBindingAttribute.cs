// <copyright file="DisableFormValueModelBindingAttribute.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// For actions that stream a multipart body themselves (<c>MultipartReader</c> over <c>Request.Body</c>). MVC binds
/// even route-only parameters through every value provider, and the form providers answer a multipart request by
/// calling <c>ReadFormAsync</c>: the whole body is consumed (and buffered) before the action runs, so the action's
/// reader then fails with "Unexpected end of Stream". Removing the form providers leaves the body untouched.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    /// <inheritdoc/>
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    /// <inheritdoc/>
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
