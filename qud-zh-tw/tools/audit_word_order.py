#!/usr/bin/env python3
"""
audit_word_order.py — 動態模板「語序」稽核工具。

背景：遊戲訊息由「模板 + 變數」組裝。逐詞替換會保留英文語序，導致中文散架
（例：「涉水 涉過 的 500 德蘭 的 鹹的 水」）。正確做法是在模板層重排語序。
但 base 有 1678 個動態模板，不能一個一個手動找。本工具按「結構模式」分類，
偵測 zh-tw 是否保留英文語序 / 殘留英文，產出優先級清單，一次修一個模式=修一批。

偵測的模式：
  of        「X of Y」→ 中文常需重排為「Y 的 X」或「X 的 Y」依語境
  from      「from X」→ 「來自 X」
  pluralize 「N pluralize:dram of X」→ 「N 德蘭的 X」
  adjective 「adjective + noun」複合名
  Does:verb 「=subject.Does:verb= ...」動詞片語

用法：
  python3 tools/audit_word_order.py                 # 全掃，輸出報告
  python3 tools/audit_word_order.py --pattern of    # 只看 of 模式
  python3 tools/audit_word_order.py --untranslated  # 只列 zh-tw 仍殘留英文的
  python3 tools/audit_word_order.py --limit 50      # 前 50 條
"""
import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROJ = ROOT.parent
BASE = Path("/mnt/g/SteamLibrary/steamapps/common/Caves of Qud/CoQ_Data/StreamingAssets/Base/ExampleLanguage/Strings.example.xml")
ZH = PROJ / "zh-tw" / "Strings.zh-tw.xml"
OUT = ROOT / "word_order_report.json"

CJK = re.compile(r"[\u4e00-\u9fff]")
ENG = re.compile(r"[A-Za-z]{3,}")


def parse_strings(path):
    """回傳 {ID: value}（ID 為 key；base 的 ID 即英文原文）。value 不含 < 故用 [^<]*。"""
    s = path.read_text(encoding="utf-8-sig", errors="ignore")
    d = {}
    for m in re.finditer(r'<string\b[^>]*ID="([^"]*)"[^>]*>([^<]*)</string>', s):
        d[m.group(1)] = m.group(2)
    # Value="..." 形式
    for m in re.finditer(r'<string\b[^>]*ID="([^"]*)"[^>]*Value="([^"]*)"', s):
        if m.group(1) not in d:
            d[m.group(1)] = m.group(2)
    return d


def classify(idv):
    """回傳該模板的語序風險模式集合。"""
    pats = set()
    if re.search(r"\bof\b", idv): pats.add("of")
    if re.search(r"\bfrom\b", idv, re.I): pats.add("from")
    if "pluralize" in idv: pats.add("pluralize")
    if "adjective" in idv.lower(): pats.add("adjective")
    if "Does:" in idv: pats.add("Does:verb")
    if re.search(r"direction", idv, re.I): pats.add("direction")
    return pats


def has_dynamic(idv):
    return bool(re.search(r"=[\w.]+[:#|]", idv))


def residual_english(value):
    """zh-tw 值中殘留的英文詞（排除模板變數 =...= 與 markup {{...}}）。"""
    v = re.sub(r"=[^=]+=", " ", value)
    v = re.sub(r"\{\{[^}]*\}\}", " ", v)
    v = re.sub(r"&#xA;", " ", v)
    return [w for w in ENG.findall(v)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pattern", help="只列此模式 (of/from/pluralize/adjective/Does:verb/direction)")
    ap.add_argument("--untranslated", action="store_true", help="只列 zh-tw 仍殘留英文的")
    ap.add_argument("--limit", type=int, default=0)
    a = ap.parse_args()

    if not BASE.exists():
        print("base 不存在:", BASE); return
    base = parse_strings(BASE)
    zh = parse_strings(ZH)

    stats = {}
    flagged = []
    for idv, bval in base.items():
        if not has_dynamic(idv):
            continue
        pats = classify(idv)
        if not pats:
            continue
        for p in pats:
            stats[p] = stats.get(p, 0) + 1
        if a.pattern and a.pattern not in pats:
            continue
        zval = zh.get(idv)
        resid = residual_english(zval) if zval else None
        if a.untranslated and not resid:
            continue
        flagged.append({
            "patterns": sorted(pats),
            "id": idv,
            "zh_value": zval,
            "residual_english": resid,
            "missing_in_zh": zval is None,
        })

    print("=== 語序風險模式統計（base 動態模板）===")
    for p, c in sorted(stats.items(), key=lambda x: -x[1]):
        print(f"  {p:12s} {c}")
    print(f"\n符合篩選的模板: {len(flagged)}")
    if a.untranslated:
        print("（以下為 zh-tw 仍殘留英文者，優先修）")

    shown = flagged if not a.limit else flagged[:a.limit]
    for item in shown[: (a.limit or 40)]:
        print(f"\n  [{','.join(item['patterns'])}] {item['id'][:90]}")
        if item["missing_in_zh"]:
            print("     zh-tw: <缺>")
        else:
            print(f"     zh-tw: {item['zh_value'][:90]}")
        if item["residual_english"]:
            print(f"     殘留英文: {item['residual_english'][:10]}")

    OUT.write_text(json.dumps({"stats": stats, "flagged": flagged}, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n報告已寫: {OUT}（{len(flagged)} 條）")


if __name__ == "__main__":
    main()
