#!/usr/bin/env python3
"""
rewrite_templates.py — 用本地 LLM 把「逐詞直譯、語序破碎」的模板重寫成自然繁中語序。

場景：遊戲訊息模板（Strings.xml）含 =variable= 佔位符。逐詞替換會保留英文語序，
讀起來散架（例：「受到 彎曲的 金屬片 來自 東方」應為「你從東方收到了彎曲的金屬片」）。
本工具把模板交給本地 LLM，要求在「保留所有 =variable= 佔位符」的前提下，
重排成自然繁中語序。產出前會校驗：輸出必須含原模板的全部 =variable=。

與 translate_batch.py 的差異：
  - translate_batch：翻「未翻譯(▶)」的字串，逐詞/直譯取向。
  - rewrite_templates：重寫「已翻但語序破碎」的模板，語序重排取向（場景 prompt 不同）。

用法：
  python3 tools/rewrite_templates.py --ids "Inventory take from direction" --dry-run
  python3 tools/rewrite_templates.py --ids "A||B||C" --apply     # 多個 ID 用 || 分隔
  python3 tools/rewrite_templates.py --from-report word_order_report.json --limit 10 --apply
"""
import argparse
import json
import re
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from translate_batch import call_api, API_URL, MODEL

ROOT = Path(__file__).resolve().parent
PROJ = ROOT.parent
BASE = Path("/mnt/g/SteamLibrary/steamapps/common/Caves of Qud/CoQ_Data/StreamingAssets/Base/ExampleLanguage/Strings.example.xml")
ZH = PROJ / "zh-tw" / "Strings.zh-tw.xml"

# =variable= 佔位符（內容不含 =）；亦抓 %=x=% 形式
TOKEN = re.compile(r"=([^=\n]+)=|%=?([^=%\n]+)=?%")


def extract_tokens(text):
    """回傳模板中所有佔位符的正規化集合（排序後的 list，可比對）。"""
    toks = []
    for m in TOKEN.finditer(text):
        t = (m.group(1) or m.group(2) or "").strip()
        if t:
            toks.append(t)
    return sorted(toks)


SYSTEM = (
    "You rewrite game text templates for the Caves of Qud Traditional Chinese (zh-TW) "
    "localization into NATURAL Chinese word order.\n"
    "A template contains placeholder tokens written as =something= (e.g. =subject.Does:take=, "
    "=object.the.name=, =dir.direction.expanded=). These are filled in by the game at runtime.\n\n"
    "TOKEN SEMANTICS (important):\n"
    "- =subject.Does:VERB= or =subject.verb:VERB= already mean '[subject] [VERB]' (the subject "
    "plus the action). Treat the token ITSELF as the verb phrase. Do NOT add another Chinese verb "
    "next to it. E.g. if the token is =subject.Does:pour=, do not also write 傾倒/倒 next to it.\n"
    "- =subject.T= / =subject.t= / =subject.name= = the subject noun (某人/你).\n"
    "- =object.the.name= / =object.name= / =object.a.name= = the object noun.\n"
    "- =subject.from.its.direction...= / =dir.direction.expanded= = a direction phrase like "
    "'from the east' (從東方). Place it where a location/source phrase naturally goes.\n"
    "- =subject.itself= / =object.itself= = reflexive pronoun (自己/它).\n"
    "- =amount= / numbers = a quantity.\n\n"
    "RULES:\n"
    "1. PRESERVE every =...= token EXACTLY (same spelling inside the = signs). Do not translate, "
    "alter, add, or drop any token.\n"
    "2. Rearrange the surrounding Chinese into natural, fluent zh-TW word order; do NOT keep "
    "English order. Prefer fronting time/place/source phrases when natural.\n"
    "3. Keep markup {{C|...}}, {{W|...}}, {{y|...}}, [ ] around the same content.\n"
    "4. A trailing '.' becomes '。'.\n"
    "5. Output ONLY the rewritten template. No explanation, no quotes.\n"
)


def load_base_map():
    s = BASE.read_text(encoding="utf-8", errors="ignore")
    d = {}
    for m in re.finditer(r'<string\b[^>]*?(?:Context="([^"]*)")?[^>]*ID="([^"]*)"[^>]*>([^<]*)</string>', s):
        ctx, idv, val = m.group(1), m.group(2), m.group(3)
        d[(ctx or "", idv)] = val
        d.setdefault((None, idv), val)
    return d


def load_zh():
    return ZH.read_text(encoding="utf-8-sig", errors="ignore")


def rewrite_one(eng, url, model):
    user = (
        "Rewrite this template into natural Traditional Chinese word order, preserving all tokens:\n"
        + eng
    )
    raw = call_api(url, model, SYSTEM, user, 0.2, 120).strip()
    # 去掉可能的引號/多行
    line = raw.splitlines()[0].strip() if raw else ""
    line = line.strip('"').strip()
    return line


def validate(eng, out):
    """校驗輸出含原模板全部佔位符。回傳 (ok, missing)。"""
    need = extract_tokens(eng)
    have = set(extract_tokens(out))
    missing = [t for t in need if t not in have]
    return (len(missing) == 0), missing


def apply_zh(idv, ctx, new_value):
    s = load_zh()
    # 找到該 ID 的 <string ...>...</string>，替換 value
    pat = re.compile(r'(<string\b[^>]*ID="' + re.escape(idv) + r'"[^>]*>)([^<]*)(</string>)')
    m = pat.search(s)
    if not m:
        return False
    s = s[:m.start(2)] + new_value + s[m.end(2):]
    ZH.write_text(s, encoding="utf-8-sig")
    return True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ids", help="模板 ID，多個用 || 分隔")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--limit", type=int, default=20)
    ap.add_argument("--url", default=API_URL)
    ap.add_argument("--model", default=MODEL)
    a = ap.parse_args()

    base = load_base_map()
    ids = [x.strip() for x in a.ids.split("||") if x.strip()] if a.ids else []
    if not ids:
        print("請用 --ids 提供模板 ID（|| 分隔）")
        return

    results = {}
    for idv in ids[:a.limit]:
        eng = base.get((None, idv))
        if eng is None:
            print(f"[SKIP] base 找不到 ID: {idv[:60]}")
            continue
        try:
            out = rewrite_one(eng, a.url, a.model)
        except Exception as e:
            print(f"[ERR] {idv[:50]}: {e}")
            continue
        ok, missing = validate(eng, out)
        tag = "OK" if ok else f"缺變數{missing}"
        print(f"\n[{tag}] ID: {idv[:70]}")
        print(f"  EN : {eng[:110]}")
        print(f"  ZH : {out[:110]}")
        results[idv] = {"eng": eng, "zh": out, "valid": ok, "missing": missing}
        time.sleep(0.1)

    valid = {k: v for k, v in results.items() if v["valid"]}
    print(f"\n共 {len(results)} 條，變數校驗通過 {len(valid)} 條")
    if a.apply:
        n = 0
        for idv, v in valid.items():
            if apply_zh(idv, None, v["zh"]):
                n += 1
        print(f"已寫入 zh-tw: {n} 條")
    elif a.dry_run:
        print("（dry-run：未寫入）")
    (ROOT / "rewrite_templates_report.json").write_text(
        json.dumps(results, ensure_ascii=False, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
