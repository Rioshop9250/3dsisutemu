using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace LicenseGuard
{
    /// <summary>利用者向け: 規約を表示し、同意した場合のみ .lgpkg を復号してインポートする。</summary>
    class ImportWindow : EditorWindow
    {
        const string PendingTempKey = "LicenseGuard.PendingTemp";

        [SerializeField] string packagePath;
        [SerializeField] string keyFilePath = "";
        [NonSerialized] string password = "";
        [NonSerialized] LgHeader header;
        [NonSerialized] string loadError;

        bool agreed, showAssets;
        Vector2 scroll;

        [MenuItem("Tools/License Guard/保護パッケージを開く（利用者向け）", priority = 2)]
        static void OpenFromMenu()
        {
            var p = EditorUtility.OpenFilePanel("保護パッケージを開く", "", "lgpkg");
            if (!string.IsNullOrEmpty(p)) Open(p);
        }

        public static void Open(string path)
        {
            var w = GetWindow<ImportWindow>(true, "LG 保護パッケージ", true);
            w.minSize = new Vector2(520, 560);
            w.Load(path);
        }

        void Load(string path)
        {
            packagePath = path;
            header = null;
            loadError = null;
            agreed = false;
            password = "";
            try
            {
                header = LgHeader.FromBytes(LgpkgFile.ReadHeaderBytes(path));
            }
            catch (Exception e)
            {
                loadError = e.Message;
            }
            Repaint();
        }

        void OnEnable()
        {
            // ドメインリロード後はヘッダを読み直す（同意状態はリセットする）
            if (header == null && !string.IsNullOrEmpty(packagePath) && File.Exists(packagePath))
                Load(packagePath);
        }

        void OnGUI()
        {
            if (loadError != null)
            {
                EditorGUILayout.HelpBox(loadError, MessageType.Error);
                return;
            }
            if (header == null)
            {
                EditorGUILayout.HelpBox("Tools > License Guard > 保護パッケージを開く から .lgpkg を選択してください。", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(header.packageName, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("作者", header.author);
                EditorGUILayout.LabelField("バージョン", header.version);
                if (!string.IsNullOrEmpty(header.contactUrl) && EditorGUILayout.LinkButton(header.contactUrl))
                    Application.OpenURL(header.contactUrl);
            }

            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
            TermsGUI.Draw(header.terms);
            EditorGUILayout.Space(6);
            showAssets = EditorGUILayout.Foldout(showAssets, $"含まれるファイル（{header.assets.Count} 件）", true);
            if (showAssets)
                foreach (var a in header.assets)
                    EditorGUILayout.LabelField(a.path, EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            if (AgreementStore.HasAgreed(header.licenseId, header.termsHash))
                EditorGUILayout.HelpBox("このプロジェクトでは既にこの規約に同意済みです。", MessageType.None);

            agreed = EditorGUILayout.ToggleLeft("上記の利用規約をすべて読み、同意します", agreed, EditorStyles.boldLabel);

            if (header.requiresPassword)
            {
                password = EditorGUILayout.PasswordField("パスワード", password);
                if (!string.IsNullOrEmpty(header.passwordHint))
                    EditorGUILayout.LabelField("ヒント: " + header.passwordHint, EditorStyles.miniLabel);
            }
            if (header.requiresKeyFile)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    keyFilePath = EditorGUILayout.TextField("キーファイル", keyFilePath);
                    if (GUILayout.Button("選択", GUILayout.Width(50)))
                    {
                        var p = EditorUtility.OpenFilePanel("キーファイルを選択", "", "");
                        if (!string.IsNullOrEmpty(p)) keyFilePath = p;
                    }
                }
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("同意しない", GUILayout.Height(28))) Close();
                bool ready = agreed
                             && (!header.requiresPassword || !string.IsNullOrEmpty(password))
                             && (!header.requiresKeyFile || File.Exists(keyFilePath));
                using (new EditorGUI.DisabledScope(!ready))
                {
                    if (GUILayout.Button("同意してインポート", GUILayout.Height(28)))
                        EditorApplication.delayCall += DoImport;
                }
            }
        }

        void DoImport()
        {
            var temp = Path.Combine(Path.GetTempPath(), "lg_" + Guid.NewGuid().ToString("N") + ".unitypackage");
            try
            {
                EditorUtility.DisplayProgressBar("License Guard", "鍵を検証中…", 0f);
                LgpkgFile.Decrypt(packagePath, header.SaltBytes, header.iterations,
                    new LgpkgFile.Keys
                    {
                        Password = header.requiresPassword ? password : null,
                        KeyFilePath = header.requiresKeyFile ? keyFilePath : null,
                    },
                    temp,
                    p => EditorUtility.DisplayProgressBar("License Guard", p < 0.5f ? "改ざんを検査中…" : "復号中…", p));
            }
            catch (LgAuthException e)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("License Guard", e.Message, "OK");
                return;
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogException(e);
                EditorUtility.DisplayDialog("License Guard", "パッケージを開けませんでした: " + e.Message, "OK");
                LgpkgFile.TryDelete(temp);
                return;
            }
            EditorUtility.ClearProgressBar();

            // 同意記録と保護情報を先に書く（インポート直後からガードが効くように）
            AgreementStore.Agree(header.licenseId, header.termsHash, header.packageName);
            LicenseRegistry.WriteManifest(header);

            RegisterTemp(temp);
            AssetDatabase.importPackageCompleted += OnImportDone;
            AssetDatabase.importPackageCancelled += OnImportDone;
            AssetDatabase.importPackageFailed += OnImportFailed;
            AssetDatabase.ImportPackage(temp, false);

            password = "";
            Close();
        }

        static void OnImportDone(string _)
        {
            Unsubscribe();
            CleanupTemps();
            LicenseRegistry.Invalidate();
        }

        static void OnImportFailed(string _, string error)
        {
            Debug.LogError("[LicenseGuard] インポートに失敗しました: " + error);
            OnImportDone(_);
        }

        static void Unsubscribe()
        {
            AssetDatabase.importPackageCompleted -= OnImportDone;
            AssetDatabase.importPackageCancelled -= OnImportDone;
            AssetDatabase.importPackageFailed -= OnImportFailed;
        }

        // 復号済み unitypackage はインポート後すぐ消す。ドメインリロードで取りこぼしても次回起動時に消す。
        static void RegisterTemp(string path)
        {
            var list = SessionState.GetString(PendingTempKey, "");
            SessionState.SetString(PendingTempKey, string.IsNullOrEmpty(list) ? path : list + "\n" + path);
        }

        static void CleanupTemps()
        {
            var list = SessionState.GetString(PendingTempKey, "");
            if (string.IsNullOrEmpty(list)) return;
            var remain = "";
            foreach (var p in list.Split('\n'))
            {
                LgpkgFile.TryDelete(p);
                if (File.Exists(p)) remain += (remain == "" ? "" : "\n") + p;
            }
            SessionState.SetString(PendingTempKey, remain);
        }

        [InitializeOnLoadMethod]
        static void CleanupOnLoad()
        {
            EditorApplication.delayCall += CleanupTemps;
            // 前回セッションの残骸（異常終了時など）
            EditorApplication.delayCall += () =>
            {
                try
                {
                    foreach (var f in Directory.GetFiles(Path.GetTempPath(), "lg_*.unitypackage"))
                        if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1))
                            LgpkgFile.TryDelete(f);
                }
                catch
                {
                    // 一時フォルダが読めない環境では何もしない
                }
            };
        }
    }
}
