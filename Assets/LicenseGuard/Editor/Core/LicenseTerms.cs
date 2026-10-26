using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LicenseGuard
{
    public enum PermissionMark
    {
        Allowed = 0,     // ○ 可
        Denied = 1,      // × 不可
        AskAuthor = 2,   // △ 要相談
        NotSpecified = 3 // ― 指定なし
    }

    public enum CreditRule
    {
        NotRequired = 0,
        Appreciated = 1,
        Required = 2,
    }

    public enum TermsDisplayMode
    {
        Both = 0,         // 簡易表示 + 自由記述
        TableOnly = 1,    // 簡易表示のみ
        FreeTextOnly = 2, // 自由記述のみ
    }

    [Serializable]
    public class TermItem
    {
        public string category;
        public string label;
        public PermissionMark mark;
        public string note;

        public TermItem() { }

        public TermItem(string category, string label, PermissionMark mark, string note = "")
        {
            this.category = category;
            this.label = label;
            this.mark = mark;
            this.note = note;
        }

        public TermItem Clone() => new TermItem(category, label, mark, note);
    }

    /// <summary>
    /// 利用規約。○×形式の簡易表示（項目は自由に追加・削除可能）と自由記述の両方を持てる。
    /// </summary>
    [Serializable]
    public class LicenseTerms
    {
        public string title = "利用規約";
        public TermsDisplayMode displayMode = TermsDisplayMode.Both;
        public List<TermItem> items = new List<TermItem>();
        public CreditRule credit = CreditRule.Appreciated;
        public string creditText = "";
        public string freeText = "";
        public string termsUrl = "";

        public bool ShowTable => displayMode != TermsDisplayMode.FreeTextOnly;
        public bool ShowFreeText => displayMode != TermsDisplayMode.TableOnly;

        public static string MarkSymbol(PermissionMark m)
        {
            switch (m)
            {
                case PermissionMark.Allowed: return "○";
                case PermissionMark.Denied: return "×";
                case PermissionMark.AskAuthor: return "△";
                default: return "―";
            }
        }

        public static string MarkLabel(PermissionMark m)
        {
            switch (m)
            {
                case PermissionMark.Allowed: return "○ 可";
                case PermissionMark.Denied: return "× 不可";
                case PermissionMark.AskAuthor: return "△ 要相談";
                default: return "― 指定なし";
            }
        }

        public static string CreditLabel(CreditRule c)
        {
            switch (c)
            {
                case CreditRule.Required: return "必須";
                case CreditRule.Appreciated: return "任意（あると嬉しい）";
                default: return "不要";
            }
        }

        public static List<TermItem> DefaultItems() => new List<TermItem>
        {
            new TermItem("利用主体", "個人での利用", PermissionMark.Allowed),
            new TermItem("利用主体", "法人・団体での利用", PermissionMark.AskAuthor),

            new TermItem("オンライン", "ソーシャルVR等でのアバター利用（VRChat など）", PermissionMark.Allowed),
            new TermItem("オンライン", "配信・動画投稿での利用", PermissionMark.Allowed),

            new TermItem("表現", "性的表現での利用", PermissionMark.AskAuthor),
            new TermItem("表現", "暴力的表現での利用", PermissionMark.Allowed),
            new TermItem("表現", "政治・宗教・思想活動での利用", PermissionMark.Denied),
            new TermItem("表現", "他者への誹謗中傷・迷惑行為", PermissionMark.Denied),

            new TermItem("改変", "改変", PermissionMark.Allowed),
            new TermItem("改変", "他モデルへのパーツ流用", PermissionMark.AskAuthor),

            new TermItem("配布", "無改変での再配布・譲渡", PermissionMark.Denied),
            new TermItem("配布", "改変データの配布（本データ購入者向け）", PermissionMark.Allowed),
            new TermItem("配布", "改変データの配布（単体で使える形）", PermissionMark.Denied),

            new TermItem("商用", "個人による商用利用", PermissionMark.Allowed),
            new TermItem("商用", "法人による商用利用", PermissionMark.AskAuthor),
            new TermItem("商用", "グッズ化・メディア化", PermissionMark.AskAuthor),

            new TermItem("その他", "AI 学習データとしての利用", PermissionMark.Denied),
            new TermItem("その他", "Unity 以外のソフトウェアへの持ち出し", PermissionMark.Denied),
        };

        public static LicenseTerms CreateDefault() => new LicenseTerms { items = DefaultItems() };

        /// <summary>カテゴリ順を保ったままグループ化する。</summary>
        public IEnumerable<IGrouping<string, TermItem>> GroupedItems() =>
            (items ?? new List<TermItem>()).GroupBy(i => string.IsNullOrEmpty(i.category) ? "その他" : i.category);

        /// <summary>BOOTH の説明文などに貼れるテキスト形式。</summary>
        public string ToPlainText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"■ {title}");
            if (ShowTable)
            {
                sb.AppendLine();
                sb.AppendLine("○ 可 / × 不可 / △ 要相談 / ― 指定なし");
                foreach (var group in GroupedItems())
                {
                    sb.AppendLine($"【{group.Key}】");
                    foreach (var item in group)
                    {
                        sb.Append($"  {MarkSymbol(item.mark)} {item.label}");
                        if (!string.IsNullOrEmpty(item.note)) sb.Append($"（{item.note}）");
                        sb.AppendLine();
                    }
                }
                sb.AppendLine($"【クレジット表記】{CreditLabel(credit)}");
                if (!string.IsNullOrEmpty(creditText)) sb.AppendLine($"  表記例: {creditText}");
            }

            if (ShowFreeText && !string.IsNullOrWhiteSpace(freeText))
            {
                sb.AppendLine();
                sb.AppendLine(freeText.TrimEnd());
            }

            if (!string.IsNullOrEmpty(termsUrl))
            {
                sb.AppendLine();
                sb.AppendLine($"規約全文: {termsUrl}");
            }
            return sb.ToString();
        }
    }
}
