using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace SonnetDB.Auth;

/// <summary>
/// 用户与 API token 的持久化存储。基于 <c>users.json</c> + 进程内 <see cref="System.Threading.Lock"/>。
/// <para>
/// 线程安全：所有改写都在 <see cref="_lock"/> 内序列化，并在写盘后重建只读快照。读热路径
/// （<see cref="TryAuthenticate"/>）走 <see cref="ConcurrentDictionary{TKey, TValue}"/> 的 token 哈希索引，
/// 命中后再加锁刷新 LastUsedAt。
/// </para>
/// </summary>
public sealed class UserStore
{
    private readonly string _filePath;
    private readonly Lock _lock = new();
    private UserFile _state;
    private readonly ConcurrentDictionary<string, (string UserName, string TokenId)> _tokenIndex
        = new(StringComparer.Ordinal);

    /// <summary>
    /// 在指定系统目录下打开（或初始化）用户存储。文件位于 <c>{systemDirectory}/users.json</c>。
    /// </summary>
    public UserStore(string systemDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(systemDirectory);
        Directory.CreateDirectory(systemDirectory);
        _filePath = Path.Combine(systemDirectory, "users.json");

        _state = AtomicJsonFile.Read(_filePath, AuthJsonContext.Default.UserFile, () => new UserFile());

        // 重建 token 索引
        foreach (var u in _state.Users)
            foreach (var t in u.Tokens)
                _tokenIndex[t.SecretHash] = (u.Name, t.Id);
    }

    /// <summary>当前用户总数。</summary>
    public int Count
    {
        get { lock (_lock) return _state.Users.Count; }
    }

    /// <summary>列出所有用户名（小写）。</summary>
    public IReadOnlyList<string> ListUserNames()
    {
        lock (_lock)
            return _state.Users.Select(u => u.Name).ToArray();
    }

    /// <summary>列出所有用户的详细摘要（用户名、是否超级、创建时间、当前 token 数）。</summary>
    /// <returns>按用户名升序排列的用户摘要。</returns>
    public IReadOnlyList<UserSummaryRecord> ListUsersDetailed()
    {
        lock (_lock)
        {
            return _state.Users
                .OrderBy(u => u.Name, StringComparer.Ordinal)
                .Select(u => new UserSummaryRecord(
                    u.Name,
                    u.IsSuperuser,
                    DateTimeOffset.FromUnixTimeMilliseconds(u.CreatedAt).UtcDateTime,
                    u.Tokens.Count))
                .ToArray();
        }
    }

    /// <summary>是否存在指定用户名（不区分大小写）。</summary>
    public bool Exists(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var key = name.ToLowerInvariant();
        lock (_lock)
            return _state.Users.Any(u => u.Name == key);
    }

    /// <summary>是否为超级用户。</summary>
    public bool IsSuperuser(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var key = name.ToLowerInvariant();
        lock (_lock)
            return _state.Users.Any(u => u.Name == key && u.IsSuperuser);
    }

    /// <summary>
    /// 创建新用户。用户名将统一转换为小写。
    /// </summary>
    /// <exception cref="InvalidOperationException">用户名已存在。</exception>
    public void CreateUser(string name, string password, bool isSuperuser)
    {
        ValidateUserName(name);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var key = name.ToLowerInvariant();
        var (salt, hash, iter) = PasswordHasher.Hash(password);

        lock (_lock)
        {
            if (_state.Users.Any(u => u.Name == key))
                throw new InvalidOperationException($"用户 '{key}' 已存在。");

            _state.Users.Add(new UserRecord
            {
                Name = key,
                Salt = Convert.ToBase64String(salt),
                PasswordHash = Convert.ToBase64String(hash),
                Iterations = iter,
                IsSuperuser = isSuperuser,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            Persist();
        }
    }

    /// <summary>
    /// 原子创建用户并导入指定 token。用于首次安装，避免用户已写入但 token 导入失败时留下半初始化状态。
    /// </summary>
    /// <param name="name">用户名。</param>
    /// <param name="password">用户密码。</param>
    /// <param name="isSuperuser">是否为超级用户。</param>
    /// <param name="token">明文 token。</param>
    /// <returns>新写入 token 的 token id。</returns>
    /// <exception cref="InvalidOperationException">用户名或 token 已存在。</exception>
    public string CreateUserWithToken(string name, string password, bool isSuperuser, string token)
    {
        ValidateUserName(name);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var key = name.ToLowerInvariant();
        var normalizedToken = NormalizeToken(token);
        var hash = ApiToken.HashHex(normalizedToken);
        var id = NewTokenId();
        var (salt, passwordHash, iterations) = PasswordHasher.Hash(password);

        lock (_lock)
        {
            if (_state.Users.Any(u => u.Name == key))
                throw new InvalidOperationException($"用户 '{key}' 已存在。");
            if (_tokenIndex.ContainsKey(hash))
                throw new InvalidOperationException("指定的 Bearer Token 已存在。");

            var user = new UserRecord
            {
                Name = key,
                Salt = Convert.ToBase64String(salt),
                PasswordHash = Convert.ToBase64String(passwordHash),
                Iterations = iterations,
                IsSuperuser = isSuperuser,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            user.Tokens.Add(new TokenRecord
            {
                Id = id,
                SecretHash = hash,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            _state.Users.Add(user);
            _tokenIndex[hash] = (key, id);
            Persist();
        }

        return id;
    }

    /// <summary>
    /// 修改密码。同时吊销该用户已颁发的所有 token。
    /// </summary>
    /// <exception cref="InvalidOperationException">用户不存在。</exception>
    public void ChangePassword(string name, string newPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(newPassword);

        var key = name.ToLowerInvariant();
        var (salt, hash, iter) = PasswordHasher.Hash(newPassword);

        lock (_lock)
        {
            var u = _state.Users.FirstOrDefault(x => x.Name == key)
                ?? throw new InvalidOperationException($"用户 '{key}' 不存在。");
            u.Salt = Convert.ToBase64String(salt);
            u.PasswordHash = Convert.ToBase64String(hash);
            u.Iterations = iter;
            // 改密后吊销所有 token
            foreach (var t in u.Tokens)
                _tokenIndex.TryRemove(t.SecretHash, out _);
            u.Tokens.Clear();
            Persist();
        }
    }

    /// <summary>
    /// 删除用户并吊销其所有 token。
    /// </summary>
    /// <returns>true 表示存在并已删除；false 表示不存在。</returns>
    public bool DeleteUser(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var key = name.ToLowerInvariant();

        lock (_lock)
        {
            var idx = _state.Users.FindIndex(x => x.Name == key);
            if (idx < 0)
                return false;
            var u = _state.Users[idx];
            foreach (var t in u.Tokens)
                _tokenIndex.TryRemove(t.SecretHash, out _);
            _state.Users.RemoveAt(idx);
            Persist();
            return true;
        }
    }

    /// <summary>
    /// 用户名 + 密码校验。
    /// </summary>
    public bool VerifyPassword(string name, string password)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(password))
            return false;
        var key = name.ToLowerInvariant();
        UserRecord? u;
        lock (_lock)
            u = _state.Users.FirstOrDefault(x => x.Name == key);
        if (u is null)
            return false;
        try
        {
            var salt = Convert.FromBase64String(u.Salt);
            var hash = Convert.FromBase64String(u.PasswordHash);
            return PasswordHasher.Verify(password, salt, hash, u.Iterations);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// 为用户颁发新 token。
    /// </summary>
    /// <returns>(明文 token, tokenId)。明文仅返回一次，调用方应立刻交付给客户端。</returns>
    /// <exception cref="InvalidOperationException">用户不存在。</exception>
    public (string Token, string TokenId) IssueToken(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var key = name.ToLowerInvariant();
        var token = ApiToken.Generate();
        var hash = ApiToken.HashHex(token);
        var id = NewTokenId();

        lock (_lock)
        {
            var u = _state.Users.FirstOrDefault(x => x.Name == key)
                ?? throw new InvalidOperationException($"用户 '{key}' 不存在。");
            u.Tokens.Add(new TokenRecord
            {
                Id = id,
                SecretHash = hash,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            _tokenIndex[hash] = (key, id);
            Persist();
        }
        return (token, id);
    }

    /// <summary>
    /// 为用户导入一个指定明文 token。用于首次安装时写入用户自定义的管理员 Bearer Token。
    /// </summary>
    /// <param name="name">用户名。</param>
    /// <param name="token">明文 token。</param>
    /// <returns>新写入 token 的 token id。</returns>
    /// <exception cref="InvalidOperationException">用户不存在或 token 已存在。</exception>
    public string ImportToken(string name, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var key = name.ToLowerInvariant();
        var normalizedToken = NormalizeToken(token);

        var hash = ApiToken.HashHex(normalizedToken);
        var id = NewTokenId();

        lock (_lock)
        {
            if (_tokenIndex.ContainsKey(hash))
            {
                throw new InvalidOperationException("指定的 Bearer Token 已存在。");
            }

            var u = _state.Users.FirstOrDefault(x => x.Name == key)
                ?? throw new InvalidOperationException($"用户 '{key}' 不存在。");

            u.Tokens.Add(new TokenRecord
            {
                Id = id,
                SecretHash = hash,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            _tokenIndex[hash] = (key, id);
            Persist();
        }

        return id;
    }

    /// <summary>
    /// 吊销指定 tokenId。
    /// </summary>
    /// <returns>true 表示存在并已吊销；false 表示不存在。</returns>
    public bool RevokeToken(string userName, string tokenId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentException.ThrowIfNullOrEmpty(tokenId);
        var key = userName.ToLowerInvariant();

        lock (_lock)
        {
            var u = _state.Users.FirstOrDefault(x => x.Name == key);
            if (u is null) return false;
            var idx = u.Tokens.FindIndex(t => t.Id == tokenId);
            if (idx < 0) return false;
            var hash = u.Tokens[idx].SecretHash;
            _tokenIndex.TryRemove(hash, out _);
            u.Tokens.RemoveAt(idx);
            Persist();
            return true;
        }
    }

    /// <summary>
    /// 仅按 token id 吊销（在所有用户中扫描）。用于 <c>REVOKE TOKEN '&lt;id&gt;'</c> SQL。
    /// </summary>
    /// <returns>true 表示存在并已吊销；false 表示 id 未命中。</returns>
    public bool RevokeTokenById(string tokenId)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenId);
        lock (_lock)
        {
            foreach (var u in _state.Users)
            {
                var idx = u.Tokens.FindIndex(t => t.Id == tokenId);
                if (idx < 0) continue;
                var hash = u.Tokens[idx].SecretHash;
                _tokenIndex.TryRemove(hash, out _);
                u.Tokens.RemoveAt(idx);
                Persist();
                return true;
            }
            return false;
        }
    }

    /// <summary>列出 token 元数据（按 user_name, created_utc 排序），永不返回明文。</summary>
    /// <param name="userName">可选用户名筛选；<c>null</c> 表示列出全部。</param>
    public IReadOnlyList<TokenSummaryRecord> ListTokensDetailed(string? userName)
    {
        string? key = userName?.ToLowerInvariant();
        lock (_lock)
        {
            var users = key is null
                ? _state.Users
                : _state.Users.Where(u => u.Name == key);
            var list = new List<TokenSummaryRecord>();
            foreach (var u in users.OrderBy(u => u.Name, StringComparer.Ordinal))
            {
                foreach (var t in u.Tokens.OrderBy(t => t.CreatedAt))
                {
                    list.Add(new TokenSummaryRecord(
                        t.Id,
                        u.Name,
                        DateTimeOffset.FromUnixTimeMilliseconds(t.CreatedAt).UtcDateTime,
                        t.LastUsedAt is null
                            ? null
                            : DateTimeOffset.FromUnixTimeMilliseconds(t.LastUsedAt.Value).UtcDateTime));
                }
            }
            return list;
        }
    }

    /// <summary>
    /// 用 Bearer token 鉴权。命中则更新 LastUsedAt。
    /// </summary>
    /// <returns>true 命中并填充 <paramref name="user"/>；false 表示未知 token。</returns>
    public bool TryAuthenticate(string token, out AuthenticatedUser user)
    {
        user = default;
        if (string.IsNullOrEmpty(token))
            return false;
        var hash = ApiToken.HashHex(token);
        if (!_tokenIndex.TryGetValue(hash, out var entry))
            return false;

        bool isSuper;
        lock (_lock)
        {
            var u = _state.Users.FirstOrDefault(x => x.Name == entry.UserName);
            if (u is null)
            {
                _tokenIndex.TryRemove(hash, out _);
                return false;
            }
            isSuper = u.IsSuperuser;
            var t = u.Tokens.FirstOrDefault(x => x.Id == entry.TokenId);
            if (t is not null)
                t.LastUsedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // LastUsedAt 不立即写盘，避免每次请求都 fsync；下次任何写操作时一同落盘。
        }
        user = new AuthenticatedUser(entry.UserName, isSuper);
        return true;
    }

    private void Persist()
    {
        // 调用方必须持有 _lock。
        AtomicJsonFile.Write(_filePath, _state, AuthJsonContext.Default.UserFile);
    }

    private static string NewTokenId()
    {
        Span<byte> buf = stackalloc byte[6];
        RandomNumberGenerator.Fill(buf);
        return "tok_" + Convert.ToHexStringLower(buf);
    }

    internal static string NormalizeToken(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var normalized = token.Trim();
        if (normalized.Length < 12 || normalized.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Bearer Token 至少 12 个字符，且不能包含空白字符。", nameof(token));
        }

        return normalized;
    }

    internal static void ValidateUserName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Length > 64)
            throw new ArgumentException("用户名最长 64 字符。", nameof(name));
        foreach (var ch in name)
        {
            // 管理端允许邮箱式登录名；SQL 中仍须用带引号的标识符引用。
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or '@' or '+'))
                throw new ArgumentException("用户名只允许 ASCII 字母、数字及 _-.@+。", nameof(name));
        }
    }
}

/// <summary>认证通过后的最小用户上下文。</summary>
/// <param name="UserName">小写用户名。</param>
/// <param name="IsSuperuser">是否超级用户。</param>
public readonly record struct AuthenticatedUser(string UserName, bool IsSuperuser);

/// <summary>用户摘要信息（用于 SHOW USERS）。</summary>
/// <param name="Name">小写用户名。</param>
/// <param name="IsSuperuser">是否超级用户。</param>
/// <param name="CreatedUtc">创建时间（UTC）。</param>
/// <param name="TokenCount">当前有效 token 数。</param>
public sealed record UserSummaryRecord(
    string Name,
    bool IsSuperuser,
    DateTime CreatedUtc,
    int TokenCount);

/// <summary>token 元数据（用于 SHOW TOKENS），永不含明文。</summary>
/// <param name="TokenId">token 短 id。</param>
/// <param name="UserName">所属用户名（小写）。</param>
/// <param name="CreatedUtc">颁发时间（UTC）。</param>
/// <param name="LastUsedUtc">最近一次使用时间（UTC），从未使用为 <c>null</c>。</param>
public sealed record TokenSummaryRecord(
    string TokenId,
    string UserName,
    DateTime CreatedUtc,
    DateTime? LastUsedUtc);
