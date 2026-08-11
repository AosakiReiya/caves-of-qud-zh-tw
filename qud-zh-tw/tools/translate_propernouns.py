#!/usr/bin/env python3
"""
translate_propernouns.py — 專有名詞音譯工具（「中文(English)」格式）。

背景：遊戲裡專名（陣營名、地名、人名、蘇丹名）有些是靜態（Factions.xml）、
有些是執行期程序化生成。漏譯時會看到英文專名（如 Uppar、Na由lil）。
本工具把這些專名音譯成「中文(English)」格式（沿用 ProperNounZh 的
穆拉普爾(Murapur)、雷舍夫(Resheph) 模式），產出可加進 TextCleanerHook.cs
ProperNounZh 字典的條目。

來源（候選專名）：
  1. base Factions.xml 的 faction Name（靜態陣營名）
  2. --log <replacer_log.txt>：執行期 LEAK/UNTRANSLATED 行裡的大寫英文詞
  3. --words w1,w2,...：手動指定

規則（與既有 ProperNounZh 一致）：
  - 已定名的不重翻（穆拉普爾/雷舍夫/夏瓦 等保留）
  - 輸出「中文(English)」格式
  - 音譯用自然繁中用字、同一音固定同一字

用法：
  python3 tools/translate_propernouns.py --list            # 只列出候選專名
  python3 tools/translate_propernouns.py --log <path>      # 從 runtime log 提取
  python3 tools/translate_propernouns.py --words Uppar,Naal # 手動指定
  python3 tools/translate_propernouns.py --apply           # 翻譯並產出 C# 條目
  python3 tools/translate_propernouns.py --dry-run         # 翻譯但不寫檔
"""
import argparse
import json
import re
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
try:
    from translate_batch import call_api, API_URL, MODEL
    HAVE_API = True
except Exception:
    HAVE_API = False
    API_URL = "http://localhost:1234/api/v1/chat"
    MODEL = "local"

ROOT = Path(__file__).resolve().parent
PROJ = ROOT.parent
REPL = PROJ.parent / "qud-zh-tw-replacers"
HOOK = REPL / "TextCleanerHook.cs"
FACTIONS_BASE = Path("/mnt/g/SteamLibrary/steamapps/common/Caves of Qud/CoQ_Data/StreamingAssets/Base/Factions.xml")
OUT = ROOT / "propernouns_report.json"

CJK = re.compile(r"[\u4e00-\u9fff]")
# 大寫開頭的英文專名 token（2+ 字母，可含撇號/連字號）
PROPER = re.compile(r"\b([A-Z][A-Za-z'\-]{2,})\b")

# 已定名（不重翻）——與 TextCleanerHook ProperNounZh / 資料 mod 一致
ESTABLISHED = {
    "Murapur", "Tarchewan", "Maazoppir", "Reshep", "Resheph", "Mamon", "Sheba",
    "Chavvah", "Joppa", "Kyakukya", "Ezra", "Barathrum", "Gyre", "Spindle",
    "Qud", "Bethsaida", "Agolgot", "Shug'ruith", "Rermadon", "Qas", "Qon",
    "Rermadon", "Ctesiphus", "0lam", "Bey Lah", "Oth", "Ibudnur", "Athenreach", "Doria",
}

# 常見英文詞（非專名，排除）
COMMON = set("""The A An Of In On At To For With By From You Your My His Her Its Their Them
Is Are Was Were Be And Or But Not This That These Those It He She They We I
East West North South Up Down Here There When Where What Which Who Why How
Sultan Village City Town Ruins Gate Temple Spire Hamlet Oasis Desert Mountain
Moon Sun Star Sky Sea Water Fire Salt Iron Stone Wood Glass Crystal Dream
King Queen Prince Lord Lady Knight Wizard Witch Priest Monk Warrior Hunter
""".split())

SYSTEM = (
    "You transliterate proper names from the game Caves of Qud into Traditional Chinese "
    "for Taiwan (zh-TW). These are names of factions, places, people, and sultans.\n"
    "Rules:\n"
    "1. Output ONLY in the exact format: 中文(English)  — the Chinese transliteration followed "
    "by the original English in parentheses. Example: 穆拉普爾(Murapur).\n"
    "2. Use natural, common Traditional Chinese transliteration characters. Keep consistent: "
    "the same sound always maps to the same characters.\n"
    "3. Transliterate sound, do not translate meaning.\n"
    "4. If the name already has an established Chinese form, use it. Established: "
    "Chavvah=夏瓦, Joppa=約帕, Kyakukya=恰庫恰, Ezra=以斯拉, Barathrum=巴拉楚姆, "
    "Resheph=雷舍夫, Murapur=穆拉普爾, Tarchewan=塔徹萬, Gyre=環流, Spindle=紡錘.\n"
    "5. No spaces inside the Chinese part.\n"
    "6. If it is not really a proper name, output it unchanged."
)


def load_established_from_hook():
    """從 TextCleanerHook ProperNounZh 讀已定名，避免重翻。"""
    est = set(ESTABLISHED)
    if HOOK.exists():
        s = HOOK.read_text(encoding="utf-8")
        m = re.search(r'ProperNounZh\s*=\s*new Dictionary<[^>]+>\([^)]*\)\s*\{', s)
        if m:
            start = m.end(); i, depth = start, 1
            while i < len(s) and depth > 0:
                if s[i] == '{': depth += 1
                elif s[i] == '}': depth -= 1
                i += 1
            for kv in re.finditer(r'\{\s*"((?:[^"\\]|\\.)*)"', s[start:i]):
                est.add(kv.group(1))
    return est


def from_factions():
    names = set()
    if FACTIONS_BASE.exists():
        s = FACTIONS_BASE.read_text(encoding="utf-8", errors="ignore")
        for m in re.finditer(r'<faction\b[^>]*Name="([^"]+)"', s):
            n = m.group(1)
            if re.search(r"[A-Za-z]", n) and not CJK.search(n):
                names.add(n)
    return names


def from_log(path):
    names = set()
    try:
        s = Path(path).read_text(encoding="utf-8", errors="ignore")
    except Exception:
        return names
    for line in s.splitlines():
        if "LEAK" in line or "UNTRANSLATED" in line:
            for w in PROPER.findall(line):
                names.add(w)
    return names


def filter_candidates(names, established):
    out = set()
    for n in names:
        if n in established: continue
        if n in COMMON: continue
        if CJK.search(n): continue
        if len(n) < 3: continue
        out.add(n)
    return sorted(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--log")
    ap.add_argument("--words")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--limit", type=int, default=50)
    ap.add_argument("--url", default=API_URL)
    ap.add_argument("--model", default=MODEL)
    a = ap.parse_args()

    established = load_established_from_hook()
    cands = set()
    cands |= from_factions()
    if a.log: cands |= from_log(a.log)
    if a.words: cands |= {w.strip() for w in a.words.split(",") if w.strip()}
    cands = filter_candidates(cands, established)
    cands = cands[:a.limit]

    print(f"已定名（保留不翻）: {len(established)}")
    print(f"候選專名: {len(cands)}")
    for c in cands:
        print("  ", c)

    if a.list:
        OUT.write_text(json.dumps({"candidates": cands}, ensure_ascii=False, indent=1), encoding="utf-8")
        print(f"\n候選已寫: {OUT}")
        return

    if not (a.apply or a.dry_run):
        print("\n（加 --apply 或 --dry-run 進行翻譯；--list 只列出）")
        return

    if not HAVE_API:
        print("無法 import call_api（translate_batch）；只能列出候選。")
        return

    results = {}
    for i, c in enumerate(cands):
        try:
            raw = call_api(a.url, a.model, SYSTEM, c, 0.2, 120).strip()
            # 取第一行、去掉多餘
            line = raw.splitlines()[0].strip() if raw else c
            results[c] = line
            print(f"  [{i+1}/{len(cands)}] {c} -> {line}")
            time.sleep(0.1)
        except Exception as e:
            print(f"  [{i+1}] {c} ERROR {e}")
            results[c] = c

    # 產出 C# ProperNounZh 條目
    print("\n=== 可加入 TextCleanerHook.cs ProperNounZh 的條目 ===")
    csharp = []
    for eng, zh in results.items():
        m = re.match(r'^(.+?)\((.+?)\)\s*$', zh)
        if m:
            csharp.append(f'            {{ "{eng}", "{zh}" }},')
        else:
            csharp.append(f'            {{ "{eng}", "{zh}({eng})" }},')
    print("\n".join(csharp))

    OUT.write_text(json.dumps(results, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n結果已寫: {OUT}")
    if a.dry_run:
        print("（dry-run：未寫入 C#）")


if __name__ == "__main__":
    main()
