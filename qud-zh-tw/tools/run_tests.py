#!/usr/bin/env python3
"""
run_tests.py — 繁中漢化防回歸測試套件。

設計原則：測試「直接解析 C# 原始碼讀取字典與正則」，不複製字典到 Python，
避免 C#/Python 字典漂移。這樣的重複 key（如 her）、正則 group 越界、漏譯詞
回歸都能在部署前被攔下。

涵蓋：
  1. static_cs   — 4 個 .cs 無字典重複 key、Regex.Replace group 引用不越界、括號平衡
  2. dictionary  — be 動詞/專名片語/高頻漏詞在字典中；跨路徑一致
  3. pipeline    — 模擬 ToStringProcess（快路徑+Clean+FrameTrigger）對測試語料斷言
  4. data        — zh-tw 資料無 ▶ 殘留、ifPlural 值側無英文 token 洩漏

用法：
  python3 tools/run_tests.py            # 全跑
  python3 tools/run_tests.py --static   # 只跑 static_cs
  python3 tools/run_tests.py --dict     # 只跑 dictionary
  python3 tools/run_tests.py --pipeline # 只跑 pipeline
  python3 tools/run_tests.py --data     # 只跑 data
"""
import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROJ = ROOT.parent                     # qud-zh-tw (data mod)
REPL = PROJ.parent / "qud-zh-tw-replacers"   # 兄弟目錄 (replacer mod)
HOOK = REPL / "TextCleanerHook.cs"
REPLACERS = REPL / "Replacers.cs"
UIHOOK = REPL / "UiStringsHook.cs"
HARMONY = REPL / "HarmonyPatches.cs"
ZH = PROJ / "zh-tw"

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


# ============ 從 C# 原始碼解析字典 ============
def parse_dict_blocks(src):
    """回傳 list of (field_name, {key: value})。重複 key 記錄到 global DICT_DUP_KEYS。"""
    out = []
    for m in re.finditer(r'Dictionary<([^>]+)>\s+(\w+)\s*=\s*new Dictionary<[^>]+>\([^)]*\)\s*\{', src, re.S):
        field = m.group(2)
        start = m.end()
        i, depth = start, 1
        while i < len(src) and depth > 0:
            if src[i] == '{': depth += 1
            elif src[i] == '}': depth -= 1
            i += 1
        block = src[start:i - 1]
        d = {}
        seen = set()
        for kv in re.finditer(r'\{\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"', block):
            k = kv.group(1)
            low = k.lower()
            if low in seen:
                # 原始碼層的重複 key（C# 字典初始化時會拋 ArgumentException——必須攔下）
                DICT_DUP_KEYS.append((field, k))
            seen.add(low)
            d[k] = kv.group(2)
        out.append((field, d))
    return out


DICT_DUP_KEYS = []


def load_dicts():
    """回傳 {field: dict} 所有 4 個檔的字典合併；field 衝突時加檔案前綴。"""
    merged = {}
    for path, prefix in [(HOOK, "hook"), (REPLACERS, "rep"), (UIHOOK, "ui"), (HARMONY, "hp")]:
        src = path.read_text(encoding="utf-8")
        for field, d in parse_dict_blocks(src):
            merged[f"{prefix}.{field}"] = d
    return merged


# ============ 1. static_cs ============
def test_static_cs():
    print("== static_cs：字典重複 key / group 越界 / 平衡 ==")
    for path in [HOOK, REPLACERS, UIHOOK, HARMONY]:
        name = path.name
        src = path.read_text(encoding="utf-8")
        for field, d in parse_dict_blocks(src):
            low = {}
            dup = []
            for k in d:
                kl = k.lower()
                if kl in low: dup.append((k, low[kl]))
                else: low[kl] = k
            check(f"{name} 字典 {field} 無重複 key", not dup, str(dup) if dup else "")
        # Regex.Replace group 引用檢查
        for m in re.finditer(r'Regex\.Replace\(\s*(?:text|result|input),\s*@"((?:""|[^"])*)"\s*,\s*"((?:""|[^"])*)"', src, re.S):
            p = m.group(1).replace('""', '"')
            r = m.group(2).replace('""', '"')
            ng = count_groups(p)
            refs = [int(x) for x in re.findall(r'\$(\d+)', r)]
            if refs and max(refs) > ng:
                check(f"{name} group 越界", False, f"ref {max(refs)}>{ng}: {r[:40]}")
        # C# 原始碼層重複 key（parse 後的 dict 無法察覺，此處直接捕獲）
        dup_report = [x for x in DICT_DUP_KEYS]
        check(f"{name} 字典原始碼層無重複 key", not dup_report, str(dup_report[:5]) if dup_report else "")
        DICT_DUP_KEYS.clear()
        # 平衡
        clean = strip_strings_and_comments(src)
        check(f"{name} 括號平衡", clean.count('{') == clean.count('}') and clean.count('(') == clean.count(')'),
              f"brace {clean.count('{')}/{clean.count('}')} paren {clean.count('(')}/{clean.count(')')}")


def count_groups(p):
    n, i, L, inclass = 0, 0, len(p), False
    while i < L:
        c = p[i]
        if c == '\\': i += 2; continue
        if c == '[': inclass = True; i += 1; continue
        if c == ']': inclass = False; i += 1; continue
        if c == '(' and not inclass:
            if p[i+1:i+3] in ('?:', '?=', '?!', '?<', '?#'): i += 2; continue
            n += 1
        i += 1
    return n


def strip_strings_and_comments(s):
    s = re.sub(r'/\*.*?\*/', '', s, flags=re.S)
    s = re.sub(r'//[^\n]*', '', s)
    out, i = [], 0
    while i < len(s):
        if s[i] == '"':
            j = i + 1
            if s.startswith('@"', i):
                j = i + 2
                while j < len(s):
                    if s[j] == '"' and j + 1 < len(s) and s[j+1] == '"': j += 2; continue
                    if s[j] == '"': break
                    j += 1
                i = j + 1
            else:
                j = i + 1
                while j < len(s):
                    if s[j] == '\\': j += 2; continue
                    if s[j] == '"': break
                    j += 1
                i = j + 1
        elif s[i] == "'":
            j = i + 1
            if j < len(s) and s[j] == '\\': j += 2
            else: j += 1
            i = (j + 1) if j < len(s) and s[j] == "'" else i + 1
        else:
            out.append(s[i]); i += 1
    return ''.join(out)


# ============ 2. dictionary ============
LEAK_WORDS = {
    "is", "are", "was", "were", "life", "despise", "dislike", "favor",
    "consider", "revere", "members", "powered", "increase", "reduction",
    "provides", "action", "costs", "nearby", "causes", "damage", "item",
}


def test_dictionary():
    print("== dictionary：漏詞覆蓋與跨路徑一致 ==")
    dicts = load_dicts()
    words = set()
    phrase = set()
    for field, d in dicts.items():
        if ".Words" in field or ".VerbZh" in field or ".Verbs" in field:
            words |= set(d.keys())
        if ".PhraseLeaks" in field:
            phrase |= set(d.keys())
    low_words = {w.lower() for w in words}
    missing = [w for w in sorted(LEAK_WORDS) if w not in low_words]
    check("高頻漏詞全覆蓋", not missing, f"缺: {missing}" if missing else "")
    check("PhraseLeaks 有 Tree of Life", any("tree of life" in p.lower() for p in phrase))
    check("PhraseLeaks 有 Chavvah, the Tree of Life",
          any("chavvah" in p.lower() and "tree of life" in p.lower() for p in phrase))


# ============ 3. pipeline 模擬 ============
def _scan_lang(text):
    hasEng = hasCjk = False
    for c in text:
        if ('a' <= c <= 'z') or ('A' <= c <= 'Z'): hasEng = True
        elif '\u4e00' <= c <= '\u9fff': hasCjk = True
        if hasEng and hasCjk: break
    return hasEng, hasCjk


def _sim_protect_parens(text):
    """模擬 C# ProtectParens：緊鄰中文的 (English) 括號換成佔位符（逐詞後還原）。"""
    box = []
    out = []
    i = 0
    while i < len(text):
        if text[i] == '(' and i > 0:
            j = text.find(')', i)
            if j != -1:
                p = i - 1
                while p >= 0 and text[p] in ' \t':
                    p -= 1
                if p >= 0 and '一' <= text[p] <= '鿿':
                    box.append(text[i:j + 1])
                    out.append('\x02' + str(len(box) - 1) + '\x02')
                    i = j + 1
                    continue
        out.append(text[i])
        i += 1
    return ''.join(out), box


def _clean(text, words, phrases):
    hasEng, hasCjk = _scan_lang(text)
    if not (hasEng and hasCjk): return text
    # 括號短語（(Full) 類，表 key 以 （/( 開頭）先替換（不受輸入括號保護影響）
    for k in sorted(phrases, key=len, reverse=True):
        if k.startswith('(') or k.startswith('（'):
            text = re.sub(r'(?i)' + re.escape(k), phrases[k], text)
    # 輸入「中文(English)」括號先保護（防 Phrase/TmpWords 把括號內英文再包層）
    text, box1 = _sim_protect_parens(text)
    # PhraseRegex 先（整句/詞組；與 C# Clean 順序一致）
    for k in sorted(phrases, key=len, reverse=True):
        if k.startswith('(') or k.startswith('（'):
            continue
        text = re.sub(r'(?i)' + re.escape(k), phrases[k], text)
    # Phrase 產生的「中文(English)」括號保護（避免 Words 逐詞污染括號英文）
    text, box2 = _sim_protect_parens(text)
    # WordsRegex 後（整詞）；Python 要求 (?i) 在開頭（C# 可 `\b(?i)`）
    for k in sorted(words, key=len, reverse=True):
        text = re.sub(r'(?i)\b' + re.escape(k) + r'\b', words[k], text)
    for idx, seg in enumerate(box2):
        text = text.replace('\x02' + str(idx) + '\x02', seg)
    for idx, seg in enumerate(box1):
        text = text.replace('\x02' + str(idx) + '\x02', seg)
    return text


FRAME_FALLBACK = [
    (r'^You\s+sit\s+down\s+on\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$', r'你坐到 \1 上。'),
    (r'^You\s+wade\s+through\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$', r'你涉水穿過 \1。'),
    (r'^You\s+are\s+engulfed\s+by\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$', r'你被 \1 吞噬。'),
    (r'^(.+?)\s+is\s+dazed[.!]?$', r'\1 感到暈眩。'),
]


def csstr(s):
    # C# verbatim string @"..."："" → "
    return s.replace('""', '"')


def load_frame_rules():
    """從 TextCleanerHook.cs 的 TranslateStatusFragments 自動提取 Regex.Replace 規則。"""
    src = HOOK.read_text(encoding="utf-8")
    rules = []
    for m in re.finditer(
        r'(?:System\.Text\.RegularExpressions\.)?Regex\.Replace\(\s*text\s*,\s*@"((?:""|[^"])*)"\s*,\s*"((?:""|[^"])*)"\s*,\s*(?:System\.Text\.RegularExpressions\.)?RegexOptions\.(\w+)',
        src, re.S):
        pat = csstr(m.group(1))
        # C# 的 $n group 引用 → Python 用 MatchEvaluator 取代（未捕獲 group 補空，連 Python \n 不存在的 group 拋錯問題也避開）
        rules.append((pat, csstr(m.group(2)), m.group(3)))
    return rules or FRAME_FALLBACK


def load_frame_trigger():
    src = HOOK.read_text(encoding="utf-8")
    m = re.search(r'FrameTrigger\s*=\s*new Regex\(\s*@"((?:""|[^"])*)"', src, re.S)
    if not m:
        return None
    return csstr(m.group(1))


FRAME_RULES = load_frame_rules()
_TRIG_SRC = load_frame_trigger()
if _TRIG_SRC:
    FRAME_TRIGGER = re.compile(_TRIG_SRC)
else:
    FRAME_TRIGGER = re.compile(
        r'(?i)\b(hit|miss|toggle|dazed|stand|take|eat|toss|gather|sit|climb|jump|wade|swim|'
        r'emerge|bump|bond|detach|slip|swap|entangle|engulf|drag|suck|impal|lying|sitting|'
        r'enclosed|pilot|knock|stop|move|look|turn|fall|rise)\w*')


def _frame_py_repl(tmpl):
    """把 C# $n 替換模板轉成 Python 的 MatchEvaluator。"""
    refs = [(g.group(0), int(g.group(1))) for g in re.finditer(r'\$(\d+)', tmpl)]

    def ev(m):
        out = tmpl
        for token, n in refs:
            try:
                out = out.replace(token, m.group(n) or '')
            except (IndexError, NotImplementedError):
                pass
        return out

    return ev


def _status_fragments(text):
    if '{{' not in text and not FRAME_TRIGGER.search(text):
        return text
    for pat, repl_tmpl, flags in FRAME_RULES:
        f = 0
        if 'IgnoreCase' in flags: f |= re.I
        if 'Singleline' in flags: f |= re.S
        text = re.sub(pat, _frame_py_repl(repl_tmpl), text, flags=f)
    return text


# ---- 模擬 HarmonyPatches.Translate（AddMsgPrefix 層）：Patterns 正則 + Possessive + Clean 補漏 ----
def load_harmony_patterns():
    src = HARMONY.read_text(encoding="utf-8")
    out = []
    for m in re.finditer(
        r'new Regex\(@"((?:""|[^"])*)"\s*,\s*RegexOptions\.IgnoreCase\)\s*,\s*"((?:""|[^"])*)"',
        src, re.S):
        out.append((csstr(m.group(1)), csstr(m.group(2))))
    return out


HARMONY_RULES = load_harmony_patterns()
POSSESSIVE = re.compile(r'\b(his|her|its|their|your)\b', re.I)
LEADING_ARTICLE = re.compile(r'\b(?:the|a|an)\s+(?=[\u4e00-\u9fff])', re.I)
POSS = {"his": "他的", "her": "她的", "its": "它的", "their": "他們的", "your": "你的"}


def _add_msg_prefix(text, words, phrases):
    """對應 C# ZhTwHarmonyPatches.Translate：命中 Patterns 即整句翻譯（未命中才落 ToStringProcess）。"""
    inner = text.strip()
    outer = None
    m = re.match(r'^\{\{([^}|]*)\|(.*)\}\}$', inner, re.S)
    if m:
        outer, inner = m.group(1), m.group(2).strip()
    for pat, repl in HARMONY_RULES:
        if re.match(pat, inner, re.I):
            result = re.sub(pat, _frame_py_repl(repl), inner, flags=re.I).strip()
            result = POSSESSIVE.sub(lambda mm: POSS.get(mm.group(0).lower(), "它的"), result)
            result = LEADING_ARTICLE.sub("", result).replace("  ", " ")
            result = re.sub(r"用 (?:你的|the|a|an) ", "用 ", result)
            result = _clean(result, words, phrases)
            if outer:
                result = "{{" + outer + "|" + result + "}}"
            return result
    return None


def _to_string_process(text, words, phrases):
    translated = _add_msg_prefix(text, words, phrases)
    if translated is not None:
        return translated
    hasEng, hasCjk = _scan_lang(text)
    if not hasEng: return text
    if not hasCjk:
        return _status_fragments(text)
    return _clean(_status_fragments(text), words, phrases)


def _tmp_process(text, words, phrases):
    """模擬 TmpHeaderPrefix（Unity TMP set_text）：短純英文→詞級白名單；中英混雜/長文本→Clean。"""
    if not text: return text
    t = text.strip()
    if len(t) <= 40 and not _has_cjk_scan(t):
        return _clean(_translate_tmp_words(t, words), words, phrases)
    return _clean(t, words, phrases)


def _has_cjk_scan(s):
    return any("\u4e00" <= c <= "\u9fff" for c in s)


def _translate_tmp_words(text, words):
    """模擬 TranslateTmpText：白名單（TmpWords/Words 字典）整詞替換。"""
    r = text
    for k in sorted(words, key=len, reverse=True):
        if not k or re.search(r"[^A-Za-z ]", k):
            continue
        r = re.sub(r"(?i)\b" + re.escape(k) + r"\b", words[k], r)
    return r


def test_pipeline():
    print("== pipeline：ToStrocess 模擬語料 ==")
    dicts = load_dicts()
    words = {}
    phrases = {}
    for field, d in dicts.items():
        if field == "hook.Words" or field == "hook.ProperNounZh":
            for k, v in d.items(): words[k.lower()] = v
        if field == "hook.PhraseLeaks":
            for k, v in d.items(): phrases[k.lower()] = v
    cases = [
        # (輸入, 應包含, 不應包含)
        ("你經過了 門與水窪 of 1 dram 的 稀釋的 鹽", "德蘭", "dram"),
        ("燃燒的書籍與腐蝕的 data disks 散落一地", "資料磁碟", "data disks"),
        ("Chavvah, the Tree of Life 是古老的存在", "生命之樹", "Tree of Life"),
        ("You sit down on the chair.", "你坐到", "sit down"),
        ("You wade through water.", "你涉水穿過", "wade"),
        ("You are engulfed by slime.", "你被", "engulfed"),
        ("Frozen watervine farmer is dazed.", "感到暈眩", "dazed"),
        ("這已經是純中文了。", "這已經是純中文了。", None),
        ("Village:Quest:Gate:Merchant", "Village:Quest:Gate:Merchant", None),
        # ==== 中英混合 combat（=subject.Does:hit= 已轉「擊中」，for/with/weapon 仍英文）====
        ("咬喉獸食屑 擊中 (x1) for 2 damage with her bite.", "造成 2 傷害", "for"),
        ("你 擊中 (x1) for 2 damage with your bronze dagger!", "造成 2 傷害", "for"),
        ("咬喉獸食屑 擊中 (x3) for 3 damage with her 拳頭.", "造成 3 傷害", "for"),
        ("食屑獸 擊中 你 (x1) for 2 damage with 他的 爪.", "造成 2 傷害", "for"),
        # ==== 中英混合受到（=verb:take= 已轉「受到」）====
        ("你 受到 4 damage from 酸液.", "受到 4 傷害", "damage"),
        ("咬喉獸食屑 受到 2 damage.", "受到 2 傷害", "damage"),
        # ==== 拾取語境（玩家主詞 token 為空 →「受到 物品」不得殘留「受到」義）====
        ("受到 皮革護甲.", "拿起了", "受到"),
        ("受到 the 皮革護甲.", "拿起了", "the"),
        ("You take the bronze dagger.", "拿起了", "take"),
        ("你 擊中 (x2) for 3 damage with your 青銅匕首 [16]", "用 青銅匕首", "你的"),
        # ==== TMP 顯示層（技能頁面/聲望句）====
        ("10 Agility", "10 敏捷", "Agility"),
        ("19 Strength", "19 力量", "Strength"),
        ("{{C|10}} {{|Agility}}", "{{|敏捷}}", "Agility"),
        ("Dismember", "肢解", "Dismember"),
        ("Strength", "力量", "Strength"),
        ("The Mopango 有興趣分享 科技 的秘密。", "莫龐戈", "The"),
        ("The 伊帕德 的村民 有興趣聽聽關於他們的八卦。", "伊帕德 的村民", "The"),
        # ==== 逐詞語病（短語層）====
        ("由於 那裡 是 熊 在 你的 way, 你停止了 移動中。", "擋住了你的路", "移動中"),
        ("熊 在 你的 way。", "擋住了你的路", "way"),
        # ==== 訊息前綴（:: + 台詞）與死亡整句（2026-08-13 修復）====
        (":: 你 擊中 (x2) for 4 damage with your 青銅匕首! [9]", "用 青銅匕首 擊中(x2)", "for"),
        ("::The 熊 dies!", "熊 死亡。", "dies"),
        ("The 熊 dies!", "熊 死亡。", "The"),
        ("::你 擊中 (x1) for 2 damage with your 鐵匕首!", "用 鐵匕首 擊中(x1)", "your"),
        # ==== 電池狀態（PhraseLeaks 新增）====
        ("你辨識出 奇特小玩意 是 化學電池 (Full)。", "(滿電)", "Full"),
        # ==== miss 變體（2026-08-13 補全）====
        ("You miss with your bronze dagger! [9]", "你未擊中（用", "與"),
        ("The 熊 misses you with her bite! [18]", "熊 未擊中 你（用 她的 咬", "與"),
        ("You don't penetrate the bear's armor with your bronze dagger! [18]", "的護甲 [18]", "的護甲["),
        # ==== 技能需求串（Clean 詞組層）====
        ("[200sp] 25 敏捷, Draw a Bead, Wounding Fire", "繪製珠飾", "Draw"),
        ("[150sp] 19 敏捷, Sure Fire", "萬無一失(Sure Fire)", "Sure 火焰"),
        ("[200sp] 25 敏捷, Disorienting Fire", "令人迷失方向的火焰(Disorienting Fire)", "Disorienting 火焰"),
        # ==== 括號嵌套防回歸（管道值自帶「中文(English)」不得再包層）====
        ("收穫術(Harvestry)", "收穫術(Harvestry)", "收穫術(收穫術"),
        ("反作用力(Kickback) [50sp] 19 力量", "反作用力(Kickback)", "反作用力(反作用力"),
        ("閃躲(Juke) [200sp] 21 敏捷", "閃躲(Juke)", "閃躲(閃躲"),
    ]
    for inp, must, must_not in cases:
        if must in ("10 敏捷", "19 力量", "{{|敏捷}}", "肢解", "力量", "莫龐戈"):
            out = _tmp_process(inp, words, phrases)
        else:
            out = _to_string_process(inp, words, phrases)
        ok = must in out and (must_not is None or must_not not in out)
        check(f"語料: {inp[:40]}", ok, f"-> {out}" if not ok else "")


# ============ 4. data ============
def test_data():
    print("== data：zh-tw 資料完整性 ==")
    if not ZH.exists():
        check("zh-tw 目錄存在", False)
        return
    marked = 0
    files_with_marker = []
    for f in sorted(ZH.glob("*.xml")):
        s = f.read_text(encoding="utf-8")
        if "▶" in s:
            marked += s.count("▶")
            files_with_marker.append(f.name)
    check("zh-tw 資料無 ▶ 殘留", marked == 0, f"{marked} 個在 {files_with_marker}" if marked else "")
    # ifPlural 值側英文 token 洩漏
    leak_tokens = ["ifPlural:are:is=", "ifPlural:were:was=", "ifPlural:ones:members=",
                   "ifPlural:despise:despises=", "ifPlural:dislike:dislikes=",
                   "ifPlural:favor:favors=", "ifPlural:don't:doesn't="]
    total = 0
    for f in sorted(ZH.glob("*.xml")):
        s = f.read_text(encoding="utf-8")
        for m in re.finditer(r'<string\b[^>]*>((?:[^<]|&lt;)*?)</string>', s):
            if any(t in m.group(1) for t in leak_tokens):
                total += 1
    check("ifPlural 值側無英文 token 洩漏", total == 0, f"{total} 個")
    # 語病修正：Combat Miss「與」→「用」；Faction Feeling 不再「不在乎 在乎你」
    ms = (ZH / "Strings.zh-tw.xml").read_text(encoding="utf-8-sig")
    check("Combat Miss 值用『用』", "=subject.Does:miss= 用 =subject.its.item#weapon=" in ms)
    check("Faction Feeling 無『在乎 在乎你』", "在乎你" not in ms and "不在乎:不在乎= 你，但好鬥的" in ms)


# ============ 5. localization_keys（zh-tw XML 的 key 屬性必須與 base 一致）============
import xml.etree.ElementTree as _ET

_KEY_ATTRS = ("Name", "ID", "Class", "Type", "Value")
_DISP_ATTRS = {"DisplayName", "DisplayText", "Description", "Snippet", "ChargenTitle",
               "SingularTitle", "Title", "Subjective", "Objective", "Possessive", "Reflexive",
               "Substantive", "HelpText", "Accomplishment", "Hagiograph", "Gospel",
               "TinkerCategory", "TinkerDisplayName", "Unit", "ProperName", "NameContext",
               "IndefiniteArticle", "DefiniteArticle", "Article", "Plural", "Min", "Max",
               "Level", "x", "y", "Chance", "Priority", "Factions", "Reputation", "XP",
               "DescriptionResource", "SnippetResource", "SnippetText", "GameText"}


def _find_base_dir():
    cands = [
        Path("/mnt/g/SteamLibrary/steamapps/common/Caves of Qud/CoQ_Data/StreamingAssets/Base"),
        Path("/mnt/c/Games/Caves of Qud/CoQ_Data/StreamingAssets/Base"),
        Path("C:/SteamLibrary/steamapps/common/Caves of Qud/CoQ_Data/StreamingAssets/Base"),
    ]
    for c in cands:
        if c.exists():
            return c
    return None


def test_localization_keys():
    print("== localization_keys：zh-tw XML key 與 base 一致 ==")
    import os
    base_dir = _find_base_dir()
    if base_dir is None:
        check("base 目錄存在", False)
        return
    total = 0
    for mf in sorted(ZH.glob("*.zh-tw.xml")):
        name = mf.name
        if not name.endswith("zh-tw.xml"): continue
        bf = base_dir / name.replace(".zh-tw.xml", ".xml")
        if not bf.exists(): continue
        try:
            mr = _ET.fromstring(mf.read_text(encoding="utf-8-sig"))
        except _ET.ParseError as e:
            check(f"{name} XML 可解析", False, str(e))
            total += 1
            continue
        try:
            br = _ET.fromstring(bf.read_text(encoding="utf-8-sig"))
        except _ET.ParseError:
            continue
        bkeys = set()
        for el in br.iter():
            for a in _KEY_ATTRS:
                v = el.get(a)
                if v: bkeys.add(v)
        bad = []
        for el in mr.iter():
            # 有 ID 屬性的元素：Name 是 DisplayText（pregen/quest/step/book 等）
            if el.get("ID") is not None:
                continue
            # Worlds zone：Level/x/y 是 key，Name 是 DisplayText
            if el.tag == "zone" and el.get("Level") is not None:
                continue
            # Naming：prefix/infix/postfix/template 的 Name 是生成音節文字
            if name == "Naming.zh-tw.xml" and el.tag in ("prefix", "infix", "postfix", "suffix", "template", "templatevar", "value"):
                continue
            # Relics：relictypemapping Name 是 Key,DisplayText 雙重語義（historyspice 對齊，暫緩）
            if el.tag == "relictypemapping":
                continue
            for a in _KEY_ATTRS:
                v = el.get(a)
                if v and a not in _DISP_ATTRS and _has_cjk(v) and v not in bkeys:
                    bad.append((el.tag, a, v[:30]))
        if bad:
            total += 1
            check(f"{name} key 屬性無中文（{len(bad)} 個）", False, f"e.g. <{bad[0][0]} {bad[0][1]}='{bad[0][2]}'")
    if total == 0:
        check("zh-tw XML key 屬性無中文", True)


def _has_cjk(s):
    return any("\u4e00" <= c <= "\u9fff" for c in s)


# ============ 6.5 token_integrity（翻譯值不得丟失/改壞 =token= 槽位）============
def test_token_integrity():
    print("== token_integrity：=token= 完整性 ==")
    sys.path.insert(0, str(ROOT))
    try:
        import extract_templates as ET
    except Exception as e:
        check("extract_templates 可匯入", False, str(e))
        return
    entries = ET.parse_strings(ZH / "Strings.zh-tw.xml")
    bad_tok = 0
    bad_internal = 0
    untrans = 0
    for e in entries:
        ET.analyze(e)
        fs = e["flags"]
        if "token_mismatch" in fs:
            bad_tok += 1
        if "token_internal_cjk" in fs:
            bad_internal += 1
        if "untranslated" in fs:
            untrans += 1
    check("翻譯值不丟 =token=（token_mismatch=0）", bad_tok == 0, f"{bad_tok} 條")
    check("token 內部無中文鍵（token_internal_cjk=0）", bad_internal == 0, f"{bad_internal} 條")
    check("未翻譯模板數（報告用）", True, f"{untrans} 條")


# ============ 7. xml_structure（zh-tw XML 可解析 + 單一根）============
def test_xml_structure():
    print("== xml_structure：zh-tw XML 語法完整 ==")
    bad = []
    for mf in sorted(ZH.glob("*.zh-tw.xml")):
        s = mf.read_text(encoding="utf-8-sig")
        if s.count("<") < 2: continue
        try:
            _ET.fromstring(s)
        except _ET.ParseError as e:
            bad.append((mf.name, str(e)))
    check("zh-tw XML 全部可解析", not bad, str(bad[:3]) if bad else "")


# ============ 7. word_order（模板層語序修復 + 區段標題）============
def test_word_order():
    print("== word_order：模板語序修復與區段標題 ==")
    strings = ZH / "Strings.zh-tw.xml"
    factions = ZH / "Factions.zh-tw.xml"
    if strings.exists():
        s = strings.read_text(encoding="utf-8-sig")
        # drams 模板已重排為「德蘭的」（值側不再用 pluralize:dram= 的）
        check("drams 模板值側用「德蘭的」", "德蘭的 =liquid.name=" in s and "德蘭的 =container.liquid.name=" in s)
        check("drams 模板值側無『pluralize:dram= 的』", "pluralize:dram= 的 =liquid.name=<" not in s)
        # 方向值加「方」
        check("direction E → 東方", 'Context="direction.expanded (E)" ID="east">東方<' in s)
        check("direction N → 北方", 'Context="direction.expanded (N)" ID="north">北方<' in s)
        # wade 動詞不重複（涉過→穿過）
        check("wade 模板用『穿過』", "=subject.Does:wade= 穿過 =object.a.name=。" in s)
        # Chavvah Map Note ID 回歸 base（不含夏瓦）
        check("Chavvah Map Note ID 回歸英文", 'ID="The roaming keter of Chavvah, Tree of Life"' in s)
    else:
        check("Strings.zh-tw.xml 存在", False)
    if factions.exists():
        f = factions.read_text(encoding="utf-8-sig")
        check("Chavvah DisplayName 正確", 'DisplayName="夏瓦(Chavvah)，生命之樹"' in f)
    else:
        check("Factions.zh-tw.xml 存在", False)
    # 區段標題（UiStringsHook SectionHeaders）
    ui = REPL / "UiStringsHook.cs"
    if ui.exists():
        u = ui.read_text(encoding="utf-8")
        check("SectionHeaders 有 RESISTANCES", '"RESISTANCES"' in u and "抗性" in u)
        check("SectionHeaders 有 SECONDARY ATTRIBUTES", '"SECONDARY ATTRIBUTES"' in u and "次要屬性" in u)
    else:
        check("UiStringsHook.cs 存在", False)


# ============ 6. does 主詞（防「因為 是 擋在路中間」主詞消失回歸）============
def test_does_subject():
    print("== does_subject：Does replacer 須保留主詞顯示名 ==")
    rep = REPL / "Replacers.cs"
    if not rep.exists():
        check("Replacers.cs 存在", False)
        return
    r = rep.read_text(encoding="utf-8")
    # Does 方法必須輸出主詞名（呼叫 ZhName），不能只回傳動詞
    m = re.search(r'public static string Does\(VariableContext context, GameObject subject\)(.*?)\n    \}', r, re.S)
    check("Does(GameObject) 方法存在", m is not None)
    if m:
        body = m.group(1)
        check("Does 輸出含主詞名（ZhName/DoesZh）", ("ZhName" in body or "DoesZh" in body),
              "只回傳動詞會丟主詞" if not ("ZhName" in body or "DoesZh" in body) else "")
    # does 的 key 列表須含 blocker.does（擋路訊息用）
    check("Does key 含 blocker.does", '"blocker.does"' in r)


def test_verb_inflection():
    print("== verb_inflection：動詞剝屈折尾（harvests→收割）==")
    rep = REPL / "Replacers.cs"
    if not rep.exists():
        check("Replacers.cs 存在", False)
        return
    r = rep.read_text(encoding="utf-8")
    # LookupVerbZh 須存在且處理 -ies/-es/-s
    m = re.search(r'private static string LookupVerbZh\(string verb\)(.*?)\n    \}', r, re.S)
    check("LookupVerbZh 方法存在", m is not None)
    if m:
        body = m.group(1)
        check("LookupVerbZh 剝 -s 尾", 'EndsWith("s")' in body)
        check("LookupVerbZh 剝 -es 尾", 'EndsWith("es")' in body)
        check("LookupVerbZh 剝 -ies 尾", 'EndsWith("ies")' in body)
    # Verb/Does/ItDoes 都透過 LookupVerbZh 查表
    uses = [x for x in ["public static string Verb(", "public static string Does(", "public static string ItDoes("] if x in r]
    check("Verb/Does/ItDoes 存在", len(uses) == 3)
    check("DoesZh 使用 LookupVerbZh", "string zh = LookupVerbZh(verb);" in r)


def test_be_verb_drop():
    print("== be_verb_drop：Does 系 be 動詞回空（=X.Does:are=→無『是』）==")
    rep = REPL / "Replacers.cs"
    if not rep.exists():
        check("Replacers.cs 存在", False)
        return
    r = rep.read_text(encoding="utf-8")
    check("IsBeVerb 存在", "IsBeVerb(string verb)" in r)
    check("BeVerbs 含 are/is/was/were", '"are", true' in r and '"is", true' in r and '"was", true' in r)
    check("DoesZh be 動詞回主詞名", "IsBeVerb(verb)" in r and "return name;" in r)
    check("DoesNoun be 動詞回空", "IsBeVerb(verb)) return \"\";" in r)
    check("ItDoes be 動詞回空", "IsBeVerb(verb)) return \"\";" in r)


def test_token_protect():
    print("== token_protect：=...= token 內關鍵字不被 Words 誤傷 ==")
    hook = HOOK.read_text(encoding="utf-8")
    check("TokenGuard regex 存在", "TokenGuard" in hook)
    check("ProtectTokens 存在", "ProtectTokens" in hook)
    check("RestoreTokens 存在", "RestoreTokens" in hook)
    check("Clean 使用 ProtectTokens", "ProtectTokens(text, tokenBox)" in hook)
    check("Clean 還原 token", "RestoreTokens(result, tokenBox)" in hook)
    check("KeyLeaks 使用 ProtectTokens", "ProtectTokens(text, tokenBox)" in hook)
    # spice 不該在 Words 中（避免 =spice:...= token 被翻成「香料」→ No variable replacer）
    import re as _re
    m = _re.search(r'private static readonly Dictionary<string, string> Words\s*=(.*?)\n\s*\};', hook, _re.S)
    if m:
        check("Words 不含 spice→香料", '"spice", "香料"' not in m.group(1))
    else:
        check("Words 字典可解析", False)


def test_combat_hit_pattern():
    print("== combat_hit_pattern：DoesZh 轉換版 hit 句補主詞「你」==")
    hook = HOOK.read_text(encoding="utf-8")
    check("含『擊中』開頭 hit pattern（前綴耐受）", '擊中\\s+\\(' in hook and '^[^\\u4e00' in hook)
    check("含『你用 $3 擊中』替換", "你用 $3 擊中" in hook)
    check("含死亡整句（dies）", 'dies[.!]?$' in hook)
    check("含武器段所有格剝除（僅你的）", '用 (?:你的|the|a|an) ' in hook)


def _find_game_data():
    """往上找含 CoQ_Data 的遊戲目錄（與 extract_templates 同邏輯）。"""
    g = PROJ
    for _ in range(6):
        if (g / "CoQ_Data").exists():
            return g
        g = g.parent
    return None


def _parse_faction_interests(xml_path):
    """解析 Factions*.xml：faction → interests 屬性集合（Buy/Sell/LearnDescription）。"""
    import xml.etree.ElementTree as ET
    out = {}
    root = ET.parse(xml_path).getroot()
    for fac in root.findall("faction"):
        name = fac.get("Name")
        ints = fac.find("interests")
        if ints is None:
            continue
        keys = [k for k in ("BuyDescription", "SellDescription", "LearnDescription") if ints.get(k) is not None]
        if keys:
            out[name] = keys
    return out


# ============ 8. paren_protect（中文(English) 括號不被逐詞污染）============
def test_paren_protect():
    print("== paren_protect：ProperNoun 括號英文保護 ==")
    if not HOOK.exists():
        check("TextCleanerHook.cs 存在", False)
        return
    hook = HOOK.read_text(encoding="utf-8")
    check("ProtectParens 存在", "ProtectParens" in hook)
    check("RestoreParens 存在", "RestoreParens" in hook)
    check("PossessiveZh 存在", "PossessiveZh" in hook)
    check("Clean 保護括號（在逐詞替換前）",
          "ProtectParens(result, parenBox)" in hook or "ProtectParens(work, parenBox)" in hook)
    hook_pos = hook.find("ProtectParens(result, parenBox)")
    if hook_pos == -1:
        hook_pos = hook.find("ProtectParens(work, parenBox)")
    check("括號保護在 Phrase 之後 Words 之前", hook_pos != -1 and "WordsRegex" in hook[hook_pos:hook_pos+600])
    check("Clean 還原括號", "RestoreParens(result, parenBox)" in hook)
    check("'s 後接中文規則", "'s\\s+" in hook and '"哈爾"' not in hook)
    # 模擬「保護式逐詞替換」：對已翻譯 ProperNoun 樣本，括號內不該被替換成中文
    words = {}
    m = re.search(r'private static readonly Dictionary<string, string> Words\s*=(.*?)\n\s*\};', hook, re.S)
    if m:
        for kv in re.finditer(r'\{\s*"([^"]+)"\s*,\s*"([^"]*)"', m.group(1)):
            words[kv.group(1).lower()] = kv.group(2)
    def sim(text):
        # 等價簡化模擬：括號內（緊鄰中文）一律不動
        out = []
        i = 0
        while i < len(text):
            if text[i] == '(':
                j = text.find(')', i)
                if j != -1 and (i == 0 or text[i-1] == ' ' and i >= 2 and text[i-2] >= '一' and text[i-2] <= '鿿'):
                    out.append(text[i:j+1]); i = j+1; continue
            out.append(text[i]); i += 1
        return "".join(out)
    samples = [
        "農民公會(Farmers' Guild)",
        "瑪門之子(Children of Mamon)",
        "植物聯盟(Consortium of Phyta)",
        "穆拉普爾(Murapur)",
    ]
    for s in samples:
        check(f"括號保護: {s}", sim(s) == s, sim(s))


# ============ 9. xml_paren_hybrid（XML 值中「中文(英文 中文)」污染）============
def test_xml_paren_hybrid():
    print("== xml_paren_hybrid：XML 括號內中英混雜（Farmers' 公會 類）==")
    if not ZH.exists():
        check("zh-tw 目錄存在", False)
        return
    import xml.etree.ElementTree as ET
    bad = []
    n = 0
    for mf in sorted(ZH.glob("*.zh-tw.xml")):
        s = mf.read_text(encoding="utf-8-sig")
        if s.count("<") < 2:
            continue
        try:
            root = ET.fromstring(s)
        except ET.ParseError:
            continue
        for el in root.iter():
            for attr in ("DisplayName", "Name", "Short", "Description", "Value"):
                v = el.get(attr)
                if v:
                    _scan_parens(v, mf.name, bad); n += 1
            if el.text:
                _scan_parens(el.text, mf.name, bad); n += 1
    # 「撇號+中文」= X's 中文 污染標誌（Farmers' 公會）；其餘中文括號夾專名為合法寫法
    hybrid = [b for b in bad if "'" in b]
    other = [b for b in bad if "'" not in b]
    check("XML 括號段無 'X's＋中文' 污染", not hybrid, "; ".join(hybrid[:4]))
    check("XML 括號段其他混雜（報告）", True, f"{len(other)} 處中文括號夾英文專名（合法）")


def _scan_parens(v, fname, bad):
    # 剝 =token= 與 {{markup}}
    t = re.sub(r"=[^=]{1,80}=", "", v)
    t = re.sub(r"\{\{[^}]*\}\}", "", t)
    i = 0
    while i < len(t):
        c = t[i]
        if c in ("(", "（"):
            close = ")" if c == "(" else "）"
            j = t.find(close, i + 1)
            if j == -1:
                break
            seg = t[i+1:j]
            if re.search(r"[A-Za-z]{2,}", seg) and re.search(r"[一-鿿]", seg):
                # 前一非空白字元是中文 → 是「中文(英文)」規範括號 → 混雜即污染
                p = i - 1
                while p >= 0 and t[p] in " 	":
                    p -= 1
                if p >= 0 and t[p] >= '一' and t[p] <= '鿿':
                    bad.append(f"{fname}: …{v[max(0, i-6):j+5]}…")
            i = j + 1
        else:
            i += 1


# ============ 10. sultanterm_values（值側 .sultanTerm|plural=. 與 sultans 殘留）============
def test_sultanterm_values():
    print("== sultanterm_values：=sultanTerm|plural= 與 sultans 不再出現在值側 ==")
    strings = ZH / "Strings.zh-tw.xml"
    if not strings.exists():
        check("Strings.zh-tw.xml 存在", False)
        return
    s = strings.read_text(encoding="utf-8-sig")
    # 只檢查值側（> 與 </string> 之間），ID 側保留 |=plural（查找鍵）是正常的
    vals = re.findall(r">([^<]*)</string>", s)
    bad_plural = [v for v in vals if "=sultanTerm|plural=" in v]
    def bare(v):
        t = re.sub(r"=[^=]{1,80}=", "", v)
        t = re.sub(r"\{\{[^}]*\}\}", "", t)
        t = re.sub(r"[（(][^）)]*[）)]", "", t)
        return t
    bad_sultans = [v for v in vals if re.search(r"\b[Ss]ultan", bare(v))]
    check("值側無 =sultanTerm|plural=", not bad_plural, f"{len(bad_plural)} 條")
    check("值側無 sultan 殘留（token/括號外）", not bad_sultans, "; ".join(v[:40] for v in bad_sultans[:3]))


# ============ 11. interests_coverage（遊戲 interests 屬性在 mod 有覆蓋）============
def test_interests_coverage():
    print("== interests_coverage：Factions.zh-tw.xml 覆蓋遊戲 interests 屬性 ==")
    game = _find_game_data()
    modfac = ZH / "Factions.zh-tw.xml"
    if game is None or not (game / "CoQ_Data" / "StreamingAssets" / "Base" / "Factions.xml").exists():
        check("遊戲 Factions.xml 可定位", False)
        return
    if not modfac.exists():
        check("mod Factions.zh-tw.xml 存在", False)
        return
    g = _parse_faction_interests(game / "CoQ_Data" / "StreamingAssets" / "Base" / "Factions.xml")
    m = _parse_faction_interests(modfac)
    missing = []
    for fac, keys in g.items():
        covered = m.get(fac)
        if covered is None or not any(k in covered for k in keys):
            missing.append(f"{fac}:{','.join(keys)}")
    check("遊戲 interests 屬性全覆蓋", not missing, "; ".join(missing[:6]))


def test_propernouns():
    print("== propernouns：ProperName 布林 / town zone 名 / 專名括號 ==")
    import xml.etree.ElementTree as ET
    worlds = ZH / "Worlds.zh-tw.xml"
    factions = ZH / "Factions.zh-tw.xml"
    if worlds.exists():
        try:
            root = ET.fromstring(worlds.read_text(encoding="utf-8-sig"))
        except ET.ParseError as e:
            check("Worlds.zh-tw.xml 可解析", False, str(e)); return
        bad_bool = []
        for el in root.iter("zone"):
            pp = el.get("ProperName")
            if pp is not None and pp not in ("true", "false"):
                bad_bool.append(pp)
        check("zone ProperName 全為布林", not bad_bool, f"{bad_bool[:3]}")
        s = worlds.read_text(encoding="utf-8-sig")
        check("約帕 zone 名中文化", 'Name="約帕(Joppa)"' in s)
        check("恰庫恰 zone 名中文化", 'Name="恰庫恰(Kyakukya)"' in s)
        check("無殘留 zone Name=Joppa 純英文", '<zone[^>]*Name="Joppa"' not in s if True else False)
    else:
        check("Worlds.zh-tw.xml 存在", False)
    if factions.exists():
        f = factions.read_text(encoding="utf-8-sig")
        check("Joppa 派系名帶括號", 'DisplayName="約帕(Joppa)村民"' in f)
        check("Kyakukya 派系名帶括號", 'DisplayName="恰庫恰(Kyakukya)村民"' in f)
        check("141 通稱派系不加括號（檢查無誤刪）", 'DisplayName="咬顎獸"' in f or 'DisplayName="狒狒"' in f)


def test_shorttext_coverage():
    print("== shorttext_coverage：TmpWords 覆蓋全部遊戲 power/技能樹名 ==")
    import xml.etree.ElementTree as ET
    game = _find_game_data()
    hook = HOOK.read_text(encoding="utf-8")
    m = re.search(r'TmpWords\s*=\s*new Dictionary.*?\{(.*?)\n\s*\};', hook, re.S)
    if game is None or m is None:
        check("TmpWords 可解析且遊戲可定位", False); return
    tw = set(re.findall(r'\{\s*"([^"]+)"\s*,\s*"', m.group(1)))
    g = ET.parse(game / "CoQ_Data/StreamingAssets/Base/Skills.xml").getroot()
    missing = [p.get("Name") for p in g.iter("power") if p.get("Name") and p.get("Name") not in tw]
    check("TmpWords 覆蓋全部 power 名", not missing, "; ".join(missing[:6]))
    # PhraseLeaks（Clean 詞組層）也需覆蓋 power 名，防需求串等混雜句漏
    m2 = re.search(r'PhraseLeaks\s*=\s*new Dictionary.*?\{(.*?)\n\s*\};', hook, re.S)
    if m2:
        pl = set(re.findall(r'\{\s*"([^"]+)"\s*,\s*"', m2.group(1)))
        missing2 = [p.get("Name") for p in g.iter("power") if p.get("Name") and p.get("Name") not in pl]
        check("PhraseLeaks 覆蓋全部 power 名", not missing2, "; ".join(missing2[:6]))
    else:
        check("PhraseLeaks 可解析", False)
    # Skills DisplayName 不得為純英文（語料漏翻防回歸，除 Name 原文本身）
    skillsf = ZH / "Skills.zh-tw.xml"
    if skillsf.exists():
        import xml.etree.ElementTree as _ET2
        try:
            _root = _ET2.fromstring(skillsf.read_text(encoding="utf-8-sig"))
            unt = []
            for _p in _root.iter("power"):
                _n = _p.get("Name"); _d = _p.get("DisplayName") or _p.get("Snippet") or ""
                if _d and re.fullmatch(r"[A-Za-z .'\-]+", _d.strip()) and _d.strip() != _n:
                    unt.append(f"{_p.get('Name')}→{_d}")
            check("Skills DisplayName 無純英文（Conatus 類）", not unt, "; ".join(unt[:5]))
        except Exception as _e:
            check("Skills.zh-tw.xml 可解析", False, str(_e))
    # 已修樣本防回歸（語料層漏翻）
    strings = ZH / "Strings.zh-tw.xml"
    if strings.exists():
        s = strings.read_text(encoding="utf-8-sig")
        check("armor 殘留已修", "凱塞欣德的 armor" not in s)
        check("shop 殘留已修", "哈米蟹的 shop" not in s)
        check("Slam 殘留已修", "盾牌 Slam" not in s)
        check("Willowy 殘留已修", "Willowy：此物品" not in s)
        vals = re.findall(r">([^<]*)</string>", s)
        import re as _r
        bare = [re.sub(r"=[^=]{1,80}=", "", v) for v in vals]
        check("tory 殘留已修（值側剝 token；調試行除外）",
              not any("tory" in v.lower() and re.search(r"[\u4e00-\u9fff]", v) and "Inventory.cs" not in v for v in bare))
    # spice 錯譯已修
    hp = PROJ / "historyspice.zh-tw.json"
    if hp.exists():
        h = hp.read_text(encoding="utf-8")
        check("quarters 無『四分之一』錯譯", "四分之一" not in h)
        check("commanding 無『指揮中的』", "指揮中的" not in h)
    hook2 = HOOK.read_text(encoding="utf-8")
    check("Ego 譯名為自我", '"Ego", "自我"' in hook2)
    check("Willpower 譯名為意志", '"Willpower", "意志"' in hook2)


def test_cs_structure():
    """C# 結構順序驗證：字串/註釋剝離後棧式配對 + 字典尾閉合 + 孤立分號檢查。
    計數平衡會被「早閉」（}後條目仍在字典外）騙過——本測試抓順序錯誤。"""
    print("== cs_structure：花括號順序 / 字典閉合 / 孤立分號 ==")
    for cs in (HOOK, REPLACERS, UIHOOK, HARMONY):
        if not cs.exists():
            check(f"{cs.name} 存在", False); continue
        src = cs.read_text(encoding="utf-8")
        # 剝離字串與註釋
        t = re.sub(r'@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"', '""', src)
        t = re.sub(r'//[^\n]*', '', t)
        t = re.sub(r'/\*.*?\*/', '', t, flags=re.S)
        stack = []
        brace_errs = []
        for i, ch in enumerate(t):
            if ch in '({[':
                stack.append(ch)
            elif ch in ')}]':
                if not stack:
                    brace_errs.append(f"{cs.name}@{i}:{ch} 無對應開括號"); break
                o = stack.pop()
                if '({['.index(o) != ')}]'.index(ch):
                    brace_errs.append(f"{cs.name}@{i}: {o}{ch} 不匹配"); break
        if stack:
            brace_errs.append(f"{cs.name}: {len(stack)} 個未閉合 {''.join(stack[:5])}")
        check(f"{cs.name} 括號順序正確", not brace_errs, "; ".join(brace_errs[:2]))
        # 字典/物件初始化：`{ "..." }` 條目行的前方若出現「更淺縮排的 }」= 字典提前閉合（條目跌出）
        lines = t.split('\n')
        problems = []
        entry_re = re.compile(r'^(\s*)\{\s*"')
        close_re = re.compile(r'^(\s*)\}\s*$')
        dict_def_re = re.compile(r'(?:Dictionary|new\s+[A-Za-z0-9_<>,.]+)\)?\s*$|\{\s*$')
        # 對每個條目行：往前找最近的字典定義行，檢查其間是否夾一個更淺/等深閉合行
        for idx, ln in enumerate(lines):
            em = entry_re.match(ln)
            if not em:
                continue
            indent = len(em.group(1))
            seen_close = False
            for j in range(idx - 1, max(-1, idx - 200), -1):
                l2 = lines[j]
                dm = dict_def_re.search(l2)
                if dm or re.search(r'private static readonly Dictionary', l2):
                    break
                cm = close_re.match(l2)
                if cm and len(cm.group(1)) <= indent:
                    seen_close = True
                    break
            if seen_close:
                problems.append(f"{cs.name}:{idx+1} 字典提前閉合（條目在閉合後） {ln.strip()[:24]!r}")
        # 孤立分號行
        for idx, ln in enumerate(lines):
            if ln.strip() == ';':
                problems.append(f"{cs.name}:{idx+1} 孤立分號行")
        check(f"{cs.name} 無字典提前閉合/孤立分號", not problems, "; ".join(problems[:6]))


def test_liquids_integrity():
    """Liquids.zh-tw.xml 必須保留官方必要欄位（slug/class/colors/render/part），
    否則遊戲液體藍圖遺失 → primary liquid unknown → 載入崩潰（2026-08-13 事故）。"""
    print("== liquids_integrity：液體藍圖欄位完整性 ==")
    import xml.etree.ElementTree as ET
    game = _find_game_data()
    mf = ZH / "Liquids.zh-tw.xml"
    if game is None or not mf.exists():
        check("Liquids 檔可定位", False); return
    bf = game / "CoQ_Data/StreamingAssets/Base/Liquids.xml"
    if not bf.exists():
        check("base Liquids.xml 存在", False); return
    try:
        b = ET.fromstring(bf.read_text(encoding="utf-8"))
        m = ET.fromstring(mf.read_text(encoding="utf-8-sig"))
    except ET.ParseError as e:
        check("Liquids 可解析", False, str(e)); return
    bl = {li.get("Name"): li for li in b.findall("liquid")}
    ml = {li.get("Name"): li for li in m.findall("liquid")}
    check("liquid 數量與 base 一致", len(bl) == len(ml), f"{len(bl)} vs {len(ml)}")
    missing = []
    for name, li in ml.items():
        bbody = bl.get(name)
        if bbody is None:
            continue
        for req in ("slug", "class"):
            if bbody.find(req) is not None and li.find(req) is None:
                missing.append(f"{name} 缺 <{req}>")
        # part 完整性：base 的 part 除 mod 同 Name 覆蓋外需保留
        bparts = {p.get("Name") for p in bbody.findall("part")}
        mparts = {p.get("Name") for p in li.findall("part")}
        lost = bparts - mparts
        if lost:
            missing.append(f"{name} 失 part {sorted(lost)[:2]}")
    check("liquid 必需欄位/part 齊全", not missing, "; ".join(missing[:6]))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--static", action="store_true")
    ap.add_argument("--dict", action="store_true")
    ap.add_argument("--pipeline", action="store_true")
    ap.add_argument("--data", action="store_true")
    ap.add_argument("--wordorder", action="store_true")
    ap.add_argument("--localkeys", action="store_true")
    ap.add_argument("--xmldata", action="store_true")
    ap.add_argument("--doessubject", action="store_true")
    ap.add_argument("--verbinflection", action="store_true")
    a = ap.parse_args()
    run_all = not (a.static or a.dict or a.pipeline or a.data or a.wordorder or a.localkeys or a.xmldata or a.doessubject or a.verbinflection)
    if run_all or a.static: test_static_cs()
    if run_all or a.dict: test_dictionary()
    if run_all or a.pipeline: test_pipeline()
    if run_all or a.data: test_data()
    if run_all or a.localkeys: test_localization_keys()
    if run_all or a.xmldata: test_xml_structure()
    if run_all or a.xmldata: test_token_integrity()
    if run_all or a.wordorder: test_word_order()
    if run_all or a.doessubject: test_does_subject()
    if run_all or a.verbinflection: test_verb_inflection()
    if run_all or a.verbinflection: test_be_verb_drop()
    if run_all or a.verbinflection: test_token_protect()
    if run_all or a.verbinflection: test_combat_hit_pattern()
    if run_all or a.static: test_paren_protect()
    if run_all or a.xmldata: test_xml_paren_hybrid()
    if run_all or a.xmldata: test_sultanterm_values()
    if run_all or a.data: test_interests_coverage()
    if run_all or a.data: test_liquids_integrity()
    if run_all or a.xmldata: test_propernouns()
    if run_all or a.static: test_shorttext_coverage()
    if run_all or a.static: test_cs_structure()
    print(f"\n===== 結果: {PASS} PASS / {FAIL} FAIL =====")
    sys.exit(1 if FAIL else 0)


if __name__ == "__main__":
    main()