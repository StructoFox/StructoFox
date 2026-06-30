using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StructoFox.AI;

/// <summary>
/// Stores per-provider API keys ONLY in the operating system's native secret store — never in a plain file:
///   • Windows → Credential Manager (advapi32, target <c>StructoFox:{provider}</c>)
///   • macOS   → Keychain via the built-in <c>security</c> tool
///   • Linux   → Secret Service (GNOME Keyring / KDE Wallet) via <c>secret-tool</c> (package <c>libsecret</c>)
/// There is deliberately NO insecure fallback. If the native store is unavailable, <see cref="Save"/> /
/// <see cref="Delete"/> throw a <see cref="KeyStoreException"/> with copy-able details so the UI can tell the
/// user exactly what failed and what to install.
/// </summary>
public static class KeyStore
{
    const string Service = "StructoFox";   // service / target-prefix used across all backends

    /// <summary>Human label of the backend used on this OS (for messages).</summary>
    public static string BackendName =>
        OperatingSystem.IsWindows() ? "Windows-Anmeldeinformationsverwaltung"
      : OperatingSystem.IsMacOS()   ? "macOS-Schlüsselbund (Keychain)"
      :                               "Secret Service (secret-tool / libsecret)";

    /// <summary>Saves the key for a provider, or clears it when <paramref name="apiKey"/> is empty.
    /// Throws <see cref="KeyStoreException"/> if the native store can't be reached.</summary>
    public static void Save(string provider, string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) { Delete(provider); return; }
        if      (OperatingSystem.IsWindows()) Win.Save(provider, apiKey);
        else if (OperatingSystem.IsMacOS())   Mac.Save(provider, apiKey);
        else                                  Sec.Save(provider, apiKey);
    }

    /// <summary>Returns the stored key, or null if none / the backend is unavailable (reading never throws,
    /// so a missing keyring just means "no key" — the failure surfaces when the user tries to SAVE).</summary>
    public static string? Load(string provider)
    {
        try
        {
            if      (OperatingSystem.IsWindows()) return Win.Load(provider);
            else if (OperatingSystem.IsMacOS())   return Mac.Load(provider);
            else                                  return Sec.Load(provider);
        }
        catch { return null; }
    }

    /// <summary>Removes the stored key. Throws <see cref="KeyStoreException"/> on a real backend failure
    /// (a "not found" is treated as success).</summary>
    public static void Delete(string provider)
    {
        if      (OperatingSystem.IsWindows()) Win.Delete(provider);
        else if (OperatingSystem.IsMacOS())   Mac.Delete(provider);
        else                                  Sec.Delete(provider);
    }

    public static bool Has(string provider) => !string.IsNullOrWhiteSpace(Load(provider));

    /// <summary>Maps a StructoFox provider id → the credential name ClaudetRelay used for the same provider
    /// (only the providers both apps share; ids differ in wording so a plain rename wouldn't match).</summary>
    public static readonly IReadOnlyDictionary<string, string> ClaudetRelayNames = new Dictionary<string, string>
    {
        ["Anthropic"]           = "Anthropic",
        ["OpenAI"]              = "OpenAI ChatGPT",
        ["Google"]              = "Google AI",
        ["Groq"]                = "Groq",
        ["Mistral"]             = "Mistral",
        ["OpenRouter"]          = "OpenRouter",
        ["xAI (Grok)"]          = "xAI Grok",
        ["Cerebras"]            = "Cerebras",
        ["DeepInfra"]           = "DeepInfra",
        ["DeepSeek"]            = "DeepSeek",
        ["Fireworks"]           = "Fireworks AI",
        ["Nvidia NIM"]          = "Nvidia NIM",
        ["Perplexity"]          = "Perplexity AI",
        ["Together"]            = "Together AI",
        ["Ollama (OpenAI API)"] = "Ollama ☁",
    };

    /// <summary>Windows only: copies API keys ClaudetRelay stored in the Credential Manager into StructoFox's
    /// own entries (mapping the differing provider ids). Existing StructoFox keys are NOT overwritten.
    /// Returns the provider ids that were imported. Throws on non-Windows.</summary>
    public static List<string> ImportFromClaudetRelay()
    {
        if (!OperatingSystem.IsWindows())
            throw new KeyStoreException("Import nur unter Windows verfügbar.",
                "Die Übernahme aus ClaudetRelay liest die Windows-Anmeldeinformationsverwaltung und ist daher "
              + "nur unter Windows möglich.");

        var imported = new List<string>();
        foreach (var (sfId, crName) in ClaudetRelayNames)
        {
            if (Has(sfId)) continue;                       // never clobber a key the user already set here
            var value = Win.LoadRaw($"ClaudetRelay:{crName}");
            if (string.IsNullOrEmpty(value)) continue;
            Save(sfId, value);
            imported.Add(sfId);
        }
        return imported;
    }

    /// <summary>Checks the native store is reachable. Returns (true, null) if OK, otherwise (false, details)
    /// with a copy-able explanation + install hint. Used to warn the user before they enter keys.</summary>
    public static (bool ok, string? details) Probe()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return (true, null);          // CredMan is always present
            // mac/Linux: prove the CLI tool exists and runs by attempting a harmless lookup.
            _ = Load("__structofox_probe__");
            if (OperatingSystem.IsMacOS()) return (true, null);            // `security` ships with macOS
            // Linux: confirm secret-tool is actually installed (Load swallows errors, so probe explicitly).
            Sec.EnsureToolPresent();
            return (true, null);
        }
        catch (KeyStoreException ex) { return (false, ex.FullText); }
        catch (Exception ex)         { return (false, ex.ToString()); }
    }

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

        static string Target(string provider) => $"{Service}:{provider}";

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
                if (!CredWrite(ref cred, 0))
                    throw new KeyStoreException(
                        "Der API-Key konnte nicht in der Windows-Anmeldeinformationsverwaltung gespeichert werden.",
                        $"CredWriteW schlug fehl. Win32-Fehlercode: {Marshal.GetLastWin32Error()} (Target: {Target(provider)}).");
            }
            finally { handle.Free(); }
        }

        public static string? Load(string provider) => LoadRaw(Target(provider));

        /// <summary>Reads a credential by its FULL target name (e.g. "ClaudetRelay:Anthropic"), so we can also
        /// read another app's generic credentials for import.</summary>
        public static string? LoadRaw(string fullTarget)
        {
            if (!CredRead(fullTarget, CRED_TYPE_GENERIC, 0, out var ptr)) return null;
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

    // ── macOS Keychain via the built-in `security` CLI ──────────────────────
    static class Mac
    {
        public static void Save(string provider, string apiKey)
        {
            // -U updates if an item already exists; service/account identify the entry.
            var r = Proc.Run("security",
                ["add-generic-password", "-U", "-s", Service, "-a", provider, "-w", apiKey],
                whatFailed: "Der API-Key konnte nicht im macOS-Schlüsselbund gespeichert werden.");
            if (r.code != 0)
                throw new KeyStoreException(
                    "Der API-Key konnte nicht im macOS-Schlüsselbund gespeichert werden.",
                    $"`security add-generic-password` Exit {r.code}.\nstderr:\n{r.err}");
        }

        public static string? Load(string provider)
        {
            var r = Proc.Run("security", ["find-generic-password", "-s", Service, "-a", provider, "-w"],
                whatFailed: "Der macOS-Schlüsselbund konnte nicht gelesen werden.");
            return r.code == 0 ? r.@out.TrimEnd('\n', '\r') : null;   // nonzero = item not found
        }

        public static void Delete(string provider)
        {
            var r = Proc.Run("security", ["delete-generic-password", "-s", Service, "-a", provider],
                whatFailed: "Der API-Key konnte nicht aus dem macOS-Schlüsselbund entfernt werden.");
            // Exit 44 = item not found → treat as already-deleted (success).
            if (r.code is not (0 or 44))
                throw new KeyStoreException(
                    "Der API-Key konnte nicht aus dem macOS-Schlüsselbund entfernt werden.",
                    $"`security delete-generic-password` Exit {r.code}.\nstderr:\n{r.err}");
        }
    }

    // ── Linux Secret Service via `secret-tool` (package libsecret) ───────────
    static class Sec
    {
        const string InstallHint =
            "Auf Linux wird das Programm `secret-tool` (Paket `libsecret`/`libsecret-tools`) benötigt, "
          + "und ein laufender Schlüsselbund-Dienst (GNOME Keyring oder KDE Wallet).\n"
          + "Installation z. B.:\n"
          + "  Debian/Ubuntu:  sudo apt install libsecret-tools\n"
          + "  Fedora:         sudo dnf install libsecret\n"
          + "  Arch:           sudo pacman -S libsecret";

        public static void EnsureToolPresent()
        {
            // A which-style check: `secret-tool --version` (or any invocation) must start.
            try { Proc.Run("secret-tool", ["--version"], whatFailed: ""); }
            catch (KeyStoreException ex)
            {
                throw new KeyStoreException(
                    "Der Linux-Schlüsselbund (Secret Service) ist nicht verfügbar.",
                    ex.Details + "\n\n" + InstallHint);
            }
        }

        public static void Save(string provider, string apiKey)
        {
            var r = Proc.Run("secret-tool",
                ["store", "--label", $"{Service} {provider}", "service", Service, "account", provider],
                stdin: apiKey,
                whatFailed: "Der API-Key konnte nicht im Linux-Schlüsselbund gespeichert werden.");
            if (r.code != 0)
                throw new KeyStoreException(
                    "Der API-Key konnte nicht im Linux-Schlüsselbund gespeichert werden.",
                    $"`secret-tool store` Exit {r.code}.\nstderr:\n{r.err}\n\n{InstallHint}");
        }

        public static string? Load(string provider)
        {
            var r = Proc.Run("secret-tool", ["lookup", "service", Service, "account", provider],
                whatFailed: "Der Linux-Schlüsselbund konnte nicht gelesen werden.");
            return r.code == 0 ? r.@out.TrimEnd('\n', '\r') : null;   // exit 1 = no match
        }

        public static void Delete(string provider)
        {
            var r = Proc.Run("secret-tool", ["clear", "service", Service, "account", provider],
                whatFailed: "Der API-Key konnte nicht aus dem Linux-Schlüsselbund entfernt werden.");
            if (r.code != 0)
                throw new KeyStoreException(
                    "Der API-Key konnte nicht aus dem Linux-Schlüsselbund entfernt werden.",
                    $"`secret-tool clear` Exit {r.code}.\nstderr:\n{r.err}\n\n{InstallHint}");
        }
    }

    // ── Tiny process runner (no shell, args passed individually; optional stdin) ──
    static class Proc
    {
        public static (int code, string @out, string err) Run(
            string file, string[] args, string? stdin = null, string whatFailed = "")
        {
            var psi = new ProcessStartInfo
            {
                FileName = file, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = stdin is not null, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            try
            {
                using var p = Process.Start(psi)
                    ?? throw new KeyStoreException(whatFailed, $"Prozess `{file}` ließ sich nicht starten.");
                if (stdin is not null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
                var so = p.StandardOutput.ReadToEnd();
                var se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, so, se);
            }
            catch (Win32Exception ex)   // tool not installed / not on PATH
            {
                throw new KeyStoreException(
                    string.IsNullOrEmpty(whatFailed) ? $"`{file}` ist nicht verfügbar." : whatFailed,
                    $"`{file}` konnte nicht gestartet werden: {ex.Message} (Win32 {ex.NativeErrorCode}).");
            }
        }
    }
}

/// <summary>Raised when the OS secret store can't be used. <see cref="Details"/> carries the technical cause
/// (command, exit code, stderr, install hints) for a copy-able error dialog.</summary>
public sealed class KeyStoreException(string message, string details) : Exception(message)
{
    /// <summary>Technical detail (command, exit code, stderr, install hints).</summary>
    public string Details { get; } = details;

    /// <summary>Summary + details, ready to drop into a copy-able text box.</summary>
    public string FullText => $"{Message}\n\n{Details}";
}
