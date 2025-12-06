namespace YtDlpFetch;

public sealed class YtDlpLocatorOptions
{
    /// <summary>Where a downloaded yt-dlp lives. Required.</summary>
    public required string ToolsDirectory { get; init; }

    /// <summary>
    /// Returns a user-chosen yt-dlp path, or null/blank for none. Called on every
    /// <see cref="YtDlpLocator.LocateAsync"/>, so a changed setting applies straight away.
    /// </summary>
    public Func<string?>? ConfiguredPath { get; init; }

    /// <summary>Overrides the <c>PATH</c> searched. Null means the process's own; empty means skip it.</summary>
    public string? PathVariable { get; init; }

    /// <summary>The client to download with. Null uses a shared default client.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>How long a download may go without receiving a byte.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>After a failed download, how long to wait before trying again.</summary>
    public TimeSpan FailureBackOff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Forces a specific release asset instead of the one for this platform.</summary>
    public string? Asset { get; init; }

    public TimeProvider? Time { get; init; }
}
