#!/usr/bin/env python3
"""
report_gaps.py — 整合缺漏報告：一次掃描 zh-tw 翻譯 + 遊戲資料檔的所有缺漏維度。

整合維度：
  L1 ▶ 殘留標記            ：zh-tw 骨架未翻譯標記（匯入 find_untranslated）
  L2 英文專名漏譯           ：翻譯內容殘留英文專名（匯入 find_untranslated）
  L3 ID 覆蓋缺漏            ：example ↔ zh-tw 翻譯鍵（匯入 find_untranslated）
  L4 遊戲資料檔深層缺漏      ：ObjectBlueprints（實際遊戲內容）vs ExampleLanguage
                              → 找出「無法被本地化系統覆蓋」的物件 / DisplayName / 描述
  L5 殘留完整英文句子        ：zh-tw 內容中未完句
  L6 Naming 模板覆蓋完整性   ：Naming.zh-tw.xml 模板數 vs example，確認 113 是否全覆蓋

輸出 tools/gaps_report.json + --print 文字報告。

用法：
  python3 tools/report_gaps.py --print
  python3 tools/report_gaps.py             # 只寫 JSON
"""
import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import find_untranslated as fu

PROJECT = Path(__file__).resolve().parents[1]
SKELETON = PROJECT / "zh-tw"
GAME = fu.GAME_ROOT
BLUEPRINTS = GAME / "CoQ_Data" / "StreamingAssets" / "Base" / "ObjectBlueprints"
EXAMPLE = GAME / "CoQ_Data" / "StreamingAssets" / "Base" / "ExampleLanguage"
SPICE_BASE = GAME / "CoQ_Data" / "StreamingAssets" / "Base" / "HistorySpice.jsonc"
SPICE_OVERRIDE = PROJECT / "historyspice.zh-tw.json"
DEFAULT_REPORT = PROJECT / "tools" / "gaps_report.json"

# 可本地化的屬性（出現在 ObjectBlueprints 與 ExampleLanguage 兩邊）
LOCALIZABLE_ATTRS = ("DisplayName", "Short", "Long", "Title")
# 描述內容區塊標籤
DESC_TAGS = ("description", "part", "gametext")


# ---------- L1/L2/L3：匯入 find_untranslated ----------

def collect_l123() -> tuple[dict, dict, dict]:
    curated = fu.load_json(fu.CURATED)
    proper = fu.load_proper_nouns(fu.PROPER_NOUNS)
    l1 = fu.scan_l1()
    l2 = {k: {"count": v["count"], "files": sorted(v["files"])}
          for k, v in fu.scan_l2(curated, proper).items()}
    l3 = fu.scan_l3()
    return l1, l2, l3


# ---------- L4：遊戲資料檔深層缺漏 ----------

def _strip_marker(v: str) -> str:
    return v[1:] if v.startswith("▶") else v


def _attr_values(text: str, attr: str) -> set[str]:
    """抽特定屬性值（去 ▶ 前綴）。"""
    return {_strip_marker(v) for v in re.findall(rf'{attr}="([^"]*)"', text) if _strip_marker(v)}


def collect_l4() -> dict:
    """比對 ObjectBlueprints 與 ExampleLanguage。

    只標記「玩家可見（含 DisplayName 或描述）但不可本地化」的物件：
      - 藍圖物件有 DisplayName/描述，但其物件名（大小寫正規化）不在 example → 無法本地化
      - 排除 base* / *_data / *_ingredient 等內部技術物件
    """
    result = {}
    if not BLUEPRINTS.exists():
        return {"note": f"找不到 {BLUEPRINTS}，跳過 L4", "objects": {}, "attrs": {}}
    for bp in sorted(BLUEPRINTS.glob("*.xml")):
        stem = bp.stem
        ex = EXAMPLE / f"{stem}.example.xml"
        b = bp.read_text(encoding="utf-8", errors="ignore")
        if not ex.exists():
            result[stem] = {"note": "無對應 example（整檔非本地化）", "object_count": len(re.findall(r'<object\b', b))}
            continue
        e = ex.read_text(encoding="utf-8", errors="ignore")
        e_objs = {o.lower() for o in re.findall(r'<object\b[^>]*?Name="([^"]+)"', e)}
        # 依 object 區塊掃：有玩家可見屬性的藍圖物件，其名是否在 example
        not_local = []
        for om in re.finditer(r'<object\b([^>]*?)>', b):
            attrs = om.group(1)
            mname = re.search(r'Name="([^"]+)"', attrs)
            if not mname:
                continue
            oname = mname.group(1)
            # 只在「該 object 區塊到下一 object 前」找 DisplayName/描述
            seg_start = om.end()
            seg_end = b.find("<object ", seg_start)
            seg = b[seg_start:seg_end if seg_end != -1 else len(b)]
            has_visible = ("DisplayName=" in seg or "Description=" in seg
                           or "<description" in seg or " Short=" in seg)
            if not has_visible:
                continue
            # 內部技術物件過濾
            low = oname.lower()
            if (low.startswith("base") or "_data" in low or "ingredient" in low
                    or low.startswith("root")):
                continue
            if low not in e_objs:
                not_local.append(oname)
        if not_local:
            result[stem] = {"player_visible_unlocalizable": sorted(set(not_local))}
    return result


# ---------- L5：殘留完整英文句子 ----------

_SENT = re.compile(r"(?<![A-Za-z])([A-Z][a-z]+(?:\s+[a-z][A-Za-z']*){2,}[.!?])(?![A-Za-z])")


def collect_l5() -> dict:
    result = {}
    total = 0
    for f in sorted(SKELETON.glob("*.xml")):
        if "Naming" in f.name:
            continue
        t = f.read_text(encoding="utf-8-sig")
        t = re.sub(r"<[^>]*>", " ", t)
        # 保留 {{X|content}} 內 content（可能是英文殘留），只移除開頭與尾巴
        t = re.sub(r"\{\{[^}|]*\|", " ", t)
        t = t.replace("}}", " ")
        t = re.sub(r"=[A-Za-z0-9_.:;|!@/()+\-]+=", " ", t)
        hits = [m.group(1).strip() for m in _SENT.finditer(t)]
        if hits:
            result[f.name] = hits
            total += len(hits)
    return {"by_file": result, "total": total}


# ---------- L6：Naming 模板覆蓋完整性 ----------

def collect_l6() -> dict:
    """比對 Naming.zh-tw.xml 與 Naming.example.xml 的模板單位。

    模板單位為含 Name 屬性的 template / value（及 prefix/infix/postfix），
    內容存於 Name 屬性、以 ▶ 標記未翻譯。
    """
    zh_f = SKELETON / "Naming.zh-tw.xml"
    ex_f = EXAMPLE / "Naming.example.xml"
    if not zh_f.exists() or not ex_f.exists():
        return {"note": "找不到 Naming 檔", "total": 0, "untranslated": 0}
    zh = zh_f.read_text(encoding="utf-8-sig")
    ex = ex_f.read_text(encoding="utf-8", errors="ignore")

    def units(t: str) -> set[str]:
        return {fu._norm_id(m) for m in re.findall(r'<(?:template|value|prefix|infix|postfix)\b[^>]*?Name="([^"]*)"', t)}

    zh_u = units(zh)
    ex_u = units(ex)
    untranslated = sorted(u for u in zh_u if u.startswith("▶"))
    return {
        "example_units": len(ex_u),
        "zh_units": len(zh_u),
        "untranslated_units": len(untranslated),
        "untranslated_samples": untranslated[:15],
    }


# ---------- L7：HistorySpice（spice 生成文本）覆蓋完整性 ----------

_SPICE_REF = re.compile(r"=[A-Za-z0-9_.:;|!@/()+\-#'\$\^\[\]]+=")
_SPICE_CJK = re.compile(r"[\u4e00-\u9fff]")
_SPICE_EMOTE = re.compile(r"\{\{emote\|")


def _spice_strings(obj):
    out = []
    def walk(o):
        if isinstance(o, dict):
            for v in o.values(): walk(v)
        elif isinstance(o, list):
            for v in o: walk(v)
        elif isinstance(o, str):
            out.append(o)
    walk(obj)
    return out


def _spice_genuine_leak(s: str) -> bool:
    """spice 模板真實漏翻：剝掉所有引用後仍有實英文、無中文、非 emote、非 camelCase。"""
    if not s or not re.search(r"[A-Za-z]{2,}", s):
        return False
    if _SPICE_CJK.search(s):
        return False
    if _SPICE_EMOTE.search(s):
        return False
    stripped = _SPICE_REF.sub("", s).replace(" ", "").replace(",", "").replace(".", "")
    if not re.search(r"[A-Za-z]{2,}", stripped):
        return False  # 全是引用（或多個連續引用），無實文字
    if " " not in stripped and re.search(r"[a-z][A-Z]", stripped):
        return False  # camelCase 識別字
    return True


def collect_l7() -> dict:
    """比對 base HistorySpice 模板與 zh-tw 覆寫，回報覆寫後仍為英文的真實漏翻。"""
    if not SPICE_BASE.exists() or not SPICE_OVERRIDE.exists():
        return {"note": "缺少 base HistorySpice 或 zh-tw 覆寫檔", "total": 0, "untranslated": 0}
    try:
        base = json.loads(re.sub(r"//[^\n]*", "", SPICE_BASE.read_text(encoding="utf-8")))
        override = json.loads(SPICE_OVERRIDE.read_text(encoding="utf-8"))
    except Exception as e:
        return {"note": f"解析失敗: {e}", "total": 0, "untranslated": 0}
    base_s = _spice_strings(base.get("spice", {}))
    ov_s = _spice_strings(override.get("spice", {}))
    leaks = sorted(set(s for s in ov_s if _spice_genuine_leak(s)))
    return {
        "base_templates": len(base_s),
        "override_templates": len(ov_s),
        "untranslated_units": len(leaks),
        "untranslated_samples": leaks[:15],
    }


# ---------- 主流程 ----------

def build_report() -> dict:
    l1, l2, l3 = collect_l123()
    l4 = collect_l4()
    l5 = collect_l5()
    l6 = collect_l6()
    l7 = collect_l7()
    return {
        "meta": {
            "skeleton": str(SKELETON),
            "blueprints": str(BLUEPRINTS),
            "example": str(EXAMPLE),
            "rules": "L1/L2/L3 靜態、L4 資料檔深層、L5 殘留句子、L6 Naming 模板、L7 spice 生成文本",
        },
        "L1_markers": l1,
        "L2_proper_nouns": l2,
        "L3_coverage": l3,
        "L4_datafile": l4,
        "L5_english_sentences": l5,
        "L6_naming": l6,
        "L7_spice": l7,
    }


def print_report(r: dict) -> None:
    print("=" * 62)
    print(f"L1 ▶ 殘留：{r['L1_markers']['total']} 筆")
    print(f"L2 英文專名漏譯：{len(r['L2_proper_nouns'])} 種")
    l3 = r["L3_coverage"]
    print(f"L3 ID 覆蓋缺漏：{'0（無缺漏）' if not l3 else f'{len(l3)} 檔'}")
    print()
    print("L4 遊戲資料檔深層缺漏（玩家可見但不可本地化）")
    for fname, info in r["L4_datafile"].items():
        if "note" in info:
            print(f"  [{fname}] {info['note']}（{info.get('object_count', 0)} 物件）")
            continue
        objs = info.get("player_visible_unlocalizable", [])
        print(f"  [{fname}] {len(objs)} 個玩家可見但不可本地化的物件")
        for o in objs[:8]:
            print(f"      · {o}")
    print()
    l5 = r["L5_english_sentences"]
    print(f"L5 殘留完整英文句子：{l5['total']} 句")
    for fname, hits in l5["by_file"].items():
        for h in hits[:2]:
            print(f"  [{fname}] {h[:70]}")
    print()
    l6 = r["L6_naming"]
    print(f"L6 Naming 模板：example {l6.get('example_units', 0)} / zh {l6.get('zh_units', 0)} / "
          f"未翻譯 {l6.get('untranslated_units', 0)}")
    for s in l6.get("untranslated_samples", [])[:8]:
        print(f"      ▶ {s[:60]}")
    print()
    l7 = r["L7_spice"]
    print(f"L7 spice 生成文本：base {l7.get('base_templates', 0)} / 覆寫 {l7.get('override_templates', 0)} / "
          f"未翻譯 {l7.get('untranslated_units', 0)}")
    for s in l7.get("untranslated_samples", [])[:8]:
        print(f"      ▶ {s[:60]}")


def main() -> None:
    ap = argparse.ArgumentParser(description="整合缺漏報告")
    ap.add_argument("--print", action="store_true", help="輸出文字報告")
    ap.add_argument("--report", default=str(DEFAULT_REPORT), help="JSON 報表路徑")
    ap.add_argument("--no-report", action="store_true", help="不寫 JSON")
    args = ap.parse_args()
    report = build_report()
    if not args.no_report:
        Path(args.report).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"報表已寫入 {args.report}")
    if args.print or args.no_report:
        print_report(report)


if __name__ == "__main__":
    main()