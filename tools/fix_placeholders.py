#!/usr/bin/env python3
"""
fix_placeholders.py — 端到端修復 placeholder 異常的翻譯。

流程：
  1. 讀取 check_quality.py 產出的 bad_quality.json（bad string IDs）
  2. 重新產生骨架（乾淨英文）
  3. 對每個 bad ID，用 extract_units 找到「實際單位內容」（含尾部空白），
     以它作為 progress.json 的鍵
  4. 用「遮罩法」翻譯：先將每個 =[A-Za-z0-9_.:;|!@/()+\-#']+= 換成不可翻譯標記，
     翻譯後還原 → placeholder 100% 保留
  5. 寫回 progress.json 並套用

用法：
  python3 tools/check_quality.py          # 產生 bad_quality.json
  python3 tools/fix_placeholders.py
  python3 tools/translate_batch.py --apply
"""
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from translate_batch import (
    PROJECT,
    call_api,
    extract_units,
    API_URL,
    MODEL,
    SINGLE_SYSTEM,
)

BAD = PROJECT / "tools" / "bad_quality.json"
PROG = PROJECT / "tools" / "progress.json"
SKEL = PROJECT / "zh-tw"

PH = re.compile(r"=[A-Za-z0-9_.:;|!@/()+\-]+=")


def do_mask(text: str) -> tuple[str, list[str]]:
    parts = list(PH.findall(text))
    counter = [0]

    def repl(m: re.Match) -> str:
        j = counter[0]
        counter[0] += 1
        return f"PPPP{j}PPPP"

    masked = PH.sub(repl, text)
    return masked, parts


def unmask(masked: str, parts: list[str]) -> str:
    out = masked
    for i, p in enumerate(parts):
        out = out.replace(f"PPPP{i}PPPP", p)
    return out


def translate_with_mask(source: str, url: str, model: str, temperature: float) -> str:
    masked, parts = do_mask(source)
    prompt = SINGLE_SYSTEM + "\nTranslate the text. Tokens like PPPP0PPPP are fixed placeholders — keep them exactly."
    for _ in range(3):
        trans = call_api(url, model, prompt, masked, temperature, 180).strip()
        restored = unmask(trans, parts)
        if set(PH.findall(restored)) == set(PH.findall(source)):
            return restored
    return None


def main() -> None:
    bad = json.loads(BAD.read_text(encoding="utf-8")) if BAD.exists() else []
    print(f"待修復：{len(bad)} 條。")

    # 重產骨架
    from generate_skeleton import GAME_EXAMPLE, generate as gen
    gen(GAME_EXAMPLE, SKEL)
    print("骨架已重產。")

    # 收集「▶+ID」→ 實際單位內容
    unit_map: dict[str, str] = {}
    for f in SKEL.glob("*.xml"):
        text = f.read_text(encoding="utf-8-sig")
        for off, ln, content, kind in extract_units(text):
            if content.startswith("▶"):
                unit_map.setdefault(content, content)

    prog = json.loads(PROG.read_text(encoding="utf-8"))
    fixed = 0
    failed = 0
    for ident in bad:
        source = ident[1:] if ident.startswith("▶") else ident
        # 找實際單位內容：▶+source 開頭（含尾部空白）
        matches = [c for c in unit_map if c[1:].startswith(source)]
        if not matches:
            failed += 1
            continue
        content = matches[0]
        trans = translate_with_mask(source, API_URL, MODEL, 0.2)
        if trans is None:
            failed += 1
            print(f"  [失敗] {source[:60]!r}")
            continue
        prog[content] = trans
        fixed += 1

    json.dump(prog, open(PROG, "w", encoding="utf-8"), ensure_ascii=False)
    print(f"完成：修復 {fixed}，失敗 {failed}。現在執行 --apply 套用。")


if __name__ == "__main__":
    main()
