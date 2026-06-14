# ytdlp-fetch

[![test](https://github.com/ezhalden/ytdlp-fetch/actions/workflows/test.yml/badge.svg)](https://github.com/ezhalden/ytdlp-fetch/actions/workflows/test.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-555)
![license: MIT](https://img.shields.io/badge/license-MIT-555)

Get a working `yt-dlp` from .NET without bundling it.

```csharp
var locator = new YtDlpLocator(new YtDlpLocatorOptions
{
    ToolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools"),
});

string ytdlp = await locator.LocateAsync();
// run it however you like: Process.Start, CliWrap, ...
```

It checks, in order:

1. **A path you configured** (`ConfiguredPath`). If it's set and the file isn't there, you get a
   `FileNotFoundException`, not a surprise second copy.
2. **`yt-dlp` on `PATH`.**
3. **A download** of the right build from yt-dlp's latest GitHub release into `ToolsDirectory`.

## Why not just bundle yt-dlp?

YouTube changes things often and yt-dlp ships fixes fast. A bundled copy goes stale and every fix
means a new release of *your* app. Fetching it (and letting it `-U` itself) avoids that.

## What the download does

- **Picks the right asset** for Windows (x64/arm64/x86), macOS (universal) and Linux (x64/arm64,
  glibc or musl, plus the armv7l zip, which it unpacks).
- **Checks it against `SHA2-512SUMS`** from the same release before returning it. No sums file, no
  entry, or a mismatch → `YtDlpVerificationException` and the file is deleted.
- **Never leaves half a binary behind.** It downloads to a side file and moves it into place.
- **Gives up on stalled connections** after `StallTimeout` without data (default 30s), as a
  `TimeoutException`. Slow-but-moving downloads are fine.
- **Backs off after a failure.** For `FailureBackOff` (default 5 min) further calls throw
  `YtDlpUnavailableException` straight away instead of retrying a 35 MB download every time.
  Cancelling via the token doesn't count as a failure.

## Options

| Option | Default | |
|---|---|---|
| `ToolsDirectory` | *(required)* | Where downloaded copies go. |
| `ConfiguredPath` | `null` | `Func<string?>`, read on every call, so a changed setting applies immediately. |
| `PathVariable` | process `PATH` | Override the `PATH` searched; `""` skips it. |
| `HttpClient` | shared default | Pass your own (e.g. from `IHttpClientFactory`). |
| `StallTimeout` | 30 s | Max time without receiving data. |
| `FailureBackOff` | 5 min | Wait after a failed download. |
| `Asset` | this platform's | Force a specific release asset. |

`locator.IsManaged(path)` tells you whether a path is a copy it downloaded. Only run
`yt-dlp -U` on those: brew/apt/winget installs refuse to self-update.

## macOS note

The downloaded macOS build is a large single-file app that macOS re-scans on every launch, which
can add seconds to each run. If that matters, `brew install yt-dlp` and it'll be found on `PATH`.

## Build

```bash
dotnet test
```

MIT licensed. Not affiliated with the yt-dlp project.
