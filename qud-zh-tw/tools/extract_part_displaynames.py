#!/usr/bin/env python3
"""
extract_part_displaynames.py — 提取所有物件藍圖 part 的 DisplayName（Render/HiddenRender 等），
找出 zh-tw 未覆蓋的漏譯名。

背景：crack 漏譯根因 = ZoneTerrain PondDown 的 HiddenRender part DisplayName="stone crack"
未被 zh-tw 覆蓋（只覆蓋了 Render）。此工具掃描所有 <part Name="X" DisplayName="..."> 的
DisplayName，對照 zh-tw 對應物件的同名 part，列出漏譯。

用法：
  python3 tools/extract_part_displaynames.py            # 列出漏譯
  python3 tools/extract_part_displaynames.py --json     # 輸出 JSON
"""
import argparse
import glob
import json
import re
import sys
from pathlib import Path

GAME = Path("/mnt/g/SteamLibrary/steamapps/common/Caves of Qud")
BASE = GAME / "CoQ_Data/StreamingAssets/Base/ObjectBlueprints"
PROJECT = Path(__file__).resolve().parents[1]
ZH = PROJECT / "zh-tw"

CN = re.compile(r"[\u4e00-\u9fff]")
# 所有 part 類型（不只 Render/HiddenRender）
PART_DN = re.compile(r'<part Name="([^"]+)"[^>]*\bDisplayName="([^"]*)"')


def parse_blueprints(root: Path) -> dict[str, dict[str, list[tuple[str, str]]]]:
    """返回 {檔案: {物件名: [(part名, DisplayName), ...]}}"""
    out = {}
    for f in sorted(root.glob("*.xml")):
        s = f.read_text(encoding="utf-8", errors="ignore")
        objs = {}
        for m in re.finditer(r'<object Name="([^"]+)".*?</object>', s, re.S):
            obj, seg = m.group(1), m.group(0)
            parts = [(p, dn) for p, dn in PART_DN.findall(seg)]
            if parts:
                objs[obj] = parts
        if objs:
            out[f.name] = objs
    return out


def scan() -> list[dict]:
    base = parse_blueprints(BASE)
    zh = parse_blueprints(ZH)
    # zh 檔名 → base 檔名（去掉 .zh-tw 後綴）
    zh_by_base = {}
    for fname, objs in zh.items():
        base_name = fname.replace(".zh-tw.xml", ".xml")
        zh_by_base[base_name] = objs
    missing = []
    for fname, objs in base.items():
        zf = zh_by_base.get(fname, {})
        for obj, parts in objs.items():
            zparts = dict(zf.get(obj, []))
            for part, dn in parts:
                if not dn or CN.search(dn):
                    continue
                zdn = zparts.get(part)
                if zdn is None or CN.search(zdn) is False:
                    # zh 未覆蓋此 part 或覆蓋值仍英文
                    missing.append({
                        "file": fname, "object": obj, "part": part,
                        "base_dn": dn, "zh_dn": zdn,
                    })
    return missing


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()
    missing = scan()
    if a.json:
        json.dump(missing, open(PROJECT / "tools" / "part_displaynames_missing.json", "w"),
                  ensure_ascii=False, indent=1)
        print(f"寫入 part_displaynames_missing.json（{len(missing)} 條）")
        return
    print(f"漏譯 part DisplayName: {len(missing)} 條")
    for x in missing:
        flag = "  <-- zh 有但仍英文" if x["zh_dn"] else "  <-- zh 完全沒覆蓋此 part"
        print(f"  [{x['file']}] {x['object']} / {x['part']}: {x['base_dn']!r}{flag}")


if __name__ == "__main__":
    main()