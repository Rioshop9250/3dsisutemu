using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LicenseGuard
{
    /// <summary>利用規約の表示・編集 GUI。</summary>
    static class TermsGUI
    {
        static GUIStyle _wrap, _bold, _markStyle, _header;

        static void Init()
        {
            if (_wrap != null) return;
            _wrap = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = false };
            _bold = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true };
            _header = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14, wordWrap = true };
            _markStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter };
        }

        public static Color MarkColor(PermissionMark m)
        {
            bool dark = EditorGUIUtility.isProSkin;
            switch (m)
            {
                case PermissionMark.Allowed: return dark ? new Color(0.45f, 0.85f, 0.5f) : new Color(0.1f, 0.55f, 0.2f);
                case PermissionMark.Denied: return dark ? new Color(1f, 0.45f, 0.45f) : new Color(0.75f, 0.1f, 0.1f);
                case PermissionMark.AskAuthor: return dark ? new Color(1f, 0.8f, 0.3f) : new Color(0.7f, 0.45f, 0f);
                default: return dark ? new Color(0.65f, 0.65f, 0.65f) : new Color(0.4f, 0.4f, 0.4f);
            }
        }

        /// <summary>読み取り専用の規約表示。</summary>
        public static void Draw(LicenseTerms t)
        {
            Init();
            if (t == null) return;

            EditorGUILayout.LabelField(string.IsNullOrEmpty(t.title) ? "利用規約" : t.title, _header);
            EditorGUILayout.Space(4);

            if (t.ShowTable)
            {
                EditorGUILayout.LabelField("○ 可 / × 不可 / △ 要相談 / ― 指定なし", EditorStyles.miniLabel);
                foreach (var group in t.GroupedItems())
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField(group.Key, _bold);
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        foreach (var item in group) DrawRow(item);
                    }
                }

                EditorGUILayout.Space(2);
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField("クレジット表記", GUILayout.Width(110));
                    EditorGUILayout.LabelField(LicenseTerms.CreditLabel(t.credit), _bold);
                }
                if (!string.IsNullOrEmpty(t.creditText))
                    EditorGUILayout.LabelField("表記例: " + t.creditText, _wrap);
            }

            if (t.ShowFreeText && !string.IsNullOrWhiteSpace(t.freeText))
            {
                EditorGUILayout.Space(6);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.SelectableLabel(t.freeText, _wrap,
                        GUILayout.Height(_wrap.CalcHeight(new GUIContent(t.freeText), EditorGUIUtility.currentViewWidth - 50)));
                }
            }

            if (!string.IsNullOrEmpty(t.termsUrl))
            {
                EditorGUILayout.Space(4);
                if (EditorGUILayout.LinkButton("規約全文: " + t.termsUrl))
                    Application.OpenURL(t.termsUrl);
            }
        }

        static void DrawRow(TermItem item)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var prev = _markStyle.normal.textColor;
                _markStyle.normal.textColor = MarkColor(item.mark);
                GUILayout.Label(LicenseTerms.MarkLabel(item.mark), _markStyle, GUILayout.Width(80));
                _markStyle.normal.textColor = prev;

                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(item.label, _wrap);
                    if (!string.IsNullOrEmpty(item.note))
                        EditorGUILayout.LabelField(item.note, EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        static readonly string[] MarkOptions =
        {
            LicenseTerms.MarkLabel(PermissionMark.Allowed),
            LicenseTerms.MarkLabel(PermissionMark.Denied),
            LicenseTerms.MarkLabel(PermissionMark.AskAuthor),
            LicenseTerms.MarkLabel(PermissionMark.NotSpecified),
        };

        static readonly string[] CreditOptions =
        {
            LicenseTerms.CreditLabel(CreditRule.NotRequired),
            LicenseTerms.CreditLabel(CreditRule.Appreciated),
            LicenseTerms.CreditLabel(CreditRule.Required),
        };

        static readonly string[] ModeOptions =
        {
            "簡易表示（○×表）＋ 自由記述",
            "簡易表示（○×表）のみ",
            "自由記述のみ",
        };

        /// <summary>規約の編集 GUI。</summary>
        public static void DrawEditor(LicenseTerms t, ref Vector2 freeTextScroll)
        {
            Init();
            t.title = EditorGUILayout.TextField("タイトル", t.title);
            t.displayMode = (TermsDisplayMode)EditorGUILayout.Popup("表示形式", (int)t.displayMode, ModeOptions);
            t.termsUrl = EditorGUILayout.TextField("規約全文URL（任意）", t.termsUrl);

            if (t.ShowTable)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("簡易表示（項目は自由に追加・削除できます）", _bold);
                if (t.items == null) t.items = new List<TermItem>();

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("分類", EditorStyles.miniBoldLabel, GUILayout.Width(80));
                    GUILayout.Label("項目", EditorStyles.miniBoldLabel);
                    GUILayout.Label("可否", EditorStyles.miniBoldLabel, GUILayout.Width(90));
                    GUILayout.Label("補足", EditorStyles.miniBoldLabel, GUILayout.Width(160));
                    GUILayout.Space(70);
                }

                int remove = -1, up = -1;
                for (int i = 0; i < t.items.Count; i++)
                {
                    var item = t.items[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        item.category = EditorGUILayout.TextField(item.category, GUILayout.Width(80));
                        item.label = EditorGUILayout.TextField(item.label);
                        var c = GUI.contentColor;
                        GUI.contentColor = MarkColor(item.mark);
                        item.mark = (PermissionMark)EditorGUILayout.Popup((int)item.mark, MarkOptions, GUILayout.Width(90));
                        GUI.contentColor = c;
                        item.note = EditorGUILayout.TextField(item.note, GUILayout.Width(160));
                        using (new EditorGUI.DisabledScope(i == 0))
                            if (GUILayout.Button("↑", GUILayout.Width(24))) up = i;
                        if (GUILayout.Button("削除", GUILayout.Width(40))) remove = i;
                    }
                }
                if (up > 0)
                {
                    (t.items[up - 1], t.items[up]) = (t.items[up], t.items[up - 1]);
                }
                if (remove >= 0) t.items.RemoveAt(remove);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("＋ 項目を追加"))
                    {
                        var cat = t.items.Count > 0 ? t.items[t.items.Count - 1].category : "その他";
                        t.items.Add(new TermItem(cat, "", PermissionMark.NotSpecified));
                    }
                    if (GUILayout.Button("初期項目に戻す", GUILayout.Width(120)) &&
                        EditorUtility.DisplayDialog("License Guard", "簡易表示の項目を初期状態に戻しますか？", "戻す", "キャンセル"))
                    {
                        t.items = LicenseTerms.DefaultItems();
                    }
                }

                EditorGUILayout.Space(4);
                t.credit = (CreditRule)EditorGUILayout.Popup("クレジット表記", (int)t.credit, CreditOptions);
                t.creditText = EditorGUILayout.TextField("表記例（任意）", t.creditText);
            }

            if (t.ShowFreeText)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("自由記述", _bold);
                freeTextScroll = EditorGUILayout.BeginScrollView(freeTextScroll, GUILayout.Height(180));
                t.freeText = EditorGUILayout.TextArea(t.freeText ?? "", new GUIStyle(EditorStyles.textArea) { wordWrap = true },
                    GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }
    }
}
