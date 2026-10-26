using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LicenseGuard
{
    /// <summary>配布者向け: FBX・プレハブを規約付きの暗号化パッケージ（.lgpkg）にする。</summary>
    class PackWindow : EditorWindow
    {
        [SerializeField] List<Object> targets = new List<Object>();
        [SerializeField] bool includeDependencies = true;
        [SerializeField] string packageName = "";
        [SerializeField] string author = "";
        [SerializeField] string version = "1.0.0";
        [SerializeField] string contactUrl = "";
        [SerializeField] LicenseTerms terms = LicenseTerms.CreateDefault();

        [SerializeField] bool usePassword;
        [SerializeField] string passwordHint = "";
        [SerializeField] bool useKeyFile;
        [SerializeField] string keyFilePath = "";
        [SerializeField] int iterations = LgCrypto.DefaultIterations;

        // パスワードはドメインリロードやレイアウト保存で残さない
        [NonSerialized] string password = "";
        [NonSerialized] string passwordConfirm = "";

        Vector2 scroll, freeTextScroll;
        bool showFiles, showPreview;
        List<string> previewPaths;

        [MenuItem("Tools/License Guard/パッケージ作成（配布者向け）", priority = 1)]
        static void Open() => GetWindow<PackWindow>("LG パッケージ作成").minSize = new Vector2(620, 500);

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            Section("1. 保護するアセット");
            DrawTargets();

            Section("2. パッケージ情報");
            packageName = EditorGUILayout.TextField("パッケージ名", packageName);
            author = EditorGUILayout.TextField("作者", author);
            version = EditorGUILayout.TextField("バージョン", version);
            contactUrl = EditorGUILayout.TextField("連絡先URL（任意）", contactUrl);

            Section("3. 利用規約");
            TermsGUI.DrawEditor(terms, ref freeTextScroll);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("テンプレート保存")) SaveTemplate();
                if (GUILayout.Button("テンプレート読込")) LoadTemplate();
                if (GUILayout.Button("テキストでコピー"))
                {
                    EditorGUIUtility.systemCopyBuffer = terms.ToPlainText();
                    ShowNotification(new GUIContent("クリップボードにコピーしました"));
                }
            }
            showPreview = EditorGUILayout.Foldout(showPreview, "利用者に表示される画面のプレビュー", true);
            if (showPreview)
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    TermsGUI.Draw(terms);

            Section("4. 保護設定（複合キー）");
            DrawSecurity();

            EditorGUILayout.Space(12);
            var error = Validate();
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Warning);
            using (new EditorGUI.DisabledScope(error != null))
            {
                if (GUILayout.Button("暗号化パッケージ（.lgpkg）を書き出す", GUILayout.Height(32)))
                    EditorApplication.delayCall += Export;
            }
            EditorGUILayout.Space(8);
            EditorGUILayout.EndScrollView();
        }

        static void Section(string title)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        void DrawTargets()
        {
            var drop = GUILayoutUtility.GetRect(0, 40, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "ここに FBX・プレハブ・フォルダをドラッグ＆ドロップ", EditorStyles.helpBox);
            var e = Event.current;
            if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && drop.Contains(e.mousePosition))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var o in DragAndDrop.objectReferences)
                        if (EditorUtility.IsPersistent(o) && !targets.Contains(o)) targets.Add(o);
                    previewPaths = null;
                }
                e.Use();
            }

            int remove = -1;
            for (int i = 0; i < targets.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    targets[i] = EditorGUILayout.ObjectField(targets[i], typeof(Object), false);
                    if (EditorGUI.EndChangeCheck()) previewPaths = null;
                    if (GUILayout.Button("削除", GUILayout.Width(40))) remove = i;
                }
            }
            if (remove >= 0)
            {
                targets.RemoveAt(remove);
                previewPaths = null;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("選択中のアセットを追加"))
                {
                    foreach (var o in Selection.objects)
                        if (EditorUtility.IsPersistent(o) && !targets.Contains(o)) targets.Add(o);
                    previewPaths = null;
                }
                if (GUILayout.Button("すべて削除", GUILayout.Width(90)))
                {
                    targets.Clear();
                    previewPaths = null;
                }
            }

            EditorGUI.BeginChangeCheck();
            includeDependencies = EditorGUILayout.ToggleLeft("依存アセット（マテリアル・テクスチャ等）も含める", includeDependencies);
            if (EditorGUI.EndChangeCheck()) previewPaths = null;

            showFiles = EditorGUILayout.Foldout(showFiles, "書き出されるファイル", true);
            if (showFiles)
            {
                if (previewPaths == null) previewPaths = CollectPaths();
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField($"{previewPaths.Count} 件", EditorStyles.miniBoldLabel);
                    foreach (var p in previewPaths) EditorGUILayout.LabelField(p, EditorStyles.miniLabel);
                }
            }
        }

        void DrawSecurity()
        {
            EditorGUILayout.HelpBox(
                "鍵は「パスワード」「キーファイル」「ツール埋め込みシークレット」「利用規約を含むヘッダ」を組み合わせて作られます。\n" +
                "どれか1つでも違えば復号できず、規約を書き換えたファイルも開けません。\n" +
                "パスワードもキーファイルも使わない場合でも暗号化はされますが、このツールさえあれば誰でも（規約に同意すれば）開けます。",
                MessageType.Info);

            usePassword = EditorGUILayout.ToggleLeft("パスワードを設定する", usePassword);
            if (usePassword)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    password = EditorGUILayout.PasswordField("パスワード", password);
                    passwordConfirm = EditorGUILayout.PasswordField("パスワード（確認）", passwordConfirm);
                    passwordHint = EditorGUILayout.TextField("ヒント（任意・公開されます）", passwordHint);
                    if (!string.IsNullOrEmpty(password) && password.Length < 8)
                        EditorGUILayout.HelpBox("8文字以上を推奨します。短いパスワードは総当たりで破られやすくなります。", MessageType.Warning);
                }
            }

            useKeyFile = EditorGUILayout.ToggleLeft("キーファイルを必須にする（パスワードとは別経路で渡す）", useKeyFile);
            if (useKeyFile)
            {
                using (new EditorGUI.IndentLevelScope())
                using (new EditorGUILayout.HorizontalScope())
                {
                    keyFilePath = EditorGUILayout.TextField("キーファイル", keyFilePath);
                    if (GUILayout.Button("選択", GUILayout.Width(50)))
                    {
                        var p = EditorUtility.OpenFilePanel("キーファイルを選択", "", "");
                        if (!string.IsNullOrEmpty(p)) keyFilePath = p;
                    }
                    if (GUILayout.Button("新規作成", GUILayout.Width(70))) CreateKeyFile();
                }
            }

            iterations = EditorGUILayout.IntSlider(new GUIContent("鍵導出の強度（反復回数）",
                    "大きいほど総当たりに強くなりますが、開くときの待ち時間が長くなります。"),
                iterations, LgCrypto.MinIterations, 1_000_000);
        }

        void CreateKeyFile()
        {
            var p = EditorUtility.SaveFilePanel("キーファイルを作成", "", (packageName == "" ? "package" : packageName) + ".lgkey", "lgkey");
            if (string.IsNullOrEmpty(p)) return;
            if (p.Replace('\\', '/').StartsWith(Path.GetFullPath("Assets").Replace('\\', '/')))
            {
                EditorUtility.DisplayDialog("License Guard", "キーファイルはプロジェクトの Assets 外に保存してください。", "OK");
                return;
            }
            File.WriteAllBytes(p, LgCrypto.RandomBytes(64));
            keyFilePath = p;
        }

        string Validate()
        {
            if (targets.All(t => t == null)) return "保護するアセットを追加してください。";
            if (string.IsNullOrWhiteSpace(packageName)) return "パッケージ名を入力してください。";
            if (terms.ShowTable && terms.items.Any(i => string.IsNullOrWhiteSpace(i.label))) return "簡易表示に空の項目があります。";
            if (terms.displayMode == TermsDisplayMode.FreeTextOnly && string.IsNullOrWhiteSpace(terms.freeText)) return "自由記述が空です。";
            if (usePassword)
            {
                if (string.IsNullOrEmpty(password)) return "パスワードを入力してください。";
                if (password != passwordConfirm) return "確認用パスワードが一致しません。";
            }
            if (useKeyFile && !File.Exists(keyFilePath)) return "キーファイルが見つかりません。";
            return null;
        }

        string ToolFolder()
        {
            var script = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
            // .../LicenseGuard/Editor/UI/PackWindow.cs → .../LicenseGuard
            var dir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(script)));
            return dir?.Replace('\\', '/');
        }

        List<string> CollectPaths()
        {
            var roots = new List<string>();
            foreach (var t in targets.Where(t => t != null))
            {
                var p = AssetDatabase.GetAssetPath(t);
                if (string.IsNullOrEmpty(p)) continue;
                if (AssetDatabase.IsValidFolder(p))
                    roots.AddRange(AssetDatabase.FindAssets("", new[] { p })
                        .Select(AssetDatabase.GUIDToAssetPath)
                        .Where(x => !AssetDatabase.IsValidFolder(x)));
                else
                    roots.Add(p);
            }

            var all = includeDependencies ? AssetDatabase.GetDependencies(roots.ToArray(), true) : roots.ToArray();
            var tool = ToolFolder();
            return all
                .Where(p => p.StartsWith("Assets/"))
                .Where(p => string.IsNullOrEmpty(tool) || !p.StartsWith(tool + "/"))
                .Where(p => !p.StartsWith(LicenseRegistry.LicenseFolder + "/"))
                .Where(p => !p.EndsWith(LicenseRegistry.ManifestExtension) && !p.EndsWith(LicenseRegistry.PackageExtension))
                .Distinct()
                .OrderBy(p => p)
                .ToList();
        }

        void Export()
        {
            var paths = CollectPaths();
            if (paths.Count == 0)
            {
                EditorUtility.DisplayDialog("License Guard", "書き出すファイルがありません。", "OK");
                return;
            }
            if (!paths.Any(p => p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
            {
                if (!EditorUtility.DisplayDialog("License Guard", "FBX・プレハブが含まれていません。このまま書き出しますか？", "書き出す", "キャンセル"))
                    return;
            }

            var outPath = EditorUtility.SaveFilePanel("保存先", "", $"{packageName}_{version}{LicenseRegistry.PackageExtension}", "lgpkg");
            if (string.IsNullOrEmpty(outPath)) return;

            var temp = Path.Combine(Path.GetTempPath(), "lg_" + Guid.NewGuid().ToString("N") + ".unitypackage");
            try
            {
                EditorUtility.DisplayProgressBar("License Guard", "unitypackage を作成中…", 0.1f);
                AssetDatabase.ExportPackage(paths.ToArray(), temp, ExportPackageOptions.Default);
                if (!File.Exists(temp)) throw new IOException("unitypackage の作成に失敗しました。");

                var salt = LgCrypto.RandomBytes(LgCrypto.SaltSize);
                var termsCopy = JsonUtility.FromJson<LicenseTerms>(JsonUtility.ToJson(terms));
                var header = new LgHeader
                {
                    licenseId = Guid.NewGuid().ToString("N"),
                    packageName = packageName.Trim(),
                    author = author,
                    version = version,
                    contactUrl = contactUrl,
                    createdAt = DateTime.Now.ToString("yyyy-MM-dd"),
                    terms = termsCopy,
                    termsHash = LgHeader.ComputeTermsHash(termsCopy),
                    requiresPassword = usePassword,
                    passwordHint = usePassword ? passwordHint : "",
                    requiresKeyFile = useKeyFile,
                    salt = Convert.ToBase64String(salt),
                    iterations = iterations,
                    assets = paths.Select(p => new AssetEntry { guid = AssetDatabase.AssetPathToGUID(p), path = p }).ToList(),
                };

                EditorUtility.DisplayProgressBar("License Guard", "鍵を生成中…", 0.2f);
                LgpkgFile.Encrypt(outPath, header.ToBytes(), salt, iterations, temp,
                    new LgpkgFile.Keys { Password = usePassword ? password : null, KeyFilePath = useKeyFile ? keyFilePath : null },
                    p => EditorUtility.DisplayProgressBar("License Guard", "暗号化中…", 0.3f + 0.7f * p));

                EditorUtility.ClearProgressBar();
                var msg = $"書き出しました（{paths.Count} ファイル）。\n{outPath}";
                if (useKeyFile) msg += "\n\nキーファイルは .lgpkg とは別の経路で購入者に渡してください。";
                if (usePassword) msg += "\nパスワードを忘れると作成者自身も復号できません。";
                EditorUtility.DisplayDialog("License Guard", msg, "OK");
                EditorUtility.RevealInFinder(outPath);
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("License Guard", "書き出しに失敗しました: " + ex.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                LgpkgFile.TryDelete(temp);
            }
        }

        void SaveTemplate()
        {
            var p = EditorUtility.SaveFilePanel("規約テンプレートを保存", "", "terms.json", "json");
            if (!string.IsNullOrEmpty(p)) File.WriteAllText(p, JsonUtility.ToJson(terms, true));
        }

        void LoadTemplate()
        {
            var p = EditorUtility.OpenFilePanel("規約テンプレートを読込", "", "json");
            if (string.IsNullOrEmpty(p)) return;
            try
            {
                var t = JsonUtility.FromJson<LicenseTerms>(File.ReadAllText(p));
                if (t != null) terms = t;
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("License Guard", "読み込めませんでした: " + ex.Message, "OK");
            }
        }
    }
}
