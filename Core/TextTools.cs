using System.Text;
using System.Text.RegularExpressions;

namespace PdfTranslator.Core;

public enum Script { None, Latin, Han, Kana, Hangul, Cyrillic, Greek, Arabic, Hebrew, Thai, Devanagari, Other }

/// <summary>Script detection, list-numbering handling and punctuation clean-up.</summary>
public static class TextTools
{
    public static Script ScriptOf(char c)
    {
        if (!char.IsLetter(c)) return Script.None;
        if (c < 0x250) return Script.Latin;
        if (c is >= 'Ͱ' and <= 'Ͽ') return Script.Greek;
        if (c is >= 'Ѐ' and <= 'ӿ') return Script.Cyrillic;
        if (c is >= '֐' and <= '׿') return Script.Hebrew;
        if (c is >= '؀' and <= 'ۿ') return Script.Arabic;
        if (c is >= 'ऀ' and <= 'ॿ') return Script.Devanagari;
        if (c is >= '฀' and <= '๿') return Script.Thai;
        if (c is >= '぀' and <= 'ヿ') return Script.Kana;
        if (c is >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or >= '豈' and <= '﫿') return Script.Han;
        if (c is >= '가' and <= '힯') return Script.Hangul;
        return Script.Other;
    }

    public static bool IsCjk(char c) => ScriptOf(c) is Script.Han or Script.Kana || c is >= '　' and <= '〿' || c is >= '＀' and <= '￯';

    public static Dictionary<Script, int> CountScripts(string s)
    {
        var d = new Dictionary<Script, int>();
        foreach (var c in s)
        {
            var sc = ScriptOf(c);
            if (sc != Script.None) d[sc] = d.GetValueOrDefault(sc) + 1;
        }
        return d;
    }

    /// <summary>The script most letters on a page are written in.</summary>
    public static Script Dominant(IEnumerable<string> texts)
    {
        var total = new Dictionary<Script, int>();
        foreach (var t in texts)
            foreach (var (k, v) in CountScripts(t)) total[k] = total.GetValueOrDefault(k) + v;
        if (total.Count == 0) return Script.None;
        // Japanese mixes Han and Kana; count them together.
        if (total.ContainsKey(Script.Kana)) total[Script.Kana] += total.GetValueOrDefault(Script.Han);
        return total.MaxBy(kv => kv.Value).Key;
    }

    public static Script ScriptOfLanguage(string code) => code switch
    {
        "zh-CN" or "zh-TW" => Script.Han,
        "ja" => Script.Kana,
        "ko" => Script.Hangul,
        "ru" or "uk" or "bg" => Script.Cyrillic,
        "el" => Script.Greek,
        "ar" => Script.Arabic,
        "he" => Script.Hebrew,
        "th" => Script.Thai,
        "hi" => Script.Devanagari,
        "auto" => Script.None,
        _ => Script.Latin,
    };

    /// <summary>
    /// Whether a block needs translating. On a page written in a non-Latin script, blocks with no letters of
    /// that script (part numbers, codes, "SIEMENS", "F52983/4") are left alone.
    /// </summary>
    public static bool ShouldTranslate(string text, Script pageScript)
    {
        if (!text.Any(char.IsLetter)) return false;
        if (pageScript is Script.None or Script.Latin) return true;
        var counts = CountScripts(text);
        if (pageScript == Script.Kana) return counts.ContainsKey(Script.Kana) || counts.ContainsKey(Script.Han);
        return counts.ContainsKey(pageScript);
    }

    /// <summary>Language code guessed from the script of the text, or null when it's ambiguous (Latin).</summary>
    public static string? GuessLanguage(string text)
    {
        var c = CountScripts(text);
        if (c.ContainsKey(Script.Kana)) return "ja";
        if (c.ContainsKey(Script.Hangul)) return "ko";
        if (c.ContainsKey(Script.Han)) return "zh-CN";
        if (c.ContainsKey(Script.Cyrillic)) return "ru";
        if (c.ContainsKey(Script.Greek)) return "el";
        if (c.ContainsKey(Script.Arabic)) return "ar";
        if (c.ContainsKey(Script.Hebrew)) return "he";
        if (c.ContainsKey(Script.Thai)) return "th";
        if (c.ContainsKey(Script.Devanagari)) return "hi";
        return null;
    }

    // ------------------------------------------------------------------ numbering

    const string CnDigits = "零一二三四五六七八九十";

    static readonly Regex NumberPrefix = new(
        @"^\s*(?:" +
        @"(?<cn>[一二三四五六七八九十]{1,3})\s*[、．.，,]" +            // 一．  二、
        @"|[（(]\s*(?<pn>\d{1,2}|[一二三四五六七八九十]{1,3})\s*[)）]" +   // (1)  （一）
        @"|(?<n>\d{1,2})\s*(?<sep>[)）.．、])(?!\d)" +                    // 1)  1．  1、   (not 1.5)
        @"|(?<circ>[①-⒇])" +                                    // ①..⑳  ⑴..⒇
        @"|(?<dot>[⒈-⒛])" +                                     // ⒈..⒛
        @"|(?<bullet>[•·▪■◆●◇○□])" +
        @")\s*");

    /// <summary>True when the text starts like a list item or numbered heading.</summary>
    public static bool StartsWithListMarker(string text) => NumberPrefix.IsMatch(text);

    /// <summary>Splits a leading list number off so it is not translated (MyMemory turns "二．" into "two.").</summary>
    public static (string prefix, string body) SplitNumbering(string text)
    {
        var m = NumberPrefix.Match(text);
        if (!m.Success || m.Length >= text.Length) return ("", text);
        return (m.Value.Trim(), text[m.Length..].Trim());
    }

    /// <summary>Rewrites a list number for a Latin-script target: 一． → "1.", （1） → "(1)", ① → "(1)".</summary>
    public static string LocalizeNumbering(string prefix, string target)
    {
        if (prefix.Length == 0) return "";
        if (Languages.IsCjk(target)) return prefix;
        var m = NumberPrefix.Match(prefix);
        if (!m.Success) return prefix;
        if (m.Groups["cn"].Success) return CnNumber(m.Groups["cn"].Value) + ".";
        if (m.Groups["pn"].Success)
        {
            var v = m.Groups["pn"].Value;
            return "(" + (char.IsDigit(v[0]) ? v : CnNumber(v).ToString()) + ")";
        }
        if (m.Groups["n"].Success) return m.Groups["n"].Value + (m.Groups["sep"].Value is ")" or "）" ? ")" : ".");
        if (m.Groups["circ"].Success) return "(" + EnclosedNumber(m.Groups["circ"].Value[0]) + ")";
        if (m.Groups["dot"].Success) return EnclosedNumber(m.Groups["dot"].Value[0]) + ".";
        return m.Groups["bullet"].Value == "□" ? "•" : m.Groups["bullet"].Value;
    }

    /// <summary>Value of ①..⑳ (U+2460), ⑴..⒇ (U+2474) or ⒈..⒛ (U+2488).</summary>
    static int EnclosedNumber(char c) => c switch
    {
        >= '⒈' => c - '⒈' + 1,
        >= '⑴' => c - '⑴' + 1,
        _ => c - '①' + 1,
    };

    /// <summary>Script an OCR recogniser language tag reads ("zh-Hans-CN" → Han).</summary>
    public static Script ScriptOfOcrTag(string tag)
    {
        var t = tag.ToLowerInvariant();
        if (t.StartsWith("zh")) return Script.Han;
        if (t.StartsWith("ja")) return Script.Kana;
        if (t.StartsWith("ko")) return Script.Hangul;
        if (t.StartsWith("ru") || t.StartsWith("uk") || t.StartsWith("bg") || t.StartsWith("sr")) return Script.Cyrillic;
        if (t.StartsWith("el")) return Script.Greek;
        if (t.StartsWith("ar")) return Script.Arabic;
        if (t.StartsWith("he")) return Script.Hebrew;
        return Script.Latin;
    }

    static int CnNumber(string s)
    {
        int Digit(char c) => CnDigits.IndexOf(c);
        if (s.Length == 1) return Digit(s[0]);
        if (s.Length == 2 && s[0] == '十') return 10 + Digit(s[1]);
        if (s.Length == 2 && s[1] == '十') return Digit(s[0]) * 10;
        if (s.Length == 3 && s[1] == '十') return Digit(s[0]) * 10 + Digit(s[2]);
        return 0;
    }

    // ------------------------------------------------------------------ contents lines

    static readonly Regex Leader = new(
        @"^(?<body>.*?\S)\s*(?:[\.。·…・‧∙⋯]{4,}|(?:\.\s){4,})\s*(?<ref>[0-9０-９A-Za-z][0-9０-９A-Za-z\-－‐–—\.．]{0,12})\s*$");

    /// <summary>True for a contents line: a title, dot leaders, then a page reference ("铭牌说明……1-1").</summary>
    public static bool HasLeader(string text) => Leader.IsMatch(text);

    /// <summary>Splits a contents line into its title and page reference; ref is null when it isn't one.</summary>
    public static (string body, string? pageRef) SplitLeader(string text)
    {
        var m = Leader.Match(text);
        return m.Success ? (m.Groups["body"].Value.Trim(), m.Groups["ref"].Value.Trim()) : (text, null);
    }

    // ------------------------------------------------------------------ punctuation

    /// <summary>
    /// Converts CJK punctuation and full-width forms to their ASCII equivalents for Latin-script output, so the
    /// output font has glyphs for them (avoids □ boxes) and spacing looks natural.
    /// </summary>
    public static string NormalizeForTarget(string text, string target)
    {
        if (Languages.IsCjk(target)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case >= '！' and <= '～': sb.Append((char)(c - 0xFEE0)); break; // full-width ASCII
                case '　': sb.Append(' '); break;
                case '、': sb.Append(", "); break;
                case '。': sb.Append(". "); break;
                case '【' or '〔' or '「' or '『': sb.Append('['); break;
                case '】' or '〕' or '」' or '』': sb.Append(']'); break;
                case '《' or '〈': sb.Append('"'); break;
                case '》' or '〉': sb.Append('"'); break;
                case >= '①' and <= '⒇': sb.Append('(').Append(EnclosedNumber(c)).Append(") "); break;
                case >= '⒈' and <= '⒛': sb.Append(EnclosedNumber(c)).Append(". "); break;
                case '' or '' or '' or '' or '' or '': sb.Append("• "); break; // Symbol/Wingdings bullets
                default: sb.Append(c); break;
            }
        }
        // Tidy spacing introduced above: no space before punctuation, single spaces.
        var s = Regex.Replace(sb.ToString(), @"[ \t]+", " ");
        s = Regex.Replace(s, @" ([,.;:!?)\]])", "$1");
        s = Regex.Replace(s, @"([(\[]) ", "$1");
        return s.Trim();
    }
}
