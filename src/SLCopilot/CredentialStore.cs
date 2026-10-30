using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SLCopilot;

/// <summary>
/// 用 Windows 凭据管理器保存 API 密钥（通用凭据，按当前用户持久化）。目标名为 <c>SLCopilot:profile:{配置 Id}</c>。
/// 读取失败返回 null；写入或删除失败抛出 Win32Exception。只能在 Windows 上使用。
/// </summary>
public static class CredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxSecretBytes = 2560;

    public static string TargetFor(string profileId) => "SLCopilot:profile:" + profileId;

    public static string? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var handle)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var secret = Encoding.Unicode.GetString(bytes);
            return string.IsNullOrWhiteSpace(secret) ? null : secret;
        }
        finally { CredFree(handle); }
    }

    public static void Write(string target, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("Secret must not be empty.");
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > MaxSecretBytes) throw new ArgumentException("Secret is too long for the credential manager.");
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric, TargetName = target, Comment = "SLCopilot API key",
                CredentialBlobSize = bytes.Length, CredentialBlob = blob, Persist = CredPersistLocalMachine, UserName = "SLCopilot",
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    /// <summary>删除凭据；不存在时视为成功。</summary>
    public static void Delete(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound) throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}
