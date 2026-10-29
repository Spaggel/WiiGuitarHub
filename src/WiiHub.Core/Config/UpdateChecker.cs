using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace WiiHub.Core.Config;

public sealed record UpdateInfo(Version Version, string Url);

public enum UpdateStatus { UpToDate, Available, Failed }

public sealed record UpdateResult(UpdateStatus Status, UpdateInfo? Update, string Message);

/// <summary>Asks GitHub for the newest release and compares it with the running version.</summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Spaggel/WiiGuitarHub/releases/latest";
    public const string ReleasesPage = "https://github.com/Spaggel/WiiGuitarHub/releases";

    /// <summary>Version of the running app (from &lt;Version&gt; in the project file).</summary>
    public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    public static async Task<UpdateResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WiiGuitarHub", CurrentVersionText));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await http.GetAsync(LatestReleaseApi, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new UpdateResult(UpdateStatus.UpToDate, null, "No releases published yet.");
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = json.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;

            if (!TryParseTag(tag, out var latest))
                return new UpdateResult(UpdateStatus.Failed, null, $"Unexpected release tag “{tag}”.");

            return latest > CurrentVersion
                ? new UpdateResult(UpdateStatus.Available, new UpdateInfo(latest, url),
                    $"Version {Format(latest)} is available (you have {CurrentVersionText}).")
                : new UpdateResult(UpdateStatus.UpToDate, null, $"You have the latest version ({CurrentVersionText}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new UpdateResult(UpdateStatus.Failed, null, $"Couldn't check for updates: {ex.Message}");
        }
    }

    /// <summary>"v1.2.3" / "1.2" → 1.2.3 / 1.2.0.</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        string t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(['-', '+']); // ignore pre-release / build suffixes
        if (dash >= 0) t = t[..dash];
        if (Version.TryParse(t, out var v) || (int.TryParse(t, out int major) && (v = new Version(major, 0)) is not null))
        {
            version = Normalize(v);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
