using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;

namespace YtDlpFetch;

/// <summary>
/// Finds a yt-dlp to run: the configured path, then <c>PATH</c>, then a copy downloaded from
/// yt-dlp's latest GitHub release and checked against its published SHA-512.
/// </summary>
public sealed class YtDlpLocator
{
    private const string ReleaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download";
    private const string SumsFile = "SHA2-512SUMS";

    // Shared across instances on purpose: two locators pointed at the same folder must not each
    // retry a failing 35 MB download.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, Exception Cause)> RecentFailures = new();

    private static readonly Lazy<HttpClient> DefaultHttp = new(() => new HttpClient());

    private readonly YtDlpLocatorOptions _options;
    private readonly string _toolsDirectory;

    public YtDlpLocator(YtDlpLocatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ToolsDirectory);

        _options = options;
        _toolsDirectory = Path.GetFullPath(options.ToolsDirectory);
    }

    /// <summary>Returns the path of a yt-dlp that is ready to run, downloading one if needed.</summary>
    /// <exception cref="FileNotFoundException">A path is configured and nothing is there.</exception>
    /// <exception cref="YtDlpVerificationException">The download didn't match the published checksum.</exception>
    /// <exception cref="YtDlpUnavailableException">A recent download failed and the back-off hasn't passed.</exception>
    /// <exception cref="TimeoutException">The download stalled for longer than <see cref="YtDlpLocatorOptions.StallTimeout"/>.</exception>
    public async Task<string> LocateAsync(CancellationToken cancellationToken = default)
    {
        var configured = _options.ConfiguredPath?.Invoke();

        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Wrong path is the caller's mistake. Downloading a second copy would hide it.
            if (!File.Exists(configured))
                throw new FileNotFoundException($"yt-dlp isn't at the configured path '{configured}'.", configured);

            return configured;
        }

        return FindOnPath() ?? await DownloadAsync(cancellationToken);
    }

    /// <summary>
    /// True if <paramref name="executablePath"/> is a copy this locator downloaded. Only those are
    /// safe to <c>yt-dlp -U</c>; brew, apt and winget installs refuse to self-update.
    /// </summary>
    public bool IsManaged(string executablePath)
        => Path.GetFullPath(executablePath)
            .StartsWith(_toolsDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private string? FindOnPath()
    {
        var path = _options.PathVariable ?? Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), YtDlpAsset.ExecutableName);

            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private async Task<string> DownloadAsync(CancellationToken cancellationToken)
    {
        var asset = _options.Asset ?? YtDlpAsset.ForCurrentPlatform();
        var isArchive = asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        // The armv7l zip is a launcher plus an _internal folder, so it gets a folder of its own.
        var destination = isArchive
            ? Path.Combine(_toolsDirectory, Path.GetFileNameWithoutExtension(asset), Path.GetFileNameWithoutExtension(asset))
            : Path.Combine(_toolsDirectory, YtDlpAsset.ExecutableName);

        if (File.Exists(destination)) return destination;

        var now = (_options.Time ?? TimeProvider.System).GetUtcNow();

        if (RecentFailures.TryGetValue(_toolsDirectory, out var failure) && now - failure.At < _options.FailureBackOff)
            throw new YtDlpUnavailableException(failure.Cause);

        try
        {
            await FetchAsync(asset, isArchive, destination, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecentFailures[_toolsDirectory] = (now, ex);
            throw;
        }

        return destination;
    }

    private async Task FetchAsync(string asset, bool isArchive, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_toolsDirectory);

        // Download to a side file and move it into place, so a dropped connection never leaves a
        // half-written binary that the next File.Exists check would happily accept.
        var staging = Path.Combine(_toolsDirectory, asset + ".downloading");
        var http = _options.HttpClient ?? DefaultHttp.Value;

        try
        {
            using (var response = await WithStallLimit(
                token => http.GetAsync($"{ReleaseUrl}/{asset}", HttpCompletionOption.ResponseHeadersRead, token),
                cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var file = File.Create(staging);
                var buffer = new byte[81920];

                // Per-read limit rather than a total one: slow links finish, dead ones don't hang.
                while (await WithStallLimit(token => body.ReadAsync(buffer, token).AsTask(), cancellationToken) is var read and > 0)
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await VerifyAsync(http, staging, asset, cancellationToken);

            if (isArchive)
                Unpack(staging, destination);
            else
                File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }

        MakeExecutable(destination);
    }

    private async Task<T> WithStallLimit<T>(Func<CancellationToken, Task<T>> step, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_options.StallTimeout);

        try
        {
            return await step(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"yt-dlp download made no progress for {_options.StallTimeout.TotalSeconds:0.#}s.");
        }
    }

    private async Task VerifyAsync(HttpClient http, string file, string asset, CancellationToken cancellationToken)
    {
        string sums;

        try
        {
            sums = await WithStallLimit(async token =>
            {
                using var response = await http.GetAsync($"{ReleaseUrl}/{SumsFile}", token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(token);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No checksum means no proof, so this fails closed.
            throw new YtDlpVerificationException(asset, $"couldn't fetch {SumsFile}", ex);
        }

        var expected = ExpectedHash(sums, asset)
            ?? throw new YtDlpVerificationException(asset, $"{SumsFile} has no entry for it");

        string actual;
        await using (var stream = File.OpenRead(file))
            actual = Convert.ToHexStringLower(await SHA512.HashDataAsync(stream, cancellationToken));

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new YtDlpVerificationException(asset, "its SHA-512 doesn't match the published one");
    }

    internal static string? ExpectedHash(string sums, string asset)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2 && string.Equals(parts[^1], asset, StringComparison.Ordinal))
                return parts[0];
        }

        return null;
    }

    private static void Unpack(string archive, string destination)
    {
        var target = Path.GetDirectoryName(destination)!;
        var staging = target + ".unpacking";

        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

        try
        {
            ZipFile.ExtractToDirectory(archive, staging);

            // Swap the whole folder in: a half-filled one would still have a launcher in it.
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
