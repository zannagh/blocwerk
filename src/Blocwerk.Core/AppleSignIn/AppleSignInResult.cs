// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.DeviceLinks;

namespace Blocwerk.Core.AppleSignIn;

/// <summary>The outcome of a Sign in with Apple and, when signed in, the new device key.</summary>
public sealed record AppleSignInResult(AppleSignInOutcome Outcome, DeviceLinkExchange? Key = null);
