using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SonnetDB.Studio;

/// <summary>只在原生宿主和 Windows 凭据库之间流动的短期公网凭据。</summary>
internal sealed record StudioCopilotCredential(string AccessToken, DateTimeOffset ExpiresAtUtc);

/// <summary>原生 Copilot 的凭据存取边界。</summary>
internal interface IStudioCopilotCredentialStore
{
    /// <summary>读取当前凭据。</summary>
    StudioCopilotCredential? Read();
    /// <summary>写入当前用户的系统凭据库。</summary>
    void Write(StudioCopilotCredential credential);
    /// <summary>删除此 broker 独占的凭据。</summary>
    void Delete();
}

/// <summary>使用 Windows Credential Manager 的当前用户 generic credential，不创建明文文件。</summary>
internal sealed class StudioCopilotWindowsCredentialStore : IStudioCopilotCredentialStore
{
    private const uint GenericCredential = 1;
    private const int NotFound = 1168;
    private const int MaximumBlobBytes = 2560;
    private readonly string _target;

    /// <summary>创建限定目标名的系统凭据适配器。</summary>
    public StudioCopilotWindowsCredentialStore(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!target.StartsWith("SonnetDB/Studio/Copilot/", StringComparison.Ordinal) || target.Length > 256)
            throw new ArgumentException("无效的 Studio Copilot 凭据目标。", nameof(target));
        _target = target;
    }

    /// <inheritdoc />
    public StudioCopilotCredential? Read()
    {
        if (CredReadW(_target, GenericCredential, 0, out var handle) == 0)
        {
            int error = Marshal.GetLastWin32Error();
            handle?.Dispose();
            if (error == NotFound) return null;
            throw new Win32Exception(error, "无法读取 Studio Copilot 系统凭据。");
        }
        using (handle)
        {
            var native = Marshal.PtrToStructure<NativeCredential>(handle.DangerousGetHandle());
            if (native.CredentialBlobSize is 0 or > MaximumBlobBytes || native.CredentialBlob == 0)
                throw new InvalidDataException("Studio Copilot 系统凭据格式无效。");
            byte[] bytes = new byte[native.CredentialBlobSize];
            try
            {
                Marshal.Copy(native.CredentialBlob, bytes, 0, bytes.Length);
                return JsonSerializer.Deserialize(bytes, StudioBridgeJsonContext.Default.StudioCopilotCredential)
                    ?? throw new InvalidDataException("Studio Copilot 系统凭据为空。");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    /// <inheritdoc />
    public void Write(StudioCopilotCredential credential)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(credential, StudioBridgeJsonContext.Default.StudioCopilotCredential);
        if (bytes.Length > MaximumBlobBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("Studio Copilot 凭据超过系统存储上限。", nameof(credential));
        }
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var native = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = _target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = pin.AddrOfPinnedObject(),
                Persist = 2, // CRED_PERSIST_LOCAL_MACHINE：当前用户，在本机登录间保留。
                UserName = "SonnetDB Studio Copilot",
            };
            if (CredWriteW(ref native, 0) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入 Studio Copilot 系统凭据。");
        }
        finally { pin.Free(); CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <inheritdoc />
    public void Delete()
    {
        if (CredDeleteW(_target, GenericCredential, 0) != 0) return;
        int error = Marshal.GetLastWin32Error();
        if (error != NotFound) throw new Win32Exception(error, "无法删除 Studio Copilot 系统凭据。");
    }

    // CREDENTIALW 按 Windows ABI 自然对齐；不是磁盘格式，不能使用 Pack=1。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    private sealed class CredentialHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public CredentialHandle() : base(true) { }
        protected override bool ReleaseHandle() { CredFree(handle); return true; }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CredReadW(string target, uint type, uint flags, out CredentialHandle handle);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CredWriteW(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(nint buffer);
}
