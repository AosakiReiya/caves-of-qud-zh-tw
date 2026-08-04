#!/usr/bin/env python3
"""
fix_english.py — 翻譯殘留的英文句子（HTML 能力描述 + 對話）。

只翻譯「完整的英文句子」，跳過：
  - 製作人員頁（人名/公司名保留）
  - 程式註解
  - 只有專名的片段

用法：
  python3 tools/fix_english.py
"""
import glob
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from translate_batch import translate_single, API_URL, MODEL

PROJECT = Path(__file__).resolve().parents[1]

# 英文句子：大寫開頭 + 2+ 個小寫詞
SENT = re.compile(r"(?<![A-Za-z])([A-Z][a-z]+(?:\s+[a-z][A-Za-z']*){2,}[.!?]?)(?![A-Za-z])")

# 跳過（不翻譯）：製作人員/人名/公司/註解 的行
SKIP_PATTERNS = [
    "製作人員", "EndCredits", "Brian Bucklew", "Jason Grinblat", "Nick Decapua",
    "Corey Frang", "Craig Hamilton", "Autumn McDonell", "Bastia Rosen",
    "Caelyn Sandel", "Brandon Tanner", "Samuel Wilson", "Polat Yarisci",
    "A Shell in the Pit", "Thaumatic Systems", "case statement should match",
]


def find_english_in_text(text: str) -> list[tuple[int, int, str]]:
    """在文字段內找英文句子 (start, end, sentence)。"""
    out = []
    for m in SENT.finditer(text):
        s = m.group(1)
        if any(p in s for p in SKIP_PATTERNS):
            continue
        out.append((m.start(), m.end(), s))
    return out


def process_text_segment(seg: str, translate: bool) -> str:
    """翻譯含英文句子的文字段。

    - 短段（<250 字）：整段翻譯（模型保留既有中文）
    - 長段（對話）：只翻譯英文句子片段，避免改動已翻好的中文
    """
    matches = find_english_in_text(seg)
    if not matches:
        return seg
    if not translate:
        return seg
    if len(seg) < 250:
        trans = translate_single(seg.strip(), API_URL, MODEL, 0.2, 180, 2)
        if trans and trans != seg.strip() and re.search(r"[\u4e00-\u9fff]", trans):
            return trans
        print(f"  [未翻] {seg.strip()[:60]!r}")
        return seg
    # 長段：只換英文句段
    for start, end, sentence in sorted(matches, key=lambda m: -m[0]):
        trans = translate_single(sentence, API_URL, MODEL, 0.2, 180, 2)
        if trans and re.search(r"[\u4e00-\u9fff]", trans):
            seg = seg[:start] + trans + seg[end:]
    return seg


def main() -> None:
    dry = "--dry-run" in sys.argv
    total_found = 0
    for f in sorted(glob.glob(str(PROJECT / "zh-tw" / "*.xml"))):
        if "Naming" in f:
            continue
        text = open(f, encoding="utf-8-sig").read()
        # 剝除註解
        text = re.sub(r"<!--.*?-->", "", text, flags=re.S)
        parts = re.split(r"(<[^>]*>)", text)
        changed = False
        file_found = 0
        for i, part in enumerate(parts):
            if part.startswith("<"):
                continue
            if not re.search(r"[A-Za-z]{4,}", part):
                continue
            if any(p in part for p in SKIP_PATTERNS):
                continue
            matches = find_english_in_text(part)
            if matches:
                file_found += len(matches)
                total_found += len(matches)
                if dry:
                    for _, _, s in matches:
                        print(f"  [{Path(f).name}] {s[:70]!r}")
                else:
                    new_part = process_text_segment(part, True)
                    if new_part != part:
                        parts[i] = new_part
                        changed = True
        if not dry and changed:
            open(f, "w", encoding="utf-8").write("\ufeff" + "".join(parts))
            print(f"{Path(f).name}: 已修 {file_found} 句")
    print(f"共找到 {total_found} 個英文句子。" + ("（dry-run）" if dry else ""))


if __name__ == "__main__":
    main()
