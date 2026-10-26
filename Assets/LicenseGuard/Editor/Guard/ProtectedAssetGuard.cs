using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LicenseGuard
{
    /// <summary>
    /// 規約に同意していない保護アセット（FBX・プレハブ等）を使えないようにする。
    ///  - シーン／プレハブ編集画面に置かれたら取り除き、同意画面を開く
    ///  - プロジェクトウィンドウでダブルクリックしても開かせない
    ///  - ビルドに含まれていたらビルドを失敗させる
    ///  - プロジェクトウィンドウに鍵アイコンを表示する
    /// </summary>
    [InitializeOnLoad]
    static class ProtectedAssetGuard
    {
        static bool _scheduled;
        static bool _running;

        static ProtectedAssetGuard()
        {
            EditorApplication.hierarchyChanged += Schedule;
            EditorSceneManager.sceneOpened += (_, __) => Schedule();
            PrefabStage.prefabStageOpened += _ => Schedule();
            LicenseRegistry.Changed += Schedule;
            EditorApplication.projectWindowItemOnGUI += DrawProjectIcon;
            EditorApplication.delayCall += StartupCheck;
        }

        static void StartupCheck()
        {
            // 別プロジェクトにコピーされた等で未同意のライセンスがあれば知らせる（スクリプト再コンパイルのたびには出さない）
            var unagreed = LicenseRegistry.Manifests.FirstOrDefault(m => !LicenseRegistry.IsAgreed(m));
            if (unagreed != null && !SessionState.GetBool("LicenseGuard.StartupShown", false))
            {
                SessionState.SetBool("LicenseGuard.StartupShown", true);
                LicenseManagerWindow.Open(unagreed);
            }
            Schedule();
        }

        static void Schedule()
        {
            if (_scheduled) return;
            _scheduled = true;
            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                Check();
            };
        }

        static void Check()
        {
            if (_running || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (LicenseRegistry.BlockedGuids.Count == 0) return;

            _running = true;
            try
            {
                var roots = new List<GameObject>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded) roots.AddRange(scene.GetRootGameObjects());
                }
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null && stage.prefabContentsRoot != null) roots.Add(stage.prefabContentsRoot);

                var hit = new HashSet<LicenseManifest>();
                foreach (var root in roots) Sanitize(root, stage != null ? stage.prefabContentsRoot : null, hit);

                if (hit.Count > 0)
                {
                    var names = string.Join("\n", hit.Select(m => "・" + m.DisplayName));
                    Debug.LogWarning("[LicenseGuard] 利用規約に同意していないアセットを取り除きました（Ctrl+Z で戻せますが、同意するまで再び取り除かれます）:\n" + names);
                    LicenseManagerWindow.Open(hit.First());
                }
            }
            finally
            {
                _running = false;
            }
        }

        static void Sanitize(GameObject root, GameObject stageRoot, HashSet<LicenseManifest> hit)
        {
            // 外側から見て、保護プレハブ／FBX のインスタンスなら丸ごと取り除く
            var removed = new HashSet<GameObject>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                var go = t.gameObject;
                if (removed.Any(r => r != null && go.transform.IsChildOf(r.transform))) continue;

                if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
                {
                    var m = BlockingManifest(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go));
                    if (m != null && !PrefabUtility.IsPartOfPrefabAsset(go))
                    {
                        hit.Add(m);
                        var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(go) ?? go;
                        // バリアント編集時のステージルート自体は消せない（開く時点で OnOpenAsset が止める）
                        if (outer != stageRoot) removed.Add(outer);
                        continue;
                    }
                }
            }

            foreach (var go in removed.Where(g => g != null))
                Undo.DestroyObjectImmediate(go);
            if (root == null) return;

            // プレハブを経由せずにメッシュだけ参照している場合は参照を外す
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = BlockingManifest(mf.sharedMesh);
                if (m == null) continue;
                hit.Add(m);
                Undo.RecordObject(mf, "LicenseGuard");
                mf.sharedMesh = null;
            }
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var m = BlockingManifest(smr.sharedMesh);
                if (m == null) continue;
                hit.Add(m);
                Undo.RecordObject(smr, "LicenseGuard");
                smr.sharedMesh = null;
            }
        }

        static LicenseManifest BlockingManifest(Object asset) =>
            asset == null ? null : BlockingManifest(AssetDatabase.GetAssetPath(asset));

        static LicenseManifest BlockingManifest(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            return LicenseRegistry.BlockedGuids.TryGetValue(guid, out var m) ? m : null;
        }

        [OnOpenAsset(0)]
        static bool OnOpenAsset(int instanceId, int line)
        {
            var path = AssetDatabase.GetAssetPath(EditorUtility.InstanceIDToObject(instanceId));
            if (string.IsNullOrEmpty(path)) return false;

            if (path.EndsWith(LicenseRegistry.PackageExtension, System.StringComparison.OrdinalIgnoreCase))
            {
                ImportWindow.Open(System.IO.Path.GetFullPath(path));
                return true;
            }

            var m = BlockingManifest(path);
            if (m == null) return false;
            LicenseManagerWindow.Open(m);
            return true; // 同意するまで開かせない
        }

        static GUIContent _lockIcon;

        static void DrawProjectIcon(string guid, Rect rect)
        {
            if (!LicenseRegistry.IsBlocked(guid)) return;
            if (_lockIcon == null)
            {
                _lockIcon = new GUIContent(EditorGUIUtility.IconContent("LockIcon-On").image, "利用規約への同意が必要です（Tools > License Guard > ライセンス管理）");
            }
            var r = rect.height > 20
                ? new Rect(rect.x + 2, rect.y + 2, 16, 16)            // グリッド表示
                : new Rect(rect.xMax - 18, rect.y, 16, rect.height); // リスト表示
            GUI.Label(r, _lockIcon);
        }
    }

    /// <summary>未同意の保護アセットを含むビルドを止める。</summary>
    class ProtectedAssetBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (LicenseRegistry.BlockedGuids.Count == 0) return;

            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToList();
            var resources = AssetDatabase.GetAllAssetPaths().Where(p => p.Contains("/Resources/")).ToList();
            var deps = AssetDatabase.GetDependencies(scenes.Concat(resources).ToArray(), true);

            var hits = deps
                .Select(p => (path: p, manifest: LicenseRegistry.BlockedGuids.TryGetValue(AssetDatabase.AssetPathToGUID(p), out var m) ? m : null))
                .Where(x => x.manifest != null)
                .ToList();
            if (hits.Count == 0) return;

            var msg = "利用規約に同意していないアセットがビルドに含まれています。Tools > License Guard > ライセンス管理 から規約を確認してください。\n"
                      + string.Join("\n", hits.Take(20).Select(h => $"・{h.path}（{h.manifest.DisplayName}）"));
            throw new BuildFailedException(msg);
        }
    }
}
