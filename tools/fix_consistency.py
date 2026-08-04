#!/usr/bin/env python3
"""
fix_consistency.py — 修復專名一致性。

功能：
  1. 變體拼法統一（如 喬帕 → 約帕）
  2. 專名補英文：策展術語表中「中文(原文)」格式的條目，
     把純中文形式補上 (原文)（避免複合詞、避免重複附註）

用法：
  python3 tools/fix_consistency.py
"""
import glob
import json
import re
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]
CURATED = PROJECT / "tools" / "glossary_curated.json"

# 變體 → 統一譯名（依長度由長到短排列；短詞會是長詞的子串時，長的放前面）
FIXES = [
    ("巴拉屈姆", "巴拉楚姆"),
    ("喬帕村民", "約帕村民"),
    ("卡庫卡村民", "恰庫恰村民"),
    ("喬帕", "約帕(Joppa)"),
    ("約巴", "約帕(Joppa)"),
    ("卡庫卡", "恰庫恰(Kyakukya)"),
    ("普特", "普托(Ptoh)"),
    ("羅馬德", "駝獸"),
]


def load_curated() -> dict[str, str]:
    data = json.loads(CURATED.read_text(encoding="utf-8"))
    return {k: v for k, v in data.items() if not k.startswith("_")}


def append_english(text: str, curated: dict[str, str]) -> tuple[str, int]:
    """把純中文專名補上 (原文)，回傳 (text, 替換數)。"""
    count = 0
    # 依 base 長度由長到短，避免短詞先被處理
    entries = []
    for en, zh in curated.items():
        if not zh or "(" not in zh:
            continue
        base = zh.split("(", 1)[0]
        entries.append((base, en, zh))
    entries.sort(key=lambda e: -len(e[0]))
    for base, en, zh in entries:
        # 純中文形式：後不接中文（避免複合詞）、前不接 (（避免已附註）
        pat = re.compile(
            r"(?<![\u4e00-\u9fff(])" + re.escape(base) + r"(?![\u4e00-\u9fff()])"
        )
        new, n = pat.subn(zh, text)
        count += n
        text = new
    return text, count


def main() -> None:
    curated = load_curated()
    total = 0
    files = 0
    for f in sorted(glob.glob(str(PROJECT / "zh-tw" / "*.xml"))):
        if "Naming" in f:
            continue
        text = open(f, encoding="utf-8-sig").read()
        orig = text
        for variant, canonical in FIXES:
            text = text.replace(variant, canonical)
        text, n = append_english(text, curated)
        total += n
        if text != orig:
            open(f, "w", encoding="utf-8").write("\ufeff" + text)
            files += 1
    print(f"修復完成：{files} 檔、{total} 處補英文。")


if __name__ == "__main__":
    main()
