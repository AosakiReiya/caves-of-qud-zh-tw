#!/usr/bin/env python3
"""
remove_bad.py — 從 progress.json 移除 check_quality.py 標記的壞翻譯鍵，使其重翻。

用法：
  python3 tools/check_quality.py      # 產出 tools/bad_quality.json
  python3 tools/remove_bad.py         # 移除壞鍵
  python3 tools/translate_batch.py --exclude "Naming*.xml"   # 重翻
"""
import json
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]
BAD = PROJECT / "tools" / "bad_quality.json"
PROG = PROJECT / "tools" / "progress.json"


def main() -> None:
    if not BAD.exists():
        print("沒有 bad_quality.json，先執行 check_quality.py")
        return
    bad = json.loads(BAD.read_text(encoding="utf-8"))
    data = json.loads(PROG.read_text(encoding="utf-8"))
    removed = 0
    for k in bad:
        if k in data:
            del data[k]
            removed += 1
    json.dump(data, open(PROG, "w", encoding="utf-8"), ensure_ascii=False)
    print(f"移除 {removed} 條壞翻譯（剩 {len(data)} 條）。")


if __name__ == "__main__":
    main()
