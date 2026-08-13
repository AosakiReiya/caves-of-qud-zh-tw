#!/usr/bin/env python3
"""
deploy_mods.py — 把開發目錄的兩個 mod 同步到遊戲的 Mods 目錄（任何平台）。

目標路徑優先順序：
  1. .env 檔（專案根目錄）的 MODS_DIR=...（支援 Windows/Unix/mac/Wine 掛載路徑）
  2. 自動探測常見位置：
     - Linux:   ~/.config/unity3d/Freehold Games/CavesOfQud/Mods
     - Linux:   ~/.local/share/unity3d/Freehold Games/CavesOfQud/Mods
     - Wine:    /mnt/c/Users/*/AppData/LocalLow/Freehold Games/CavesOfQud/Mods
     - mac:     ~/Library/Application Support/...（你斟酌）
  3. --dir 參數強制指定

.env 範例（放在 qud-zh-tw 專案根目錄）：
  MODS_DIR="/mnt/c/Users/samso/AppData/LocalLow/Freehold Games/CavesOfQud/Mods"
  MODS_DIR="C:\\Users\\samso\\AppData\\LocalLow\\Freehold Games\\CavesOfQud\\Mods"

同步項目：
  qud-zh-tw（data）        ：zh-tw/*.xml、Languages.xml、historyspice.zh-tw.json、manifest.json
  qud-zh-tw-replacers（cs）：*.cs、manifest.json、README.txt

用法：
  python3 tools/deploy_mods.py            # 部署（先列差異）
  python3 tools/deploy_mods.py --dry-run  # 只列差異
  python3 tools/deploy_mods.py --dir /path/to/Mods
"""
import argparse
import os
import re
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROJ = ROOT.parent                       # qud-zh-tw 專案根
DATA_MOD = PROJ                          # data mod（zh-tw/ 等直接在專案根）
REPL_MOD = PROJ.parent / "qud-zh-tw-replacers"   # replacers mod

DATA_FILES = ["manifest.json", "Languages.xml", "historyspice.zh-tw.json"]
DATA_DIRS = ["zh-tw"]
REPL_GLOBS = ["*.cs", "manifest.json", "README.txt"]

CANDIDATES = [
    Path.home() / ".config/unity3d/Freehold Games/CavesOfQud/Mods",
    Path.home() / ".local/share/unity3d/Freehold Games/CavesOfQud/Mods",
]
# Wine / 掛載盤探測
for p in Path("/mnt").glob("*") if Path("/mnt").exists() else []:
    CANDIDATES.extend(sorted((p / "Users").glob("*/AppData/LocalLow/Freehold Games/CavesOfQud/Mods")) if (p / "Users").exists() else [])
# Windows 原生路徑（py 在 Windows 跑）
CANDIDATES.append(Path(os.environ.get("LOCALAPPDATA", "")) / "Low/Freehold Games/CavesOfQud/Mods" if os.environ.get("LOCALAPPDATA") else None)


def load_env_dir() -> str | None:
    env = PROJ / ".env"
    if not env.exists():
        return None
    for line in env.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line.startswith("MODS_DIR=") or line.startswith("MODS_DIR ="):
            val = line.split("=", 1)[1].strip().strip('"').strip("'")
            return val
    return None


def find_mods_dir(forced: str | None) -> Path:
    if forced:
        return Path(forced)
    env = load_env_dir()
    if env:
        d = Path(env)
        if d.is_dir():
            return d
        print(f"[警告] .env 的 MODS_DIR 不存在：{env}")
    for c in CANDIDATES:
        if c and c.is_dir():
            return c
    print("錯誤：找不到遊戲 Mods 目錄。請在專案根建立 .env 設定 MODS_DIR=...（見 docstring）")
    sys.exit(1)


def sync(src: Path, dst: Path, spec_files: list[str] | None, spec_dirs: list[str] | None,
         globs: list[str] | None, dry: bool) -> tuple[int, list[str]]:
    changed = 0
    details = []
    dst.mkdir(parents=True, exist_ok=True)
    if spec_files:
        for rel in spec_files:
            sf = src / rel
            if not sf.exists():
                continue
            df = dst / rel
            if not df.exists() or sf.read_bytes() != df.read_bytes():
                details.append(f"  更新 {rel}")
                changed += 1
                if not dry:
                    shutil.copy2(sf, df)
    if spec_dirs:
        for rd in spec_dirs:
            for sf in (src / rd).glob("*"):
                if not sf.is_file():
                    continue
                df = dst / rd / sf.name
                df.parent.mkdir(parents=True, exist_ok=True)
                if not df.exists() or sf.read_bytes() != df.read_bytes():
                    details.append(f"  更新 {rd}/{sf.name}")
                    changed += 1
                    if not dry:
                        shutil.copy2(sf, df)
    if globs:
        for g in globs:
            for sf in src.glob(g):
                df = dst / sf.name
                if not df.exists() or sf.read_bytes() != df.read_bytes():
                    details.append(f"  更新 {sf.name}")
                    changed += 1
                    if not dry:
                        shutil.copy2(sf, df)
    return changed, details


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=None, help="強制指定遊戲 Mods 目錄")
    ap.add_argument("--dry-run", action="store_true", help="只列差異不複製")
    a = ap.parse_args()

    mods = find_mods_dir(a.dir)
    print(f"目標 Mods 目錄：{mods}\n")

    total = 0
    # data mod
    dst1 = mods / "qud-zh-tw"
    c1, d1 = sync(DATA_MOD, dst1, DATA_FILES, DATA_DIRS, None, a.dry_run)
    idx = 0
    for i, _ in enumerate(d1):
        d1[i] = f"  [data] {d1[i]}"
    print(f"== data mod（qud-zh-tw）：{c1} 個變更 ==")
    for x in d1[:20]:
        print(x)
    total += c1
    # replacers mod
    dst2 = mods / "qud-zh-tw-replacers"
    c2, d2 = sync(REPL_MOD, dst2, None, None, REPL_GLOBS, a.dry_run)
    for i, _ in enumerate(d2):
        d2[i] = f"  [replacers] {d2[i]}"
    print(f"\n== replacers mod（qud-zh-tw-replacers）：{c2} 個變更 ==")
    for x in d2[:20]:
        print(x)
    total += c2

    if a.dry_run:
        print(f"\n（dry-run 完成，共 {total} 檔需同步）")
    else:
        print(f"\n部署完成：{total} 檔同步。請重啟遊戲讓 mod 重新編譯載入。")


if __name__ == "__main__":
    main()