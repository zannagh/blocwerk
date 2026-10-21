// <copyright file="NoticeLink.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>One link listed under a notice shown after a deploy's reload (deploy-drafts.js renders them).</summary>
public sealed record NoticeLink(string Text, string Href);
