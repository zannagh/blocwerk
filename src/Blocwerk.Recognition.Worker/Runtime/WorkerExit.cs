// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Runtime;

/// <summary>
/// Stops the process with an exit code the orchestrator can tell apart. 2: the server confirmed no recognition kind (§1.5);
/// 3: the key was refused; 4: memory stayed above <c>JOB_MAX_MEMORY_MB</c> after the job was cancelled (native work still
/// running); 5: a cancelled job's work did not end by its run deadline. The first code requested wins.
/// </summary>
public sealed class WorkerExit(IHostApplicationLifetime lifetime)
{
    public const int NotConfirmed = 2;

    public const int Unauthorized = 3;

    public const int MemoryNotReleased = 4;

    public const int WorkNotSettled = 5;

    public int? Code { get; private set; }

    public void Request(int code)
    {
        Code ??= code;
        Environment.ExitCode = Code.Value;
        lifetime.StopApplication();
    }
}
