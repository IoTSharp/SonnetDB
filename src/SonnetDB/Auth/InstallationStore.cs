namespace SonnetDB.Auth;

/// <summary>
/// 首次安装元数据存储。负责判断服务器是否已完成初始化，并持久化服务器标识、组织等安装信息。
/// </summary>
internal sealed class InstallationStore
{
    private readonly string _systemDirectory;
    private readonly string _filePath;
    private readonly Lock _lock = new();
    private InstallationFile _state;

    /// <summary>
    /// 在指定系统目录下打开（或初始化）安装信息存储。文件位于 <c>{systemDirectory}/installation.json</c>。
    /// </summary>
    public InstallationStore(string systemDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(systemDirectory);
        Directory.CreateDirectory(systemDirectory);
        _systemDirectory = systemDirectory;
        _filePath = Path.Combine(systemDirectory, "installation.json");
        _state = AtomicJsonFile.Read(_filePath, AuthJsonContext.Default.InstallationFile, () => new InstallationFile());
    }

    /// <summary>获取当前安装状态快照。</summary>
    public InstallationStatusSnapshot GetStatus(int userCount, int databaseCount)
    {
        lock (_lock)
        {
            var needsSetup = NeedsSetupCore(userCount);
            return new InstallationStatusSnapshot(
                needsSetup,
                GetSuggestedServerId(),
                string.IsNullOrWhiteSpace(_state.ServerId) ? null : _state.ServerId,
                string.IsNullOrWhiteSpace(_state.Organization) ? null : _state.Organization,
                userCount,
                databaseCount);
        }
    }

    /// <summary>
    /// 完成首次初始化：写入安装元数据，并要求调用方提前完成管理员用户与 token 创建。
    /// </summary>
    public InstallationBootstrapResult CompleteInitialization(
        string serverId,
        string organization,
        string adminUserName,
        string initialTokenId,
        int userCount,
        int databaseCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverId);
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(adminUserName);
        ArgumentException.ThrowIfNullOrEmpty(initialTokenId);

        var normalizedServerId = NormalizeServerId(serverId);
        var normalizedOrganization = NormalizeOrganization(organization);
        var normalizedAdminUser = adminUserName.Trim().ToLowerInvariant();

        lock (_lock)
        {
            if (!NeedsSetupCore(userCount))
            {
                throw new InvalidOperationException("SonnetDB Server 已完成初始化。");
            }

            _state = new InstallationFile
            {
                ServerId = normalizedServerId,
                Organization = normalizedOrganization,
                AdminUserName = normalizedAdminUser,
                InitialTokenId = initialTokenId.Trim(),
                InitializedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            Persist();

            return new InstallationBootstrapResult(
                normalizedServerId,
                normalizedOrganization,
                normalizedAdminUser,
                initialTokenId.Trim(),
                userCount,
                databaseCount);
        }
    }

    private bool NeedsSetupCore(int userCount)
    {
        return userCount <= 0
            || !HasInstallationMetadata()
            || !Directory.EnumerateFileSystemEntries(_systemDirectory).Any();
    }

    private bool HasInstallationMetadata()
    {
        return !string.IsNullOrWhiteSpace(_state.ServerId)
            && !string.IsNullOrWhiteSpace(_state.Organization)
            && !string.IsNullOrWhiteSpace(_state.AdminUserName)
            && !string.IsNullOrWhiteSpace(_state.InitialTokenId);
    }

    private void Persist()
    {
        AtomicJsonFile.Write(_filePath, _state, AuthJsonContext.Default.InstallationFile);
    }

    internal static string NormalizeServerId(string serverId)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverId);
        var trimmed = serverId.Trim();
        if (trimmed.Length is < 3 or > 64)
        {
            throw new ArgumentException("服务器 ID 长度必须在 3 到 64 个字符之间。", nameof(serverId));
        }

        foreach (var ch in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            {
                throw new ArgumentException("服务器 ID 仅允许 [A-Za-z0-9_-]。", nameof(serverId));
            }
        }

        return trimmed;
    }

    internal static string NormalizeOrganization(string organization)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        var trimmed = organization.Trim();
        if (trimmed.Length is < 2 or > 80)
        {
            throw new ArgumentException("组织名称长度必须在 2 到 80 个字符之间。", nameof(organization));
        }

        return trimmed;
    }

    internal static string GetSuggestedServerId()
    {
        return HostServerIdGenerator.GetSuggestedServerId();
    }
}

/// <summary>安装状态快照。</summary>
/// <param name="NeedsSetup">是否需要首次安装。</param>
/// <param name="SuggestedServerId">首次安装时推荐的服务器 ID。</param>
/// <param name="ServerId">当前服务器 ID。</param>
/// <param name="Organization">当前组织名称。</param>
/// <param name="UserCount">当前用户数。</param>
/// <param name="DatabaseCount">当前数据库数。</param>
internal readonly record struct InstallationStatusSnapshot(
    bool NeedsSetup,
    string SuggestedServerId,
    string? ServerId,
    string? Organization,
    int UserCount,
    int DatabaseCount);

/// <summary>首次安装完成后的结果快照。</summary>
/// <param name="ServerId">服务器 ID。</param>
/// <param name="Organization">组织名称。</param>
/// <param name="AdminUserName">管理员用户名。</param>
/// <param name="InitialTokenId">初始 Bearer Token 的 token id。</param>
/// <param name="UserCount">初始化完成后的用户数。</param>
/// <param name="DatabaseCount">初始化完成后的数据库数。</param>
internal readonly record struct InstallationBootstrapResult(
    string ServerId,
    string Organization,
    string AdminUserName,
    string InitialTokenId,
    int UserCount,
    int DatabaseCount);
