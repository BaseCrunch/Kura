using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Kura.Core.Models;

namespace Kura.Core.Security;

/// <summary>
/// Stores server secrets in Windows Credential Manager (DPAPI-backed, per user) as generic credentials
/// named <c>Kura:server:{id}</c>. Fixes plaintext secrets (SPEC 4.2 S5).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore(string prefix = "Kura:server:") : ICredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    private string Target(long id) => prefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public ServerCredentials Get(long serverId)
    {
        if (!CredRead(Target(serverId), CredTypeGeneric, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ErrorNotFound) return ServerCredentials.Empty;
            throw new Win32Exception(err);
        }
        try
        {
            var cred = Marshal.PtrToStructure<NativeCredential>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return ServerCredentials.Empty;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            var payload = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(bytes));
            return payload is null ? ServerCredentials.Empty : new ServerCredentials(payload.ApiKey, payload.Password);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Set(long serverId, ServerCredentials credentials)
    {
        if (credentials.IsEmpty) { Delete(serverId); return; }
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Payload(credentials.ApiKey, credentials.Password)));
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = Target(serverId),
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "Kura",
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(long serverId)
    {
        if (!CredDelete(Target(serverId), CredTypeGeneric, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ErrorNotFound) throw new Win32Exception(err);
        }
    }

    private sealed record Payload(string? ApiKey, string? Password);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
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
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
