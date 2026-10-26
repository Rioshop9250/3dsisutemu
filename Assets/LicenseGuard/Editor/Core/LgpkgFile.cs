using System;
using System.IO;
using System.Security.Cryptography;

namespace LicenseGuard
{
    /// <summary>パスワード・キーファイルの誤り、またはファイルの改ざん。</summary>
    public class LgAuthException : Exception
    {
        public LgAuthException(string message) : base(message) { }
    }

    /// <summary>.lgpkg ではない、または壊れている。</summary>
    public class LgFormatException : Exception
    {
        public LgFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// .lgpkg コンテナの読み書き。UnityEngine には依存しない。
    ///
    /// レイアウト:
    ///   "LGPK"(4) | ヘッダ長 u32 LE(4) | ヘッダ UTF-8 JSON | IV(16) | 暗号文 | HMAC-SHA256(32)
    ///
    /// ヘッダは平文（同意前に規約を表示するため）。ただし HMAC と鍵導出の両方に含まれるので改ざんはできない。
    /// salt と反復回数はヘッダ JSON 内に入れる（呼び出し側の責任）。
    /// </summary>
    public static class LgpkgFile
    {
        public static readonly byte[] Magic = { (byte)'L', (byte)'G', (byte)'P', (byte)'K' };
        const int MaxHeaderSize = 16 * 1024 * 1024;
        const int Chunk = 1 << 16; // 16 の倍数であること

        public struct Keys
        {
            public string Password;
            public string KeyFilePath;
        }

        /// <summary>ヘッダ JSON（UTF-8）だけを読む。復号はしない。</summary>
        public static byte[] ReadHeaderBytes(string path)
        {
            using (var fs = File.OpenRead(path))
                return ReadHeaderBytes(fs, out _);
        }

        static byte[] ReadHeaderBytes(FileStream fs, out long payloadOffset)
        {
            var prefix = new byte[8];
            if (ReadFull(fs, prefix, 8) != 8) throw new LgFormatException("ファイルが短すぎます。");
            for (int i = 0; i < 4; i++)
                if (prefix[i] != Magic[i]) throw new LgFormatException("LicenseGuard パッケージ（.lgpkg）ではありません。");

            int headerLen = prefix[4] | (prefix[5] << 8) | (prefix[6] << 16) | (prefix[7] << 24);
            if (headerLen <= 0 || headerLen > MaxHeaderSize) throw new LgFormatException("ヘッダが壊れています。");

            var header = new byte[headerLen];
            if (ReadFull(fs, header, headerLen) != headerLen) throw new LgFormatException("ヘッダが壊れています。");

            payloadOffset = 8L + headerLen;
            long minLen = payloadOffset + LgCrypto.IvSize + 16 + LgCrypto.MacSize;
            if (fs.Length < minLen) throw new LgFormatException("ファイルが途中で切れています。");
            return header;
        }

        /// <summary>
        /// payloadPath（.unitypackage）を暗号化して outPath に書き出す。
        /// salt / iterations はヘッダ JSON にも書いておき、復号時に同じ値を渡すこと。
        /// </summary>
        public static void Encrypt(string outPath, byte[] headerBytes, byte[] salt, int iterations,
            string payloadPath, Keys keys, Action<float> progress = null)
        {
            if (headerBytes.Length > MaxHeaderSize) throw new ArgumentException("ヘッダが大きすぎます。");
            DeriveKeys(headerBytes, salt, iterations, keys, out var encKey, out var macKey);
            var iv = LgCrypto.RandomBytes(LgCrypto.IvSize);

            try
            {
                using (var output = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                using (var input = File.OpenRead(payloadPath))
                using (var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey))
                using (var aes = LgCrypto.CreateAes(encKey, iv))
                using (var enc = aes.CreateEncryptor())
                {
                    void Emit(byte[] buf, int count)
                    {
                        if (count <= 0) return;
                        output.Write(buf, 0, count);
                        mac.AppendData(buf, 0, count);
                    }

                    var len = headerBytes.Length;
                    var prefix = new byte[]
                    {
                        Magic[0], Magic[1], Magic[2], Magic[3],
                        (byte)len, (byte)(len >> 8), (byte)(len >> 16), (byte)(len >> 24),
                    };
                    Emit(prefix, prefix.Length);
                    Emit(headerBytes, headerBytes.Length);
                    Emit(iv, iv.Length);

                    var buf = new byte[Chunk];
                    var outBuf = new byte[Chunk + 32];
                    long total = Math.Max(1, input.Length), done = 0;
                    while (true)
                    {
                        int n = ReadFull(input, buf, Chunk);
                        done += n;
                        progress?.Invoke((float)done / total);
                        if (n == Chunk)
                        {
                            Emit(outBuf, enc.TransformBlock(buf, 0, n, outBuf, 0));
                        }
                        else
                        {
                            var fin = enc.TransformFinalBlock(buf, 0, n);
                            Emit(fin, fin.Length);
                            break;
                        }
                    }

                    var tag = mac.GetHashAndReset();
                    output.Write(tag, 0, tag.Length);
                }
            }
            catch
            {
                TryDelete(outPath);
                throw;
            }
            finally
            {
                Array.Clear(encKey, 0, encKey.Length);
                Array.Clear(macKey, 0, macKey.Length);
            }
        }

        /// <summary>
        /// 改ざん検査を行ったうえで復号し、outPath（.unitypackage）に書き出す。
        /// 鍵が違う・改ざんされている場合は何も書き出さずに LgAuthException を投げる。
        /// </summary>
        public static void Decrypt(string path, byte[] salt, int iterations, Keys keys, string outPath,
            Action<float> progress = null)
        {
            if (iterations < LgCrypto.MinIterations || iterations > LgCrypto.MaxIterations)
                throw new LgFormatException("ヘッダの鍵導出パラメータが不正です。");

            using (var fs = File.OpenRead(path))
            {
                var headerBytes = ReadHeaderBytes(fs, out var payloadOffset);
                DeriveKeys(headerBytes, salt, iterations, keys, out var encKey, out var macKey);
                try
                {
                    long macOffset = fs.Length - LgCrypto.MacSize;
                    long cipherOffset = payloadOffset + LgCrypto.IvSize;
                    long cipherLen = macOffset - cipherOffset;
                    if (cipherLen <= 0 || cipherLen % 16 != 0) throw new LgFormatException("暗号データが壊れています。");

                    // 1パス目: 改ざん検査（ここを通るまで一切復号しない）
                    var buf = new byte[Chunk];
                    using (var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey))
                    {
                        fs.Position = 0;
                        long remaining = macOffset;
                        while (remaining > 0)
                        {
                            int want = (int)Math.Min(Chunk, remaining);
                            int n = ReadFull(fs, buf, want);
                            if (n != want) throw new LgFormatException("ファイルの読み込みに失敗しました。");
                            mac.AppendData(buf, 0, n);
                            remaining -= n;
                            progress?.Invoke(0.5f * (macOffset - remaining) / macOffset);
                        }

                        var expected = new byte[LgCrypto.MacSize];
                        if (ReadFull(fs, expected, expected.Length) != expected.Length)
                            throw new LgFormatException("ファイルが途中で切れています。");
                        if (!LgCrypto.FixedTimeEquals(mac.GetHashAndReset(), expected))
                            throw new LgAuthException("パスワード／キーファイルが違うか、ファイルが改ざんされています。");
                    }

                    // 2パス目: 復号
                    var iv = new byte[LgCrypto.IvSize];
                    fs.Position = payloadOffset;
                    ReadFull(fs, iv, iv.Length);

                    try
                    {
                        using (var output = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                        using (var aes = LgCrypto.CreateAes(encKey, iv))
                        using (var dec = aes.CreateDecryptor())
                        {
                            var outBuf = new byte[Chunk + 32];
                            long remaining = cipherLen;
                            while (remaining > Chunk)
                            {
                                if (ReadFull(fs, buf, Chunk) != Chunk) throw new LgFormatException("ファイルの読み込みに失敗しました。");
                                int w = dec.TransformBlock(buf, 0, Chunk, outBuf, 0);
                                output.Write(outBuf, 0, w);
                                remaining -= Chunk;
                                progress?.Invoke(0.5f + 0.5f * (cipherLen - remaining) / cipherLen);
                            }

                            int last = (int)remaining;
                            if (ReadFull(fs, buf, last) != last) throw new LgFormatException("ファイルの読み込みに失敗しました。");
                            var fin = dec.TransformFinalBlock(buf, 0, last);
                            output.Write(fin, 0, fin.Length);
                        }
                    }
                    catch
                    {
                        TryDelete(outPath);
                        throw;
                    }
                }
                finally
                {
                    Array.Clear(encKey, 0, encKey.Length);
                    Array.Clear(macKey, 0, macKey.Length);
                }
            }
        }

        static void DeriveKeys(byte[] headerBytes, byte[] salt, int iterations, Keys keys, out byte[] encKey, out byte[] macKey)
        {
            if (salt == null || salt.Length != LgCrypto.SaltSize) throw new LgFormatException("salt が不正です。");
            byte[] keyFileHash = null;
            if (!string.IsNullOrEmpty(keys.KeyFilePath))
            {
                if (!File.Exists(keys.KeyFilePath)) throw new LgAuthException("キーファイルが見つかりません。");
                keyFileHash = LgCrypto.Sha256File(keys.KeyFilePath);
            }

            var material = LgCrypto.BuildKeyMaterial(LgCrypto.Sha256(headerBytes), keys.Password, keyFileHash, BindingSecret.Get());
            try
            {
                LgCrypto.DeriveKeys(material, salt, iterations, out encKey, out macKey);
            }
            finally
            {
                Array.Clear(material, 0, material.Length);
            }
        }

        static int ReadFull(Stream s, byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 一時ファイルの削除失敗は致命的ではない（次回起動時に再試行する）
            }
        }
    }
}
