using System.Text.Json;

namespace SonnetDB.Studio;

/// <summary>
/// 管理 Studio 桌面连接库的磁盘持久化文件。
/// </summary>
internal sealed class StudioConnectionLibrary
{
    private const string LocalProfileId = "managed-local";
    private readonly string _filePath;
    private readonly string _managedServerUrl;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 创建连接库。
    /// </summary>
    /// <param name="filePath">连接库 JSON 文件路径。</param>
    /// <param name="managedServerUrl">托管本地 server 默认地址。</param>
    public StudioConnectionLibrary(string filePath, string managedServerUrl)
    {
        _filePath = filePath;
        _managedServerUrl = NormalizeUrl(managedServerUrl);
    }

    /// <summary>
    /// 读取连接库；文件不存在或损坏时返回默认托管本地连接。
    /// </summary>
    public async Task<StudioConnectionLibrarySnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
                return DefaultSnapshot();

            await using var stream = File.OpenRead(_filePath);
            var snapshot = await JsonSerializer.DeserializeAsync(
                stream,
                StudioBridgeJsonContext.Default.StudioConnectionLibrarySnapshot,
                cancellationToken).ConfigureAwait(false);

            return NormalizeSnapshot(snapshot);
        }
        catch (JsonException)
        {
            return DefaultSnapshot();
        }
        catch (IOException)
        {
            return DefaultSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 原子写入连接库快照。
    /// </summary>
    /// <param name="snapshot">待保存的连接库状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SaveAsync(StudioConnectionLibrarySnapshot snapshot, CancellationToken cancellationToken)
    {
        var normalized = NormalizeSnapshot(snapshot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var tempPath = _filePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    normalized,
                    StudioBridgeJsonContext.Default.StudioConnectionLibrarySnapshot,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private StudioConnectionLibrarySnapshot NormalizeSnapshot(StudioConnectionLibrarySnapshot? snapshot)
    {
        if (snapshot is null)
            return DefaultSnapshot();

        var now = Now();
        var profiles = snapshot.Profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
            .Select(profile => NormalizeProfile(profile, now))
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();

        if (profiles.All(profile => !string.Equals(profile.Id, LocalProfileId, StringComparison.OrdinalIgnoreCase)))
            profiles.Insert(0, DefaultLocalProfile(now));

        var activeProfileId = profiles.Any(profile => string.Equals(profile.Id, snapshot.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
            ? snapshot.ActiveProfileId
            : profiles[0].Id;
        return new StudioConnectionLibrarySnapshot(
            profiles.ToArray(),
            activeProfileId,
            snapshot.ActiveDatabase ?? string.Empty);
    }

    private StudioConnectionProfile NormalizeProfile(StudioConnectionProfile profile, long now)
    {
        var kind = string.Equals(profile.Kind, "remote", StringComparison.OrdinalIgnoreCase)
            ? "remote"
            : "managed-local";
        var name = string.IsNullOrWhiteSpace(profile.Name)
            ? (kind == "remote" ? "Remote" : "Managed Local")
            : profile.Name.Trim();
        var baseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl)
            ? (kind == "remote" ? _managedServerUrl : _managedServerUrl)
            : NormalizeUrl(profile.BaseUrl);

        return new StudioConnectionProfile(
            profile.Id.Trim(),
            name,
            kind,
            baseUrl,
            profile.DefaultDatabase?.Trim() ?? string.Empty,
            "current-session",
            profile.CreatedAt > 0 ? profile.CreatedAt : now,
            profile.UpdatedAt > 0 ? profile.UpdatedAt : now);
    }

    private StudioConnectionLibrarySnapshot DefaultSnapshot()
    {
        var now = Now();
        return new StudioConnectionLibrarySnapshot(
            [DefaultLocalProfile(now)],
            LocalProfileId,
            string.Empty);
    }

    private StudioConnectionProfile DefaultLocalProfile(long now)
        => new(
            LocalProfileId,
            "Managed Local",
            "managed-local",
            _managedServerUrl,
            string.Empty,
            "current-session",
            now,
            now);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string NormalizeUrl(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed == "/")
            return "/";
        return trimmed.TrimEnd('/');
    }
}
