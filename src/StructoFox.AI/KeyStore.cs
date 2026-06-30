using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace StructoFox.AI;

/// <summary>
/// Stores per-provider API keys outside the plain-text settings JSON. On Windows the keys live in the
/// Windows Credential Manager (target <c>StructoFox:{provider}</c>), exactly like ClaudetRelay. On other
/// platforms we fall back to a DPAPI-less, machine-local file under the user's app-data folder (best effort —
/// the OS keychain integration is Windows-only, which is what the user asked for).
/// </summary>
public static class KeyStore
{
    /// <summary>Saves (or clears, if <paramref name="apiKey"/> is empty) the key for a provider.</summary>
    public static void Save(string provider, string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) { Delete(provider); return; }
        if (OperatingSystem.IsWindows()) Win.Save(provider, apiKey);
        else                             FileFallback.Save(provider, apiKey);
    }

    /// <summary>Returns the stored key for a provider, or null if none.</summary>
    public static string? Load(string provider) =>
        OperatingSystem.IsWindows() ? Win.Load(provider) : FileFallback.Load(provider);

    /// <summary>Removes the stored key for a provider (no-op if none).</summary>
    public static void Delete(string provider)
    {
        if (OperatingSystem.IsWindows()) Win.Delete(provider);
        else                             FileFallback.Delete(provider);
    }

    /// <summary>True if a non-empty key is configured for the provider.</summary>
    public static bool Has(string provider) => !string.IsNullOrWhiteSpace(Load(provider));

    // ── Windows Credential Manager (CRED_TYPE_GENERIC) ──────────────────────
    static class Win
    {
        const int CRED_TYPE_GENERIC          = 1;
        const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CREDENTIAL
        {
            public uint   Flags;
            public int    Type;
            [MarshalAs(UnmanagedType.LPWStr)] public string  TargetName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
            public long   LastWritten;
            public uint   CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint   Persist;
            public uint   AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)] public string  UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW",   CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredRead  (string target, int type, int flags, out IntPtr credPtr);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW",  CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredWrite (ref CREDENTIAL cred, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredDelete(string target, int type, int flags);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern void CredFree  (IntPtr buffer);

        static string Target(string provider) => $"StructoFox:{provider}";

        public static void Save(string provider, string apiKey)
        {
            var bytes  = Encoding.UTF8.GetBytes(apiKey);
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var cred = new CREDENTIAL
                {
                    Type               = CRED_TYPE_GENERIC,
                    TargetName         = Target(provider),
                    UserName           = provider,
                    CredentialBlob     = handle.AddrOfPinnedObject(),
                    CredentialBlobSize = (uint)bytes.Length,
                    Persist            = CRED_PERSIST_LOCAL_MACHINE
                };
                CredWrite(ref cred, 0);
            }
            finally { handle.Free(); }
        }

        public static string? Load(string provider)
        {
            if (!CredRead(Target(provider), CRED_TYPE_GENERIC, 0, out var ptr)) return null;
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                if (cred.CredentialBlobSize == 0) return null;
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { CredFree(ptr); }
        }

        public static void Delete(string provider) => CredDelete(Target(provider), CRED_TYPE_GENERIC, 0);
    }

    // ── Non-Windows fallback: a single JSON file in app-data (keys lightly obfuscated, not OS-secured) ──
    static class FileFallback
    {
        static string Path_ => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StructoFox", "keys.json");

        static Dictionary<string, string> Read()
        {
            try
            {
                return File.Exists(Path_)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path_)) ?? new()
                    : new();
            }
            catch { return new(); }
        }

        static void Write(Dictionary<string, string> d)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(d));
        }

        static string Enc(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        static string Dec(string s) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch { return ""; } }

        public static void Save(string provider, string apiKey) { var d = Read(); d[provider] = Enc(apiKey); Write(d); }
        public static string? Load(string provider) => Read().TryGetValue(provider, out var v) ? Dec(v) : null;
        public static void Delete(string provider) { var d = Read(); if (d.Remove(provider)) Write(d); }
    }
}
