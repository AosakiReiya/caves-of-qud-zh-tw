#!/usr/bin/env python3
"""
audit_description_parens.py — 審核藍圖 Description 中「（English）」括號是否為誤譯。

背景：zh-tw 藍圖描述常用「中文(English)」格式標註專名（正確慣例），
但也可能誤譯（如 hyena tribeskin→「鬣狗部落皮膚」，應為「鬣狗同族」）。
本工具列出全部（English）括號，用本地 LLM 判定 OK（專名慣例）或 FIX（誤譯）。

用法：
  python3 tools/audit_description_parens.py            # 列出全部 + LLM 判定
  python3 tools/audit_description_parens.py --no-llm   # 只列出（不呼叫 LLM）
"""
import argparse
import glob
import re
import sys
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]
ZH = PROJECT / "zh-tw"

try:
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from translate_batch import call_api, API_URL, MODEL
    HAVE_API = True
except Exception:
    HAVE_API = False

PAREN = re.compile(r"（([A-Za-z][A-Za-z \-]{2,30})）")

SYSTEM = (
    "你是 Caves of Qud 繁中翻譯審核員。判斷句中的（English）括號是否為「專名慣例」"
    "（專有名詞音譯後附原文，正確格式）還是「誤譯」（一般詞語意錯誤）。\n"
    "只回覆: OK 或 FIX（附正確中文）。"
)


def scan() -> list[tuple[str, str, str]]:
    items = []
    for f in sorted(ZH.glob("*.xml")):
        s = f.read_text(encoding="utf-8-sig")
        for m in re.finditer(r'<part Name="Description" Short="([^"]*)"', s):
            d = m.group(1)
            for pm in PAREN.finditer(d):
                ctx = d[max(0, pm.start() - 30):pm.end() + 20]
                items.append((f.name, pm.group(1), ctx))
    return items


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--no-llm", action="store_true")
    a = ap.parse_args()
    items = scan()
    print(f"「（English）」括號總數: {len(items)}")
    if a.no_llm or not HAVE_API:
        for f, en, ctx in items:
            print(f"  [{f}] ({en}) {ctx[:60]}")
        return
    bad = []
    for f, en, ctx in items:
        try:
            r = call_api(API_URL, MODEL, SYSTEM, f"句子：{ctx}", 0.1, 40)
            if r and ("FIX" in r.upper()[:20]):
                bad.append((f, en, ctx, r.strip()[:80]))
        except Exception:
            pass
    print(f"\n疑似誤譯: {len(bad)}")
    for f, en, ctx, r in bad:
        print(f"  [{f}] ({en})")
        print(f"    句: {ctx[:70]}")
        print(f"    LLM: {r}")
        print()


if __name__ == "__main__":
    main()