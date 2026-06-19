using Velopack;
using Velopack.Sources;

namespace WhisperTrigger;

// Thin wrapper around Velopack's UpdateManager pointed at our GitHub releases.
// Velopack can only self-update an installed build, so every call is a no-op when
// running from a dev/bin folder (IsInstalled == false).
sealed class Updater
{
    private const string RepoUrl = "https://github.com/modarken/whisper-trigger";

    private readonly UpdateManager _mgr = new(new GithubSource(RepoUrl, null, false));

    public bool IsInstalled => _mgr.IsInstalled;

    // Returns the available update, or null if none / not an installed build.
    public async Task<UpdateInfo?> CheckAsync()
        => _mgr.IsInstalled ? await _mgr.CheckForUpdatesAsync() : null;

    public Task DownloadAsync(UpdateInfo info) => _mgr.DownloadUpdatesAsync(info);

    // Applies the downloaded update and relaunches the app.
    public void ApplyAndRestart(UpdateInfo info) => _mgr.ApplyUpdatesAndRestart(info.TargetFullRelease);
}
