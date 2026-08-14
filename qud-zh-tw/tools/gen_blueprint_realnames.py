#!/usr/bin/env python3
# 藍圖真名對照表生成器（2026-08-14）
# 從遊戲 ObjectBlueprints 掃描，產生 {藍圖Name -> 遊戲英文真名} 對照表：
#   1) Render DisplayName（英文原始，去 {{…}} markup）→ 真名
#   2) 無 DisplayName → CamelCase 空格分割（HalfFullWaterskin → Half-full Waterskin）
#   3) 首字母大寫（真名小寫如 "odd trinket" → "Odd Trinket"，維持「中文(英文)」慣例）
# 輸出：tools/blueprint_realnames.json（key=藍圖Name，value=真名）
import json, re, pathlib, sys

ROOT = pathlib.Path(__file__).resolve().parent
GAME = pathlib.Path("CoQ_Data/StreamingAssets/Base/ObjectBlueprints")
if not GAME.exists():
    GAME = pathlib.Path("/mnt/g/SteamLibrary/steamapps/common/Caves of Qud") / GAME

def camel_split(name: str) -> str:
    s = re.sub(r"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ", name)
    return s.strip()

def strip_markup(v: str) -> str:
    v = re.sub(r"\{\{[^}]*\|", "", v)   # {{B|folded carbide}} → folded carbide}}
    v = re.sub(r"\}\}", "", v)
    v = re.sub(r"&[A-Za-z]", "", v)     # 色彩碼 &B &Y
    return v.strip()

def title_word(s: str) -> str:
    # Title Case（每個詞首字母大寫；已知小寫專名保留）：odd trinket → Odd Trinket
    lower_keep = {"of", "the", "and", "on", "in", "at", "with", "for", "an", "a"}
    parts = s.split()
    out = []
    for i, w in enumerate(parts):
        if w.lower() in lower_keep and i != 0:
            out.append(w.lower())
        else:
            out.append(w[0].upper() + w[1:])
    return " ".join(out)

def main():
    table = {}
    for f in sorted(GAME.glob("*.xml")):
        try:
            src = f.read_text(encoding="utf-8-sig", errors="ignore")
        except Exception:
            continue
        for m in re.finditer(r'<object\s+Name="([^"]+)"(.*?)</object>', src, re.S):
            name = m.group(1)
            body = m.group(2)
            dn = re.search(r'DisplayName="([^"]*)"', body)
            if dn:
                real = strip_markup(dn.group(1))
                real = real.strip()
                if real and real.lower() != name.lower():
                    table[name] = title_word(real)
                    continue
            # 無 DisplayName 或同義 → Camel 分割（HalfFull→Half-full 特例；Tutorial 去前綴）
            real = name
            for k, v in {"HalfFull": "Half-full", "HalfEmpty": "Half-empty"}.items():
                real = real.replace(k, v)
            real = camel_split(real)
            if name.startswith("Tutorial"):
                real = real.replace("Tutorial ", "", 1)
            table[name] = title_word(real)
    out = ROOT / "blueprint_realnames.json"
    out.write_text(json.dumps(table, ensure_ascii=False, indent=1, sort_keys=True), encoding="utf-8")
    print(f"藍圖總數: {len(table)} -> {out.name}")
    for k in ["UnknownOddTrinket", "HalfFullWaterskin", "EmptyWaterskin", "EmptyCanteen",
              "TutorialTorch", "Light Torch", "Albino ape", "Bear Jerky"]:
        print(f"  {k} => {table.get(k, '(缺)')}")
    return 0

if __name__ == "__main__":
    sys.exit(main())