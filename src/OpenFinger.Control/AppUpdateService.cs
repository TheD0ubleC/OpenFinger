using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace OpenFinger.Control;

public sealed class AppUpdateCheckResult
{
    public bool Success { get; init; }
    public bool HasUpdate { get; init; }
    public string CurrentVersion { get; init; } = "0.0.0";
    public string LatestVersion { get; init; } = string.Empty;
    public string LatestTag { get; init; } = string.Empty;
    public string ReleaseName { get; init; } = string.Empty;
    public string ReleaseNotes { get; init; } = string.Empty;
    public string ReleasePageUrl { get; init; } = AppUpdateService.ReleasePageUrl;
    public string AssetName { get; init; } = string.Empty;
    public string AssetDownloadUrl { get; init; } = string.Empty;
    public DateTimeOffset? PublishedAtUtc { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class AppPreparedUpdatePackage
{
    public string Version { get; init; } = string.Empty;
    public string ZipPath { get; init; } = string.Empty;
    public string ExtractedDirectory { get; init; } = string.Empty;
    public string SourceDirectory { get; init; } = string.Empty;
}

public static class AppUpdateService
{
    public const string RepositoryOwner = "TheD0ubleC";
    public const string RepositoryName = "OpenFinger";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/TheD0ubleC/OpenFinger/releases/latest";
    public const string ReleasePageUrl = "https://github.com/TheD0ubleC/OpenFinger/releases";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static string GetCurrentVersionText()
    {
        var assembly = typeof(AppUpdateService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var normalized = NormalizeVersionText(informational);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        var version = assembly.GetName().Version?.ToString();
        return NormalizeVersionText(version);
    }

    public static string GetUpdatesRootDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenFinger",
            "updates");
    }

    public static string NormalizeVersionText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.0.0";
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var plusIndex = normalized.IndexOf('+');
        if (plusIndex >= 0)
        {
            normalized = normalized[..plusIndex];
        }

        return string.IsNullOrWhiteSpace(normalized) ? "0.0.0" : normalized;
    }

    public static int CompareVersions(string? left, string? right)
    {
        var leftParts = ParseVersionParts(left);
        var rightParts = ParseVersionParts(right);
        var count = Math.Max(leftParts.Count, rightParts.Count);
        for (var index = 0; index < count; index++)
        {
            var leftValue = index < leftParts.Count ? leftParts[index] : 0;
            var rightValue = index < rightParts.Count ? rightParts[index] : 0;
            var comparison = leftValue.CompareTo(rightValue);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        var leftPre = ExtractPrerelease(left);
        var rightPre = ExtractPrerelease(right);
        if (string.IsNullOrWhiteSpace(leftPre) && string.IsNullOrWhiteSpace(rightPre))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(leftPre))
        {
            return 1;
        }

        if (string.IsNullOrWhiteSpace(rightPre))
        {
            return -1;
        }

        return string.Compare(leftPre, rightPre, StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatPublishedText(DateTimeOffset? publishedAtUtc)
    {
        if (publishedAtUtc is null)
        {
            return "未知时间";
        }

        return publishedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static async Task<AppUpdateCheckResult> CheckLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentVersionText();
        var preferSelfContained = IsCurrentAppSelfContained();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            var latestTag = GetString(root, "tag_name");
            var latestVersion = NormalizeVersionText(latestTag);
            var asset = SelectPrimaryAsset(root, preferSelfContained);
            var publishedAt = TryGetDateTimeOffset(root, "published_at");
            var hasUpdate = CompareVersions(latestVersion, currentVersion) > 0;

            return new AppUpdateCheckResult
            {
                Success = true,
                HasUpdate = hasUpdate,
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                LatestTag = latestTag,
                ReleaseName = GetString(root, "name"),
                ReleaseNotes = GetString(root, "body"),
                ReleasePageUrl = GetString(root, "html_url", ReleasePageUrl),
                AssetName = asset.name,
                AssetDownloadUrl = asset.downloadUrl,
                PublishedAtUtc = publishedAt,
                Message = hasUpdate
                    ? $"发现新版本 {latestVersion}"
                    : $"当前已是最新版本 ({currentVersion})"
            };
        }
        catch (Exception ex)
        {
            return new AppUpdateCheckResult
            {
                Success = false,
                CurrentVersion = currentVersion,
                Message = $"检查更新失败：{ex.Message}"
            };
        }
    }

    public static async Task<AppPreparedUpdatePackage> PrepareUpdatePackageAsync(AppUpdateCheckResult update, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.AssetDownloadUrl) || string.IsNullOrWhiteSpace(update.AssetName))
        {
            throw new InvalidOperationException("当前版本没有可下载的发行包。");
        }

        var safeVersion = MakeSafePathSegment(string.IsNullOrWhiteSpace(update.LatestVersion) ? update.LatestTag : update.LatestVersion);
        var rootDirectory = Path.Combine(GetUpdatesRootDirectory(), safeVersion);
        var zipPath = Path.Combine(rootDirectory, update.AssetName);
        var extractDirectory = Path.Combine(rootDirectory, "content");

        Directory.CreateDirectory(rootDirectory);

        var tempZipPath = zipPath + ".download";
        using (var response = await HttpClient.GetAsync(update.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(tempZipPath);
            await input.CopyToAsync(output, cancellationToken);
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        File.Move(tempZipPath, zipPath);

        if (Directory.Exists(extractDirectory))
        {
            Directory.Delete(extractDirectory, recursive: true);
        }

        ZipFile.ExtractToDirectory(zipPath, extractDirectory, overwriteFiles: true);
        var sourceDirectory = ResolveUpdateSourceDirectory(extractDirectory);

        return new AppPreparedUpdatePackage
        {
            Version = safeVersion,
            ZipPath = zipPath,
            ExtractedDirectory = extractDirectory,
            SourceDirectory = sourceDirectory
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"OpenFinger-Control/{GetCurrentVersionText()}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static List<int> ParseVersionParts(string? value)
    {
        var normalized = NormalizeVersionText(value);
        var core = normalized.Split('-', 2)[0];
        return core
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0)
            .ToList();
    }

    private static string ExtractPrerelease(string? value)
    {
        var normalized = NormalizeVersionText(value);
        var hyphenIndex = normalized.IndexOf('-');
        return hyphenIndex >= 0 ? normalized[(hyphenIndex + 1)..] : string.Empty;
    }

    private static string MakeSafePathSegment(string value)
    {
        var safe = value;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(safe) ? "latest" : safe;
    }

    private static string ResolveUpdateSourceDirectory(string extractDirectory)
    {
        if (File.Exists(Path.Combine(extractDirectory, "OpenFinger.Control.exe")))
        {
            return extractDirectory;
        }

        var directSubdirectories = Directory.GetDirectories(extractDirectory);
        if (directSubdirectories.Length == 1
            && File.Exists(Path.Combine(directSubdirectories[0], "OpenFinger.Control.exe")))
        {
            return directSubdirectories[0];
        }

        var nested = Directory
            .EnumerateFiles(extractDirectory, "OpenFinger.Control.exe", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (!string.IsNullOrWhiteSpace(nested))
        {
            return nested;
        }

        throw new InvalidOperationException("更新包里没有找到 OpenFinger.Control.exe。");
    }

    private static string GetString(JsonElement element, string propertyName, string fallback = "")
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        var text = GetString(element, propertyName);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }

    private static bool IsCurrentAppSelfContained()
    {
        var baseDirectory = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(baseDirectory, "hostfxr.dll"))
               || File.Exists(Path.Combine(baseDirectory, "coreclr.dll"))
               || File.Exists(Path.Combine(baseDirectory, "clrjit.dll"));
    }

    private static (string name, string downloadUrl) SelectPrimaryAsset(JsonElement root, bool preferSelfContained)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, string.Empty);
        }

        var preferredLabel = preferSelfContained ? "-self-contained.zip" : "-dotnet.zip";
        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            var url = GetString(asset, "browser_download_url");
            if (name.StartsWith("OpenFinger-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(preferredLabel, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(url))
            {
                return (name, url);
            }
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            var url = GetString(asset, "browser_download_url");
            if (preferSelfContained
                && name.StartsWith("OpenFinger-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && name.Contains("self-contained", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(url))
            {
                return (name, url);
            }

            if (!preferSelfContained
                && name.StartsWith("OpenFinger-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("self-contained", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("setup", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(url))
            {
                return (name, url);
            }
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            var url = GetString(asset, "browser_download_url");
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
            {
                return (name, url);
            }
        }

        return (string.Empty, string.Empty);
    }
}
