using System.Security.Cryptography;
using System.Text;
using PcBridge.Agent.Core;

namespace PcBridge.Agent.Windows;

public sealed class DpapiCredentialStore(string? baseDirectory = null) : ICredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PC Bridge Agent credential v1");
    private readonly string _path = Path.Combine(baseDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PC Bridge Agent"), "credential.bin");

    public async Task SaveTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var clear = Encoding.UTF8.GetBytes(token);
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.LocalMachine);
            var temporary = _path + ".tmp";
            await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken);
            File.Move(temporary, _path, true);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;
        var protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken);
        var clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
        try { return Encoding.UTF8.GetString(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public Task RemoveTokenAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_path)) File.Delete(_path);
        return Task.CompletedTask;
    }
}
