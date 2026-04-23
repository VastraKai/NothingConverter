using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NothingConverter.Services;

// https://github.com/SillyTeamInc/RattedSystemsCli

public class GhReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";
    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("body")]
    public string Body { get; set; } = "";
    [JsonPropertyName("assets")]
    public List<GhReleaseAsset> Assets { get; set; } = new();
}

public class GhReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";
}

public class UpdateChecker
{
    public static string GetRepositoryUrl()
    {
        return ThisAssembly.Git.RepositoryUrl.Replace(".git", "");
    }

    public static string GetRepository()
    {
        string repoUrl = GetRepositoryUrl();
        if (repoUrl.EndsWith("/")) repoUrl = repoUrl[..^1];
        int lastSlash = repoUrl.LastIndexOf('/');
        int secondLastSlash = repoUrl.LastIndexOf('/', lastSlash - 1);
        if (secondLastSlash == -1 || lastSlash == -1 || lastSlash <= secondLastSlash)
            throw new Exception("Invalid repository URL");
        return repoUrl[(secondLastSlash + 1)..];
    }

    public static string GetCurrentTag()
    {
        return string.IsNullOrEmpty(ThisAssembly.Git.BaseTag) ? ThisAssembly.Git.Branch : ThisAssembly.Git.BaseTag;
    }

    public static async Task<GhReleaseInfo?> FetchLatestReleaseInfoAsync()
    {
        string url = $"https://api.github.com/repos/{GetRepository()}/releases/latest";

        using HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NothingConverter");

        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception("Failed to fetch latest release info from GitHub. " +
                                $"Status code: {response.StatusCode}");
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GhReleaseInfo>(responseBody);
    }

    public static async Task<UpdateInfo> IsUpdateAvailableAsync(string currentTag, GhReleaseInfo releaseInfo)
    {
        // remove v prefix and split pre-release suffix if present
        string localRaw = currentTag.TrimStart('v');
        string remoteRaw = releaseInfo.TagName.TrimStart('v');

        var localParts = localRaw.Split('-', 2);
        var remoteParts = remoteRaw.Split('-', 2);

        Version.TryParse(localParts[0], out var localVer);
        Version.TryParse(remoteParts[0], out var remoteVer);

        var isUpdateAvailable = false;

        if (remoteVer > localVer)
        {
            isUpdateAvailable = true;
        }
        else if (remoteVer == localVer)
        {
            bool localHasSuffix = localParts.Length > 1;
            bool remoteHasSuffix = remoteParts.Length > 1;

            if (localHasSuffix && !remoteHasSuffix || localHasSuffix && remoteHasSuffix && localParts[1] != remoteParts[1])
            {
                isUpdateAvailable = true;
            }
        }

        return new UpdateInfo
        {
            IsUpdateAvailable = isUpdateAvailable,
            CurrentVersion = localRaw,
            LatestVersion = remoteRaw,
            ReleaseUrl = releaseInfo.HtmlUrl,
            ReleaseNotes = releaseInfo.Body
        };
    }

    public class UpdateInfo
    {
        public bool IsUpdateAvailable { get; set; }
        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public string ReleaseUrl { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
    }
}