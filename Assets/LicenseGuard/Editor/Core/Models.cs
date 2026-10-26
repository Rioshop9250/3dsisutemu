using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace LicenseGuard
{
    [Serializable]
    public class AssetEntry
    {
        public string guid;
        public string path;
    }

    /// <summary>.lgpkg の平文ヘッダ。同意前に表示する情報と、復号に必要なパラメータ。</summary>
    [Serializable]
    public class LgHeader
    {
        public int formatVersion = 1;
        public string licenseId;
        public string packageName;
        public string author;
        public string version;
        public string contactUrl;
        public string createdAt;

        public LicenseTerms terms;
        public string termsHash;

        public bool requiresPassword;
        public string passwordHint;
        public bool requiresKeyFile;

        public string salt; // Base64
        public int iterations;

        public List<AssetEntry> assets = new List<AssetEntry>();

        public byte[] SaltBytes => Convert.FromBase64String(salt ?? "");

        public byte[] ToBytes() => Encoding.UTF8.GetBytes(JsonUtility.ToJson(this));

        public static LgHeader FromBytes(byte[] bytes)
        {
            LgHeader h;
            try
            {
                h = JsonUtility.FromJson<LgHeader>(Encoding.UTF8.GetString(bytes));
            }
            catch (Exception e)
            {
                throw new LgFormatException("ヘッダを読み取れません: " + e.Message);
            }
            if (h == null || h.formatVersion != 1 || string.IsNullOrEmpty(h.licenseId))
                throw new LgFormatException("未対応のパッケージ形式です。ツールを更新してください。");
            if (h.terms == null) h.terms = new LicenseTerms();
            if (h.assets == null) h.assets = new List<AssetEntry>();
            return h;
        }

        public static string ComputeTermsHash(LicenseTerms terms) =>
            LgCrypto.ToHex(LgCrypto.Sha256(Encoding.UTF8.GetBytes(JsonUtility.ToJson(terms))));

        public LicenseManifest ToManifest() => new LicenseManifest
        {
            licenseId = licenseId,
            packageName = packageName,
            author = author,
            version = version,
            contactUrl = contactUrl,
            termsHash = termsHash,
            terms = terms,
            assets = new List<AssetEntry>(assets),
        };
    }

    /// <summary>
    /// インポート先プロジェクトに置く保護情報（Assets/LicenseGuardLicenses/*.lglicense）。
    /// どのアセットがどの規約で保護されているかを示す。
    /// </summary>
    [Serializable]
    public class LicenseManifest
    {
        public string licenseId;
        public string packageName;
        public string author;
        public string version;
        public string contactUrl;
        public string termsHash;
        public LicenseTerms terms;
        public List<AssetEntry> assets = new List<AssetEntry>();

        [NonSerialized] public string manifestPath;

        public string DisplayName => string.IsNullOrEmpty(packageName) ? licenseId : packageName;
    }

    [Serializable]
    public class AgreementRecord
    {
        public string licenseId;
        public string termsHash;
        public string packageName;
        public string agreedAt;
        public string agreedBy;
    }
}
