#!/usr/bin/env python3
"""
build_popup_leaks.py — 從反編譯原始碼萃取動態 Popup 範本，讓 gemma 意譯，輸出 C# 表。

遊戲 Popup.Show("You gain {{C|" + amount + "}} skill points!") 等動態範本，
無法用精確字典匹配。本工具：
  1. 掃描原始碼，解析 Popup.Show/ShowFail/ShowBlock 的完整字串建構式
  2. 把「+ 變數 +」轉成 {N} 佔位（N 為捕獲序）
  3. 丟給 gemma 生成繁體中文意譯（保留 {N} 與 {{X|...}} 色彩標記）
  4. 輸出 suggestions JSON 供審核 → generate 產出 C# regex 條目

用法：
  python3 tools/build_popup_leaks.py --source /tmp/opencode/decompY --dry-run
  python3 tools/build_popup_leaks.py --source /tmp/opencode/decompY --gen
"""
import argparse
import glob
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from translate_batch import call_api, API_URL, MODEL

OUT = Path(__file__).resolve().parent / "popup_leaks.json"

VAR_RE = re.compile(r'[A-Za-z_][A-Za-z0-9_.\[\]()]*')
STR_RE = re.compile(r'"((?:[^"\\]|\\.)*)"', re.S)

SYSTEM = (
    "You are a localization specialist for the game Caves of Qud. Translate English UI templates "
    "into Traditional Chinese for Taiwan (zh-TW). The template contains placeholders {1}, {2}, ... "
    "which the game substitutes at runtime. Rules:\n"
    "1. Keep every {N} placeholder exactly where its meaning belongs (you may reorder to fit "
    "Chinese grammar).\n"
    "2. Keep {{X|...}} color/markup structure intact; translate only the visible words inside.\n"
    "3. Use natural Traditional Chinese, full-width punctuation.\n"
    "4. Translate by meaning (意譯), not transliteration, for normal words.\n"
    "5. Output ONLY the Chinese template, nothing else."
)


def extract_expr(text):
    """回傳 Popup.XXX( 的完整參數 (含巢狀括號)。"""
    m = re.search(r'Popup\.(Show|ShowFail|ShowBlock)\s*\((.*)', text, re.S)
    if not m:
        return None, None
    expr = m.group(2)
    # 找匹配的右括號
    depth = 0
    for i, c in enumerate(expr):
        if c == '(':
            depth += 1
        elif c == ')':
            depth -= 1
            if depth == 0:
                return m.group(1), expr[:i]
    return m.group(1), expr


def expr_to_template(expr):
    """把字串建構式轉成 {N} 範本與變數序。"""
    # 依 字串"..." + 變數 + 字串"..." 模式逐步建構
    template = []
    captures = []
    pos = 0
    n = len(expr)
    capture_idx = 0
    while pos < n:
        # 找開頭字串字面值
        if expr[pos] == '"':
            m = STR_RE.match(expr, pos)
            if not m:
                return None, None
            template.append(m.group(1))
            pos = m.end()
            # 吃 + 或 ,
            pos = _skip_ws(expr, pos)
            if pos < n and (expr[pos] == '+' or expr[pos] == ','):
                pos += 1
        else:
            # 變數
            m = VAR_RE.match(expr, pos)
            if m:
                capture_idx += 1
                captures.append(m.group(0))
                template.append("{" + str(capture_idx) + "}")
                pos = m.end()
                pos = _skip_ws(expr, pos)
                if pos < n and expr[pos] == '+':
                    pos += 1
            else:
                # 未知字元，跳過一個
                pos += 1
    return "".join(template), captures


def _skip_ws(s, i):
    while i < len(s) and s[i] in " \t":
        i += 1
    return i


def collect_source(source_dir):
    """萃取乾淨的動態 Popup 範本：開頭為字串字面值，變數為簡單識別字。"""
    VAR_SIMPLE = re.compile(r'^[A-Za-z_][A-Za-z0-9_]*$')
    results = {}
    for f in glob.glob(str(Path(source_dir) / "**" / "*.cs"), recursive=True):
        try:
            text = open(f, encoding="utf-8", errors="ignore").read()
        except Exception:
            continue
        for line in text.split('\n'):
            if not re.search(r'Popup\.(Show|ShowFail|ShowBlock)\(', line):
                continue
            m = re.search(r'Popup\.(Show|ShowFail|ShowBlock)\s*\("((?:[^"\\]|\\.)*)"(.*)', line)
            if not m:
                continue
            method, start, rest = m.group(1), m.group(2), m.group(3)
            template = start
            caps = []
            ok = True
            n = 0
            r = rest
            while True:
                mm = re.match(r'\s*\+\s*([A-Za-z_][A-Za-z0-9_.\[\]]*)\s*\+\s*"((?:[^"\\]|\\.)*)"', r)
                if not mm:
                    break
                var, seg = mm.group(1), mm.group(2)
                if not VAR_SIMPLE.match(var):
                    ok = False
                    break
                n += 1
                caps.append(var)
                template += "{" + str(n) + "}" + seg
                r = r[mm.end():]
            if not ok or not caps:
                continue
            results.setdefault(template, {"method": method, "template": template, "caps": caps, "file": f})
    return list(results.values())


def translate_one(template, url, model, temperature, timeout, retries):
    last = None
    for _ in range(retries + 1):
        try:
            r = call_api(url, model, SYSTEM, template, temperature, timeout).strip()
            if re.search(r'[\u4e00-\u9fff]', r):
                return r
            last = r
        except Exception as e:
            last = str(e)
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--gen", action="store_true", help="翻譯並存 suggestions")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--url", default=API_URL)
    ap.add_argument("--model", default=MODEL)
    args = ap.parse_args()

    if not args.source:
        raise SystemExit("需要 --source")
    items = collect_source(args.source)
    print(f"動態 Popup 範本：{len(items)} 個")

    if args.dry_run:
        for it in items[:40]:
            print(f"  {it['template'][:70]!r}  caps={it['captures']}")
        return

    if args.gen:
        if args.limit:
            items = items[: args.limit]
        results = {}
        for it in items:
            zh = translate_one(it["template"], args.url, args.model, 0.2, 120, 2)
            results[it["template"]] = {
                "zh": zh, "method": it["method"],
                "caps": it["caps"], "file": it["file"],
            }
            print(f"  {'✓' if zh else '✗'} {it['template'][:50]!r} → {zh}")
        OUT.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"\n已寫入 {OUT}")


if __name__ == "__main__":
    main()