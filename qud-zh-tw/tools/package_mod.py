#!/usr/bin/env python3
"""
package_mod.py — 打包成可安裝的 mod 資料夾（複製到遊戲 Mods 目錄即可用）。

產出：release/qud-zh-tw/
  ├── manifest.json
  ├── Languages.xml
  └── zh-tw/*.xml          # 翻譯資料（根元素帶 Lang="zh-tw"）

可選 --zip 產出 release/qud-zh-tw.zip 方便發布。

安裝位置：
  Windows: %USERPROFILE%\\AppData\\LocalLow\\Freehold Games\\CavesOfQud\\Mods\\qud-zh-tw\\
  Linux:   ~/.config/unity3d/Freehold Games/CavesOfQud/Mods/qud-zh-tw/
"""
import argparse
import glob
import shutil
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]
SRC = PROJECT / "zh-tw"
OUT = PROJECT / "release" / "qud-zh-tw"
REPL_SRC = PROJECT.parent / "qud-zh-tw-replacers"   # 兄弟的 replacers mod
OUT_REPL = PROJECT / "release" / "qud-zh-tw-replacers"


def validate() -> list[str]:
    """檢查所有翻譯 XML：well-formed 且根元素有 Lang="zh-tw"。"""
    errors = []
    for f in glob.glob(str(SRC / "*.xml")):
        try:
            root = ET.parse(f).getroot()
        except Exception as e:
            errors.append(f"{f}: {e}")
            continue
        if root.attrib.get("Lang") != "zh-tw":
            errors.append(f"{f}: 根元素缺 Lang=\"zh-tw\"")
    return errors


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--zip", action="store_true", help="同時產出 zip")
    args = ap.parse_args()

    errors = validate()
    if errors:
        print("打包中止，以下檔案有問題：")
        for e in errors:
            print("  ", e)
        raise SystemExit(1)
    print(f"驗證通過：{len(glob.glob(str(SRC / '*.xml')))} 個翻譯檔。")

    if OUT.exists():
        shutil.rmtree(OUT)
    OUT.mkdir(parents=True)
    shutil.copy(PROJECT / "manifest.json", OUT / "manifest.json")
    shutil.copy(PROJECT / "Languages.xml", OUT / "Languages.xml")
    if (PROJECT / "workshop.json").exists():
        shutil.copy(PROJECT / "workshop.json", OUT / "workshop.json")
    (OUT / "zh-tw").mkdir()
    for f in sorted(glob.glob(str(SRC / "*.xml"))):
        shutil.copy(f, OUT / "zh-tw" / Path(f).name)
    # 語言子目錄中的資料覆寫檔（如 historyspice.zh-tw.jsonc）一併打包
    for f in sorted(glob.glob(str(SRC / "*.jsonc"))):
        shutil.copy(f, OUT / "zh-tw" / Path(f).name)
        print(f"  複製語言資料檔: {Path(f).name}")
    # mod 根目錄的 spice 語言覆寫檔（historyspice.*.json）→ 放 mod 根
    for f in sorted(glob.glob(str(PROJECT / "historyspice.*.json"))):
        shutil.copy(f, OUT / Path(f).name)
        print(f"  複製 spice 覆寫檔: {Path(f).name}")
    print(f"已打包至 {OUT}")

    # ---- replacers mod（動態文字 C#）----
    if REPL_SRC.exists():
        if OUT_REPL.exists():
            shutil.rmtree(OUT_REPL)
        OUT_REPL.mkdir(parents=True)
        n = 0
        for glob2 in ("*.cs", "manifest.json", "README.txt", "workshop.json", "preview.png"):
            for f in sorted(glob.glob(str(REPL_SRC / glob2))):
                shutil.copy(f, OUT_REPL / Path(f).name)
                n += 1
        print(f"已打包 replacers mod 至 {OUT_REPL}（{n} 檔）—— 發布時必須一起安裝！")
    else:
        print("[警告] 找不到 replacers mod 目錄，只打包 data mod。")

    if args.zip:
        zip_path = PROJECT / "release" / "qud-zh-tw.zip"
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
            for f in OUT.rglob("*"):
                z.write(f, f.relative_to(PROJECT / "release"))
            for f in OUT_REPL.rglob("*"):
                z.write(f, f.relative_to(PROJECT / "release"))
        print(f"已產生 {zip_path}（含 qud-zh-tw/ 與 qud-zh-tw-replacers/ 兩個 mod）")


if __name__ == "__main__":
    main()
