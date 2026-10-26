using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LicenseGuard
{
    /// <summary>このプロジェクトでの同意記録（ProjectSettings/LicenseGuardAgreements.json）。</summary>
    public static class AgreementStore
    {
        const string FilePath = "ProjectSettings/LicenseGuardAgreements.json";

        [Serializable]
        class Data
        {
            public List<AgreementRecord> records = new List<AgreementRecord>();
        }

        static Data _cache;

        static Data Load()
        {
            if (_cache != null) return _cache;
            _cache = new Data();
            try
            {
                if (File.Exists(FilePath))
                    _cache = JsonUtility.FromJson<Data>(File.ReadAllText(FilePath)) ?? new Data();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LicenseGuard] 同意記録を読み込めませんでした: " + e.Message);
            }
            if (_cache.records == null) _cache.records = new List<AgreementRecord>();
            return _cache;
        }

        static void Save()
        {
            File.WriteAllText(FilePath, JsonUtility.ToJson(Load(), true));
            LicenseRegistry.Invalidate();
        }

        public static bool HasAgreed(string licenseId, string termsHash) =>
            Load().records.Any(r => r.licenseId == licenseId && r.termsHash == termsHash);

        public static AgreementRecord Find(string licenseId, string termsHash) =>
            Load().records.FirstOrDefault(r => r.licenseId == licenseId && r.termsHash == termsHash);

        public static void Agree(string licenseId, string termsHash, string packageName)
        {
            var data = Load();
            data.records.RemoveAll(r => r.licenseId == licenseId);
            data.records.Add(new AgreementRecord
            {
                licenseId = licenseId,
                termsHash = termsHash,
                packageName = packageName,
                agreedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                agreedBy = string.IsNullOrEmpty(CloudProjectSettings.userName) ? Environment.UserName : CloudProjectSettings.userName,
            });
            Save();
        }

        public static void Revoke(string licenseId)
        {
            Load().records.RemoveAll(r => r.licenseId == licenseId);
            Save();
        }
    }

    /// <summary>プロジェクト内の .lglicense を集め、未同意で保護中のアセットを判定する。</summary>
    public static class LicenseRegistry
    {
        public const string LicenseFolder = "Assets/LicenseGuardLicenses";
        public const string ManifestExtension = ".lglicense";
        public const string PackageExtension = ".lgpkg";

        static List<LicenseManifest> _manifests;
        static Dictionary<string, LicenseManifest> _blockedGuids;
        static Dictionary<string, LicenseManifest> _protectedGuids;

        public static event Action Changed;

        public static void Invalidate()
        {
            _manifests = null;
            _blockedGuids = null;
            _protectedGuids = null;
            Changed?.Invoke();
        }

        public static IReadOnlyList<LicenseManifest> Manifests
        {
            get
            {
                if (_manifests == null) Rebuild();
                return _manifests;
            }
        }

        /// <summary>未同意の規約で保護されているアセット GUID → マニフェスト。</summary>
        public static IReadOnlyDictionary<string, LicenseManifest> BlockedGuids
        {
            get
            {
                if (_blockedGuids == null) Rebuild();
                return _blockedGuids;
            }
        }

        public static bool IsAgreed(LicenseManifest m) => AgreementStore.HasAgreed(m.licenseId, m.termsHash);

        public static bool IsBlocked(string guid) => !string.IsNullOrEmpty(guid) && BlockedGuids.ContainsKey(guid);

        public static LicenseManifest FindByGuid(string guid)
        {
            if (_protectedGuids == null) Rebuild();
            return guid != null && _protectedGuids.TryGetValue(guid, out var m) ? m : null;
        }

        static void Rebuild()
        {
            _manifests = new List<LicenseManifest>();
            _blockedGuids = new Dictionary<string, LicenseManifest>();
            _protectedGuids = new Dictionary<string, LicenseManifest>();

            string[] files;
            try
            {
                files = Directory.GetFiles("Assets", "*" + ManifestExtension, SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    var m = JsonUtility.FromJson<LicenseManifest>(File.ReadAllText(file));
                    if (m == null || string.IsNullOrEmpty(m.licenseId)) continue;
                    m.manifestPath = file.Replace('\\', '/');
                    if (m.terms == null) m.terms = new LicenseTerms();
                    _manifests.Add(m);

                    bool agreed = IsAgreed(m);
                    foreach (var guid in ResolveGuids(m))
                    {
                        _protectedGuids[guid] = m;
                        if (!agreed) _blockedGuids[guid] = m;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[LicenseGuard] {file} を読み込めませんでした: {e.Message}");
                }
            }
        }

        /// <summary>GUID がインポート時に振り直されていてもパスから辿れるようにする。</summary>
        static IEnumerable<string> ResolveGuids(LicenseManifest m)
        {
            foreach (var a in m.assets ?? new List<AssetEntry>())
            {
                if (!string.IsNullOrEmpty(a.guid) && !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(a.guid)))
                    yield return a.guid;
                else if (!string.IsNullOrEmpty(a.path))
                {
                    var g = AssetDatabase.AssetPathToGUID(a.path);
                    if (!string.IsNullOrEmpty(g)) yield return g;
                }
            }
        }

        public static string WriteManifest(LgHeader header)
        {
            Directory.CreateDirectory(LicenseFolder);
            var safe = string.Concat((header.packageName ?? "package").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = $"{LicenseFolder}/{safe}_{header.licenseId.Substring(0, Math.Min(8, header.licenseId.Length))}{ManifestExtension}";
            File.WriteAllText(path, JsonUtility.ToJson(header.ToManifest(), true));
            AssetDatabase.ImportAsset(path);
            Invalidate();
            return path;
        }
    }

    /// <summary>.lglicense や保護アセットの追加・削除・移動を検知してキャッシュを捨てる。</summary>
    class LicenseManifestPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            // GUID ⇔ パスの解決結果が変わり得るので、アセットの増減があれば作り直す（走査は軽い）
            if (imported.Length > 0 || deleted.Length > 0 || moved.Length > 0)
                LicenseRegistry.Invalidate();
        }
    }
}
