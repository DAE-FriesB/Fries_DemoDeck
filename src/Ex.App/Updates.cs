using Velopack;
using Velopack.Sources;

namespace Ex.App;

/// <summary>
/// Updates through Velopack, from the GitHub releases of the project. Only active in an installed copy:
/// in a development build (dotnet run, Visual Studio) every call is a no-op.
/// </summary>
public sealed class Updates
{
    /// <summary>Where releases are published (see .github/workflows/release.yml).</summary>
    public const string RepositoryUrl = "https://github.com/DAE-FriesB/Fries_DemoDeck";

    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
    private UpdateInfo? _available;

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>The installed version, or null in a development build.</summary>
    public string? CurrentVersion => _manager.IsInstalled ? _manager.CurrentVersion?.ToString() : null;

    /// <summary>
    /// Checks the releases and downloads a newer version in the background. Returns its version number, or null when
    /// there is none, the app is not installed, or GitHub cannot be reached (no network, private repository).
    /// </summary>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (!_manager.IsInstalled)
            return null;
        try
        {
            _available = await _manager.CheckForUpdatesAsync();
            if (_available == null)
                return null;
            await _manager.DownloadUpdatesAsync(_available);
            return _available.TargetFullRelease.Version.ToString();
        }
        catch (Exception)
        {
            _available = null;
            return null; // an update check must never get in the way of using the app
        }
    }

    /// <summary>Closes the app, installs the downloaded version and starts it again.</summary>
    public void RestartToUpdate()
    {
        if (_available != null)
            _manager.ApplyUpdatesAndRestart(_available);
    }
}
