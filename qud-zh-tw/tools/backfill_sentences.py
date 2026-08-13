#!/usr/bin/env python3
"""
backfill_sentences.py — 把 translated_msg.json 回填到 TextCleanerHook.SentenceDict。

規則：無變量完整句（翻譯 ≠ 原文）；值消毒（換行/控制字元/引號）；大小寫敏感去重。
完成後自動跑 run_tests 驗證（含紅線與「字典值無換行」防回歸）。
用法：python3 tools/backfill_sentences.py
"""
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
HOOK = ROOT.parent.parent / "qud-zh-tw-replacers" / "TextCleanerHook.cs"


def main():
    src = ROOT / "translated_msg.json"
    if not src.exists():
        print("缺少 translated_msg.json（先跑 run_pipeline translate --src msg_templates.json）")
        sys.exit(1)
    items = json.loads(src.read_text(encoding="utf-8"))
    dedup = {}
    for x in items:
        zh = (x.get("zh") or "").strip()
        en = x["text"].strip()
        if not zh or zh == en or not en:
            continue
        # LLM 偽代碼/格式破壞過濾
        if "=> _S(" in zh or "=> _S(" in en or en.startswith('"') or zh.startswith('"'):
            continue
        if re.search(r'(?<!\\)"', zh) or re.search(r'(?<!\\)"', en):
            continue
        zh = re.sub(r"[\r\n\t\x00-\x08\x0b\x0c\x0e-\x1f]", " ", zh)
        zh = re.sub(r"\s{2,}", " ", zh).strip().replace('"', '\\"')
        if len(en) > 160 or en in dedup:
            continue
        dedup[en] = zh

    hook = HOOK.read_text(encoding="utf-8")
    m = re.search(r"private static readonly Dictionary<string, string> SentenceDict =\s*new Dictionary<string, string>\(StringComparer\.Ordinal\)\s*\n\s*\{", hook)
    if not m:
        print("SentenceDict 結構未識別")
        sys.exit(1)
    start = hook.find("StringComparer.Ordinal)", m.start()) + len("StringComparer.Ordinal)")
    begin = hook.find("{", start) + 1
    end = hook.find("\n    };", begin)
    if end < 0:
        end = hook.find("};", begin)
    indent = "        "

    existing = set(re.findall(r'\{\s*"([^"]+)"\s*,\s*"', hook[begin:end]))
    entries = []
    added = 0
    for en, zh in sorted(dedup.items()):
        if en in existing:
            continue
        entries.append(f'{indent}{{ "{en}", "{zh}" }},')
        added += 1
    if entries:
        hook = hook[:begin] + "\n" + "\n".join(entries) + "\n" + hook[begin:]
    HOOK.write_text(hook, encoding="utf-8")
    print(f"SentenceDict 回填 {added} 條（現有 {len(existing)}）")

    r = subprocess.run([sys.executable, str(ROOT / "run_tests.py")], capture_output=True, text=True)
    tail = r.stdout.strip().splitlines()[-1] if r.stdout.strip() else "?"
    print("run_tests:", tail)
    if "FAIL" in tail:
        print(r.stdout[-1500:])


if __name__ == "__main__":
    main()