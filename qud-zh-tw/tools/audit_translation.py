#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""翻譯審計工具：檢查/提取/驗證/修復 語料層與 replacer 的翻譯一致性問題。

用法：
  python3 audit_translation.py          # 只出報告 report_translation.md
  python3 audit_translation.py --apply  # 套用機械類修復（C1 括號空格 / C2 月名 / C3 種族括號）
  python3 audit_translation.py --check  # 檢查模式：C1/C2/C7 有違例即非零退出（供 run_tests 呼叫）

輸出：tools/report_translation.md
"""
import re
import sys
import glob
import os
from collections import Counter, defaultdict

TOOLS = os.path.dirname(os.path.abspath(__file__))
ZH = os.path.abspath(os.path.join(TOOLS, "..", "zh-tw"))
REPLACER = os.path.abspath(os.path.join(TOOLS, "..", "..", "qud-zh-tw-replacers"))
REPORT = os.path.join(TOOLS, "report_translation.md")

CJK = re.compile(r"[\u4e00-\u9fff]")


def has_zh(v):
    return bool(CJK.search(v))


def has_en(v):
    return bool(re.search(r"[A-Za-z]{2,}", v))


# --------------------------------------------------------------------------
# 載入語料層
# --------------------------------------------------------------------------
def load_corpus():
    """回傳 [{file, context, id, value, line}]；line 為 XML 內行號（1 起）。"""
    rows = []
    for f in sorted(glob.glob(os.path.join(ZH, "*.xml"))):
        txt = open(f, encoding="utf-8").read()
        lines = txt.split("\n")
        offset = 0
        pat = re.compile(
            r'<string Context="(?P<c>[^"]*)" ID="(?P<i>[^"]*)"[^>]*>'
            r"(?P<v>[^<]*)</string>"
        )
        pat2 = re.compile(r'<string ID="(?P<i>[^"]*)"[^>]*>(?P<v>[^<]*)</string>')
        for m in pat.finditer(txt):
            line = txt.count("\n", 0, m.start()) + 1
            rows.append({"file": os.path.basename(f), "context": m.group("c"),
                         "id": m.group("i"), "value": m.group("v"), "line": line})
        for m in pat2.finditer(txt):
            if any(r["id"] == m.group("i") and r["line"] == txt.count("\n", 0, m.start()) + 1
                   for r in rows):
                continue  # 已由 pat 捕獲
            rows.append({"file": os.path.basename(f), "context": "",
                         "id": m.group("i"), "value": m.group("v"),
                         "line": txt.count("\n", 0, m.start()) + 1})
    return rows


# --------------------------------------------------------------------------
# 載入 replacer 字典（從 TextCleanerHook.cs 提取）
# --------------------------------------------------------------------------
def load_replacer_dicts():
    """回傳 {dict名: {key: value}}。只處理 Dictionary<string,string> 靜態字典。"""
    out = {}
    dups = []
    cs = os.path.join(REPLACER, "TextCleanerHook.cs")
    if not os.path.exists(cs):
        return out, dups
    txt = open(cs, encoding="utf-8", errors="replace").read()
    for m in re.finditer(
        r"Dictionary<string, string>\s+(\w+)\s*=\s*new Dictionary<string, string>"
        r"\(\s*StringComparer\.(\w+)\s*\)\s*\{(.*?)\n\s*\};",
        txt, re.S):
        name, cmp, body = m.group(1), m.group(2), m.group(3)
        d = {}
        for e in re.finditer(r'\{\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\}', body):
            k, v = e.group(1), e.group(2)
            if k in d and d[k] != v:
                dups.append((name, k, d[k], v))
            d.setdefault(k, v)
        out[name] = d
    return out, dups


# --------------------------------------------------------------------------
# 檢查器
# --------------------------------------------------------------------------
def c1_paren_spacing(rows):
    """「中文 (英文)」帶空格 → 統一無空格。"""
    issues = []
    for r in rows:
        v = r["value"]
        if has_zh(v) and re.search(r"[\u4e00-\u9fff]\s+\([A-Za-z]{2,}", v):
            issues.append(r)
    return issues


def c2_months(rows):
    """Calendar Month 13 條：Value 必須含「(ID)」且無空格。"""
    issues = []
    for r in rows:
        if r["context"] != "Calendar Month":
            continue
        base = r["id"]
        v = r["value"]
        if not has_zh(v):
            issues.append((r, "未翻譯"))
        elif not re.search(re.escape("(" + base + ")"), v):
            issues.append((r, "缺 (原文) 括號"))
        elif re.search(r"[\u4e00-\u9fff]\s+\([A-Za-z]{%d,%d}" % (len(base), len(base) + 6), v):
            issues.append((r, "括號前帶空格"))
    return issues


def c3_races(rows, replacer):
    """種族級 Creatures DisplayName 應含「原文」括號。輸出候選清單。"""
    races = {}
    for r in rows:
        if r["file"] != "Creatures.zh-tw.xml" or "DisplayName" not in r["id"]:
            continue
        if r["id"] != "Render" and "DisplayName" not in r["context"]:
            continue
    # 直接掃 Creatures.zh-tw.xml 的 object Name + DisplayName
    path = os.path.join(ZH, "Creatures.zh-tw.xml")
    txt = open(path, encoding="utf-8").read()
    items = []
    for m in re.finditer(
        r'<object Name="(?P<on>[^"]*)"[^>]*>\s*<part Name="Render" DisplayName="(?P<dn>[^"]*)"',
        txt):
        items.append((m.group("on"), m.group("dn"), txt.count("\n", 0, m.start()) + 1))
    # 種族級：object Name 在已知清單，或 DisplayName 純中文無括號者列「候選」
    known = {"Goatfolk", "Snapjaw", "Cragmensch", "Troll"}
    issues = []
    candidates = []
    for on, dn, line in items:
        if not has_zh(dn):
            continue
        if on in known:
            if not re.search(re.escape("(" + on + ")"), dn):
                issues.append(({"file": "Creatures.zh-tw.xml", "id": on,
                                "value": dn, "line": line, "context": "Race"},
                               f"種族 {on} 無括號"))
        else:
            candidates.append((on, dn, line))
    return issues, candidates


def c4_untranslated(rows):
    """Value 無中文字：過濾「應保留」白名單，其餘為疑似漏翻。"""
    KEEP = [
        r"^\{?\{emote\|",            # emote
        r"^[hwghk]*\.?$",            # 擬聲（短
        r"\b(st|nd|rd|th)\b$",       # 序數詞尾殘
        r"^[+#-]?\d*\s*[A-Za-z %]+$",  # 純數值/單位
        r"^High?$|^Grin?$|^Awe?$|^hover$|^Close$|^Open$",  # 過濾單詞
        r"^&lt;|^&gt;",              # 標籤
        r"^\{.*\}$",                 # markup 純標示
        r"^(NON |BY |BEFORE|IN MAQQOM|CREDO|SHEBA)",  # 宗教語句
        r"\b(Liquids|liquid|chance|Title|text|The )\b",  # 生成器片段
        r"=",                        # 含 =變數= 或模板
        r"(\.{1,1}[a-z ]*\w?)\.?$",   # 已截尾的短句殘段
    ]
    keep_pat = re.compile("|".join(KEEP))
    # 擬聲判定：重複字母（rrrr / ekekek / hahaha）或已知咕嚕詞集
    GROWL = {"gn", "grn", "hrf", "hn", "hf", "lib", "drin", "dronk", "liffen",
             "dick", "ek", "rrr", "hh", "doh", "ah", "yeu", "mgl", "drw"}
    def is_growl(v):
        if re.search(r"(.)\1{2,}", v):  # ehehehe / rrrr / hhhhh
            return True
        for tok in re.findall(r"[a-z]{2,}", v.lower()):
            if tok not in GROWL and len(tok) > 3:
                return False
        return bool(re.search(r"[a-z]{2,}", v.lower()))
    suspected = []
    keep = []
    for r in rows:
        v = r["value"].strip("\n\r ")
        if has_zh(r["value"]) or not has_en(r["value"]):
            continue
        if len(v) > 120:
            continue
        if keep_pat.search(v) or len(set(v)) <= 3 or is_growl(v):
            keep.append(r)
        else:
            suspected.append(r)
    return suspected, keep


def c5_conflicts(rows, replacer):
    """語料層 DisplayName（含括號原文） vs replacer 字典值衝突。"""
    # 語料層對照：英文原名 → 語料層慣用中文（取「中文(原名)」或純中文）
    canon = {}
    for m in glob.glob(os.path.join(ZH, "*.xml")):
        txt = open(m, encoding="utf-8").read()
        for mm in re.finditer(
            r'<object Name="(?P<on>[^"]*)"[^>]*>\s*<part Name="Render" DisplayName="(?P<dn>[^"]*)"',
            txt):
            dn = mm.group("dn")
            zh = re.sub(r"\(.*?\)", "", dn).strip()
            while re.search(r"\{[^}]*\}", zh):      # 剝離嵌套 markup
                zh = re.sub(r"\{[^}]*\}", "", zh).strip()
            zh = zh.replace("}", "").strip()
            if has_zh(zh):
                canon.setdefault(mm.group("on"), zh)
    conflicts = []
    for dname, d in replacer.items():
        for k, v in d.items():
            if not has_zh(v) or "(" in k:
                continue
            for src, canon_zh in canon.items():
                if k.lower() == src.lower() and v != canon_zh and "(" + src + ")" not in v:
                    conflicts.append((dname, k, v, src, canon_zh))
    return conflicts


def c6_residue(rows):
    """語料層即時的序數/stratum 殘留（注入點問題由 replacer 修；此處是語料自身）。"""
    issues = []
    for r in rows:
        v = r["value"]
        if re.search(r"\d(?:st|nd|rd|th)\b", v) or re.search(r"\bstratum|strata\b", v, re.I):
            issues.append(r)
    return issues


def c7_common_residue(rows):
    """中文條目內殘留常見英文詞巡檢。"""
    RES_WORDS = ["damage", "weapon", "armor", "armour", "rounds", "hit points",
                 "for 1d", " the ", " of ", "species", "creature", "faction"]
    found = defaultdict(list)
    VAR = re.compile(r"=[A-Za-z][^=\u4e00-\u9fff]*")
    ENPAREN = re.compile(r"[（(][A-Za-z][^）)]*[）)]")  # 括號內「英文原文」（半/全角）
    for r in rows:
        if has_zh(r["value"]):
            v = VAR.sub(" ", r["value"])
            v = ENPAREN.sub(" ", v)  # 剝離 (English original)
            for w in RES_WORDS:
                if re.search(r"\b" + w + r"\b", v, re.I):
                    found[w].append(r)
    return dict(found)


def c8_short_labels(rows):
    """短英文標籤（<=14 字元、無中文、非擬聲非 emote）——疑似 UI 未翻。"""
    KEEP = [
        r"=",                          # =變量= 模板
        r"^\{?\{\s*[A-Za-z]|^\{\{[A-Za-z]{1,6}\}\}",  # markup 短標籤
        r"^(OK|Yes|No|Up|Down|Left|Right)\b",         # 已知 UI 詞（多已有翻譯）
        r"^[Hh]?[Aa]?[Hh]{1,}$",                       # 擬聲 ah/hah
        r"QQ|zzz|gn\.|hrf|lib|dick|liffen",            # 生物咕嚕聲子串
        r"Night|Him|Her|It",                           # 代名詞/時間
    ]
    keep = re.compile("|".join(KEEP))
    out = []
    for r in rows:
        v = r["value"].strip("\n\r ")
        if has_zh(v) or not has_en(v) or len(v) > 14 or len(v) < 2:
            continue
        if keep.search(v):
            continue
        out.append(r)
    return out


def c9_duplicate_keys(dups):
    """字典鍵衝突：同一 key 在（同字典或跨字典）存在且值不同 → 譯名衝突殘留。

    同字典重複由 load 層收集；跨字典重複（如 PhraseLeaks 與 SentenceDict 同 key
    不同譯文）runtime 兩路都會套用、後者覆蓋，造成一詞多譯時有時無。
    """
    return dups


def c10_replacer_paren_spacing(replacer):
    """replacer 字典值中的「中文 (英文)」帶空格 → 統一無空格。"""
    out = []
    for dname, d in replacer.items():
        for k, v in d.items():
            if has_zh(v) and re.search(r"[\u4e00-\u9fff]\s+\([A-Za-z]{2,}", v):
                out.append((dname, k, v))
    return out


def c11_missing_propernoun(rows, replacer):
    """語料層「中文(原文)」樣式中的專名，若 ProperNounZh 缺條目 → 注入點漏翻風險
    （Kindrish 型：全句已翻、字典缺、其他注入點漏）。"""
    proper = set(k.lower() for k in replacer.get("ProperNounZh", {}))
    proper |= set(k.lower() for k in replacer.get("TmpWords", {}))
    missing = []
    for r in rows:
        for m in re.finditer(r"[（(]([A-Z][A-Za-z' -]{3,})[）)]", r["value"]):
            name = m.group(1).strip()
            if has_zh(name):
                continue
            core = re.split(r"[\s']", name)[0]
            if len(core) >= 4 and core.lower() not in proper and core not in proper:
                freq = sum(1 for rr in rows if core.lower() in rr["value"].lower())
                if freq <= 2:  # 高頻名=語料自我覆蓋，非注入點風險
                    missing.append((r["file"], r["line"], r["id"][:40], name))
    return missing


# --------------------------------------------------------------------------
# 修復（機械類）
# --------------------------------------------------------------------------
def apply_fixes(rows, c1, c2, c3):
    """C1/C2/C3 自動修。回傳修改統計。"""
    stats = Counter()
    plan = {}

    for r in c1:  # 去空格
        key = os.path.join(ZH, r["file"])
        plan.setdefault(key, {})
        v = r["value"]
        fixed = re.sub(r"([\u4e00-\u9fff])\s+\(([A-Za-z]{2,})", r"\1(\2", v)
        # 記入該檔對應行替換：以「行號→原值→新值」精確替換
        plan[key][r["line"]] = (v, fixed, "C1 去括號空格")
        stats["C1"] += 1

    for r, why in c2:  # 月名
        key = os.path.join(ZH, r["file"])
        plan.setdefault(key, {})
        v = r["value"]
        fixed = v
        if why == "未翻譯":
            fixed = "中文占位"
            fixed = ""
            # 未翻譯需人工翻譯（不自動）：僅記入報告
            stats["C2-手動"] += 1
            continue
        fixed = re.sub(r"[\u4e00-\u9fff]{2,}(\([A-Za-z][^)]*\))?", lambda m:
                       re.sub(r"\s+\((.*?)\)", "(\1)", m.group(0))
                       if "(" in m.group(0) else m.group(0) + "(" + r["id"] + ")", fixed, count=1)
        # 保守：自動只處理「帶空格→無空格」與「缺括號補(ID)」
        fixed = re.sub(r"([\u4e00-\u9fff]+)\s+\((.*?)\)", r"\1(\2)", v)
        if why == "缺 (原文) 括號" and "(" + r["id"] + ")" not in fixed:
            fixed = v + "(" + r["id"] + ")"
        plan[key][r["line"]] = (v, fixed, "C2 月名")
        stats["C2"] += 1

    for r, why in c3:  # 種族
        key = os.path.join(ZH, "Creatures.zh-tw.xml")
        plan.setdefault(key, {})
        v = r["value"]
        fixed = v + "(" + r["id"] + ")"
        plan[key][r["line"]] = (v, fixed, "C3 種族括號")
        stats["C3"] += 1

    # 套用
    for path, subs in plan.items():
        txt = open(path, encoding="utf-8").readlines()
        for line, (old, new, kind) in subs.items():
            if line - 1 < len(txt) and old in txt[line - 1]:
                txt[line - 1] = txt[line - 1].replace(old, new, 1)
            else:
                for i, l in enumerate(txt):
                    if old in l:
                        txt[i] = l.replace(old, new, 1)
                        break
        open(path, "w", encoding="utf-8", newline="\n").writelines(txt)
    return stats


# --------------------------------------------------------------------------
def main():
    args = sys.argv[1:]
    mode = "report"
    if "--apply" in args:
        mode = "apply"
    if "--check" in args:
        mode = "check"

    rows = load_corpus()
    replacer, dup_keys = load_replacer_dicts()

    c1 = c1_paren_spacing(rows)
    c2 = c2_months(rows)
    c3, c3_cand = c3_races(rows, replacer)
    c4, c4_keep = c4_untranslated(rows)
    c5 = c5_conflicts(rows, replacer)
    c6 = c6_residue(rows)
    c7 = c7_common_residue(rows)
    c8 = c8_short_labels(rows)
    _all = {}
    def _is_protection_marker(_v, _k):
        return _v == "" or (f"({_k})" in _v or f"({_k.lower().capitalize()})" in _v) or _v == _k
    for _dn, _d in replacer.items():
        for _k, _v in _d.items():
            if _k in _all and _all[_k][1] != _v:
                _pa, _pb = _all[_k][1], _v
                if (_k.count(" ") >= 2):
                    continue  # 長句 key 分屬不同字典路徑，非核心衝
                if not (_is_protection_marker(_pa, _k) or _is_protection_marker(_pb, _k)):
                    dup_keys.append((f"{_all[_k][0]}+{_dn}", _k, _pa, _pb))
            _all.setdefault(_k, (_dn, _v))
    c9 = c9_duplicate_keys(dup_keys)
    c10 = c10_replacer_paren_spacing(replacer)
    c11 = c11_missing_propernoun(rows, replacer)

    L = []
    L.append("# 翻譯審計報告\n")
    L.append(f"- 語料 <string> 條目：{len(rows)}　|　replacer 字典：{sum(len(d) for d in replacer.values())}\n")
    L.append(f"- 模式：{mode}\n")

    def sec(title, items, fmt=None):
        L.append(f"\n## {title}（{len(items)}）\n")
        for it in items[:80]:
            if fmt:
                L.append("- " + fmt(it))
            else:
                r = it[0]
                L.append(f"- {r['file']}:{r['line']} [{r.get('context','')}] {r['id']} → {r['value'][:60]!r} {it[1] if len(it)>1 else ''}")
        if len(items) > 80:
            L.append(f"- …（共 {len(items)} 條，略）")

    sec("C1 括號帶空格（中文 (英文)）→ 應無空格", c1, lambda r: f"{r['file']}:{r['line']} [{r.get('context','')}] {r['id'][:40]} → {r['value'][:50]!r}")
    sec("C2 月名問題", c2)
    sec("C3 種族缺括號", c3)
    L.append(f"\n## C3b 未列入種族清單的生物（候選，不自動修）\n")
    for on, dn, line in c3_cand[:40]:
        L.append(f"- Creatures.zh-tw.xml:{line} {on} → {dn}")
    L.append(f"（共 {len(c3_cand)} 個候選，略）\n")

    sec("C4 疑似漏翻（無中文且非擬聲/emote/生成式）", c4, lambda r: f"{r['file']}:{r['line']} [{r.get('context','')}] ID={r['id'][:50]} → {r['value'][:40]!r}")
    L.append(f"\n## C4b 判定「應保留」的清單（僅統計）\n- {len(c4_keep)} 條（擬聲/emote/數值/宗教語句/生成式片段）\n")

    sec("C5 語料層 vs replacer 譯名衝突（以語料層為準）", c5,
        lambda t: f"{t[0]} 字典 [{t[1]}] = {t[2]!r}　衝突：語料 {t[3]} → {t[4]}")

    sec("C6 語料層內序數/stratum 殘留", c6, lambda r: f"{r['file']}:{r['line']} [{r.get('context','')}] → {r['value'][:60]!r}")

    L.append(f"\n## C7 中文條目殘留英文詞巡檢（期望全 0）\n")
    if c7:
        for w, items in sorted(c7.items()):
            L.append(f"- 「{w}」×{len(items)}：例 {items[0]['file']}:{items[0]['line']} {items[0]['value'][:50]!r}")
    else:
        L.append("- 全 0 ✓\n")

    sec("C8 短英文標籤疑似未翻", c8, lambda r: f"{r['file']}:{r['line']} [{r.get('context','')}] {r['id'][:44]} → {r['value']!r}")

    sec("C9 replacer 同字典重複 key 且值不一（後者生效）", c9,
        lambda t: f"{t[0]} [{t[1][:50]}] 值A={t[2][:36]!r} 值B={t[3][:36]!r}")

    sec("C10 replacer 字典值帶空格括號 中文 (英文)", c10,
        lambda t: f"{t[0]} [{t[1][:40]}] → {t[2][:60]!r}")

    sec("C11 語料『中文(原文)』專名未入 ProperNounZh（注入點漏翻風險）", c11,
        lambda t: f"{t[0]}:{t[1]} [{t[2]}] → 缺:「{t[3]}」")

    rep = "\n".join(L) + "\n"
    open(REPORT, "w", encoding="utf-8").write(rep)
    print(rep[:600])
    print(f"\n---- 報告已寫入 {REPORT} ----")

    hard_fail = 0
    if mode == "apply":
        stats = apply_fixes(rows, c1, c2, c3)
        print("已套用修復：", dict(stats))
    if mode == "check":
        hard_fail = (len(c1) + len(c2) + len(c3) + len(c7)) 
        print("check 模式：嚴重類別違例數 =", hard_fail)
        sys.exit(0 if hard_fail == 0 else 1)


if __name__ == "__main__":
    main()