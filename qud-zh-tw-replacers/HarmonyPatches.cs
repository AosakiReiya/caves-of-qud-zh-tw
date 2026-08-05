// HarmonyPatches.cs — Qud 繁中：硬編碼戰鬥訊息補丁
// 遊戲的 melee hit / death / damage / penetration / equip 等訊息由 C# 硬編碼
// 建構（不在本地化系統內），透過 Harmony 前置補丁在訊息加入佇列前翻譯骨架。

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;
using XRL.Messages;

public static class ZhTwHarmonyPatches
{
    private static bool Initialized;

    private static readonly List<Tuple<Regex, string>> Patterns = new List<Tuple<Regex, string>>
    {
        // ==== 玩家命中（含 xN 倍率）====
        Tuple.Create(new Regex(@"^You hit \((x\d+)\) for (\d+) damage with (.+?)(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $3 擊中($1)，造成 $2 傷害$4"),
        Tuple.Create(new Regex(@"^You hit \((x\d+)\) for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用$1 擊中，造成 $2 傷害$3"),
        Tuple.Create(new Regex(@"^You hit (.+?) for (\d+) damage with (.+?)(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $3 擊中 $1，造成 $2 傷害$4"),
        Tuple.Create(new Regex(@"^You hit (.+?) for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你擊中 $1，造成 $2 傷害$3"),
        Tuple.Create(new Regex(@"^You hit for (\d+) damage with (.+?)(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $2 擊中，造成 $1 傷害$3"),
        Tuple.Create(new Regex(@"^You hit for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你擊中，造成 $1 傷害$2"),
        // ==== 生物命中 ====
        Tuple.Create(new Regex(@"^The (.+?) hits \((x\d+)\) for (\d+) damage with (.+?)(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用 $4 擊中($2)，造成 $3 傷害$5"),
        Tuple.Create(new Regex(@"^The (.+?) hits \((x\d+)\) for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用$2 擊中，造成 $3 傷害$4"),
        Tuple.Create(new Regex(@"^The (.+?) hits (.+?) for (\d+) damage with (.+?)(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用 $4 擊中 $2，造成 $3 傷害$5"),
        Tuple.Create(new Regex(@"^The (.+?) hits (.+?) for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 擊中 $2，造成 $3 傷害$4"),
        Tuple.Create(new Regex(@"^The (.+?) hits for (\d+) damage with (.+?)(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用 $3 擊中，造成 $2 傷害$4"),
        Tuple.Create(new Regex(@"^The (.+?) hits for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 擊中，造成 $2 傷害$3"),
        // ==== 落空 ====
        Tuple.Create(new Regex(@"^You miss (.+?)(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你未擊中 $1$2"),
        Tuple.Create(new Regex(@"^The (.+?) misses (.+?)(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 未擊中 $2$3"),
        // ==== 死亡 ====
        Tuple.Create(new Regex(@"^The (.+?) dies[.!]?$", RegexOptions.IgnoreCase), "$1 死亡。"),
        Tuple.Create(new Regex(@"^(.+?) dies[.!]?$", RegexOptions.IgnoreCase), "$1 死亡。"),
        // ==== 受到傷害 ====
        Tuple.Create(new Regex(@"^The (.+?) takes (\d+) damage from (.+?)(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 因 $3 受到 $2 傷害$4"),
        Tuple.Create(new Regex(@"^You take (\d+) damage from (.+?)(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你因 $2 受到 $1 傷害$3"),
        Tuple.Create(new Regex(@"^You take (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你受到 $1 傷害$2"),
        Tuple.Create(new Regex(@"^The (.+?) takes (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 受到 $2 傷害$3"),
        // ==== 穿透失敗 ====
        Tuple.Create(new Regex(@"^You don't penetrate (?:the )?(.+?)'s armor with (.+?)(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你未能用 $2 穿透 $1 的護甲$3"),
        Tuple.Create(new Regex(@"^You don't penetrate (?:the )?(.+?)'s armor(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你未能穿透 $1 的護甲$2"),
        // ==== 裝備/卸下/開始/停止（英文骨架）====
        Tuple.Create(new Regex(@"^You equip the (.+?)[.!]?$", RegexOptions.IgnoreCase), "你裝備了 $1。"),
        Tuple.Create(new Regex(@"^You equip (.+?)[.!]?$", RegexOptions.IgnoreCase), "你裝備了 $1。"),
        Tuple.Create(new Regex(@"^You unequip the (.+?)[.!]?$", RegexOptions.IgnoreCase), "你卸下了 $1。"),
        Tuple.Create(new Regex(@"^You begin (.+?)[.!]?$", RegexOptions.IgnoreCase), "你開始 $1"),
        Tuple.Create(new Regex(@"^You stop (.+?)[.!]?$", RegexOptions.IgnoreCase), "你停止 $1"),
        // ==== 移動受阻 ====
        Tuple.Create(new Regex(@"^The way is blocked by (.+?)[.!]?$", RegexOptions.IgnoreCase), "道路被 $1 阻擋"),
        Tuple.Create(new Regex(@"^There is (.+?) in (your|the|my|its) way[.!]?$", RegexOptions.IgnoreCase), "$1 擋在你面前"),
        Tuple.Create(new Regex(@"^(.+?) is in (your|the|my|its) way[.!]?$", RegexOptions.IgnoreCase), "$1 擋在你面前"),
        Tuple.Create(new Regex(@"^You can't move there[.!]?$", RegexOptions.IgnoreCase), "你無法移動到那裡"),
        Tuple.Create(new Regex(@"^You cannot move there[.!]?$", RegexOptions.IgnoreCase), "你無法移動到那裡"),
    };

    private static readonly Regex Possessive = new Regex(@"\b(his|her|its|their|your)\b", RegexOptions.IgnoreCase);
    private static readonly Regex LeadingArticle = new Regex(@"\b(?:the|a|an)\s+(?=[\u4e00-\u9fff])", RegexOptions.IgnoreCase);

    public static void Init()
    {
        if (Initialized) return;
        Initialized = true;
        try
        {
            Harmony harmony = new Harmony("qud_zh_tw_replacers.harmony");
            int patched = 0;
            foreach (var mi in AccessTools.GetDeclaredMethods(typeof(MessageQueue)))
            {
                if (mi.Name == "AddPlayerMessage" || (mi.Name == "Add" && mi.GetParameters().Length == 1 && mi.GetParameters()[0].ParameterType == typeof(string)))
                {
                    harmony.Patch(mi, prefix: new HarmonyMethod(typeof(ZhTwHarmonyPatches), nameof(AddMsgPrefix)));
                    patched++;
                }
            }
            ZhTwReplacers.Log("Harmony patched AddPlayerMessage/Add x" + patched);
        }
        catch (Exception e)
        {
            ZhTwReplacers.Log("Harmony Init error: " + e.GetType().Name + " " + e.Message);
        }
    }

    public static void AddMsgPrefix(ref string __0)
    {
        try
        {
            if (string.IsNullOrEmpty(__0)) return;
            string t = Translate(__0);
            if (t != __0)
            {
                ZhTwReplacers.Log("TRANSLATED: '" + __0 + "' -> '" + t + "'");
                __0 = t;
            }
        }
        catch
        {
        }
    }

    private static readonly Regex MarkupInner = new Regex(@"\{\{([^}|]*)\|([^{}]*)\}\}");
    private static readonly Regex InYourWay = new Regex(@"there is (.+?) in (your|the|my|its) way", RegexOptions.IgnoreCase);

    private static string StripMarkup(string msg)
    {
        // 循環剝離最內層 {{X|text}} markup，只取 | 後的文字（支援巢狀）
        string result = msg;
        for (int i = 0; i < 8; i++)
        {
            string next = MarkupInner.Replace(result, "$2");
            if (next == result) break;
            result = next;
        }
        return result;
    }

    private static string Translate(string msg)
    {
        // 記住外層顏色 wrapper（如 {{g|...}} / {{G|...}}），翻譯後重新包上
        string outerColor = null;
        string inner = msg.Trim();
        var om = Regex.Match(inner, @"^\{\{([^}|]*)\|(.*)\}\}$", RegexOptions.Singleline);
        if (om.Success)
        {
            outerColor = om.Groups[1].Value;
            inner = om.Groups[2].Value.Trim();
        }
        // 嘗試匹配模式：命中則剝 markup 後翻譯
        string plain = StripMarkup(inner).Trim();
        foreach (var kv in Patterns)
        {
            if (kv.Item1.IsMatch(plain))
            {
                string result = kv.Item1.Replace(plain, kv.Item2).Trim();
                result = Possessive.Replace(result, new MatchEvaluator(PossPronoun));
                result = LeadingArticle.Replace(result, "");
                result = result.Replace("  ", " ");
                if (outerColor != null) result = "{{" + outerColor + "|" + result + "}}";
                return result;
            }
        }
        // 未命中模式：保留原始 markup（顏色），只做冠詞剝除 + 中段「there is X in your way」
        string cleaned = LeadingArticle.Replace(msg, "").Replace("  ", " ");
        cleaned = InYourWay.Replace(cleaned, "$1 擋在你面前");
        return cleaned != msg ? cleaned : msg;
    }

    private static string PossPronoun(Match mm)
    {
        string w = mm.Value.ToLowerInvariant();
        switch (w)
        {
            case "his": return "他的";
            case "her": return "她的";
            case "its": return "它的";
            case "their": return "他們的";
            case "your": return "你的";
            default: return "它的";
        }
    }
}