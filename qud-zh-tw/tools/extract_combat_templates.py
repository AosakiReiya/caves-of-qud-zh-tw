#!/usr/bin/env python3
"""
extract_combat_templates.py — 從遊戲 DLL 自動提取「戰鬥/動作訊息模板」，對照現有 pattern 覆蓋，
輸出未覆蓋清單（工具閉環：提取 → 覆蓋比對 → LLM 生成 → 測試樣本）。

來源：Assembly-CSharp.dll #Strings heap 中
  ① 含 {N} 數字佔位（string.Format 樣板）且含戰鬥動詞
  ② 已知動詞（hit/miss/dies/takes/penetrate/wade/engulfed...）
對照：HarmonyPatches.cs 的 Patterns + TextCleanerHook.cs 的 TranslateStatusFragments 正則
      （以「動詞關鍵詞」比對是否已被處理）。
輸出：tools/combat_template_gaps.json — 未覆蓋樣板清單（供 LLM 生成 pattern）。
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REPL = ROOT.parent.parent / "qud-zh-tw-replacers"

GAME = ROOT.parent
for _ in range(4):
    if (GAME / "CoQ_Data").exists():
        break
    GAME = GAME.parent
DLL = GAME / "CoQ_Data" / "Managed" / "Assembly-CSharp.dll"

VERBS = re.compile(
    r"\b(hit|hits|miss(?:ed|es)?|die|dies|died|death|takes?|penetrat\w*|wade\w*|"
    r"engulf\w*|sits?\s+down|stand\w*\s+up|dazed|toggle\w*|attack\w*|strike\w*|"
    r"damage\s+from|damage\s+with)\b", re.I)
FORMAT = re.compile(r"\{[\d,:a-zA-Z ]+\}")


def scan_strings(data):
    import dnfile
    pe = dnfile.dnPE(str(DLL))
    sh = pe.net.strings
    secs = [(int(s.VirtualAddress), int(s.VirtualAddress) + int(s.Misc_VirtualSize), int(s.PointerToRawData))
            for s in pe.sections]
    def rva2off(r):
        for a, b, raw in secs:
            if a <= r < b:
                return raw + (r - a)
        return None
    off = rva2off(sh.rva)
    if off is None:
        return []
    raw = data[off:off + 24_000_000]
    out = []
    i = 0
    while i < len(raw) - 3:
        b = raw[i]
        if b & 0x80 == 0:
            ln, hdr = b, 1
        elif b & 0xC0 == 0x80:
            ln, hdr = ((b & 0x3F) << 8) | raw[i + 1], 2
        else:
            i += 1
            continue
        if ln <= 0 or ln > 400 or i + hdr + ln + 1 > len(raw):
            i += 1
            continue
        chunk = raw[i + hdr:i + hdr + ln]
        try:
            s = chunk.decode("utf-8")
        except Exception:
            i += hdr + ln + 1
            continue
        if len(s) > 8 and (FORMAT.search(s) or VERBS.search(s)) and not s.startswith("="):
            out.append(s)
        i += hdr + ln + 1
    return out


def existing_verb_coverage():
    """從現有 pattern 源提取「已被處理的動詞/短語」。"""
    covered = set()
    for cs in ("HarmonyPatches.cs", "TextCleanerHook.cs"):
        s = (REPL / cs).read_text(encoding="utf-8")
        for m in re.finditer(r'@"((?:""|[^"])*)"', s):
            pat = m.group(1).replace('""', '"')
            vm = re.findall(r"(?i)\b(hit|miss(?:ed|es)?|die|dies|died|takes?|penetrat\w*|wade\w*|engulf\w*|dazed|toggle\w*|damage)", pat)
            covered.update(vm)
    return covered


def main():
    data = DLL.read_bytes()
    print(f"DLL #Strings 掃描… ({(len(data)//1024)//1024} MB)")
    strings = scan_strings(data)
    fmt = [s for s in strings if FORMAT.search(s) and len(s) < 200]
    combat = [s for s in fmt if VERBS.search(s)]
    covered = existing_verb_coverage()
    print(f"含 {{N}} 樣板: {len(fmt)} | 戰鬥樣板: {len(combat)}")
    gaps = []
    for s in combat:
        # 樣板去佔位 → 檢查是否已有動詞層處理（以第一動詞為 key）
        vfirst = None
        m = VERBS.search(s)
        if m:
            vfirst = m.group(1).lower()
        if vfirst and vfirst not in covered:
            gaps.append(s)
    out = ROOT / "combat_template_gaps.json"
    out.write_text(json.dumps(gaps, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"未覆蓋模板: {len(gaps)} → {out}")
    for g in gaps[:40]:
        print("  ", g[:110])


if __name__ == "__main__":
    main()