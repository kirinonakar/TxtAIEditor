using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    public sealed class SecureNoteEncryptionService
    {
        public const string Version = "SECURE_NOTE_V1";
        private const string Kdf = "PBKDF2WithHmacSHA256";
        private const string Cipher = "AES-256-GCM";
        private const int KeySizeBytes = 32;
        private const int SaltSizeBytes = 16;
        private const int IvSizeBytes = 12;
        private const int TagSizeBytes = 16;
        private const int Iterations = 600_000;
        private const int FileReadMaxAttempts = 8;
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public async Task<bool> IsSecureNoteFileAsync(string filePath)
        {
            try
            {
                using var stream = await OpenReadWithRetryAsync(filePath);
                using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
                string? firstLine = await reader.ReadLineAsync();
                firstLine = firstLine?.TrimStart('\uFEFF');
                return string.Equals(firstLine, Version, StringComparison.Ordinal);
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        public async Task<string> DecryptFileAsync(string filePath, string password)
        {
            string encryptedText = await ReadAllTextWithRetryAsync(filePath);
            return DecryptText(encryptedText, password);
        }

        public string DecryptText(string encryptedText, string password)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("암호를 입력해 주세요.");
            }

            var lines = encryptedText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n', 3);
            if (lines.Length < 2 || !string.Equals(lines[0], Version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("지원하지 않는 암호화 파일 형식입니다.");
            }

            SecureNotePayload? payload = JsonSerializer.Deserialize<SecureNotePayload>(lines[1]);
            if (payload == null ||
                !string.Equals(payload.Version, Version, StringComparison.Ordinal) ||
                !string.Equals(payload.Kdf, Kdf, StringComparison.Ordinal) ||
                !string.Equals(payload.Cipher, Cipher, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("지원하지 않는 암호화 파일 형식입니다.");
            }

            byte[] salt = Convert.FromBase64String(payload.Salt);
            byte[] iv = Convert.FromBase64String(payload.Iv);
            byte[] encrypted = Convert.FromBase64String(payload.Ciphertext);
            if (salt.Length != SaltSizeBytes || iv.Length != IvSizeBytes || encrypted.Length < TagSizeBytes)
            {
                throw new InvalidOperationException("암호화 파일이 손상되었거나 지원하지 않는 형식입니다.");
            }

            byte[] key = DeriveKey(password, salt, payload.Iterations);
            byte[] ciphertext = encrypted[..^TagSizeBytes];
            byte[] tag = encrypted[^TagSizeBytes..];
            byte[] plaintext = new byte[ciphertext.Length];

            try
            {
                using var aes = new AesGcm(key, TagSizeBytes);
                aes.Decrypt(iv, ciphertext, tag, plaintext);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException("암호가 올바르지 않거나 파일을 복호화할 수 없습니다.", ex);
            }
        }

        public async Task SaveEncryptedTextFileAsync(string filePath, string plainText, string password)
        {
            string encryptedText = EncryptText(plainText, password);
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempFilePath = Path.Combine(directory ?? Path.GetTempPath(), $"._{Path.GetFileName(filePath)}.tmp");
            string backupFilePath = filePath + ".bak";

            try
            {
                await File.WriteAllTextAsync(tempFilePath, encryptedText, Utf8NoBom);
                if (File.Exists(filePath))
                {
                    File.Replace(tempFilePath, filePath, backupFilePath);
                    if (File.Exists(backupFilePath))
                    {
                        File.Delete(backupFilePath);
                    }
                }
                else
                {
                    File.Move(tempFilePath, filePath);
                }
            }
            catch (Exception ex)
            {
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }

                throw new IOException($"암호화 파일 저장 실패 (안전 복구 완료): {ex.Message}", ex);
            }
        }

        public string EncryptText(string plainText, string password)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("암호를 입력해 주세요.");
            }

            byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
            byte[] iv = RandomNumberGenerator.GetBytes(IvSizeBytes);
            byte[] key = DeriveKey(password, salt, Iterations);
            byte[] plaintext = Encoding.UTF8.GetBytes(plainText ?? string.Empty);
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagSizeBytes];

            using (var aes = new AesGcm(key, TagSizeBytes))
            {
                aes.Encrypt(iv, plaintext, ciphertext, tag);
            }

            byte[] combined = new byte[ciphertext.Length + tag.Length];
            Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, tag.Length);

            var payload = new SecureNotePayload
            {
                Version = Version,
                Kdf = Kdf,
                Iterations = Iterations,
                Salt = Convert.ToBase64String(salt),
                Cipher = Cipher,
                Iv = Convert.ToBase64String(iv),
                Ciphertext = Convert.ToBase64String(combined)
            };

            string json = JsonSerializer.Serialize(payload);
            return $"{Version}\n{json}";
        }

        private static byte[] DeriveKey(string password, byte[] salt, int iterations)
        {
            if (iterations <= 0)
            {
                throw new InvalidOperationException("암호화 파일의 KDF 반복 횟수가 올바르지 않습니다.");
            }

            return Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                KeySizeBytes);
        }

        private static async Task<FileStream> OpenReadWithRetryAsync(string filePath)
        {
            for (int attempt = 1; attempt <= FileReadMaxAttempts; attempt++)
            {
                try
                {
                    return new FileStream(
                        filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 64 * 1024,
                        useAsync: true);
                }
                catch (Exception ex) when (IsTransientCloudFileAccessError(ex) && attempt < FileReadMaxAttempts)
                {
                    await Task.Delay(GetRetryDelay(attempt));
                }
            }

            return new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                useAsync: true);
        }

        private static async Task<string> ReadAllTextWithRetryAsync(string filePath)
        {
            using var stream = await OpenReadWithRetryAsync(filePath);
            using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: false);
            return await reader.ReadToEndAsync();
        }

        private static bool IsTransientCloudFileAccessError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException;
        }

        private static TimeSpan GetRetryDelay(int attempt)
        {
            int milliseconds = Math.Min(1000, 75 * attempt * attempt);
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        private sealed class SecureNotePayload
        {
            [JsonPropertyName("version")]
            public string Version { get; set; } = string.Empty;

            [JsonPropertyName("kdf")]
            public string Kdf { get; set; } = string.Empty;

            [JsonPropertyName("iterations")]
            public int Iterations { get; set; }

            [JsonPropertyName("salt")]
            public string Salt { get; set; } = string.Empty;

            [JsonPropertyName("cipher")]
            public string Cipher { get; set; } = string.Empty;

            [JsonPropertyName("iv")]
            public string Iv { get; set; } = string.Empty;

            [JsonPropertyName("ciphertext")]
            public string Ciphertext { get; set; } = string.Empty;
        }
    }
}
