using System.Runtime.InteropServices;

namespace YtDlpFetch.Tests;

public sealed class YtDlpLocatorTests : IDisposable
{
    private static readonly TimeSpan ShortStall = TimeSpan.FromMilliseconds(150);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ytdlp-fetch-{Guid.NewGuid():n}");
    private readonly FakeRelease _release = new();

    public YtDlpLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Tools => Path.Combine(_root, "tools");

    private string DownloadedPath => Path.Combine(Tools, YtDlpAsset.ExecutableName);

    // PATH defaults to empty: whether this machine has yt-dlp installed must not change the result.
    private YtDlpLocator Locator(
        Func<string?>? configured = null,
        string path = "",
        string? asset = null,
        TimeSpan? stall = null,
        TimeProvider? time = null)
        => new(new YtDlpLocatorOptions
        {
            ToolsDirectory = Tools,
            ConfiguredPath = configured,
            PathVariable = path,
            HttpClient = _release.Client(),
            Asset = asset,
            StallTimeout = stall ?? TimeSpan.FromSeconds(30),
            Time = time,
        });

    private async Task<string> WriteFake(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "fake");
        return path;
    }

    private void AssertNothingLeftInTools()
        => Assert.Empty(Directory.Exists(Tools) ? Directory.GetFiles(Tools) : []);

    [Fact]
    public async Task Given_a_configured_path_that_exists_When_locating_Then_it_is_returned_without_any_download()
    {
        // Given
        var mine = await WriteFake(Path.Combine(_root, "my-yt-dlp"));

        // When
        var located = await Locator(configured: () => mine).LocateAsync();

        // Then
        Assert.Equal(mine, located);
        Assert.Empty(_release.Requests);
    }

    [Fact]
    public async Task Given_the_configured_path_changes_When_locating_again_Then_the_new_path_is_used()
    {
        // Given
        var first = await WriteFake(Path.Combine(_root, "first"));
        var second = await WriteFake(Path.Combine(_root, "second"));
        var current = first;
        var locator = Locator(configured: () => current);
        await locator.LocateAsync();

        // When
        current = second;
        var located = await locator.LocateAsync();

        // Then
        Assert.Equal(second, located);
    }

    [Fact]
    public async Task Given_a_configured_path_with_nothing_there_When_locating_Then_it_throws_and_downloads_nothing()
    {
        // Given
        var missing = Path.Combine(_root, "nowhere", "yt-dlp");

        // When
        var locate = () => Locator(configured: () => missing).LocateAsync();

        // Then
        await Assert.ThrowsAsync<FileNotFoundException>(locate);
        Assert.Empty(_release.Requests);
    }

    [Fact]
    public async Task Given_yt_dlp_on_PATH_When_locating_Then_that_copy_is_returned_without_any_download()
    {
        // Given
        var bin = Path.Combine(_root, "bin");
        var onPath = await WriteFake(Path.Combine(bin, YtDlpAsset.ExecutableName));

        // When
        var located = await Locator(path: bin).LocateAsync();

        // Then
        Assert.Equal(onPath, located);
        Assert.Empty(_release.Requests);
    }

    [Fact]
    public async Task Given_nothing_installed_When_locating_Then_the_asset_is_downloaded_verified_and_made_executable()
    {
        // Given
        var locator = Locator();

        // When
        var located = await locator.LocateAsync();

        // Then
        Assert.Equal(DownloadedPath, located);
        Assert.Equal(_release.Body, await File.ReadAllTextAsync(located));
        Assert.Contains(_release.Requests, r => r.EndsWith("/" + _release.Asset, StringComparison.Ordinal));
        Assert.Contains(_release.Requests, r => r.EndsWith("/SHA2-512SUMS", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(located).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task Given_a_copy_was_already_downloaded_When_locating_again_Then_nothing_is_fetched()
    {
        // Given
        await Locator().LocateAsync();
        var requestsAfterFirst = _release.Requests.Count;

        // When
        await Locator().LocateAsync();

        // Then
        Assert.Equal(requestsAfterFirst, _release.Requests.Count);
    }

    [Fact]
    public async Task Given_the_connection_drops_mid_download_When_locating_Then_no_partial_file_is_left_behind()
    {
        // Given
        _release.DropMidDownload = true;

        // When
        var locate = () => Locator().LocateAsync();

        // Then
        await Assert.ThrowsAsync<IOException>(locate);
        Assert.False(File.Exists(DownloadedPath));
        AssertNothingLeftInTools();
    }

    [Fact]
    public async Task Given_the_checksum_does_not_match_When_locating_Then_it_throws_and_discards_the_download()
    {
        // Given
        _release.WrongHash = true;

        // When
        var locate = () => Locator().LocateAsync();

        // Then
        var ex = await Assert.ThrowsAsync<YtDlpVerificationException>(locate);
        Assert.Equal(_release.Asset, ex.Asset);
        AssertNothingLeftInTools();
    }

    [Fact]
    public async Task Given_the_release_has_no_checksums_file_When_locating_Then_it_fails_closed()
    {
        // Given
        _release.NoSums = true;

        // When
        var locate = () => Locator().LocateAsync();

        // Then
        await Assert.ThrowsAsync<YtDlpVerificationException>(locate);
        AssertNothingLeftInTools();
    }

    [Fact]
    public async Task Given_the_checksums_file_lists_other_assets_only_When_locating_Then_it_fails_closed()
    {
        // Given
        _release.Asset = "some_other_asset";

        // When
        var locate = () => Locator(asset: YtDlpAsset.ForCurrentPlatform()).LocateAsync();

        // Then
        var ex = await Assert.ThrowsAsync<YtDlpVerificationException>(locate);
        Assert.Contains("no entry", ex.Message);
        AssertNothingLeftInTools();
    }

    [Fact]
    public async Task Given_a_zip_asset_When_locating_Then_it_is_unpacked_into_its_own_folder_and_the_zip_removed()
    {
        // Given
        const string zip = "yt-dlp_linux_armv7l.zip";
        _release.Asset = zip;
        _release.ZipEntries = new()
        {
            ["yt-dlp_linux_armv7l"] = "#!launcher",
            ["_internal/base_library.zip"] = "payload",
        };

        // When
        var located = await Locator(asset: zip).LocateAsync();

        // Then
        var folder = Path.Combine(Tools, "yt-dlp_linux_armv7l");
        Assert.Equal(Path.Combine(folder, "yt-dlp_linux_armv7l"), located);
        Assert.Equal("#!launcher", await File.ReadAllTextAsync(located));
        Assert.True(File.Exists(Path.Combine(folder, "_internal", "base_library.zip")));
        AssertNothingLeftInTools();
    }

    [Fact]
    public async Task Given_the_server_never_answers_When_locating_Then_it_times_out_instead_of_hanging()
    {
        // Given
        _release.HangOnConnect = true;

        // When
        var locate = () => Locator(stall: ShortStall).LocateAsync();

        // Then
        await Assert.ThrowsAsync<TimeoutException>(locate);
        AssertNothingLeftInTools();
    }

    [Fact]
    public async Task Given_the_body_goes_quiet_mid_download_When_locating_Then_it_times_out_and_leaves_nothing()
    {
        // Given
        _release.HangMidBody = true;

        // When
        var locate = () => Locator(stall: ShortStall).LocateAsync();

        // Then
        await Assert.ThrowsAsync<TimeoutException>(locate);
        AssertNothingLeftInTools();
    }

    [Fact]
    public async Task Given_the_checksum_fetch_hangs_When_locating_Then_verification_fails_with_the_timeout_inside()
    {
        // Given
        _release.HangOnSums = true;

        // When
        var locate = () => Locator(stall: ShortStall).LocateAsync();

        // Then
        var ex = await Assert.ThrowsAsync<YtDlpVerificationException>(locate);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task Given_the_caller_cancels_When_locating_Then_it_stays_a_cancel_and_earns_no_back_off()
    {
        // Given
        _release.HangOnConnect = true;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // When
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Locator().LocateAsync(cts.Token));
        _release.HangOnConnect = false;
        var located = await Locator().LocateAsync();

        // Then
        Assert.True(File.Exists(located));
    }

    [Fact]
    public async Task Given_a_download_just_failed_When_locating_inside_the_back_off_Then_it_does_not_retry()
    {
        // Given
        var clock = new FakeClock();
        _release.Offline = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => Locator(time: clock).LocateAsync());
        var requestsAfterFailure = _release.Requests.Count;
        _release.Offline = false;

        // When
        clock.Now += TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1);
        var locate = () => Locator(time: clock).LocateAsync();

        // Then
        var ex = await Assert.ThrowsAsync<YtDlpUnavailableException>(locate);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Equal(requestsAfterFailure, _release.Requests.Count);
    }

    [Fact]
    public async Task Given_a_download_failed_When_the_back_off_has_passed_Then_it_downloads_again()
    {
        // Given
        var clock = new FakeClock();
        _release.Offline = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => Locator(time: clock).LocateAsync());
        _release.Offline = false;

        // When
        clock.Now += TimeSpan.FromMinutes(5);
        var located = await Locator(time: clock).LocateAsync();

        // Then
        Assert.True(File.Exists(located));
    }

    [Fact]
    public async Task Given_a_downloaded_copy_When_asking_IsManaged_Then_only_that_copy_counts()
    {
        // Given
        var locator = Locator();
        var downloaded = await locator.LocateAsync();

        // When
        var ours = locator.IsManaged(downloaded);
        var brews = locator.IsManaged("/opt/homebrew/bin/yt-dlp");
        var sibling = locator.IsManaged(Tools + "-other/yt-dlp");

        // Then
        Assert.True(ours);
        Assert.False(brews);
        Assert.False(sibling);
    }
}

public sealed class YtDlpAssetTests
{
    [Theory]
    [InlineData("windows", Architecture.X64, false, "yt-dlp.exe")]
    [InlineData("windows", Architecture.Arm64, false, "yt-dlp_arm64.exe")]
    [InlineData("windows", Architecture.X86, false, "yt-dlp_x86.exe")]
    [InlineData("osx", Architecture.Arm64, false, "yt-dlp_macos")]
    [InlineData("osx", Architecture.X64, false, "yt-dlp_macos")]
    [InlineData("linux", Architecture.X64, false, "yt-dlp_linux")]
    [InlineData("linux", Architecture.Arm64, false, "yt-dlp_linux_aarch64")]
    [InlineData("linux", Architecture.Arm, false, "yt-dlp_linux_armv7l.zip")]
    [InlineData("linux", Architecture.X64, true, "yt-dlp_musllinux")]
    [InlineData("linux", Architecture.Arm64, true, "yt-dlp_musllinux_aarch64")]
    public void Given_a_platform_When_picking_the_asset_Then_the_matching_release_file_is_named(
        string os, Architecture arch, bool musl, string expected)
    {
        // Given
        var platform = OSPlatform.Create(os.ToUpperInvariant());

        // When
        var asset = YtDlpAsset.For(platform, arch, musl);

        // Then
        Assert.Equal(expected, asset);
    }

    [Fact]
    public void Given_musl_on_32_bit_arm_When_picking_the_asset_Then_it_is_unsupported()
    {
        // Given
        var platform = OSPlatform.Linux;

        // When
        var pick = () => YtDlpAsset.For(platform, Architecture.Arm, musl: true);

        // Then
        Assert.Throws<PlatformNotSupportedException>(pick);
    }

    [Fact]
    public void Given_an_unknown_os_When_picking_the_asset_Then_it_is_unsupported()
    {
        // Given
        var platform = OSPlatform.FreeBSD;

        // When
        var pick = () => YtDlpAsset.For(platform, Architecture.X64);

        // Then
        Assert.Throws<PlatformNotSupportedException>(pick);
    }
}
