#!/usr/bin/env python3
"""
backfill.py — 把翻譯結果自動回填到 TextCleanerHook 詞典（Words/PhraseLeaks）。
輸入：translated_ui.json（[{text, zh}]）或 --limit。
規則：含空白或含動詞短語 → PhraseLeaks；單詞 → Words。
產出：改寫 TextCleanerHook.cs（去重、格式對齊），並自動重跑 run_tests 驗證。

用法：
  python3 tools/backfill.py                       # 回填 translated_ui.json
  python3 tools/backfill.py --file path.json
"""
import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROJ = ROOT.parent
HOOK = PROJ.parent / "qud-zh-tw-replacers" / "TextCleanerHook.cs"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--file", default=str(ROOT / "translated_ui.json"))
    a = ap.parse_args()

    def sanitize(v):
        # LLM 輸出消毒：真換行/控制字元 → 空格壓平；引號轉義（防 C# 字串斷行 CS1010）
        v = re.sub(r"[\r\n\t\x00-\x08\x0b\x0c\x0e-\x1f]", " ", v)
        v = re.sub(r"\s{2,}", " ", v).strip()
        return v.replace('"', '\\"')

    src = Path(a.file)
    if not src.exists():
        print(f"找不到翻譯結果 {src}（先跑 run_pipeline translate）")
        sys.exit(1)
    items = json.loads(src.read_text(encoding="utf-8"))
    pairs = []
    skipped = 0
    for x in items:
        zh = x.get("zh")
        if not zh or zh == x["text"]:
            continue
        zh = sanitize(zh)
        if not zh or zh == x["text"]:
            continue
        pairs.append((x["text"], zh))
    print(f"待回填: {len(pairs)} 條（消毒跳過 {skipped} 條異常）")

    hook_src = HOOK.read_text(encoding="utf-8")

    def dict_block(name, src):
        m = re.search(r'private static readonly Dictionary<string, string> ' + name + r'\s*=\s*new Dictionary.*?\n(\s*\{)', src)
        if not m:
            return None, None
        start = m.end() - 1
        end = src.find("};", start)
        return start, end

    def has_key(name, key):
        s, e = dict_block(name, hook_src)
        if s is None:
            return False
        return re.search(r'\{\s*"([^"]+)"\s*,\s*"', hook_src[s:e], re.I) is not None and \
               any(k.lower() == key.lower() for k in re.findall(r'\{\s*"([^"]+)"\s*,\s*"', hook_src[s:e]))

    added = 0
    for en, zh in pairs:
        en = en.strip()
        if not en or len(en) > 90:
            continue
        target = "PhraseLeaks" if (" " in en or len(en) > 6) else "Words"
        if has_key(target, en):
            continue
        s, e = dict_block(target, hook_src)
        if s is None:
            print(f"找不到字典 {target}（字典結構未識別）")
            continue
        indent = "            "
        entry = f'{indent}{{ "{en}", "{zh}" }},'
        # 插到字典閉合 }; 之前
        hook_src = hook_src[:e] + "\n" + entry + hook_src[e:]
        added += 1

    if added == 0:
        print("無新增（全部已譯或重複）")
        return
    HOOK.write_text(hook_src, encoding="utf-8")
    print(f"已回填 {added} 條到 TextCleanerHook.cs（Words/PhraseLeaks）")

    # 驗證
    r = subprocess.run([sys.executable, str(ROOT / "run_tests.py")], capture_output=True, text=True)
    tail = r.stdout.strip().splitlines()[-1] if r.stdout.strip() else "?"
    print("run_tests:", tail)
    if "FAIL" in tail:
        print(r.stdout[-1200:])


if __name__ == "__main__":
    main()