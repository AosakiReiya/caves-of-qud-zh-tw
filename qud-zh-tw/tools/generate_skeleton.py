#!/usr/bin/env python3
"""
generate_skeleton.py — 從官方 ExampleLanguage 產生 zh-tw 翻譯骨架。

來源：
  1. 遊戲資料夾的 ExampleLanguage（優先，最權威）
  2. 或 _dev/example-language（clone）

輸出：
  zh-tw/<name>.zh-tw.xml

作法：
  - 將 Lang="example" 改為 Lang="zh-tw"
  - 保留官方 ▶ 標記（代表「尚未翻譯」）
  - 翻譯者直接在骨架內把 ▶xxx 換成正體中文即完成

用法：
  python3 tools/generate_skeleton.py                 # 使用遊戲內 ExampleLanguage
  python3 tools/generate_skeleton.py --source <path> # 指定來源
  python3 tools/generate_skeleton.py --out <dir>     # 指定輸出
"""
import argparse
import re
import sys
from pathlib import Path

GAME_EXAMPLE = Path(__file__).resolve().parents[3] / "CoQ_Data" / "StreamingAssets" / "Base" / "ExampleLanguage"

DEFAULT_OUT = Path(__file__).resolve().parents[1] / "zh-tw"


def generate(source: Path, out: Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    files = sorted(source.glob("*.example.xml"))
    if not files:
        sys.exit(f"未在 {source} 找到 .example.xml 檔案")
    for src in files:
        text = src.read_text(encoding="utf-8-sig")
        text = re.sub(r'Lang="[^"]*"', 'Lang="zh-tw"', text, count=1)
        dest = out / (src.stem.replace(".example", "") + ".zh-tw.xml")
        dest.write_text("\ufeff" + text, encoding="utf-8")
        count = text.count("▶")
        print(f"  {dest.name:<40s} 未翻譯標記: {count}")
    print(f"\n完成：{len(files)} 個骨架檔已產生至 {out}")
    print("翻譯方式：開啟 zh-tw/*.xml，把 ▶前綴的英文替換成正體中文（記得刪掉 ▶）")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", default=str(GAME_EXAMPLE))
    ap.add_argument("--out", default=str(DEFAULT_OUT))
    args = ap.parse_args()
    generate(Path(args.source), Path(args.out))


if __name__ == "__main__":
    main()
