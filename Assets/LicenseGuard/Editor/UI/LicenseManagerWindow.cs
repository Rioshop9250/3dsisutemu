using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LicenseGuard
{
    /// <summary>プロジェクト内の保護パッケージ一覧。規約の確認・同意・同意の撤回を行う。</summary>
    class LicenseManagerWindow : EditorWindow
    {
        [SerializeField] string selectedId;
        bool agreeCheck, showAssets;
        Vector2 listScroll, detailScroll;

        [MenuItem("Tools/License Guard/ライセンス管理", priority = 3)]
        static void OpenFromMenu() => Open(null);

        public static void Open(LicenseManifest select)
        {
            var w = GetWindow<LicenseManagerWindow>("LG ライセンス管理");
            w.minSize = new Vector2(640, 420);
            if (select != null && w.selectedId != select.licenseId)
            {
                w.selectedId = select.licenseId;
                w.agreeCheck = false;
            }
            w.Show();
            w.Focus();
        }

        void OnEnable() => LicenseRegistry.Changed += Repaint;
        void OnDisable() => LicenseRegistry.Changed -= Repaint;

        void OnGUI()
        {
            var manifests = LicenseRegistry.Manifests;
            if (manifests.Count == 0)
            {
                EditorGUILayout.HelpBox("このプロジェクトに保護パッケージはありません。", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(220)))
                {
                    listScroll = EditorGUILayout.BeginScrollView(listScroll);
                    foreach (var m in manifests)
                    {
                        bool ok = LicenseRegistry.IsAgreed(m);
                        var label = (ok ? "[同意済] " : "[未同意] ") + m.DisplayName;
                        var style = m.licenseId == selectedId ? EditorStyles.boldLabel : EditorStyles.label;
                        if (GUILayout.Button(label, style))
                        {
                            selectedId = m.licenseId;
                            agreeCheck = false;
                        }
                    }
                    EditorGUILayout.EndScrollView();
                }

                var sel = manifests.FirstOrDefault(m => m.licenseId == selectedId) ?? manifests[0];
                selectedId = sel.licenseId;
                using (new EditorGUILayout.VerticalScope())
                    DrawDetail(sel);
            }
        }

        void DrawDetail(LicenseManifest m)
        {
            bool agreed = LicenseRegistry.IsAgreed(m);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(m.DisplayName, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("作者", m.author);
                EditorGUILayout.LabelField("バージョン", m.version);
                var rec = AgreementStore.Find(m.licenseId, m.termsHash);
                EditorGUILayout.LabelField("状態", agreed ? $"同意済み（{rec?.agreedAt} / {rec?.agreedBy}）" : "未同意 — このプロジェクトでは使用できません");
            }

            detailScroll = EditorGUILayout.BeginScrollView(detailScroll, GUILayout.ExpandHeight(true));
            TermsGUI.Draw(m.terms);
            EditorGUILayout.Space(6);
            showAssets = EditorGUILayout.Foldout(showAssets, $"保護されているファイル（{m.assets.Count} 件）", true);
            if (showAssets)
                foreach (var a in m.assets)
                    EditorGUILayout.LabelField(a.path, EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();

            if (agreed)
            {
                if (GUILayout.Button("同意を撤回する（このプロジェクトで使用不可にする）") &&
                    EditorUtility.DisplayDialog("License Guard", "同意を撤回すると、シーン内のこのアセットは取り除かれます。よろしいですか？", "撤回", "キャンセル"))
                {
                    AgreementStore.Revoke(m.licenseId);
                }
            }
            else
            {
                agreeCheck = EditorGUILayout.ToggleLeft("上記の利用規約をすべて読み、同意します", agreeCheck, EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(!agreeCheck))
                {
                    if (GUILayout.Button("同意する", GUILayout.Height(28)))
                    {
                        AgreementStore.Agree(m.licenseId, m.termsHash, m.packageName);
                        agreeCheck = false;
                    }
                }
            }
        }
    }
}
