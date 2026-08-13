#!/usr/bin/env python3
"""
parse_player_log.py — 從遊戲 Player.log 自動提取「顯示層漏詞/漏句」，對照語料與詞表，
輸出待翻譯清單（用戶不需手動報——遊戲日誌自動供源）。

來源：遊戲 Player.log（Windows: %USERPROFILE%\\AppData\\LocalLow\\Freehold Games\\CavesOfQud\\Player.log
      Linux: ~/.config/unity3d/Freehold Games/CavesOfQud/Player.log）
提取：
  ① UNTRANSLATED / STRING_MISS / LEAK 行（mod 自己的 log）
  ② 含英文實詞（3+ 字母）與中文字的日誌行（執行期顯示漏）
對照：mod 語料（zh-tw/*.xml 值）+ Words/TmpWords/PhraseLeaks/ProperNounZh 表
輸出：tools/log_leaks.json（去重，依頻率排序）

用法：
  python3 tools/parse_player_log.py [--log /path/Player.log]
"""
import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROJ = ROOT.parent
ZH = PROJ / "zh-tw"
REPL = PROJ.parent / "qud-zh-tw-replacers"

CANDIDATES = [
    Path.home() / ".config/unity3d/Freehold Games/CavesOfQud/Player.log",
    Path.home() / ".local/share/unity3d/Freehold Games/CavesOfQud/Player.log",
    Path("/mnt/c/Users") if Path("/mnt/c").exists() else None,
]

ENG = re.compile(r"[A-Za-z]{3,}")

STOP = set(
    "the and of you are is not for with your this that from into onto about than were was has had "
    "will have its their him her them can all his she our been does did out off over under above "
    "below through while then again some those these who whom whose when where what which would "
    "could should may might must once soon very much many more most only each every both either "
    "neither other another such same own just also even still well back down up away here there "
    "now why how because before after during between among across around behind beside inside "
    "outside near next past along until via per oh ah hey ok yes no they we me my one two three "
    "ten first but not got get see log info unity player".split())


def mod_covered():
    """語料 + 詞表已覆蓋的詞/詞組（假陽性過濾）。"""
    cov = set()
    for f in ZH.glob("*.zh-tw.xml"):
        s = f.read_text(encoding="utf-8-sig")
        cov.update(re.findall(r"[A-Za-z][A-Za-z .'\\-]{2,}", s))
    for cs in ("TextCleanerHook.cs", "UiStringsHook.cs", "Replacers.cs", "HarmonyPatches.cs"):
        s = (REPL / cs).read_text(encoding="utf-8")
        cov.update(re.findall(r'\\{\\s*"([A-Za-z][A-Za-z .\'\\-]*)"', s))
    return cov


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", default=None)
    a = ap.parse_args()

    log = None
    if a.log:
        log = Path(a.log)
    else:
        for c in CANDIDATES:
            if c and c.is_file():
                log = c
                break
        if log is None and Path("/mnt/c").exists():
            for u in sorted(Path("/mnt/c/Users").glob("*")):
                cand = u / "AppData/LocalLow/Freehold Games/CavesOfQud/Player.log"
                if cand.exists():
                    log = cand
                    break
    if log is None or not log.exists():
        print("找不到 Player.log（可用 --log 指定）")
        return

    text = log.read_text(encoding="utf-8", errors="ignore")
    covered = mod_covered()
    # ① 優先：mod 殘英掃描器的 LEAK[..] 行（精確來源，含 <- 殘英清單）
    leak_lines = []
    for line in text.splitlines():
        if "LEAK[" in line and "<-" in line:
            leak_lines.append(line)
    # ② 一般中英混合行兜底
    hits = []
    for line in text.splitlines():
        if len(line) > 300:
            continue
        if not re.search(r"[\u4e00-\u9fff]", line):
            continue
        words = [w for w in ENG.findall(line) if w.lower() not in STOP]
        words = [w for w in words if w not in covered]
        if words:
            hits.append((line.strip(), words))

    def final_words(line_with_marker):
        m_ = re.search(r"<- ([A-Za-z,]+)$", line_with_marker)
        if m_:
            return [w for w in m_.group(1).split(",") if w]
        return []

    seen = {}
    for line, words in hits:
        lw = final_words(line)
        words = lw if lw else words
        key = " ".join(words)
        seen.setdefault(key, []).append(line)
    # LEAK 行併入（無 ENG 詞的也收）
    for line in leak_lines:
        lw = final_words(line)
        if not lw:
            continue
        key = " ".join(lw)
        seen.setdefault(key, []).append(line.split("]: ")[1][:150] if "]: " in line else line[:150])
    out = [{"words": k, "count": len(v), "sample": v[0][:150]} for k, v in seen.items()]
    out.sort(key=lambda x: -x["count"])
    dest = ROOT / "log_leaks.json"
    dest.write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"完成：{len(out)} 組漏詞 → {dest}")
    for o in out[:20]:
        print(f"  x{o['count']} {' '.join(o['words'][:8])} | {o['sample'][:70]}")


if __name__ == "__main__":
    main()