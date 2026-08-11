#!/usr/bin/env python3
"""
export_leak_words.py — 導出「動態文字會洩漏」的英文詞匯清單。

背景：靜態文字（物件名/描述）由 zh-tw/*.xml 翻譯；但「動態組裝文字」
（歷史書本、村民八卦、陣營描述、模板 token 如 =ifPlural:are:is=）會在執行期
拼出英文詞，只能靠 replacer 字典（Words/Verbs/PhraseLeaks）逐詞兜底。
本工具從遊戲 base 資料提取所有「人類可讀」英文詞，對比全部 replacer 字典，
輸出漏詞清單（含頻率與例句環境），供（1）人工/LLM 翻譯後補進字典，
（2）run_tests.py 的 LEAK_WORDS 擴增。

輸入 base 來源：
  - ExampleLanguage/Strings.example.xml（+ Conversations）
  - Books.xml、HistorySpice.jsonc、Naming.xml 的顯示文字
輸出：tools/leak_words_report.json + 主控台摘要

用法：
  python3 tools/export_leak_words.py                 # 全掃，輸出報告
  python3 tools/export_leak_words.py --min-freq 5    # 只列 >=5 次的詞
  python3 tools/export_leak_words.py --context       # 附例句環境
"""
import argparse
import json
import re
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROJ = ROOT.parent
REPL = PROJ.parent / "qud-zh-tw-replacers"
GAME_BASE = Path("/mnt/g/SteamLibrary/steamapps/common/Caves of Qud/CoQ_Data/StreamingAssets/Base")

C_FILES = [REPL / "TextCleanerHook.cs", REPL / "Replacers.cs",
           REPL / "UiStringsHook.cs", REPL / "HarmonyPatches.cs"]


def parse_dict_blocks(src):
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


def load_replacer_words():
    low = set()
    for f in C_FILES:
        if not f.exists(): continue
        s = f.read_text(encoding="utf-8")
        for field, d in parse_dict_blocks(s):
            for k in d:
                low.add(k.lower())
    return low


# 停用詞（文法詞，通常不值得當漏詞補——除非已在字典）
STOP = set("""the a an of to in on at for with without by from about into over under after before
during between through out up down off against while when where why what which who whom whose
this that these those there here it its is are was were be been being am do does did doing
have has had having can could will would shall should may might must not no nor so and or but
as than then if else because since until unless while all any both each few more most other
some such only own same too very just also again further once twice one two three done going
your you your my me mine our ours their theirs his her hers him we they them us""".split())


def extract_source_texts():
    """回傳 {來源: [顯示文字片段]}。"""
    sources = {}
    src_dir = GAME_BASE / "ExampleLanguage"
    for name in ["Strings.example.xml", "Strings.Conversations.example.xml"]:
        p = src_dir / name
        if p.exists():
            s = p.read_text(encoding="utf-8", errors="ignore")
            sources[name] = [m.group(1) for m in re.finditer(r'<string[^>]*>(?:▶)?((?:[^<]|\n)*?)</string>', s)]
    for name in ["Books.xml", "Naming.xml"]:
        p = GAME_BASE / name
        if p.exists():
            s = p.read_text(encoding="utf-8", errors="ignore")
            sources[name] = re.findall(r'Value="((?:[^"\\]|\\.)*)"', s)
    p = GAME_BASE / "HistorySpice.jsonc"
    if p.exists():
        s = p.read_text(encoding="utf-8", errors="ignore")
        # 值字串（"..." 內含 2+ 英文字母、非純引用）
        sources["HistorySpice.jsonc"] = [m.group(1) for m in re.finditer(r'"((?:[^"\\]|\\.)*)"', s)
                                         if len(re.findall(r"[A-Za-z]{2,}", m.group(1))) >= 2]
    return sources


def _tokens(text):
    text = re.sub(r'=[^=]+=', ' ', text)      # 模板 token
    text = re.sub(r'\{\{[^}]*\}\}', ' ', text)  # markup
    text = re.sub(r'<[^>]+>', ' ', text)       # 標籤
    for w in re.findall(r"[A-Za-z]{3,}", text):
        yield w.lower()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--min-freq", type=int, default=3)
    ap.add_argument("--context", action="store_true")
    ap.add_argument("--verbose", action="store_true")
    a = ap.parse_args()

    covered = load_replacer_words()
    sources = extract_source_texts()
    cnt = Counter()
    ctx = defaultdict(list)
    for name, texts in sources.items():
        for t in texts:
            for w in _tokens(t):
                cnt[w] += 1
                if len(ctx[w]) < 5:
                    ctx[w].append({"src": name, "ex": t[:120]})

    missing = {w: c for w, c in cnt.items()
               if w not in covered and w not in STOP and c >= a.min_freq}

    print(f"覆蓋字典詞數: {len(covered)}")
    print(f"顯示文字唯一英文詞: {len(cnt)}")
    print(f"\n=== 漏詞（>= {a.min_freq} 次、不在字典、非停用詞）===")
    for w, c in sorted(missing.items(), key=lambda x: -x[1])[:60]:
        line = f"  {w:18s} x{c}"
        if a.context and ctx[w]:
            line += "  | " + ctx[w][0]["ex"].replace("\n", " ")
        print(line)

    out = ROOT / "leak_words_report.json"
    out.write_text(json.dumps({
        "generated_for_dicts": [f.name for f in C_FILES],
        "min_freq": a.min_freq,
        "missing": {w: {"count": c, "examples": ctx[w]} for w, c in missing.items()},
    }, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n報告已寫: {out}（{len(missing)} 個漏詞）")


if __name__ == "__main__":
    main()