// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Runtime;

/// <summary>Reads the memory the container (or, outside one, this process) uses now.</summary>
public interface IMemoryProbe
{
    long CurrentBytes();
}
