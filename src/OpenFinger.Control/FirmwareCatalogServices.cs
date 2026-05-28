using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenFinger.Control;

public sealed class FirmwareBundleFileManifest
{
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("offset")]
    public string Offset { get; set; } = "0x0000";
}

public sealed class FirmwareBundleProfileManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "官方推荐预设";

    [JsonPropertyName("thumb_pin")]
    public int ThumbPin { get; set; }

    [JsonPropertyName("index_pin")]
    public int IndexPin { get; set; } = 1;

    [JsonPropertyName("middle_pin")]
    public int MiddlePin { get; set; } = 2;

    [JsonPropertyName("ring_pin")]
    public int RingPin { get; set; } = 3;

    [JsonPropertyName("pinky_pin")]
    public int PinkyPin { get; set; } = 4;

    [JsonPropertyName("tracking_switch_pin")]
    public int TrackingSwitchPin { get; set; } = -1;

    [JsonPropertyName("tracking_switch_mode")]
    public string TrackingSwitchMode { get; set; } = "disabled";

    [JsonPropertyName("joystick_vrx_pin")]
    public int JoystickVrxPin { get; set; } = -1;

    [JsonPropertyName("joystick_vry_pin")]
    public int JoystickVryPin { get; set; } = -1;

    [JsonPropertyName("joystick_sw_pin")]
    public int JoystickSwPin { get; set; } = -1;

    [JsonPropertyName("battery_adc_pin")]
    public int BatteryAdcPin { get; set; } = -1;

    [JsonPropertyName("battery_charge_pin")]
    public int BatteryChargePin { get; set; } = -1;
}

public sealed class FirmwareBundleManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = FirmwareTargetCatalog.Esp32C3;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("report_rate_hz")]
    public int ReportRateHz { get; set; } = 30;

    [JsonPropertyName("boot_hint")]
    public string BootHint { get; set; } = string.Empty;

    [JsonPropertyName("download_base_url")]
    public string DownloadBaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("default_profile")]
    public FirmwareBundleProfileManifest DefaultProfile { get; set; } = new();

    [JsonPropertyName("bootloader")]
    public FirmwareBundleFileManifest Bootloader { get; set; } = new();

    [JsonPropertyName("partitions")]
    public FirmwareBundleFileManifest Partitions { get; set; } = new();

    [JsonPropertyName("firmware")]
    public FirmwareBundleFileManifest Firmware { get; set; } = new();

    [JsonIgnore]
    public string ManifestPath { get; set; } = string.Empty;

    [JsonIgnore]
    public string DirectoryPath => Path.GetDirectoryName(ManifestPath) ?? string.Empty;

    [JsonIgnore]
    public string Summary => $"{DisplayName} · {Version} · {ReportRateHz} Hz";
}

public static class FirmwareCatalogService
{
    private static readonly HttpClient HttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string ResolveBundledDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "FirmwarePackages"),
            Path.Combine(FirmwareTools.ResolveRepositoryRoot(), "src", "OpenFinger.Control", "FirmwarePackages")
        };

        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    public static async Task<IReadOnlyList<FirmwareBundleManifest>> LoadCatalogAsync(string sourceKind, string externalPath, string onlineCatalogUrl, CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(sourceKind) ? "bundled" : sourceKind.Trim().ToLowerInvariant();
        return normalized switch
        {
            "external" => LoadFromPath(externalPath),
            "online" => await LoadOnlineCatalogAsync(onlineCatalogUrl, cancellationToken),
            _ => LoadFromPath(ResolveBundledDirectory())
        };
    }

    public static FirmwarePackageVm ToPackageVm(FirmwareBundleManifest manifest, string sourceKind)
    {
        return new FirmwarePackageVm
        {
            Id = manifest.Id,
            DisplayName = manifest.DisplayName,
            Target = FirmwareTargetCatalog.NormalizeTarget(manifest.Target),
            Version = manifest.Version,
            ReportRateHz = manifest.ReportRateHz,
            ManifestPath = manifest.ManifestPath,
            SourceKind = sourceKind,
            ProfileName = string.IsNullOrWhiteSpace(manifest.DefaultProfile?.Name) ? "官方推荐预设" : manifest.DefaultProfile.Name,
            Summary = manifest.Summary,
            BootHint = string.IsNullOrWhiteSpace(manifest.BootHint) ? FirmwareTargetCatalog.Get(manifest.Target).BootHint : manifest.BootHint
        };
    }

    public static FirmwareBundleManifest LoadManifestOrThrow(string manifestPath)
    {
        var manifest = LoadManifestFromFile(manifestPath);
        if (manifest is null)
        {
            throw new InvalidOperationException("固件包清单无效。");
        }

        return manifest;
    }

    private static IReadOnlyList<FirmwareBundleManifest> LoadFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Array.Empty<FirmwareBundleManifest>();
        }

        var manifests = new List<FirmwareBundleManifest>();
        if (File.Exists(path))
        {
            var single = LoadManifestFromFile(path);
            if (single is not null)
            {
                manifests.Add(single);
            }

            return manifests;
        }

        if (!Directory.Exists(path))
        {
            return manifests;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(path, "manifest.json", SearchOption.AllDirectories))
        {
            var manifest = LoadManifestFromFile(manifestPath);
            if (manifest is not null)
            {
                manifests.Add(manifest);
            }
        }

        return manifests
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static FirmwareBundleManifest? LoadManifestFromFile(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifest = JsonSerializer.Deserialize<FirmwareBundleManifest>(File.ReadAllText(manifestPath, Encoding.UTF8), JsonOptions);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
        {
            return null;
        }

        manifest.Target = FirmwareTargetCatalog.NormalizeTarget(manifest.Target);
        manifest.ReportRateHz = Math.Clamp(manifest.ReportRateHz <= 0 ? FirmwareTargetCatalog.Get(manifest.Target).DefaultReportRateHz : manifest.ReportRateHz, 10, 240);
        manifest.BootHint = string.IsNullOrWhiteSpace(manifest.BootHint) ? FirmwareTargetCatalog.Get(manifest.Target).BootHint : manifest.BootHint;
        manifest.ManifestPath = manifestPath;
        manifest.DefaultProfile ??= new FirmwareBundleProfileManifest();
        manifest.Bootloader ??= new FirmwareBundleFileManifest();
        manifest.Partitions ??= new FirmwareBundleFileManifest();
        manifest.Firmware ??= new FirmwareBundleFileManifest();
        return manifest;
    }

    private static async Task<IReadOnlyList<FirmwareBundleManifest>> LoadOnlineCatalogAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Array.Empty<FirmwareBundleManifest>();
        }

        var text = await HttpClient.GetStringAsync(url, cancellationToken);
        var node = JsonNode.Parse(text);
        if (node is null)
        {
            return Array.Empty<FirmwareBundleManifest>();
        }

        if (node is JsonObject directObject && directObject["id"] is not null)
        {
            var single = await CacheRemoteManifestAsync(url, directObject, cancellationToken);
            return single is null ? Array.Empty<FirmwareBundleManifest>() : [single];
        }

        var list = new List<FirmwareBundleManifest>();
        var packagesNode = node is JsonObject catalogObject ? catalogObject["packages"] : node;
        if (packagesNode is not JsonArray packageArray)
        {
            return list;
        }

        foreach (var item in packageArray)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var manifestUrl))
            {
                var manifest = await CacheRemoteManifestAsync(manifestUrl, null, cancellationToken);
                if (manifest is not null)
                {
                    list.Add(manifest);
                }

                continue;
            }

            if (item is JsonObject objectNode)
            {
                var manifestUrlText = objectNode["manifest_url"]?.GetValue<string>() ?? objectNode["url"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(manifestUrlText))
                {
                    var manifest = await CacheRemoteManifestAsync(manifestUrlText, null, cancellationToken);
                    if (manifest is not null)
                    {
                        list.Add(manifest);
                    }

                    continue;
                }

                if (objectNode["id"] is not null)
                {
                    var manifest = await CacheRemoteManifestAsync(url, objectNode, cancellationToken);
                    if (manifest is not null)
                    {
                        list.Add(manifest);
                    }
                }
            }
        }

        return list;
    }

    private static async Task<FirmwareBundleManifest?> CacheRemoteManifestAsync(string manifestUrl, JsonObject? manifestNode, CancellationToken cancellationToken)
    {
        var manifestText = manifestNode?.ToJsonString() ?? await HttpClient.GetStringAsync(manifestUrl, cancellationToken);
        var manifest = JsonSerializer.Deserialize<FirmwareBundleManifest>(manifestText, JsonOptions);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
        {
            return null;
        }

        manifest.Target = FirmwareTargetCatalog.NormalizeTarget(manifest.Target);
        manifest.ReportRateHz = Math.Clamp(manifest.ReportRateHz <= 0 ? FirmwareTargetCatalog.Get(manifest.Target).DefaultReportRateHz : manifest.ReportRateHz, 10, 240);
        manifest.DefaultProfile ??= new FirmwareBundleProfileManifest();

        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenFinger",
            "firmware-cache",
            HashShort(manifest.Id));
        Directory.CreateDirectory(cacheRoot);

        manifest.BootHint = string.IsNullOrWhiteSpace(manifest.BootHint) ? FirmwareTargetCatalog.Get(manifest.Target).BootHint : manifest.BootHint;
        manifest.ManifestPath = Path.Combine(cacheRoot, "manifest.json");
        await File.WriteAllTextAsync(manifest.ManifestPath, manifestText, Encoding.UTF8, cancellationToken);

        var baseUrl = string.IsNullOrWhiteSpace(manifest.DownloadBaseUrl)
            ? new Uri(new Uri(manifestUrl), ".").ToString()
            : manifest.DownloadBaseUrl;
        await DownloadBundleFileAsync(baseUrl, manifest.Bootloader, cacheRoot, cancellationToken);
        await DownloadBundleFileAsync(baseUrl, manifest.Partitions, cacheRoot, cancellationToken);
        await DownloadBundleFileAsync(baseUrl, manifest.Firmware, cacheRoot, cancellationToken);
        return manifest;
    }

    private static async Task DownloadBundleFileAsync(string baseUrl, FirmwareBundleFileManifest fileManifest, string cacheRoot, CancellationToken cancellationToken)
    {
        if (fileManifest is null || string.IsNullOrWhiteSpace(fileManifest.File))
        {
            return;
        }

        var targetPath = Path.Combine(cacheRoot, fileManifest.File);
        if (File.Exists(targetPath))
        {
            return;
        }

        var fileUrl = new Uri(new Uri(baseUrl), fileManifest.File);
        var bytes = await HttpClient.GetByteArrayAsync(fileUrl, cancellationToken);
        await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
    }

    private static string HashShort(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes[..8]);
    }
}

public static class FirmwareToolClient
{
    public static string ResolveExecutable()
    {
        var repoRoot = FirmwareTools.ResolveRepositoryRoot();
        var candidates = new[]
        {
            Path.Combine(repoRoot, "build", "Debug", "openfinger_firmware_tool.exe"),
            Path.Combine(repoRoot, "build", "Release", "openfinger_firmware_tool.exe")
            ,
            Path.Combine(AppContext.BaseDirectory, "openfinger_firmware_tool.exe")
        };

        var exe = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new FileNotFoundException("没有找到 openfinger_firmware_tool.exe。");
        }

        return exe;
    }

    public static async Task<JsonObject> RunJsonAsync(params string[] arguments)
    {
        var executable = ResolveExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动固件刷写器。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdout).Trim();
        var error = (await stderr).Trim();
        var payload = string.IsNullOrWhiteSpace(output) ? error : output;
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidOperationException("固件刷写器没有返回任何内容。");
        }

        var node = JsonNode.Parse(payload) as JsonObject;
        if (node is null)
        {
            throw new InvalidOperationException(payload);
        }

        if (node["stderr"] is null && !string.IsNullOrWhiteSpace(error))
        {
            node["stderr"] = error;
        }

        return node;
    }
}
