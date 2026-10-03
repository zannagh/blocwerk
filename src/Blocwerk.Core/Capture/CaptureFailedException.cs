namespace Blocwerk.Core.Capture;

/// <summary>Stops a capture with a message that is shown to the admin as is.</summary>
public sealed class CaptureFailedException(string message, Exception? inner = null) : Exception(message, inner);
