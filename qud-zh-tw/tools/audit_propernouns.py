#!/usr/bin/env python3
"""
audit_propernouns.py — 盤點「重要專名」的顯示名是否都有「中文(English)」括號格式。

原則：Qud 沒有自動加括號機制，「中文(English)」靠每個覆蓋值自己寫。
本工具對比遊戲原始專名表 vs mod 覆蓋，列出「中文顯示名缺 (English)」的清單，
輸出 tools/propernoun_audit.txt 供人工逐條補齊。

來源表（遊戲 Base/*.xml vs mod zh-tw/*.zh-tw.xml）：
  Factions.xml         → faction DisplayName
  Worlds.xml           → world/cell/zone Name（重要城鎮）
  ChiliadFactions.xml  → faction DisplayName
  Quests.xml           → quest Name

用法：
  python3 tools/audit_propernouns.py            # 產生清單（不修改）
  python3 tools/audit_propernouns.py --apply    # 依清單檔逐條確認後套用（未來擴展）
"""
import argparse
import re
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROJ = ROOT.parent
ZH = PROJ / "zh-tw"

GAME = ROOT.parent
for _ in range(4):
    if (GAME / "CoQ_Data").exists():
        break
    GAME = GAME.parent
BASE = GAME / "CoQ_Data" / "StreamingAssets" / "Base"

# (遊戲檔, mod 檔, 元素名, 屬性, 審查項目說明)
TABLES = [
    ("Factions.xml", "Factions.zh-tw.xml", "faction", ("Name", "DisplayName"), "陣營"),
    ("Worlds.xml", "Worlds.zh-tw.xml", "world", ("Name", "DisplayName"), "世界"),
    ("Worlds.xml", "Worlds.zh-tw.xml", "cell", ("Name", "DisplayName"), "區域"),
    ("ChiliadFactions.xml", "ChiliadFactions.zh-tw.xml", "faction", ("Name", "DisplayName"), "歷史陣營"),
    ("Quests.xml", "Quests.zh-tw.xml", "quest", ("Name", "Name"), "任務"),
]

EN_RE = re.compile(r"[A-Za-z]{2,}")
CJK_RE = re.compile(r"[\u4e00-\u9fff]")


def load_overrides(mod_path):
    """mod 覆蓋：primary key → 顯示值（元素屬性或 text）。"""
    out = {}
    if not mod_path.exists():
        return out
    root = ET.fromstring(mod_path.read_text(encoding="utf-8-sig"))
    # 直接抓所有元素屬性 + text，以「屬性名為 DisplayName 或元素是 faction/quest」標記
    for el in root.iter():
        if el.tag in ("faction", "quest", "world", "cell", "zone"):
            key = el.get("Name") or el.get("ID")
            if not key:
                continue
            disp = el.get("DisplayName")
            if disp is not None:
                out[(el.tag, key)] = disp
            elif el.text and el.text.strip() and not el.text.strip().startswith("<"):
                out[(el.tag, key)] = el.text.strip()
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep-authored", action="store_true",
                    help="保留「純中文無括號」者（如 約帕村民 類群體名）於待定清單")
    a = ap.parse_args()

    problems = []   # 有中文但無 (English)
    all_zh = []
    for gf, mf, tag, (key_attr, disp_attr), desc in TABLES:
        gpath = BASE / gf
        mpath = ZH / mf
        if not gpath.exists() or not mpath.exists():
            continue
        ov = load_overrides(mpath)
        groot = ET.fromstring(gpath.read_text(encoding="utf-8"))
        seen = set()
        for el in groot.iter(tag):
            key = el.get(key_attr)
            if not key or (tag, key) in seen:
                continue
            seen.add((tag, key))
            zh = ov.get((tag, key))
            if not zh:
                continue
            en = el.get(disp_attr) or ""
            if disp_attr == "Name" and not en:
                en = key
            if CJK_RE.search(zh) and not EN_RE.search(zh):
                # 純中文無括號 → 需確認是否也無 (English) 附加
                all_zh.append((desc, tag, key, zh, en, "純中文"))
            elif CJK_RE.search(zh):
                # 有中文且有英文（可能括號格式）→ 檢查括號
                mm = re.findall(r"[（(]([A-Za-z][^（）()]*)[）)]", zh)
                if not mm:
                    problems.append((desc, tag, key, zh, en, "中文+英文但無括號"))
                else:
                    for paren in mm:
                        if any(ch.isalpha() and ch.islower() for ch in paren):
                            continue
                        if not re.search(r"(?i)" + re.escape(paren.strip()), en) and paren.strip() not in key:
                            problems.append((desc, tag, key, zh, en, f"括號({paren}) 與原文不符"))
                            break

    out = ROOT / "propernoun_audit.txt"
    with open(out, "w", encoding="utf-8") as f:
        f.write("=== 重要專名顯示名審核清單 ===\n\n")
        f.write(f"[A] 純中文無 (English) 括號：{len(all_zh)} 條\n")
        for d_, t, k, zh, en, why in all_zh:
            f.write(f"  [{d_}] {t} {k} → {zh}  (EN: {en})\n")
        f.write(f"\n[B] 中文+英文但缺括號 / 括號不符：{len(problems)} 條\n")
        for d_, t, k, zh, en, why in problems:
            f.write(f"  [{d_}] {t} {k} → {zh}  (EN: {en}) [{why}]\n")
    print(f"已寫入 {out}: [A] {len(all_zh)} 條純中文、[B] {len(problems)} 條括號問題")
    print("\n[A] 純中文專名（節錄）:")
    for d_, t, k, zh, en, why in all_zh[:25]:
        print(f"  [{d_}] {k} → {zh}")


if __name__ == "__main__":
    main()