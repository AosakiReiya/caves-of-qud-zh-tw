#!/usr/bin/env python3
"""
fix_consistency.py — 修復專名一致性。

功能：
  1. 變體拼法統一（如 喬帕 → 約帕）
  2. 殘留英文專名 → 中文(原文)（修漏翻譯，英文整詞代換）
  3. 專名補英文：策展術語表（併入 glossary_proposed.json 已確認名）中
     「中文(原文)」格式的條目，把純中文形式補上 (原文)（全數出現處）

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
    # 併入 glossary_proposed.json 中已確認（zh 非空）的專名，擴大「附 (English)」範圍
    try:
        proposed = json.loads(PROJECT.joinpath("tools", "glossary_proposed.json").read_text(encoding="utf-8"))
        for k, v in proposed.items():
            if k.startswith("_"):
                continue
            if isinstance(v, dict) and v.get("zh"):
                data[k] = v["zh"]
    except Exception:
        pass
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


def replace_english(text: str, curated: dict[str, str]) -> tuple[str, int]:
    """把殘留的英文專名（已確認譯名）整詞代換成「中文(原文)」，修漏翻譯。

    安全防護：
      - 只處理「中文(原文)」格式的條目
      - 英文 key 以整詞邊界匹配，且已在「(原文)」括號內的英文不換（防雙重附註）
      - 若 key 是某個更長英文專名的組成詞，跳過（由長 key 處理，防誤傷，
        如 Qud 在 Caves of Qud 內）
    """
    paren_entries = {en: zh for en, zh in curated.items() if en and zh and "(" in zh}
    # 檢查 en 是否為任一更長 key 的組成詞
    all_keys = [k for k in curated if k]
    subsumed = set()
    for en in paren_entries:
        for long in all_keys:
            if len(long) > len(en) and re.search(
                r"(?<![A-Za-z])" + re.escape(en) + r"(?![A-Za-z])", long
            ):
                subsumed.add(en)
                break
    count = 0
    for en, zh in sorted(paren_entries.items(), key=lambda e: -len(e[0])):
        if en in subsumed:
            continue
        # 前後都不接英文字母，且不緊接在 ( 後（已在括號內則跳過）
        pat = re.compile(r"(?<![A-Za-z(])" + re.escape(en) + r"(?![A-Za-z])")
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
        # 1) 殘留英文專名 → 中文(原文)（修漏翻譯）
        text, n = replace_english(text, curated)
        total += n
        # 2) 純中文專名 → 補上 (原文)（全數出現處一致性）
        text, n = append_english(text, curated)
        total += n
        if text != orig:
            open(f, "w", encoding="utf-8").write("\ufeff" + text)
            files += 1
    print(f"修復完成：{files} 檔、{total} 處統一（含補 (原文) 與英文殘留代換）。")


if __name__ == "__main__":
    main()
