using System.Runtime.InteropServices;

namespace YtDlpFetch;

/// <summary>Maps the current OS and CPU to the yt-dlp release asset built for it.</summary>
public static class YtDlpAsset
{
    /// <summary>What the binary is called on disk once downloaded.</summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

    /// <exception cref="PlatformNotSupportedException">yt-dlp publishes no build for this platform.</exception>
    public static string ForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
            return For(OSPlatform.Windows, RuntimeInformation.OSArchitecture);

        if (OperatingSystem.IsMacOS())
            return For(OSPlatform.OSX, RuntimeInformation.OSArchitecture);

        if (OperatingSystem.IsLinux())
            return For(OSPlatform.Linux, RuntimeInformation.OSArchitecture, IsMusl());

        throw new PlatformNotSupportedException($"yt-dlp publishes no build for {RuntimeInformation.OSDescription}.");
    }

    /// <summary>The asset for a given platform. Exposed so every branch can be tested from one machine.</summary>
    public static string For(OSPlatform os, Architecture arch, bool musl = false)
    {
        if (os == OSPlatform.Windows)
            return arch switch
            {
                Architecture.Arm64 => "yt-dlp_arm64.exe",
                Architecture.X86 => "yt-dlp_x86.exe",
                _ => "yt-dlp.exe",
            };

        // One universal2 build for Intel and Apple silicon.
        if (os == OSPlatform.OSX)
            return "yt-dlp_macos";

        if (os != OSPlatform.Linux)
            throw new PlatformNotSupportedException($"yt-dlp publishes no build for {os}.");

        if (musl)
            return arch switch
            {
                Architecture.Arm64 => "yt-dlp_musllinux_aarch64",
                Architecture.X64 => "yt-dlp_musllinux",
                _ => throw new PlatformNotSupportedException(
                    $"yt-dlp publishes no musl build for {arch}. Install it yourself and set a configured path."),
            };

        return arch switch
        {
            Architecture.Arm64 => "yt-dlp_linux_aarch64",
            Architecture.Arm => "yt-dlp_linux_armv7l.zip",
            _ => "yt-dlp_linux",
        };
    }

    // glibc builds won't load under musl (Alpine and friends).
    private static bool IsMusl()
        => RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)
            || (Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any());
}
