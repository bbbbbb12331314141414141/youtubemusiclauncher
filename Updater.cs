using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;

public static class Updater
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public static async Task<string?> CheckForUpdatesAsync(bool showNoUpdate = false)
    {
        try
        {
            if (AppInfo.UpdateManifestUrl.Contains("bbbbbb12331314141414141", StringComparison.Ordinal))
                return "Update service is not configured yet.";

            Http.DefaultRequestHeaders.UserAgent.ParseAdd("YouTubeLauncher-Updater/2.0");
            var manifest = await Http.GetFromJsonAsync<UpdateManifest>(AppInfo.UpdateManifestUrl);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
                return "Update manifest was invalid.";

            if (!Version.TryParse(manifest.Version, out var remote))
                return "Update version was invalid.";

            if (remote <= AppInfo.Version)
                return showNoUpdate ? $"You are up to date ({AppInfo.VersionString})." : null;

            var rid = GetRid();
            var asset = manifest.Assets?.FirstOrDefault(a =>
                a.RuntimeIdentifier.Equals(rid, StringComparison.OrdinalIgnoreCase));

            if (asset is null || string.IsNullOrWhiteSpace(asset.Url))
                return $"Version {manifest.Version} is available, but no build exists for {rid}.";

            if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var assetUri) || assetUri.Scheme != Uri.UriSchemeHttps)
                return "Update manifest contained an invalid download URL.";

            var expectedSha256 = asset.Sha256;
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                var checksumUri = new Uri(assetUri.AbsoluteUri.Replace(".zip", ".sha256", StringComparison.OrdinalIgnoreCase));
                var checksumText = await Http.GetStringAsync(checksumUri);
                expectedSha256 = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }

            if (expectedSha256 is null || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
                return "Update did not include a valid SHA-256 checksum.";

            var tempZip = Path.Combine(Path.GetTempPath(), $"YouTubeLauncher-{remote}.zip");
            await using (var input = await Http.GetStreamAsync(assetUri))
            await using (var output = File.Create(tempZip))
                await input.CopyToAsync(output);

            if (!string.IsNullOrWhiteSpace(asset.Sha256))
            {
                var hash = await SHA256.HashDataAsync(File.OpenRead(tempZip));
                var actual = Convert.ToHexString(hash);
                if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Update SHA-256 did not match the manifest.");
            }

            var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var pid = Environment.ProcessId;

            if (OperatingSystem.IsWindows())
            {
                var script = Path.Combine(Path.GetTempPath(), $"ytl-update-{pid}.ps1");
                await File.WriteAllTextAsync(script, BuildPowerShellUpdater(tempZip, installDir, pid));
                Process.Start(new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            else
            {
                var script = Path.Combine(Path.GetTempPath(), $"ytl-update-{pid}.sh");
                await File.WriteAllTextAsync(script, BuildBashUpdater(tempZip, installDir, pid));
                Process.Start(new ProcessStartInfo("/bin/sh", $"\"{script}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }

            Environment.Exit(0);
            return $"Updating to {manifest.Version}...";
        }
        catch (Exception ex)
        {
            return $"Update check failed: {ex.Message}";
        }
    }

    private static string GetRid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "win-arm64",
                Architecture.X86 => "win-x86",
                _ => "win-x64"
            };

        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "linux-arm64",
            Architecture.X86 => "linux-x86",
            _ => "linux-x64"
        };
    }

    private static string BuildPowerShellUpdater(string zip, string installDir, int pid)
    {
        var safeZip = zip.Replace("\"", "\"\"");
        var safeInstallDir = installDir.Replace("\"", "\"\"");

        return
            "$ErrorActionPreference = \"Stop\"\n" +
            "$launcherPid = " + pid + "\n" +
            "while (Get-Process -Id $launcherPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }\n" +
            "$temp = Join-Path $env:TEMP \"ytl-update-" + pid + "-extract\"\n" +
            "if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }\n" +
            "New-Item -ItemType Directory -Path $temp | Out-Null\n" +
            "Expand-Archive -LiteralPath \"" + safeZip + "\" -DestinationPath $temp -Force\n" +
            "Copy-Item -Path (Join-Path $temp \"*\") -Destination \"" + safeInstallDir + "\" -Recurse -Force\n" +
            "Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue\n" +
            "Remove-Item \"" + safeZip + "\" -Force -ErrorAction SilentlyContinue\n" +
            "Start-Process -FilePath (Join-Path \"" + safeInstallDir + "\" \"YouTubeLauncher.exe\")\n";
    }

    private static string BuildBashUpdater(string zip, string installDir, int pid)
    {
        var safeZip = EscapeSh(zip);
        var safeInstallDir = EscapeSh(installDir);
        var launcher = EscapeSh(Path.Combine(installDir, "YouTubeLauncher"));

        return
            "#!/bin/sh\n" +
            "set -eu\n" +
            "while kill -0 " + pid + " 2>/dev/null; do sleep 0.25; done\n" +
            "TEMP_DIR=\"${TMPDIR:-/tmp}/ytl-update-" + pid + "\"\n" +
            "rm -rf \"$TEMP_DIR\"\n" +
            "mkdir -p \"$TEMP_DIR\"\n" +
            "unzip -q -o " + safeZip + " -d \"$TEMP_DIR\"\n" +
            "cp -a \"$TEMP_DIR\"/. " + safeInstallDir + "/\n" +
            "rm -rf \"$TEMP_DIR\"\n" +
            "rm -f " + safeZip + "\n" +
            "chmod +x " + launcher + " 2>/dev/null || true\n" +
            "nohup " + launcher + " >/dev/null 2>&1 &\n";
    }

    private static string EscapeSh(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public sealed class UpdateManifest
    {
        public string? Version { get; set; }
        public List<UpdateAsset>? Assets { get; set; }
    }

    public sealed class UpdateAsset
    {
        public string RuntimeIdentifier { get; set; } = "";
        public string Url { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
}
