using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI;

namespace YuiToIssho;

internal sealed class NekoBridgeDiscoveryPublisher
{
    private const int DiscoveryVersion = 2;
    private const int ProtocolVersion = 2;
    private const int MaximumDescriptorBytes = 4 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string configPath;
    private readonly IMonitor monitor;
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private readonly string startedAtUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private string? publishedPath;
    private string? temporaryPath;
    private int cleanedUp;
    private bool permissionWarningLogged;

    public NekoBridgeDiscoveryPublisher(string modDirectoryPath, IMonitor monitor)
    {
        this.configPath = Path.GetFullPath(Path.Combine(modDirectoryPath, "config.json"));
        this.monitor = monitor;
        AppDomain.CurrentDomain.ProcessExit += this.OnProcessExit;
    }

    public bool Publish(string endpoint)
    {
        if (this.publishedPath is not null)
            return true;

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new DiscoveryDescriptor
            {
                InstanceId = this.instanceId,
                ProcessId = Environment.ProcessId,
                CreatedAtUtc = this.startedAtUtc,
                Endpoint = endpoint,
                ConfigPath = this.configPath,
            },
            JsonOptions
        );
        if (payload.Length > MaximumDescriptorBytes)
        {
            this.monitor.Log("HY-NEKO-DISCOVERY-PUBLISH-FAILED: Descriptor exceeded its byte budget.", LogLevel.Error);
            return false;
        }

        Exception? lastError = null;
        foreach (DiscoveryDirectory directory in GetDiscoveryDirectories())
        {
            try
            {
                string targetDirectory = this.EnsureSafeDirectory(directory);
                string targetPath = Path.Combine(targetDirectory, $"{this.instanceId}.json");
                string temporaryPath = Path.Combine(targetDirectory, $"{this.instanceId}.{Guid.NewGuid():N}.tmp");
                this.temporaryPath = temporaryPath;
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                }
                this.TryRestrictPermissions(temporaryPath, 0x180);
                File.Move(temporaryPath, targetPath);
                this.temporaryPath = null;
                this.publishedPath = targetPath;
                this.monitor.Log("HY-AGENT-GATEWAY-DISCOVERY-PUBLISHED: Published one local WebSocket descriptor.", LogLevel.Info);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                lastError = ex;
                this.DeleteOwnedFile(this.temporaryPath);
                this.temporaryPath = null;
            }
        }

        string errorType = lastError?.GetType().Name ?? "UnsupportedPlatform";
        this.monitor.Log($"HY-NEKO-DISCOVERY-PUBLISH-FAILED: Bridge remains available after {errorType}.", LogLevel.Error);
        return false;
    }

    public void Cleanup()
    {
        if (Interlocked.Exchange(ref this.cleanedUp, 1) != 0)
            return;
        AppDomain.CurrentDomain.ProcessExit -= this.OnProcessExit;
        this.DeleteOwnedFile(this.temporaryPath);
        this.DeleteOwnedFile(this.publishedPath);
        this.temporaryPath = null;
        this.publishedPath = null;
    }

    private void OnProcessExit(object? sender, EventArgs e) => this.Cleanup();

    private static IReadOnlyList<DiscoveryDirectory> GetDiscoveryDirectories()
    {
        var directories = new List<DiscoveryDirectory>();
        if (OperatingSystem.IsWindows())
        {
            AddAbsoluteRoot(directories, Environment.GetEnvironmentVariable("LOCALAPPDATA"), "YuiToIssho", "agent-gateway-discovery", "v2");
            return directories;
        }
        if (OperatingSystem.IsMacOS())
        {
            AddAbsoluteRoot(directories, Environment.GetEnvironmentVariable("HOME"), "Library", "Application Support", "YuiToIssho", "agent-gateway-discovery", "v2");
            return directories;
        }

        AddAbsoluteRoot(directories, Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), "yuitoissho", "agent-gateway-discovery", "v2");
        AddAbsoluteRoot(directories, Environment.GetEnvironmentVariable("XDG_STATE_HOME"), "yuitoissho", "agent-gateway-discovery", "v2");
        string? home = Environment.GetEnvironmentVariable("HOME");
        AddAbsoluteRoot(directories, home, ".local", "state", "yuitoissho", "agent-gateway-discovery", "v2");
        return directories;
    }

    private static void AddAbsoluteRoot(List<DiscoveryDirectory> directories, string? root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            return;
        string normalizedRoot = Path.GetFullPath(root);
        if (directories.Any(existing => string.Equals(existing.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && existing.Segments.SequenceEqual(segments, StringComparer.Ordinal)))
            return;
        directories.Add(new DiscoveryDirectory(normalizedRoot, segments));
    }

    private string EnsureSafeDirectory(DiscoveryDirectory directory)
    {
        DirectoryInfo current = new(directory.Root);
        if (!current.Exists || IsReparsePoint(current))
            throw new IOException("Discovery root is missing or redirected.");

        foreach (string segment in directory.Segments)
        {
            current = Directory.CreateDirectory(Path.Combine(current.FullName, segment));
            if (IsReparsePoint(current))
                throw new IOException("Discovery directory is redirected.");
            this.TryRestrictPermissions(current.FullName, 0x1C0);
        }
        return current.FullName;
    }

    private static bool IsReparsePoint(FileSystemInfo value) => (value.Attributes & FileAttributes.ReparsePoint) != 0;

    private void TryRestrictPermissions(string path, uint mode)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            if (Chmod(path, mode) == 0)
                return;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            this.LogPermissionWarning(ex.GetType().Name);
            return;
        }
        this.LogPermissionWarning("NativeError");
    }

    private void LogPermissionWarning(string errorType)
    {
        if (this.permissionWarningLogged)
            return;
        this.permissionWarningLogged = true;
        this.monitor.Log($"HY-NEKO-DISCOVERY-PERMISSION: Descriptor permissions remained platform-default after {errorType}.", LogLevel.Warn);
    }

    [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
    private static extern int Chmod(string path, uint mode);

    private void DeleteOwnedFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.monitor.Log($"HY-NEKO-DISCOVERY-CLEANUP: Owned descriptor remained after {ex.GetType().Name}.", LogLevel.Warn);
        }
    }

    private sealed record DiscoveryDirectory(string Root, IReadOnlyList<string> Segments);

    private sealed class DiscoveryDescriptor
    {
        [JsonPropertyName("discovery_version")]
        public int DiscoveryVersion { get; init; } = NekoBridgeDiscoveryPublisher.DiscoveryVersion;

        [JsonPropertyName("protocol_version")]
        public int ProtocolVersion { get; init; } = NekoBridgeDiscoveryPublisher.ProtocolVersion;

        [JsonPropertyName("instance_id")]
        public string InstanceId { get; init; } = string.Empty;

        [JsonPropertyName("process_id")]
        public int ProcessId { get; init; }

        [JsonPropertyName("created_at_utc")]
        public string CreatedAtUtc { get; init; } = string.Empty;

        [JsonPropertyName("transport")]
        public string Transport { get; init; } = "websocket";

        [JsonPropertyName("endpoint")]
        public string Endpoint { get; init; } = string.Empty;

        [JsonPropertyName("config_path")]
        public string ConfigPath { get; init; } = string.Empty;
    }
}
