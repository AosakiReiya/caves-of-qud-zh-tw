#!/usr/bin/env python3
"""
gen_msg_hardening.py — 將 hardcoded_ui_suggestions.json 的 msg_missing 意譯固化為 C#。

分類：
  - 完整句（句尾句點/驚嘆號/問號，非前綴）→ UiPhrases 精確字典
  - 前綴片段（結尾為空白，執行期組裝成完整句）→ DynamicPopupPatterns regex
    ^<escaped fragment>(.+)$ → <zh>{1}
  - 單字/過短/含 {{ 標記的中段 → 略過（交給 Words 字典或風險高）

輸出：msg_uiadd.txt（UiPhrases 區塊）與 msg_dynadd.txt（DynamicPopupPatterns 區塊）
"""
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SUGG = ROOT / "hardcoded_ui_suggestions.json"
OUT_UI = ROOT / "msg_uiadd.txt"
OUT_DYN = ROOT / "msg_dynadd.txt"

SENTENCE_END = (".", "!", "?", "。", "！", "？")
EXCLUDE = {"stare", "are", "Time", "die", "gain", "swoop", "chirp", "swim",
           "appear", "disappear", "feel", "lock", "Your ", "You are ", "It is not "}

def esc(s):
    # C# 字串 "..."：僅需跳脫反斜線、引號與換行；{ } 為常值
    return (s.replace("\\", "\\\\").replace('"', "\\\"")
             .replace("\n", "\\n").replace("\r", "\\r"))

def esc_regex(s):
    # @"" verbatim：引號加倍，反斜線為常值；regex 特殊字元轉義
    s = re.escape(s)
    return s.replace('"', '""')


def classify(lit, zh):
    if not zh or not re.search(r'[\u4e00-\u9fff]', zh):
        return None
    if lit in EXCLUDE:
        return None
    # 前綴片段：結尾空白
    if lit.endswith(" "):
        base = lit.rstrip()
        if len(base) < 4:
            return None
        return ("dyn", base, zh)
    # 完整句：結尾為句點符號且非以空白結尾
    if lit.endswith(SENTENCE_END) and len(lit) >= 8:
        return ("ui", lit, zh)
    return None


def main():
    sugg = json.load(open(SUGG, encoding="utf-8"))
    ui = []
    dyn = []
    skipped = []
    for lit, info in sugg.items():
        zh = info.get("zh")
        r = classify(lit, zh)
        if r is None:
            skipped.append(lit)
            continue
        kind, key, z = r
        if kind == "ui":
            ui.append((key, z))
        else:
            dyn.append((key, z))

    ui_lines = []
    for key, z in sorted(ui):
        ui_lines.append(f'            {{ "{esc(key)}", "{esc(z)}" }},')
    dyn_lines = []
    for base, z in sorted(dyn):
        pat = f"^" + esc_regex(base) + r"(.+?)$"
        repl = esc(z) + "{1}"
        dyn_lines.append(
            f"System.Tuple.Create(new System.Text.RegularExpressions.Regex(@\"{pat}\", "
            f"System.Text.RegularExpressions.RegexOptions.IgnoreCase), \"{repl}\"),"
        )

    OUT_UI.write_text("\n".join(ui_lines) + "\n", encoding="utf-8")
    OUT_DYN.write_text("\n".join(dyn_lines) + "\n", encoding="utf-8")
    print(f"UiPhrases 完整句: {len(ui)}")
    print(f"DynamicPopupPatterns 前綴: {len(dyn)}")
    print(f"略過: {len(skipped)}")


if __name__ == "__main__":
    main()