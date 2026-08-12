#!/usr/bin/env python3
"""
fix_residual_english.py — 掃描並修復「中文語境夾雜的殘留英文詞」。

只處理執行期會顯示給玩家的值（<string> 的文本或 Value 屬性），並且：
  - 去掉 =token=、~cmd、@tag、{{markup}}、&entity;、數字、（）括號
  - 只保留「前或後緊鄰中文」的英文詞（真正的夾雜殘留）
  - 排除已知專名（中文(English) 括號格式、glossary、proper_nouns.txt、黑名單）

用法：
  python3 tools/fix_residual_english.py --scan          # 列出殘留（預設）
  python3 tools/fix_residual_english.py --apply         # 套用 word_map 替換
  python3 tools/fix_residual_english.py --apply --dry   # 乾跑（不寫檔）
"""
import argparse
import json
import re
import sys
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]
TOOLS = Path(__file__).resolve().parent

CN = re.compile(r"[\u4e00-\u9fff]")
WORD = re.compile(r"(?<![A-Za-z])[A-Za-z][a-zA-Z]+")

# 已知專名 / 應保留的英文（glossary + proper_nouns + 額外黑名單）
KNOWN = set()
for f in ("glossary_curated.json", "naming_translation.json"):
    try:
        d = json.load(open(TOOLS / f))
        KNOWN |= {str(k).lower() for k in d}
    except Exception:
        pass
for line in (TOOLS / "proper_nouns.txt").read_text(encoding="utf-8").splitlines():
    w = line.strip().lower()
    if w:
        KNOWN.add(w)
BLACKLIST = {  # 專名/保留詞，勿翻
    "arrr", "atatat", "bep", "bloop", "blorp", "bodi", "chavvah", "dien",
    "dweller", "eir", "eirs", "emself", "esk", "faundren", "geeub", "gjaus",
    "gnaa", "goek", "graah", "grre", "gyamyo", "hamsa", "hine", "hoh",
    "kendren", "keter", "ey", "eyes", "many", "lah", "meyvn", "mind",
    "minyan", "moloch", "musa", "nacham", "nephilim", "nou", "rrk",
    "rrkself", "saad", "salum", "shem", "shomer", "shomrim", "six",
    "slynth", "soft", "spindle", "stilt", "svy", "tau", "taulike",
    "taproot", "tilli", "thpthp", "tory", "urshiib", "waydroid",
    "waydroids", "xem", "xym", "xyr", "yma", "yurl", "ziv", "gram",
    "paces", "goblin", "dromad", "hindren", "kisu", "sheba", "keh",
    "gyl", "non", "q", "qud", "joppa", "ezra", "bey", "eskhind",
    "kesehind", "mopango", "omonporch", "putus", "templar", "resheph",
    "reshephs", "grand", "doe", "klanq", "pax", "girl", "ortho", "othom",
    "spara", "prickles", "primrose", "witchwood", "skipper",
    "galgal", "freehold", "many_e", "yd", "goek", "mak", "geyub",
}
# 殘留詞 → 中文（只放無歧義、確定要翻的詞）
WORD_MAP = {
    "parasangs": "帕拉桑",
    "parasang": "帕拉桑",
    "glotrot": "舌腐症",
    "ironshank": "鐵腳病",
    "funglefection": "真菌感染",
    "drams": "德蘭",
    "dram": "德蘭",
    "drame": "德蘭",
    "office": "辦公室",
    "steward": "管事",
    "incoming": "襲來的",
    "shock": "震擊",
    "strixes": "鴞梟",
    "rukhs": "巨梟",
    "shaken": "顫抖的",
    "baetyl": "聖碑",
    "digitum": "數位寶庫",
    "fracti": "裂縫",
    "isolationism": "孤立主義",
    "metazoon": "後生生物",
    "plagues": "瘟疫",
    "wielding": "揮舞著",
    "health": "生命值",
    "snarcomfagus": "斯納科穆法格斯",
    "atzmus": "阿特茲穆斯",
    "elseing": "轉生儀式",
    "gyredream": "環流之夢",
    "hindriarchy": "欣德里亞克政權",
    "starshiib": "星之熊族",
    "ptychoscan": "心靈掃描",
    "varrrdym": "瓦爾德姆",
    "quetzal": "羽蛇",
    "teraphim": "特辣菲姆",
    "dreen": "德林",
    "saads": "薩德們",
    "then": "然後",
    "two": "二",
    "with": "與",
}


def clear(v: str) -> str:
    v = re.sub(r"=[^=]*=", " ", v)
    v = re.sub(r"~[A-Za-z0-9_]+", " ", v)
    v = re.sub(r"@[A-Za-z0-9_.|]+", " ", v)
    v = re.sub(r"\{\{[^}]*\}\}", " ", v)
    v = re.sub(r"（[^）]*）", " ", v)
    v = re.sub(r"\([^)]*\)", " ", v)
    v = re.sub(r"&[a-zA-Z#0-9]+;", " ", v)
    v = re.sub(r"[0-9%]+", " ", v)
    return v


def is_residual(word: str) -> bool:
    w = word.lower()
    if len(w) < 3:
        return False
    if w in KNOWN or w in BLACKLIST:
        return False
    return True


def scan() -> dict:
    hits = {}
    for f in sorted(PROJECT.glob("zh-tw/*.xml")):
        if "Naming" in f.name:
            continue
        s = f.read_text(encoding="utf-8-sig")
        for m in re.finditer(r'<string\b[^>]*>([^<]*)</string>|<string\b[^>]*\bValue="([^"]*)"', s):
            val = m.group(1) or m.group(2) or ""
            if not CN.search(val):
                continue
            v = clear(val)
            for w in WORD.finditer(v):
                lo, hi = w.span()
                # 前或後 2 字元內有中文 → 夾雜殘留
                ctx = v[max(0, lo - 2):hi + 2]
                if not CN.search(ctx):
                    continue
                ww = w.group(0)
                if not is_residual(ww):
                    continue
                hits.setdefault(ww, []).append((f.name, val[:120]))
    return hits


def apply(dry: bool = False) -> tuple[int, int]:
    """只替換「中文語境中的殘留英文詞」，token/括號內部不動。"""
    files_changed = 0
    total_fixed = 0
    for f in sorted(PROJECT.glob("zh-tw/*.xml")):
        if "Naming" in f.name:
            continue
        s = f.read_text(encoding="utf-8-sig")
        changed = False
        parts = re.split(r"(<[^>]*>)", s)
        PAT = re.compile(r"=[^=]*=|\{\{[^}]*\}\}|（[^）]*）|\([^)]*\)|&[a-zA-Z#0-9]+;|[0-9%]+")
        for i, part in enumerate(parts):
            if part.startswith("<"):
                continue
            # 用佔位符屏蔽需保護的內容
            protected = []
            masked = PAT.sub(lambda m: _protect(m.group(0), protected), part)
            new_part = masked
            for en, zh in sorted(WORD_MAP.items(), key=lambda kv: -len(kv[0])):
                new_part = re.sub(rf"(?<![A-Za-z]){en}(?![A-Za-z])", zh, new_part)
            if new_part != masked:
                # 還原佔位符
                def _restore(m):
                    return protected[int(m.group(1))]
                new_part = re.sub(r"\x00(\d+)\x00", _restore, new_part)
                parts[i] = new_part
                changed = True
        if changed:
            files_changed += 1
            if not dry:
                open(f, "w", encoding="utf-8").write("\ufeff" + "".join(parts))
            print(f"{'[dry]' if dry else '[ok]'} {f.name}")
    return files_changed, total_fixed


def _protect(tok: str, box: list) -> str:
    box.append(tok)
    return f"\x00{len(box) - 1}\x00"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--scan", action="store_true")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--dry", action="store_true")
    a = ap.parse_args()
    if a.apply:
        n = apply(a.dry)
        print(f"處理 {n} 個檔案（{'乾跑' if a.dry else '已寫入'}）")
        return
    hits = scan()
    print(f"殘留英文詞類型: {len(hits)}")
    for w, occ in sorted(hits.items()):
        print(f"  {w} ×{len(occ)}: {occ[0][1][:60]!r}")


if __name__ == "__main__":
    main()