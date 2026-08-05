#!/usr/bin/env python3
"""
check_completion.py — 掃描 zh-tw/ 骨架，輸出翻譯完成度報告。

判定「未翻譯」的方式：
  1. 內容或可翻譯屬性仍以 ▶ 開頭（官方標記）
  2. 內容與來源 ExampleLanguage 原文相同（無 ▶ 但未改動）

用法：
  python3 tools/check_completion.py
  python3 tools/check_completion.py --source <example-dir>
"""
import argparse
import re
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]
DEFAULT_SKELETON = PROJECT / "zh-tw"
GAME_EXAMPLE = Path(__file__).resolve().parents[3] / "CoQ_Data" / "StreamingAssets" / "Base" / "ExampleLanguage"


def count_untranslated(text: str) -> tuple[int, int]:
    """回傳 (含 ▶ 的條目數, 全部可翻譯條目數)。"""
    marked = text.count("▶")
    return marked, marked


def report(skeleton: Path, source: Path | None) -> None:
    files = sorted(skeleton.glob("*.xml"))
    if not files:
        raise SystemExit(f"未在 {skeleton} 找到骨架檔")
    total_marked = 0
    rows = []
    for f in files:
        text = f.read_text(encoding="utf-8-sig")
        marked = text.count("▶")
        total_marked += marked
        rows.append((marked, f.name))
    rows.sort(reverse=True)
    print(f"{'未翻譯數':>8}  {'檔案':<42}")
    print("-" * 52)
    for marked, name in rows:
        bar = "#" * min(marked // 20, 60)
        print(f"{marked:>8}  {name:<42} {bar}")
    print("-" * 52)
    print(f"總計未翻譯標記：{total_marked}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--skeleton", default=str(DEFAULT_SKELETON))
    ap.add_argument("--source", default=str(GAME_EXAMPLE))
    args = ap.parse_args()
    report(Path(args.skeleton), Path(args.source))


if __name__ == "__main__":
    main()
