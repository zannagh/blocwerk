// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Store;

/// <summary>One cached data document as read from the device: its sha and its JSON.</summary>
/// <param name="Sha">The sha the text was cached under.</param>
/// <param name="Text">The document's canonical JSON.</param>
public sealed record StoreDocText(string Sha, string Text);
