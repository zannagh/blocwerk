namespace Blocwerk.Core.Configuration;

/// <summary>
/// One rung of the HLS adaptive-bitrate ladder: a target output <paramref name="Height"/> (px) with
/// its video and audio bitrates (kbps). The transcoder scales the source down to this height (never
/// up) and emits one variant playlist + segment set per included rung.
/// </summary>
public record HlsRung(int Height, int VideoKbps, int AudioKbps);
