// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.AppleSignIn;

/// <summary>A verified Apple identity token's subject: Apple's stable user id for this team.</summary>
public sealed record AppleIdentity(string Subject);
