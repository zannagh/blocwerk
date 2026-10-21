// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints;

/// <summary>One validated storage-probe report, as <see cref="StorageProbeEndpoints"/> logs it.</summary>
internal sealed record StorageProbeLine(string Run, string Context, int Seq, string Step, string Result, string Data);
