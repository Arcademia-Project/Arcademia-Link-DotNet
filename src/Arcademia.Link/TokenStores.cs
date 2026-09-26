using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Arcademia.Link
{
    public interface ITokenStore
    {
        Task<StoredTokens> LoadAsync(CancellationToken ct = default);
        Task SaveAsync(StoredTokens tokens, CancellationToken ct = default);
        Task ClearAsync(CancellationToken ct = default);
    }

    public sealed class MemoryTokenStore : ITokenStore
    {
        private StoredTokens _tokens;

        public Task<StoredTokens> LoadAsync(CancellationToken ct = default) => Task.FromResult(_tokens);

        public Task SaveAsync(StoredTokens tokens, CancellationToken ct = default)
        {
            _tokens = tokens;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            _tokens = null;
            return Task.CompletedTask;
        }
    }

    public sealed class FileTokenStore : ITokenStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Arcademia.Link");
        private readonly string _path;
        private readonly bool _protect;

        public FileTokenStore(string path, bool protectOnWindows = true)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _protect = protectOnWindows && RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        public static FileTokenStore ForClient(string clientId) =>
            new FileTokenStore(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Arcademia",
                    "Link",
                    clientId + ".tokens"
                )
            );

        public string FilePath => _path;

        public Task<StoredTokens> LoadAsync(CancellationToken ct = default)
        {
            try
            {
                if (!File.Exists(_path))
                    return Task.FromResult<StoredTokens>(null);
                var bytes = File.ReadAllBytes(_path);
                if (_protect)
                    bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
                return Task.FromResult(JsonSerializer.Deserialize<StoredTokens>(bytes));
            }
            catch (Exception ex) when (ex is IOException || ex is CryptographicException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                return Task.FromResult<StoredTokens>(null);
            }
        }

        public Task SaveAsync(StoredTokens tokens, CancellationToken ct = default)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(tokens);
            if (_protect)
                bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);

            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(_path))
                File.Replace(temp, _path, null);
            else
                File.Move(temp, _path);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            if (File.Exists(_path))
                File.Delete(_path);
            return Task.CompletedTask;
        }
    }
}
