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
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You hit \((x\d+)\) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $3 擊中($1)，造成 $2 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You hit \((x\d+)\) for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用$1 擊中，造成 $2 傷害$3"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You hit (.+?) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $3 擊中 $1，造成 $2 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You hit (.+?) for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你擊中 $1，造成 $2 傷害$3"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You hit for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $2 擊中，造成 $1 傷害$3"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You hit for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你擊中，造成 $1 傷害$2"),
        // ==== 玩家爆擊（critical hit 變體）====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You critically hit \((x\d+)\) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $3 爆擊($1)，造成 $2 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You critically hit \((x\d+)\) for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你爆擊($1)，造成 $2 傷害$3"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You critically hit (.+?) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $3 爆擊 $1，造成 $2 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You critically hit (.+?) for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你爆擊 $1，造成 $2 傷害$3"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You critically hit for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $2 爆擊，造成 $1 傷害$3"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You critically hit for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你爆擊，造成 $1 傷害$2"),
        // ==== 生物爆擊 ====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) critically hits (.+?) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用 $4 爆擊 $2，造成 $3 傷害$5"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) critically hits for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用 $3 爆擊，造成 $2 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) critically hits (.+?) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用 $4 爆擊 $2，造成 $3 傷害$5"),
        // ==== 開始流血 ====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) begins bleeding[.!]?$", RegexOptions.IgnoreCase), "$1 開始流血了！"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You begin bleeding[.!]?$", RegexOptions.IgnoreCase), "你開始流血了！"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) begins bleeding[.!]?$", RegexOptions.IgnoreCase), "$1 開始流血了！"),
        // ==== 玩家爆擊（critical hit 變體）====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You critically hit \((x\d+)\) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $3 爆擊($1)，造成 $2 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You critically hit \((x\d+)\) for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你爆擊($1)，造成 $2 傷害$3"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You critically hit (.+?) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $3 爆擊 $1，造成 $2 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You critically hit (.+?) for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你爆擊 $1，造成 $2 傷害$3"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You critically hit for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $2 爆擊，造成 $1 傷害$3"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You critically hit for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你爆擊，造成 $1 傷害$2"),
        // ==== 開始流血 ====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) begins bleeding[.!]?$", RegexOptions.IgnoreCase), "$1 開始流血了！"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You begin bleeding[.!]?$", RegexOptions.IgnoreCase), "你開始流血了！"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) begins bleeding[.!]?$", RegexOptions.IgnoreCase), "$1 開始流血了！"),
        // ==== 生物命中 ====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) hits \((x\d+)\) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用 $4 擊中($2)，造成 $3 傷害$5"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) hits \((x\d+)\) for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用$2 擊中，造成 $3 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) hits (.+?) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用 $4 擊中 $2，造成 $3 傷害$5"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) hits (.+?) for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 擊中 $2，造成 $3 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) hits for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用 $3 擊中，造成 $2 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) hits for (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 擊中，造成 $2 傷害$3"),
        // ==== 落空 ====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You miss with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你未擊中（用 $1）$2"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) misses (.+?) with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 未擊中 $2（用 $3）$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You miss (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你未擊中 $1$2"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) misses (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 未擊中 $2$3"),
        // ==== 死亡 ====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) dies[.!]?$", RegexOptions.IgnoreCase), "$1 死亡。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) dies[.!]?$", RegexOptions.IgnoreCase), "$1 死亡。"),
        // ==== 受到傷害 ====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) takes (\d+) damage from (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 因 $3 受到 $2 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You take (\d+) damage from (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你因 $2 受到 $1 傷害$3"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You take (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你受到 $1 傷害$2"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The (.+?) takes (\d+) damage(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 受到 $2 傷害$3"),
        // ==== 穿透失敗 ====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You don't penetrate (?:the )?(.+?)'s armor with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你未能用 $2 穿透 $1 的護甲 $3"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You don't penetrate (?:the )?(.+?)'s armor(?:!? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你未能穿透 $1 的護甲 $2"),
        // ==== 中英混合 combat（=subject.Does:hit= 等 token 已在訊息建構時轉成「擊中」，輸入為「X 擊中 (x1) for N damage with Y」）====
        // ---- 玩家命中 ----
        Tuple.Create(new Regex(@"^你 擊中 \((x\d+)\) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $3 擊中($1)，造成 $2 傷害$4"),
        Tuple.Create(new Regex(@"^你 擊中 \((x\d+)\) for (\d+) damage(?:[.!]? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你擊中($1)，造成 $2 傷害$3"),
        Tuple.Create(new Regex(@"^你 擊中 (.+?) \((x\d+)\) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $4 擊中 $1($2)，造成 $3 傷害$5"),
        Tuple.Create(new Regex(@"^你 擊中 (.+?) \((x\d+)\) for (\d+) damage(?:[.!]? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你擊中 $1($2)，造成 $3 傷害$4"),
        Tuple.Create(new Regex(@"^你 擊中 for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你用 $2 擊中，造成 $1 傷害$3"),
        Tuple.Create(new Regex(@"^你 擊中 for (\d+) damage(?:[.!]? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你擊中，造成 $1 傷害$2"),
        // ---- 生物命中 ----
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) 擊中 \((x\d+)\) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用 $4 擊中($2)，造成 $3 傷害$5"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) 擊中 \((x\d+)\) for (\d+) damage(?:[.!]? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 擊中($2)，造成 $3 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) 擊中 (.+?) \((x\d+)\) for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用 $5 擊中 $2($3)，造成 $4 傷害$6"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) 擊中 (.+?) \((x\d+)\) for (\d+) damage(?:[.!]? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 擊中 $2($3)，造成 $4 傷害$5"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) 擊中 for (\d+) damage with (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 用 $3 擊中，造成 $2 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) 擊中 for (\d+) damage(?:[.!]? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 擊中，造成 $2 傷害$3"),
        // ---- 受到傷害（=verb:take= 已轉「受到」）----
        Tuple.Create(new Regex(@"^你 受到 (\d+) damage from (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你因 $2 受到 $1 傷害$3"),
        Tuple.Create(new Regex(@"^你 受到 (\d+) damage(?:[.!]? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你受到 $1 傷害$2"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) 受到 (\d+) damage from (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 因 $3 受到 $2 傷害$4"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) 受到 (\d+) damage(?:[.!]? ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 受到 $2 傷害$3"),
        // ---- 落空（=verb:miss= 已轉「落空」）----
        Tuple.Create(new Regex(@"^你 落空 (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "你未擊中 $1$2"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) 落空 (.+?)[.!]?(?: ?(\[[^\]]*\]))?$", RegexOptions.IgnoreCase), "$1 未擊中 $2$3"),
        // ==== 拾取/奪取語境（英文開頭 + =verb:take= 已轉「受到」形態）====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You take (?:the |a |an )?(.+?) from (.+?)[.!]?$", RegexOptions.IgnoreCase), "你從 $2 拿走了 $1。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You take (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你拿起了 $1。"),
        // ==== 拾取/奪取語境（=verb:take= 已轉「受到」，玩家主詞 token 為空 → 以「受到」開頭）====
        Tuple.Create(new Regex(@"^受到 (?:the |a |an )?(.+?) from (.+?)[.!]?$", RegexOptions.IgnoreCase), "你從 $2 拿走了 $1。"),
        Tuple.Create(new Regex(@"^你 受到 (?:the |a |an )?(.+?) from (.+?)[.!]?$", RegexOptions.IgnoreCase), "你從 $2 拿走了 $1。"),
        Tuple.Create(new Regex(@"^受到 (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你拿起了 $1。"),
        Tuple.Create(new Regex(@"^你 受到 (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你拿起了 $1。"),
        // ==== 裝備/卸下/開始/停止（英文骨架）====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You equip the (.+?)[.!]?$", RegexOptions.IgnoreCase), "你裝備了 $1。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You equip (.+?)[.!]?$", RegexOptions.IgnoreCase), "你裝備了 $1。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You unequip the (.+?)[.!]?$", RegexOptions.IgnoreCase), "你卸下了 $1。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You begin (.+?)[.!]?$", RegexOptions.IgnoreCase), "你開始 $1"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You stop (.+?)[.!]?$", RegexOptions.IgnoreCase), "你停止 $1"),
        // ==== 移動受阻 ====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*The way is blocked by (.+?)[.!]?$", RegexOptions.IgnoreCase), "道路被 $1 阻擋"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*There is (.+?) in (your|the|my|its) way[.!]?$", RegexOptions.IgnoreCase), "$1 擋在你面前"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?) is in (your|the|my|its) way[.!]?$", RegexOptions.IgnoreCase), "$1 擋在你面前"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You can't move there[.!]?$", RegexOptions.IgnoreCase), "你無法移動到那裡"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You cannot move there[.!]?$", RegexOptions.IgnoreCase), "你無法移動到那裡"),
        // ==== 中英混合移動受阻（=subject.T= 已輸出「由於」/「The」/「」等，in your way 逐詞拆碎）====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(?:由於|The|) ?(.+?) 是 (.+?) 在 你的 way,? 你停止了 移動(?:中)?[。.!]?$", RegexOptions.IgnoreCase), "$2 擋住了你的路，你停止了移動。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(?:由於|The|) ?(.+?) 是 (.+?) 在 你的 way[。.!]?$", RegexOptions.IgnoreCase), "$2 擋住了你的路。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(?:由於|The|) ?(.+?) 是 在 你的 way,? 你停止了 移動(?:中)?[。.!]?$", RegexOptions.IgnoreCase), "$1 擋住了你的路，你停止了移動。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(?:由於|The|) ?(.+?) 是 在 你的 way[。.!]?$", RegexOptions.IgnoreCase), "$1 擋住了你的路。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(?:由於|The|) ?(.+?) 在 你的 way,? 你停止了 移動(?:中)?[。.!]?$", RegexOptions.IgnoreCase), "$1 擋住了你的路，你停止了移動。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*(?:由於|The|) ?(.+?) 在 你的 way[。.!]?$", RegexOptions.IgnoreCase), "$1 擋住了你的路。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You are stopped short by (.+?)[.!]?$", RegexOptions.IgnoreCase), "你被 $1 擋住了去路。"),
        // ==== 日誌筆記（硬編碼：You note this piece of information in the {{W|類別}} section of your journal.）====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You note this piece of information in the \{\{W\|(.+?)\}\} section of your journal\.?$", RegexOptions.IgnoreCase), "你在日誌的 {{W|$1}} 區段中記下這項資訊。"),
        // ==== 站起/起身（Prone.cs / Sitting.cs 的 DidX）====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You stand up\.?$", RegexOptions.IgnoreCase), "你站起來了。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You stand up from (.+?)[.!]?$", RegexOptions.IgnoreCase), "你從 $1 站起來。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You rise from (.+?)[.!]?$", RegexOptions.IgnoreCase), "你從 $1 起身。"),
        // ==== XDidYToZ frame（Chair.cs 等組裝句，AddMsgPrefix 多層攔截）====
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You sit down on (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你坐到 $1 上。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You sit down in (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你坐到 $1 裡。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You climb onto (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你爬上 $1。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You jump onto (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你跳到 $1 上。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You wade through (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你涉水穿過 $1。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You swim through (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你游泳穿過 $1。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You emerge from (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你從 $1 現身。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You bump into (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你撞到 $1。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You are engulfed by (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你被 $1 吞噬。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You are dragged toward (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你被拖向 $1。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You are sucked into (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你被吸入 $1。"),
        Tuple.Create(new Regex(@"^[^\u4e00-\u9fffA-Za-z0-9]*You are impaled by (?:the |a |an )?(.+?)[.!]?$", RegexOptions.IgnoreCase), "你被 $1 刺穿。"),
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
                // 攔截所有 Add/AddPlayerMessage 多載（combat 訊息可能走 Add(string, Color) 等）
                // 第一個參數是 string 的才攔（訊息文本），避免誤 patch 其他型別
                if ((mi.Name == "Add" || mi.Name == "AddPlayerMessage") && mi.GetParameters().Length >= 1 &&
                    mi.GetParameters()[0].ParameterType == typeof(string))
                {
                    harmony.Patch(mi, prefix: new HarmonyMethod(typeof(ZhTwHarmonyPatches), nameof(AddMsgPrefix)));
                    patched++;
                }
            }
            ZhTwReplacers.LogAlways("Harmony MessageQueue patched=" + patched);
            // ===== 硬編碼 UI：官方 Strings._S 攔截（3 個多載）=====
            foreach (var mi in AccessTools.GetDeclaredMethods(typeof(XRL.Language.Strings)))
            {
                if (mi.Name != "_S") continue;
                var ps = mi.GetParameters();
                HarmonyMethod prefix = null, postfix = null;
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                    prefix = new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings._SHook1));
                else if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                {
                    prefix = new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings._SHook2));
                    postfix = new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings._SMissPostfix));
                }
                else if (ps.Length == 4)
                    prefix = new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings._SHook3));
                if (prefix != null)
                {
                    harmony.Patch(mi, prefix: prefix, postfix: postfix);
                    patched++;
                }
            }
            // ===== 硬編碼 UI：Popup 顯示層（原始字面值）=====
            foreach (var mi in AccessTools.GetDeclaredMethods(typeof(XRL.UI.Popup)))
            {
                if (mi.Name == "ShowYesNoCancel" && mi.GetParameters().Length >= 1 && mi.GetParameters()[0].Name == "Message")
                    harmony.Patch(mi, prefix: new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings.PopupYesNoPrefix)));
                else if (mi.Name == "PickOption" && mi.ReturnType == typeof(int))
                    harmony.Patch(mi, prefix: new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings.PopupPickPrefix)));
                else if (mi.Name == "ShowOptionList" && mi.ReturnType == typeof(int))
                    harmony.Patch(mi, prefix: new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings.PopupOptionListPrefix)));
                else if (mi.Name == "Show" && mi.GetParameters().Length >= 1 && mi.GetParameters()[0].ParameterType == typeof(string))
                    harmony.Patch(mi, prefix: new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings.PopupShowPrefix)));
            }
            // ===== 側欄/狀態畫面屬性標籤（console 渲染）=====
            // 需用 prefix：Write(string) 在方法體內就把字元畫進 buffer，postfix 改 s 無效
            var sbw = AccessTools.Method(typeof(ConsoleLib.Console.ScreenBuffer), "Write",
                new Type[] { typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(List<string>), typeof(int) });
            if (sbw != null)
                harmony.Patch(sbw, prefix: new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings.SidebarLabelPrefix)));
            // StringBuilder 版（側欄 ST/AG 實際走這條）
            var sbwsb = AccessTools.Method(typeof(ConsoleLib.Console.ScreenBuffer), "Write",
                new Type[] { typeof(System.Text.StringBuilder), typeof(int) });
            if (sbwsb != null)
                harmony.Patch(sbwsb, prefix: new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings.SidebarLabelSBufPrefix)));
            // ===== 區段標題（RESISTANCES/SECONDARY ATTRIBUTES）：對所有 Write 多載掛精確翻譯 =====
            // 角色狀態畫面用的多載不確定，故全掛；只精確匹配 SectionHeaders，安全低開銷
            int writePatched = 0;
            foreach (var wmi in AccessTools.GetDeclaredMethods(typeof(ConsoleLib.Console.ScreenBuffer)))
            {
                if (wmi.Name != "Write") continue;
                var wps = wmi.GetParameters();
                if (wps.Length == 0) continue;
                try
                {
                    if (wps[0].ParameterType == typeof(string))
                    {
                        harmony.Patch(wmi, prefix: new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings.SectionHeaderWritePrefix)));
                        writePatched++;
                    }
                    else if (wps[0].ParameterType == typeof(System.Text.StringBuilder))
                    {
                        harmony.Patch(wmi, prefix: new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings.SectionHeaderSBufWritePrefix)));
                        writePatched++;
                    }
                }
                catch (Exception wex)
                {
                    ZhTwReplacers.LogAlways("Write overload patch skip: " + wmi + " " + wex.Message);
                }
            }
            ZhTwReplacers.LogAlways("SectionHeader Write overloads patched=" + writePatched);
            // ===== BookUI（Active Effects / No active effects. 等）=====
            var book = AccessTools.Method(typeof(XRL.UI.BookUI), "ShowBook",
                new Type[] { typeof(string), typeof(string), typeof(string), typeof(Action<int>), typeof(Action<int>) });
            if (book != null)
                harmony.Patch(book, prefix: new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings.BookShowPrefix)));
            // ===== Unity UI（TMP）區段標題：RESISTANCES/SECONDARY ATTRIBUTES 等 =====
            // 角色狀態畫面是 Unity UI（TMP），不走 console ScreenBuffer，故另掛 TMP_Text.set_text。
            // 用執行期型別查找，避免編譯期對 TMPro 的硬依賴。
            try
            {
                var tmpType = AccessTools.TypeByName("TMPro.TMP_Text");
                if (tmpType != null)
                {
                    var tmpSetText = AccessTools.Method(tmpType, "set_text");
                    if (tmpSetText != null)
                    {
                        harmony.Patch(tmpSetText, prefix: new HarmonyMethod(typeof(ZhTwUiStrings), nameof(ZhTwUiStrings.TmpHeaderPrefix)));
                        ZhTwReplacers.LogAlways("TMP_Text.set_text patched (section headers)");
                    }
                    else ZhTwReplacers.LogAlways("TMP set_text NOT FOUND");
                }
                else ZhTwReplacers.LogAlways("TMPro.TMP_Text type NOT FOUND");
            }
            catch (Exception tmpEx)
            {
                ZhTwReplacers.LogAlways("TMP patch skip: " + tmpEx.Message);
            }
            ZhTwReplacers.LogAlways("Harmony all patches done, total=" + patched);
            // ===== 全域文本後處理（TextMeshProUGUI.text）=====
            ZhTwTextCleaner.Init();
        }
        catch (Exception e)
        {
            ZhTwReplacers.LogAlways("Harmony Init error: " + e.GetType().Name + " " + e.Message + "\n" + e.StackTrace);
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
            else
            {
                // 未命中任何模式、原樣通過 → 未替換的英文訊息（供 scan_replacer_log.py 搜尋）
                ZhTwReplacers.Log("UNTRANSLATED: '" + __0 + "'");
            }
        }
        catch (Exception e)
        {
            ZhTwReplacers.LogAlways("AddMsgPrefix EX: " + e.GetType().Name + " " + e.Message);
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
                // 語境化：自己的武器/部位前的所有格冗餘（「用 你的 青銅匕首」→「用 青銅匕首」）
                result = Regex.Replace(result, @"用 (?:你的|the|a|an) ", "用 ");
                // 補逐詞：pattern 攔截後 weapon 段可能殘留英文（如 her bite / your iron dagger），
                // 由 Clean 逐詞層兜底翻成中文（bite→咬、iron→鐵），不重跑整句 pattern。
                try { result = ZhTwTextCleaner.Clean(result); } catch { }
                if (outerColor != null) result = "{{" + outerColor + "|" + result + "}}";
                return result;
            }
        }
        // 未命中模式：保留原始 markup（顏色），只做冠詞剝除 + 中段「there is X in your way」
        string cleaned = LeadingArticle.Replace(msg, "").Replace("  ", " ");
        cleaned = InYourWay.Replace(cleaned, "$1 擋在你面前");
        // fallback：交給 TextCleaner 的完整鏈（combat 整句 pattern + Clean 逐詞），
        // 確保不走 MessageQueue 多載的 combat 句也能翻譯（如「You hit (x3) for N damage with Y」）
        if (cleaned == msg)
        {
            try
            {
                cleaned = ZhTwTextCleaner.ToStringProcess(cleaned);
            }
            catch
            {
            }
        }
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