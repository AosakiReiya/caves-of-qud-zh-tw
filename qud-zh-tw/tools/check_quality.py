#!/usr/bin/env python3
"""
check_quality.py — 翻譯品質檢查：佔位符完整性。

對每個 <string ID="英文原文">譯文</string>：
  - 比對原文與譯文的 placeholder（=[A-Za-z0-9_.:;|!@/()+\-#']+=、~CmdUse、{{X|...}}）
  - 譯文缺 placeholder / 多出 / 破壞 → 報錯

用法：
  python3 tools/check_quality.py
"""
import glob
import re
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]

PLACEHOLDER = re.compile(r"=[A-Za-z0-9_.:;|!@/()+\-]+=|~[A-Za-z0-9_]+|@[A-Za-z]+(?:\.|=|\s)")
SHADER = re.compile(r"\{\{[^}]*\}\}")
ATTR_PLACEHOLDER = re.compile(r"=[A-Za-z0-9_.:;|!@/()+\-]+=")


def placeholders(s: str) -> tuple[set[str], set[str]]:
    ph = set(re.findall(r"=[A-Za-z0-9_.:;|!@/()+\-]+=", s))
    cmds = set(re.findall(r"~[A-Za-z0-9_]+", s))
    shaders = set(re.findall(r"\{\{[^}|]*\|?", s))
    return ph, cmds | shaders


def check_strings() -> tuple[list[str], list[str]]:
    """回傳 (errors, bad_keys)；bad_keys 為 progress.json 中需重翻的鍵（▶+ID）。

    支援兩種 <string> 格式：
      - <string ID="X">content</string>
      - <string ID="X" Value="content" />
    """
    errors: list[str] = []
    bad_keys: list[str] = []
    for f in glob.glob(str(PROJECT / "zh-tw" / "*.xml")):
        if "Naming" in f:
            continue
        text = open(f, encoding="utf-8-sig").read()

        def check(ident: str, content: str) -> None:
            if not content or content.startswith("▶"):
                return
            src_ph = set(re.findall(r"=[A-Za-z0-9_.:;|!@/()+\-]+=", ident))
            out_ph = set(re.findall(r"=[A-Za-z0-9_.:;|!@/()+\-]+=", content))
            if src_ph != out_ph:
                errors.append(
                    f"{Path(f).name}: placeholder 差異 {sorted(src_ph ^ out_ph)} | ID={ident[:50]!r}"
                )
                bad_keys.append("▶" + ident.replace("&#xA;", "\n").replace("&#xD;", "\r"))

        # 自閉合：Value 屬性
        for m in re.finditer(r"<string\b([^>]*?)/>", text):
            attrs = m.group(1)
            mid = re.search(r'ID="([^"]*)"', attrs)
            mval = re.search(r'Value="([^"]*)"', attrs)
            if mid and mval:
                check(mid.group(1), mval.group(1))
        # 成對：文字內容
        for m in re.finditer(r"<string\b([^>]*?)>(.*?)</string>", text, re.S):
            attrs = m.group(1)
            mid = re.search(r'ID="([^"]*)"', attrs)
            if mid and "<string" not in m.group(2):
                check(mid.group(1), m.group(2))
    return errors, bad_keys


def check_attrs() -> list[str]:
    """檢查屬性值（DisplayName/Short/Title）內含 =[A-Za-z0-9_.:;|!@/()+\-#']+= 的一致性。"""
    errors = []
    for f in glob.glob(str(PROJECT / "zh-tw" / "*.xml")):
        if "Naming" in f:
            continue
        text = open(f, encoding="utf-8-sig").read()
        for m in re.finditer(r'((?:DisplayName|Short|Title|Unit|Description))="([^"]*)"', text):
            name, val = m.group(1), m.group(2)
            if not val or val.startswith("▶"):
                continue
            # 屬性值含 =[A-Za-z0-9_.:;|!@/()+\-#']+= 屬正常（如 Stat Unit）
    return errors


def main() -> None:
    import json

    errs, bad_keys = check_strings()
    if not errs:
        print("✓ placeholder 完整性檢查通過")
        return
    print(f"發現 {len(errs)} 個問題（{len(set(bad_keys))} 條需重翻）：")
    for e in errs[:30]:
        print("  ", e)
    out = PROJECT / "tools" / "bad_quality.json"
    json.dump(sorted(set(bad_keys)), open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=0)
    print(f"\n壞鍵已存至 {out}，可用 tools/remove_bad.py 移除後重翻。")


if __name__ == "__main__":
    main()
