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
    """回傳 list of (field_name, {key: value})。"""
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
        for kv in re.finditer(r'\{\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"', block):
            d[kv.group(1)] = kv.group(2)
        out.append((field, d))
    return out


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


def _clean(text, words, phrases):
    hasEng, hasCjk = _scan_lang(text)
    if not (hasEng and hasCjk): return text
    # PhraseRegex 先（整句）
    for k in sorted(phrases, key=len, reverse=True):
        text = re.sub(r'(?i)' + re.escape(k), phrases[k], text)
    # WordsRegex 後（整詞）；Python 要求 (?i) 在開頭（C# 可 `\b(?i)`）
    for k in sorted(words, key=len, reverse=True):
        text = re.sub(r'(?i)\b' + re.escape(k) + r'\b', words[k], text)
    return text


FRAME_RULES = [
    (r'^You\s+sit\s+down\s+on\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$', r'你坐到 \1 上。'),
    (r'^You\s+wade\s+through\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$', r'你涉水穿過 \1。'),
    (r'^You\s+are\s+engulfed\s+by\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$', r'你被 \1 吞噬。'),
    (r'^(.+?)\s+is\s+dazed[.!]?$', r'\1 感到暈眩。'),
]
FRAME_TRIGGER = re.compile(
    r'(?i)\b(hit|miss|toggle|dazed|stand|take|eat|toss|gather|sit|climb|jump|wade|swim|'
    r'emerge|bump|bond|detach|slip|swap|entangle|engulf|drag|suck|impal|lying|sitting|'
    r'enclosed|pilot|knock|stop|move|look|turn|fall|rise)\w*')


def _status_fragments(text):
    if '{{' not in text and not FRAME_TRIGGER.search(text):
        return text
    for pat, repl in FRAME_RULES:
        text = re.sub(pat, repl, text, flags=re.I)
    return text


def _to_string_process(text, words, phrases):
    hasEng, hasCjk = _scan_lang(text)
    if not hasEng: return text
    if not hasCjk:
        return _status_fragments(text)
    return _clean(_status_fragments(text), words, phrases)


def test_pipeline():
    print("== pipeline：ToStrocess 模擬語料 ==")
    dicts = load_dicts()
    words = {}
    phrases = {}
    for field, d in dicts.items():
        if field == "hook.Words":
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
    ]
    for inp, must, must_not in cases:
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


# ============ 5. word_order（模板層語序修復 + 區段標題）============
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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--static", action="store_true")
    ap.add_argument("--dict", action="store_true")
    ap.add_argument("--pipeline", action="store_true")
    ap.add_argument("--data", action="store_true")
    ap.add_argument("--wordorder", action="store_true")
    ap.add_argument("--doessubject", action="store_true")
    a = ap.parse_args()
    run_all = not (a.static or a.dict or a.pipeline or a.data or a.wordorder or a.doessubject)
    if run_all or a.static: test_static_cs()
    if run_all or a.dict: test_dictionary()
    if run_all or a.pipeline: test_pipeline()
    if run_all or a.data: test_data()
    if run_all or a.wordorder: test_word_order()
    if run_all or a.doessubject: test_does_subject()
    print(f"\n===== 結果: {PASS} PASS / {FAIL} FAIL =====")
    sys.exit(1 if FAIL else 0)


if __name__ == "__main__":
    main()