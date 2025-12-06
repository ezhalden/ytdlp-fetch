namespace YtDlpFetch;

/// <summary>A recent download failed, and the locator is waiting out its back-off before retrying.</summary>
public sealed class YtDlpUnavailableException(Exception cause)
    : Exception("yt-dlp isn't installed and couldn't be downloaded.", cause);

/// <summary>A downloaded yt-dlp couldn't be verified against the release's SHA2-512SUMS, so it was discarded.</summary>
public sealed class YtDlpVerificationException(string asset, string reason, Exception? inner = null)
    : Exception($"Refusing to use downloaded '{asset}': {reason}.", inner)
{
    public string Asset { get; } = asset;
}
