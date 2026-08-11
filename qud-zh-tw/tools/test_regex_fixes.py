#!/usr/bin/env python3
"""
test_regex_fixes.py — 驗證三處 bug 修復 + 產生動態前綴風險目錄。

修復對照：
  Bug1  Philosophical 殘留 → historyspice.zh-tw.json 陣列 key 加 = 後綴（取代英文）
  Bug2  雷舍夫 (Resheph) (雷舍夫(Resheph)) 重複 → NameToken/CjkNameRefmt 排除全形括號 U+FF08
  Bug3  lying on/prone 狀態標籤未譯 → GetDisplayNameEvent.GetFor postfix

用法：
  python3 tools/test_regex_fixes.py            # 全跑
  python3 tools/test_regex_fixes.py --spice    # 只驗 spam 陣列 = 後綴
  python3 tools/test_regex_fixes.py --name     # 只驗雷舍夫重複
  python3 tools/test_regex_fixes.py --prone    # 只驗 lying on/prone
  python3 tools/test_regex_fixes.py --dyn      # 產生動態前綴風險目錄
"""
import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROJ = ROOT.parent
SPICE = PROJ / "historyspice.zh-tw.json"
REPL = PROJ.parent / "qud-zh-tw-replacers" / "TextCleanerHook.cs"
DYN_ADD = ROOT / "msg_dynadd_new.txt"
RISK_OUT = ROOT / "dynamic_prefix_risks.json"

PASS = 0
FAIL = 0


def check(name, ok, detail=""):
    global PASS, FAIL
    if ok:
        PASS += 1
        print(f"  [PASS] {name} {detail}")
    else:
        FAIL += 1
        print(f"  [FAIL] {name} {detail}")


# ============ Python 版 C# 正則（與 TextCleanerHook.cs 一致） ============
ProperNounZh = {
    "Resheph": "雷舍夫(Resheph)", "Abram": "亞伯拉罕(Abram)", "Gyre": "環流(Gyre)",
    "Qud": "卡德(Qud)", "Ezra": "以斯拉(Ezra)", "Spindle": "紡錘(Spindle)",
    "Murapur": "穆拉普爾(Murapur)", "Maazoppir": "馬佐皮爾(Maazoppir)",
    "Tarchewan": "塔徹萬(Tarchewan)",
}
CjkProperNoun = {
    "雷舍夫": "雷舍夫(Resheph)", "雷斯嘿芙": "雷舍夫(Resheph)", "雷谢夫": "雷舍夫(Resheph)",
    "穆拉普爾": "穆拉普爾(Murapur)", "塔徹萬": "塔徹萬(Tarchewan)", "馬佐皮爾": "馬佐皮爾(Maazoppir)",
}
# （Bug2 修復前不含 \uFF08）
NameToken_OLD = re.compile(r'(?<![\u4e00-\u9fff(#])\b([A-Z][A-Za-z\'-]{2,})\b(?![-0-9])')
NameToken_NEW = re.compile(r'(?<![\u4e00-\u9fff(#\uFF08])\b([A-Z][A-Za-z\'-]{2,})\b(?![-0-9])')
CjkNameRefmt_OLD = re.compile(r'(?<![\u4e00-\u9fff(])(雷舍夫|雷斯嘿芙|雷谢夫|穆拉普爾|塔徹萬|馬佐皮爾)(?![\u4e00-\u9fff(\s]*\()')
CjkNameRefmt_NEW = re.compile(r'(?<![\u4e00-\u9fff(\uFF08])(雷舍夫|雷斯嘿芙|雷谢夫|穆拉普爾|塔徹萬|馬佐皮爾)(?![\u4e00-\u9fff(\uFF08\s]*[\u0028\uFF08])')

# Bug3 的 lying on / prone 正則
LyingOnTag = re.compile(r'\{\{B\|lying on (.+?)\}\}')
ProneTag = re.compile(r'\{\{B\|prone\}\}')


def name_match(m):
    return ProperNounZh.get(m.group(1), m.group(0))


def cjk_match(m):
    return CjkProperNoun.get(m.group(0), m.group(0))


def clean_names(text, NameToken=NameToken_NEW, CjkNameRefmt=CjkNameRefmt_NEW):
    r = NameToken.sub(name_match, text)
    return CjkNameRefmt.sub(cjk_match, r)


def test_name():
    print("== Bug2: 雷舍夫/全形括號重複 ==")
    # 遊戲自身譯句（全形括號）
    s = "在 %=year=% 年，雷舍夫（Resheph）淨化了沼澤地中的 環流(Gyre) 之瘟疫，並教導亞伯拉罕（Abram）沿著肥沃的痕跡種植 水藤。"
    new = clean_names(s, NameToken_NEW, CjkNameRefmt_NEW)
    old = clean_names(s, NameToken_OLD, CjkNameRefmt_OLD)
    check("修復後不重複（全形括號）", "重複" not in new and "（雷舍夫(Resheph)）" not in new, f"-> {new}")
    check("修復前確實重複（驗證 bug 存在）", "（雷舍夫(Resheph)）" in old, f"-> {old}")
    # 半形括號格式不重複
    s2h = "在 %=year=% 年，雷舍夫(Resheph) 淨化"
    check("半形括號不重複", clean_names(s2h).count("雷舍夫") == 1, f"-> {clean_names(s2h)}")
    # 純英文名仍可譯
    s3 = "In 3 AR, Resheph cleansed the marshlands"
    check("純英文名仍可譯", "雷舍夫(Resheph)" in clean_names(s3), f"-> {clean_names(s3)}")


def test_prone():
    print("== Bug3: 狀態效果名（lying on / prone / wading / 效果表）==")
    EffectZh = {
        "frenzied": "狂暴的", "wading": "涉水", "prone": "俯臥", "bleeding": "流血中",
        "confused": "困惑", "dazed": "暈眩", "terrified": "恐懼", "overburdened": "負重過度",
        "paralyzed": "麻痺", "stunned": "暈眩", "submerged": "淹沒", "swimming": "游泳",
        "sitting": "坐著", "piloting": "駕駛中", "exhaustion": "精疲力竭",
    }
    def f(s):
        if len(s) > 120:
            return s
        s = re.sub(r'\{\{(B|C)\|lying on (.+?)\}\}', r'{{\1|躺在 \2 上}}', s, flags=re.I)
        s = re.sub(r'\{\{(B|C)\|sitting on (.+?)\}\}', r'{{\1|坐在 \2 上}}', s, flags=re.I)
        s = re.sub(r'\{\{(B|C)\|enclosed in (.+?)\}\}', r'{{\1|被困在 \2 內}}', s, flags=re.I)
        s = re.sub(r'\{\{(B|C)\|engulfed by (.+?)\}\}', r'{{\1|被 \2 吞噬}}', s, flags=re.I)
        s = re.sub(r'\{\{(B|C)\|piloting (.+?)\}\}', r'{{\1|駕駛 \2}}', s, flags=re.I)
        def em(m):
            color, word = m.group(1), m.group(2).strip()
            return "{{" + color + "|" + EffectZh.get(word, word) + "}}" if word in EffectZh else m.group(0)
        return re.sub(r'\{\{([^}|]+)\|([^{}]*?)\}\}', em, s)

    # $1 修正：不得輸出字面 {1}
    r = f("{{C|lying on 刻印床}}")
    check("lying on → 躺在...上（無 {1} 字面）", "躺在 刻印床 上" in r and "{1}" not in r and "lying" not in r, f"-> {r}")
    check("wading → 涉水", f("{{B|wading}}") == "{{B|涉水}}", f"-> {f('{{B|wading}}')}")
    check("bleeding → 流血中", f("{{r|bleeding}}") == "{{r|流血中}}", f"-> {f('{{r|bleeding}}')}")
    check("dazed → 暈眩", f("{{C|dazed}}") == "{{C|暈眩}}", f"-> {f('{{C|dazed}}')}")
    check("terrified → 恐懼", f("{{W|terrified}}") == "{{W|恐懼}}", f"-> {f('{{W|terrified}}')}")
    check("exhaustion → 精疲力竭", f("{{K|exhaustion}}") == "{{K|精疲力竭}}", f"-> {f('{{K|exhaustion}}')}")
    check("enclosed in X → 被困在 X 內", "被困在" in f("{{B|enclosed in a cave}}"), f"-> {f('{{B|enclosed in a cave}}')}")
    check("engulfed by X → 被 X 吞噬", "被" in f("{{B|engulfed by slime}}"), f"-> {f('{{B|engulfed by slime}}')}")
    combo = f("水藤農夫 [{{B|lying on 刻印 床}}, {{B|wading}}]")
    check("複合狀態", "躺在 刻印 床 上" in combo and "涉水" in combo, f"-> {combo}")
    check("無狀態標籤不受影響", f("Joopa 的村民") == "Joopa 的村民")
    src = REPL.read_text(encoding="utf-8")
    check("C# 有 EffectZh 表", "EffectZh" in src and '"wading"' in src)
    check("C# 有 TranslateStatusFragments", "TranslateStatusFragments" in src)
    check("C# 無 {1} 字面迴溯", "躺在 {1}" not in src)
    # DisplayNamePostfix 有跑 Clean()（GetFor 熱路徑漏翻根因）
    check("DisplayNamePostfix 有 Clean()", "DisplayNamePostfix" in src and "Clean(__result)" in src)
    dn_idx = src.index("public static void DisplayNamePostfix")
    dn_block = src[dn_idx:dn_idx + 600]
    check("DisplayNamePostfix 無 Length>120", "Length > 120" not in dn_block)
    # DisplayNamePostfix 熱路徑：只 call 輕量 TranslateDisplayNameFragments，不跑戰鬥/烹飪
    check("C# 有 TranslateDisplayNameFragments（熱路徑輕量）", "TranslateDisplayNameFragments" in src)
    dp_idx = src.find("public static void DisplayNamePostfix")
    dp_body = src[dp_idx:src.find("}", dp_idx) + 1] if dp_idx >= 0 else ""
    check("DisplayNamePostfix 不 call 完整 TranslateStatusFragments",
          "TranslateStatusFragments(" not in dp_body, f"body: {dp_body[:80]}")


def test_spice():
    print("== Bug1: historyspice 陣列 = 後綴 ==")
    d = json.load(open(SPICE, encoding="utf-8"))
    spice = d["spice"]

    # 驗證所有陣列 key 都有 = 後綴
    bad = []
    total_arr = 0

    def walk(o, path):
        nonlocal total_arr
        if isinstance(o, dict):
            for k, v in o.items():
                if isinstance(v, list):
                    total_arr += 1
                    if not k.endswith("="):
                        bad.append(path + k)
                else:
                    walk(v, path + k + ".")
    walk(spice, "")
    check("所有陣列 key 皆帶 = 後綴", not bad, f"({total_arr} 陣列, 缺 {len(bad)}: {bad[:5]})")

    # 驗證 scholarship 已譯
    sch = spice["elements"]["scholarship"]
    check("scholarship.adjectives= 中文", sch.get("adjectives=") == ["哲學性的", "精明的", "好奇的"],
          f"-> {sch.get('adjectives=')}")
    check("scholarship.ruinReason 中文", "充斥著" in sch.get("ruinReason", ""), f"-> {sch.get('ruinReason')}")

    # 驗證 C# PhraseLeaks 有 philosophical 對應（執行期兜底）
    src = REPL.read_text(encoding="utf-8")
    check("PhraseLeaks 含 philosophical", "philosophical" in src)


def test_dyn():
    print("== 動態前綴風險目錄 ==")
    src = REPL.parent / "UiStringsHook.cs"
    if not src.exists():
        print("  無 UiStringsHook.cs，跳過")
        return
    cs = src.read_text(encoding="utf-8")
    # 從 DynamicPopupPatterns 區塊抓所有 Regex(@"^PREFIX(.+?)$")
    pat = re.compile(r'Regex\(@"\^(.*?)\(\.\+\?\)\$"')
    patterns = []
    for m in pat.finditer(cs):
        prefix = m.group(1)
        if prefix.startswith(r"\{\{"):
            # markup 開頭（如 {{K|...）→ 明確語境，不算 generic
            words = re.findall(r'[A-Za-z]+', prefix)
            is_generic = len(words) <= 2
        else:
            words = re.findall(r'[A-Za-z]+', prefix)
            is_generic = len(prefix) < 12 or len(words) <= 2
        patterns.append({"prefix": prefix, "generic": is_generic})

    generic = [p for p in patterns if p["generic"]]
    out = {
        "total": len(patterns),
        "generic_flagged": len(generic),
        "generic_patterns": [p["prefix"] for p in generic],
    }
    print(f"  動態前綴總數: {len(patterns)}")
    print(f"  flagged generic: {len(generic)}")
    for p in generic:
        print(f"    - {p['prefix']}")
    RISK_OUT.write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"  風險目錄寫入 {RISK_OUT.name}")


def test_journal():
    print("== 日誌畫面：tab 名 + [History of X] ==")
    cs = (REPL.parent / "UiStringsHook.cs").read_text(encoding="utf-8")
    # 驗證 SidebarLabelPrefix（prefix，非 postfix）已有 JournalCategories 比對與 HistoryOf regex
    check("SidebarLabelPrefix 有 JournalCategories 比對", "JournalCategories.TryGetValue(inner" in cs)
    check("SidebarLabelPrefix 有 HistoryOf regex", "HistoryOf" in cs)
    check("Sidebar 用 prefix（非 postfix）", "SidebarLabelPrefix" in cs and "SidebarLabelPostfix" not in cs)
    # Harmony 註冊：prefix + Write(StringBuilder) overload
    hp = (REPL.parent / "HarmonyPatches.cs").read_text(encoding="utf-8")
    check("Harmony 註冊 SidebarLabelPrefix", "SidebarLabelPrefix" in hp)
    check("Harmony 註冊 StringBuilder overload", "SidebarLabelSBufPrefix" in hp)

    JournalCategories = {
        "Sultan Histories": "蘇丹歷史", "Village Histories": "村莊歷史",
        "Gossip and Lore": "閒談與傳說", "General Notes": "一般筆記",
        "Recipes": "食譜", "Locations": "地點", "Chronology": "編年史",
    }

    def postfix(s):
        if len(s) > 40:
            return s
        JournalMarkup = re.compile(r'^\{\{([^}|]+)\|([^{}]*)\}\}$')
        HistoryOf = re.compile(r'^\[History (?:of|的) (.+?)(?:, Vol\. (.+?))?\]$')
        SultanHistories = re.compile(r'^\s*([^|{}\[\]]+?)\s+Histories\s*$')
        wm = re.match(r'^\{\{([^}|]+)\|([^{}]*)\}\}$', s)
        wrap_open = wrap_close = None
        inner = s
        if wm:
            wrap_open = "{{" + wm.group(1) + "|"; wrap_close = "}}"
            inner = wm.group(2)
        if inner in JournalCategories:
            return (wrap_open or "") + JournalCategories[inner] + (wrap_close or "")
        shm = re.match(r'^\s*([^|{}\[\]]+?)\s+Histories\s*$', inner)
        if shm:
            return (wrap_open or "") + shm.group(1) + " 的歷史" + (wrap_close or "")
        hm = re.search(r'^\[History (?:of|的) (.+?)(?:, Vol\. (.+?))?\]$', inner)
        if hm:
            x, vol = hm.group(1), hm.group(2)
            zh = (f"[{x} 第 {vol} 卷的歷史]" if vol else f"[{x} 的歷史]")
            return (wrap_open or "") + zh + (wrap_close or "")
        return s

    tab_map = {
        "{{W|Locations}}": "{{W|地點}}",
        "{{K|Gossip and Lore}}": "{{K|閒談與傳說}}",
        "{{W|Sultan Histories}}": "{{W|蘇丹歷史}}",
        "{{W|Village Histories}}": "{{W|村莊歷史}}",
        "{{W|Chronology}}": "{{W|編年史}}",
        "{{W|General Notes}}": "{{W|一般筆記}}",
        "{{W|Recipes}}": "{{W|食譜}}",
    }
    for src, want in tab_map.items():
        got = postfix(src)
        check(f"tab: {src}", got == want, f"-> {got}")
    h1 = postfix("[History of 瑞謝夫 (RESHEPH)]")
    check("[History of X]", h1 == "[瑞謝夫 (RESHEPH) 的歷史]", f"-> {h1}")
    h2 = postfix("[History of Joppa, Vol. III]")
    check("[History of X, Vol. N]", h2 == "[Joppa 第 III 卷的歷史]", f"-> {h2}")
    # 動態蘇丹 tab（GetSultansDisplayName 組裝）
    sh = postfix("{{W|Resheph Histories}}")
    check("動態 Sultan tab", sh == "{{W|Resheph 的歷史}}", f"-> {sh}")
    sh2 = postfix("{{W|雷舍夫 (Resheph) Histories}}")
    check("動態 Sultan tab（中文名）", sh2 == "{{W|雷舍夫 (Resheph) 的歷史}}", f"-> {sh2}")
    # C# 有 BookShow prefix
    check("C# 有 BookShowPrefix", "BookShowPrefix" in cs)
    hp2 = (REPL.parent / "HarmonyPatches.cs").read_text(encoding="utf-8")
    check("Harmony 註冊 BookShow", "BookShowPrefix" in hp2)
    # BookUI 翻譯邏輯
    def book(pt, bt):
        if pt and pt.strip() == "No active effects.":
            pt = "沒有主動效果。"
        if bt and bt.startswith("&WActive Effects&Y"):
            bt = "&W主動效果&Y" + bt[len("&WActive Effects&Y"):]
        return pt, bt
    b1 = book("No active effects.", "&WActive Effects&Y - 水藤農夫")
    check("No active effects.", b1[0] == "沒有主動效果。", f"-> {b1[0]}")
    check("Active Effects title", b1[1] == "&W主動效果&Y - 水藤農夫", f"-> {b1[1]}")
    # 純數據性能守衛：長字串不處理
    long = "X" * 50
    check("長字串效能守衛", postfix(long) == long)


def test_standup():
    print("== You stand up / rise 訊息列 ==")
    hp = (REPL.parent / "HarmonyPatches.cs").read_text(encoding="utf-8")
    check("Patterns 有 You stand up\\.", r"^You stand up" in hp)
    check("Patterns 有 You stand up from", r"You stand up from" in hp)
    check("Patterns 有 You rise from", r"You rise from" in hp)
    # 驗證翻譯字串
    check("stand up → 你站起來了。", "你站起來了。" in hp)
    check("stand up from → 你從 $1 站起來。", "你從 $1 站起來。" in hp)
    check("rise from → 你從 $1 起身。", "你從 $1 起身。" in hp)


def test_combat():
    print("== 戰鬥/動作訊息整句 + Words 缺口 + 烹飪 ==")
    src = REPL.read_text(encoding="utf-8")
    # C# 有整句 pattern（正則內含 \s，用關鍵字查）
    check("C# 有 You hit 整句", "hit" in src and "擊中" in src and r"damage" in src)
    check("C# 有 is dazed", "dazed" in src and "感到暈眩" in src)
    check("C# 有 stands up 整句", "站起來了" in src)
    check("C# 有 toggle", "切換為" in src)
    check("C# 有 cooking 兜底", "丟進鍋子" in src and "吃下了這份餐點" in src)
    # Words 缺口
    check("Words 有 his", '"his"' in src)
    check("Words 有 toggle", '"toggle"' in src)
    check("Words 有 knocked", '"knocked"' in src)
    check("Words 有 moving", '"moving"' in src)
    # 整句翻譯邏輯
    def t(text):
        text = re.sub(r'^You\s+hit\s+\((.+?)\)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)!?\s*\[(.+?)\]$', r'你用 \3 擊中(\1)，造成 \2 傷害[\4]', text, flags=re.I)
        text = re.sub(r'^(.+?)\s+is\s+dazed[.!]?$', r'\1 感到暈眩。', text, flags=re.I)
        text = re.sub(r'^You\s+eat\s+the\s+meal[.!]?$', r'你吃下了這份餐點。', text, flags=re.I)
        return text
    check("hit 整句", "你用" in t("You hit (x1) for 2 damage with your bronze dagger! [17]"))
    check("is dazed 整句", "感到暈眩" in t("Frozen watervine farmer is dazed."))
    check("cooking 兜底", t("You eat the meal.") == "你吃下了這份餐點。")
    # XDidYToZ frame 整句（物件名可能未本地化，frame 必先翻）
    def fr(text):
        text = re.sub(r'^You\s+sit\s+down\s+on\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$', r'你坐到 \1 上。', text, flags=re.I)
        text = re.sub(r'^You\s+wade\s+through\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$', r'你涉水穿過 \1。', text, flags=re.I)
        text = re.sub(r'^You\s+are\s+engulfed\s+by\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$', r'你被 \1 吞噬。', text, flags=re.I)
        return text
    check("sit down on frame（中文物件）", fr("You sit down on 椅子.") == "你坐到 椅子 上。", f"-> {fr('You sit down on 椅子.')}")
    check("sit down on frame（英文物件+冠詞）", fr("You sit down on the chair.") == "你坐到 chair 上。", f"-> {fr('You sit down on the chair.')}")
    check("wade through frame", "涉水穿過" in fr("You wade through water."))
    check("are engulfed by frame", "被" in fr("You are engulfed by slime."))
    check("C# 有 sit down on frame", "sit" in src and "坐到" in src)
    # Harmony Patterns 也有 XDidYToZ frame（AddMsgPrefix 多層攔截）
    hp = (REPL.parent / "HarmonyPatches.cs").read_text(encoding="utf-8")
    check("Harmony 有 sit down on frame", "sit down on" in hp and "你坐到" in hp)
    check("Harmony 有 wade through frame", "wade through" in hp and "涉水穿過" in hp)
    check("Harmony 有 are engulfed by frame", "engulfed by" in hp and "吞噬" in hp)
    # 萃取器有 textbuilder_missing + xdidytoz_frames
    ex = Path(__file__).resolve().parent / "extract_hardcoded_ui.py"
    ex_src = ex.read_text(encoding="utf-8")
    check("萃取器有 textbuilder 鏈", "textbuilder_missing" in ex_src)
    check("萃取器有 xdidytoz_frames", "xdidytoz_frames" in ex_src)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--spice", action="store_true")
    ap.add_argument("--name", action="store_true")
    ap.add_argument("--prone", action="store_true")
    ap.add_argument("--dyn", action="store_true")
    ap.add_argument("--journal", action="store_true")
    ap.add_argument("--standup", action="store_true")
    ap.add_argument("--combat", action="store_true")
    args = ap.parse_args()

    any_flag = args.spice or args.name or args.prone or args.dyn or args.journal or args.standup or args.combat
    if any_flag:
        if args.spice:
            test_spice()
        if args.name:
            test_name()
        if args.prone:
            test_prone()
        if args.dyn:
            test_dyn()
        if args.journal:
            test_journal()
        if args.standup:
            test_standup()
        if args.combat:
            test_combat()
    else:
        test_spice()
        test_name()
        test_prone()
        test_dyn()
        test_journal()
        test_standup()
        test_combat()

    print(f"\n結果: {PASS} passed, {FAIL} failed")
    sys.exit(1 if FAIL else 0)


if __name__ == "__main__":
    main()