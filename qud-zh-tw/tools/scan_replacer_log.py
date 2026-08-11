#!/usr/bin/env python3
"""
scan_replacer_log.py — 掃描動態替換側（replacer/Harmony）「未替換」的內容。

動態側的硬編碼遊戲訊息（戰鬥、死亡、傷害等）藏在遊戲 DLL，不在本地化系統內，
只能靠 Harmony 補丁在 runtime 攔截。本工具解析 replacer_log.txt：

  1. UNTRANSLATED: '...'   → 未命中任何模式、原樣通過的英文訊息（主要搜尋目標）
  2. TRANSLATED:   '...' → '...' → 已成功替換的訊息（覆蓋參照）
  3. 從 HarmonyPatches.cs 抽取已覆蓋的模式，顯示「已覆蓋 vs 未替換」對照

用法（先啟動遊戲遊玩一段，收集 replacer_log.txt）：
  python3 tools/scan_replacer_log.py --print          # 文字報告
  python3 tools/scan_replacer_log.py --report        # 寫 JSON（預設）
  python3 tools/scan_replacer_log.py --log <path>    # 指定 log 檔
"""
import argparse
import json
import re
import sys
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]
REPLACERS_DIR = PROJECT.parent / "qud-zh-tw-replacers"
HARMONY = REPLACERS_DIR / "HarmonyPatches.cs"
DEFAULT_REPORT = PROJECT / "tools" / "replacer_report.json"

# 常見 replacer_log.txt 位置（先試本機，再往常見跨層路徑）
CANDIDATE_PATHS = [
    Path.home() / ".local/share/LocalLow/Freehold Games/CavesOfQud/replacer_log.txt",
    Path.home() / ".local/share/Steam/steamapps/compatdata/650700/pfx/drive_c/users/steamuser/AppData/Local/LocalLow/Freehold Games/CavesOfQud/replacer_log.txt",
    Path.home() / "AppData/Local/LocalLow/Freehold Games/CavesOfQud/replacer_log.txt",
    Path("/mnt/g/SteamLibrary/steamapps/common/Caves of Qud/CoQ_Data/../../../../LocalLow/Freehold Games/CavesOfQud/replacer_log.txt"),
]

UNTRANSLATED = re.compile(r"UNTRANSLATED: '?(.+?)'?$")
TRANSLATED = re.compile(r"TRANSLATED: '(.*)' -> '(.*)'")


def find_log(custom: str | None) -> Path | None:
    if custom:
        p = Path(custom)
        return p if p.exists() else None
    for p in CANDIDATE_PATHS:
        if p.exists():
            return p
    return None


def extract_harmony_patterns() -> list[str]:
    """從 HarmonyPatches.cs 抽取已覆蓋的正則模式（粗取，供參照）。"""
    if not HARMONY.exists():
        return []
    pats = []
    for line in HARMONY.read_text(encoding="utf-8").splitlines():
        m = re.search(r'new Regex\(@"(.*?)"', line)
        if m and ("^" in m.group(1) or "hit" in m.group(1) or "dies" in m.group(1)):
            pats.append(m.group(1))
    return pats


def parse_log(log: Path) -> tuple[dict[str, int], dict[str, str]]:
    """回傳 (unreplaced: {msg: 次數}, translated: {msg: zh})。"""
    unreplaced: dict[str, int] = {}
    translated: dict[str, str] = {}
    for line in log.read_text(encoding="utf-8", errors="ignore").splitlines():
        m = UNTRANSLATED.search(line)
        if m:
            msg = m.group(1).strip().strip("'")
            if msg:
                unreplaced[msg] = unreplaced.get(msg, 0) + 1
            continue
        m = TRANSLATED.search(line)
        if m:
            translated[m.group(1)] = m.group(2)
    return unreplaced, translated


def main() -> None:
    ap = argparse.ArgumentParser(description="掃描動態替換側未替換內容")
    ap.add_argument("--log", default=None, help="replacer_log.txt 路徑（預設自動找）")
    ap.add_argument("--print", action="store_true", help="輸出文字報告")
    ap.add_argument("--report", default=str(DEFAULT_REPORT), help="JSON 報表路徑")
    ap.add_argument("--no-report", action="store_true", help="不寫 JSON")
    args = ap.parse_args()

    log = find_log(args.log)
    if not log:
        raise SystemExit("找不到 replacer_log.txt。請先啟動遊戲遊玩一段（需載入 replacer/harmony），再重跑。可用 --log 指定路徑。")

    unreplaced, translated = parse_log(log)
    patterns = extract_harmony_patterns()

    report = {
        "meta": {
            "log": str(log),
            "harmony_patterns": len(patterns),
            "note": "未替換訊息需人工逐條加入 HarmonyPatches.cs 模式或本地化系統",
        },
        "unreplaced": {k: v for k, v in sorted(unreplaced.items(), key=lambda x: -x[1])},
        "covered_patterns": patterns,
    }

    if not args.no_report:
        Path(args.report).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"報表已寫入 {args.report}")

    if args.print or args.no_report:
        print("=" * 60)
        print(f"未替換的英文訊息（{len(unreplaced)} 種）— 需補 Harmony 模式或本地化")
        print("=" * 60)
        if not unreplaced:
            print("  （無，全部已替換）")
        for msg, n in sorted(unreplaced.items(), key=lambda x: -x[1]):
            print(f"  ×{n:<3} {msg[:90]}")
        print()
        print(f"已覆蓋的 Harmony 模式：{len(patterns)} 條")
        print(f"已替換（TRANSLATED）訊息：{len(translated)} 則")


if __name__ == "__main__":
    main()