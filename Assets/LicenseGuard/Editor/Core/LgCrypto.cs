using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LicenseGuard
{
    /// <summary>
    /// 暗号処理。UnityEngine には依存しない（単体テスト可能にするため）。
    ///
    /// 方式:
    ///   鍵素材 = 長さ付き連結( ドメイン文字列, SHA256(ヘッダ), パスワード, SHA256(キーファイル), 埋め込みシークレット )
    ///   PBKDF2-HMAC-SHA256(鍵素材, salt, iterations) → 64byte → 暗号鍵32byte + MAC鍵32byte
    ///   本体 = AES-256-CBC(PKCS7)、改ざん検知 = HMAC-SHA256（Encrypt-then-MAC、ファイル全体を対象）
    ///
    /// ヘッダ（利用規約を含む）のハッシュを鍵に混ぜているため、規約を書き換えると復号できなくなる。
    /// </summary>
    public static class LgCrypto
    {
        public const int KeySize = 32;
        public const int MacSize = 32;
        public const int IvSize = 16;
        public const int SaltSize = 32;
        public const int MinIterations = 50_000;
        public const int MaxIterations = 5_000_000;
        public const int DefaultIterations = 200_000;

        const string Domain = "LicenseGuard/lgpkg/v1";

        public static byte[] RandomBytes(int length)
        {
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return bytes;
        }

        public static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(data);
        }

        public static byte[] Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return sha.ComputeHash(fs);
        }

        public static string ToHex(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 2);
            foreach (var b in data) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>複合キーの鍵素材を作る。未使用の要素は空として扱う。</summary>
        public static byte[] BuildKeyMaterial(byte[] headerHash, string password, byte[] keyFileHash, byte[] bindingSecret)
        {
            using (var ms = new MemoryStream())
            {
                WriteField(ms, Encoding.ASCII.GetBytes(Domain));
                WriteField(ms, headerHash ?? Array.Empty<byte>());
                WriteField(ms, string.IsNullOrEmpty(password)
                    ? Array.Empty<byte>()
                    : Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)));
                WriteField(ms, keyFileHash ?? Array.Empty<byte>());
                WriteField(ms, bindingSecret ?? Array.Empty<byte>());
                return ms.ToArray();
            }
        }

        static void WriteField(Stream s, byte[] data)
        {
            s.WriteByte((byte)data.Length);
            s.WriteByte((byte)(data.Length >> 8));
            s.WriteByte((byte)(data.Length >> 16));
            s.WriteByte((byte)(data.Length >> 24));
            s.Write(data, 0, data.Length);
        }

        public static void DeriveKeys(byte[] keyMaterial, byte[] salt, int iterations, out byte[] encKey, out byte[] macKey)
        {
            var okm = Pbkdf2Sha256(keyMaterial, salt, iterations, KeySize * 2);
            encKey = new byte[KeySize];
            macKey = new byte[KeySize];
            Buffer.BlockCopy(okm, 0, encKey, 0, KeySize);
            Buffer.BlockCopy(okm, KeySize, macKey, 0, KeySize);
            Array.Clear(okm, 0, okm.Length);
        }

        /// <summary>
        /// PBKDF2-HMAC-SHA256 (RFC 8018)。Unity の Mono 実装差異を避けるため自前実装。
        /// </summary>
        public static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int iterations, int length)
        {
            if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));
            const int hLen = 32;
            var result = new byte[length];
            var block = new byte[salt.Length + 4];
            Buffer.BlockCopy(salt, 0, block, 0, salt.Length);

            using (var hmac = new HMACSHA256(password))
            {
                int blocks = (length + hLen - 1) / hLen;
                for (int i = 1; i <= blocks; i++)
                {
                    block[salt.Length] = (byte)(i >> 24);
                    block[salt.Length + 1] = (byte)(i >> 16);
                    block[salt.Length + 2] = (byte)(i >> 8);
                    block[salt.Length + 3] = (byte)i;

                    var u = hmac.ComputeHash(block);
                    var t = (byte[])u.Clone();
                    for (int j = 1; j < iterations; j++)
                    {
                        u = hmac.ComputeHash(u);
                        for (int k = 0; k < hLen; k++) t[k] ^= u[k];
                    }

                    int offset = (i - 1) * hLen;
                    Buffer.BlockCopy(t, 0, result, offset, Math.Min(hLen, length - offset));
                }
            }
            return result;
        }

        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static Aes CreateAes(byte[] key, byte[] iv)
        {
            var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;
            return aes;
        }
    }
}
