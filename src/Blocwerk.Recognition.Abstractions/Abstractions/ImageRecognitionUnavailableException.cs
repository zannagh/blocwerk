// <copyright file="ImageRecognitionUnavailableException.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Image recognition did not run: it is switched off (<c>HoldDetection:Enabled=false</c>), every slot stayed busy for
/// longer than the gate waits, or no recognition runner served the job. The message is written for the person who asked.
/// </summary>
public sealed class ImageRecognitionUnavailableException : InvalidOperationException
{
    /// <summary>The message while recognition is switched off.</summary>
    public const string DisabledMessage = "Hold recognition is switched off on this server, so wall updates and hold recognition are unavailable.";

    /// <summary>The message after a wait for a free slot timed out.</summary>
    public const string BusyMessage = "Hold recognition is busy with other photos. Please try again in a minute.";

    /// <summary>The message when no recognition runner took or finished the job (<c>RECOGNITION__MODE=queue</c>).</summary>
    public const string QueueMessage = "Hold recognition is unavailable right now. Please retry in a minute.";

    /// <summary>Initializes a new instance of the <see cref="ImageRecognitionUnavailableException"/> class.</summary>
    /// <param name="message">One of <see cref="DisabledMessage"/>, <see cref="BusyMessage"/> and <see cref="QueueMessage"/>.</param>
    public ImageRecognitionUnavailableException(string message)
        : base(message)
    {
    }
}
