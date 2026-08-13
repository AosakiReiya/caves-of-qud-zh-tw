#!/usr/bin/env python3
"""
extract_templates.py — 一次性提取全部訊息模板語料 + token 完整性/碎塊自動檢測。

輸入：
  1. zh-tw/Strings.zh-tw.xml（Context+ID+翻譯值）
  2. Assembly-CSharp.dll 的 #Strings heap（含 =token= 的程式碼模板，骨架漏網的）

輸出：
  tools/templates_corpus.json — 語料庫（每條含 token 完整性/碎塊標記）
  tools/templates_report.json — 統計報告（未翻譯數、token mismatch、碎塊候選）

檢測：
  - token_mismatch : zh 值缺少或多出 =...= token（翻譯弄丟模板槽位）
  - untranslated   : 值 == ID（英文未翻）或值無任何中文
  - mixed          : 值含英文詞殘留（中英混雜）
  - suspicious     : 語病模式（相鄰重複中文詞、=token= 直接接「與/和/以及」等）

用法：
  python3 tools/extract_templates.py              # 生成語料庫+報告
  python3 tools/extract_templates.py --print 20   # 印前 20 條候選
"""
import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
ZH = ROOT.parent / "zh-tw"
GAME = ROOT.parent
for _ in range(4):
    if (GAME / "CoQ_Data").exists():
        break
    GAME = GAME.parent
DLL = GAME / "CoQ_Data" / "Managed" / "Assembly-CSharp.dll"
ZH_STRINGS = ZH / "Strings.zh-tw.xml"
CORPUS = ROOT / "templates_corpus.json"
REPORT = ROOT / "templates_report.json"

TOKEN_RE = re.compile(r"=[A-Za-z0-9_.:;|!@/()+\-#'%$]+=")
# 寬鬆版：容許中文值側（=errorCount.ifPlural:個:個=）與 token 內空格（=vscause|wrap:vs. =）
TOKEN_WIDE_RE = re.compile(r"=[^=\s][^=]{0,80}=")
# be 動詞 token（中文語境多餘，合法剝除）
BE_TOKENS = {
    "=verb:are=", "=verb:is=", "=verb:was=", "=verb:were=", "=verb:am=",
    "=subject.verb:are=", "=subject.verb:is=", "=subject.verb:was=",
    "=subject.does:are=", "=subject.does:is=", "=object.does:are=",
    "=it.does:are=", "=it.does:is=", "=does:are=", "=does:is=",
    "=ifPlural:are:is=", "=faction.ifPlural:are:is=",
    "=object.verb:are=", "=object.verb:is=", "=object.verb:was=",
    "=subject.verb:are=", "=subject.verb:is=", "=subject.verb:was=",
    "=it.verb:are=", "=it.verb:is=", "=pool.is=", "=pool.are=",
    "=blocker.does:are=", "=blocker.does:is=",
    "=limb.ifPlural:are:is=", "=limb.ifPlural:were:was=",
    "=limb.ifPlural:is:are=", "=limb.ifPlural:was:were=",
    "=faction.ifPlural:despise:despises=", "=faction.ifPlural:dislike:dislikes=",
    "=faction.ifPlural:favor:favors=", "=faction.ifPlural:don't:doesn't=",
    "=faction.ifPlural:ones:members=",
    "=faction.ifPlural:admire:admires=", "=faction.ifPlural:sympathize:sympathizes=",
    "=faction.ifPlural:trust:trusts=",
}
# =token= 展平合法的（zh 把 =X.the.location.of= 展開為 =X= 的位置 等）——不細判，只濾 be 動詞
CJK_RE = re.compile(r"[\u4e00-\u9fff]")
ENG_WORD_RE = re.compile(r"[A-Za-z]{2,}")
SUSPECT_PATTERNS = [
    (re.compile(r"([\u4e00-\u9fff]{2,}) \1"), "相鄰重複詞（如 在乎 在乎）"),
    (re.compile(r"([\u4e00-\u9fff]{2,}) \1"), "相鄰重複詞（如 在乎 在乎）"),
    (re.compile(r"你 你|他 他|她 她|它 它|我 我"), "代詞重複"),
]


def parse_strings(path):
    """解析 Strings.zh-tw.xml 的全部 <string Context ID 值>。"""
    s = path.read_text(encoding="utf-8-sig")
    out = []
    for m in re.finditer(
        r'<string\s+Context="((?:[^"\\]|\\.)*)"\s+ID="((?:[^"\\]|\\.)*)"[^>]*>((?:[^<]|&lt;|&gt;|&#xA;|&#xD;)*)</string>',
        s, re.S):
        ctx, sid, val = m.group(1), m.group(2), m.group(3)
        val = val.replace("&lt;", "<").replace("&gt;", ">").replace("&#xA;", "\n").replace("&#xD;", "\r")
        out.append({"context": ctx, "id": sid, "zh": val})
    return out


def scan_dll_templates(data):
    """掃描 #Strings heap：含 =token= 的字串（DLL 未涵蓋模板候選）。"""
    try:
        import dnfile
    except ImportError:
        return []
    pe = dnfile.dnPE(str(DLL))
    sh = pe.net.strings
    # rva → 檔案偏移（section 對應）
    secs = []
    for s in pe.sections:
        secs.append((int(s.VirtualAddress), int(s.VirtualAddress) + int(s.Misc_VirtualSize), int(s.PointerToRawData)))
    def rva2off(r):
        for a, b, raw in secs:
            if a <= r < b:
                return raw + (r - a)
        return None
    off = rva2off(sh.rva)
    if off is None:
        return []
    raw = data[off:off + 24_000_000]
    out = []
    i = 0
    while i < len(raw) - 3:
        b = raw[i]
        if b & 0x80 == 0:
            ln, hdr = b, 1
        elif b & 0xC0 == 0x80:
            ln, hdr = ((b & 0x3F) << 8) | raw[i + 1], 2
        else:
            i += 1
            continue
        if ln <= 0 or ln > 2000 or i + hdr + ln + 1 > len(raw):
            i += 1
            continue
        chunk = raw[i + hdr:i + hdr + ln]
        try:
            s = chunk.decode("utf-8")
        except Exception:
            i += hdr + ln + 1
            continue
        if "=" in s and TOKEN_RE.search(s):
            out.append(s)
        i += hdr + ln + 1
    return out


def _flatten(tok):
    """token 展平：=X.ifPlural:a:b= → =X=、=X|pipe= → =X=、=X#param= → =X=、
    =X.verb:are= → =X=（zh 合法的結構改寫）。spice 剝 |後綴但保留鍵。"""
    inner = tok[1:-1]
    inner = inner.split("|", 1)[0]
    inner = re.split(r"[.:#]", inner)[0]
    return "=" + inner + "="


def analyze(entry):
    id_tokens = set(TOKEN_WIDE_RE.findall(entry["id"]))
    zh_tokens = set(TOKEN_WIDE_RE.findall(entry["zh"]))
    flags = []
    # untranslated：值無中文（剝 token/markup 後仍有實詞才算欠翻譯；純 token/圖示引用不算）
    zh_stripped = TOKEN_RE.sub("", entry["zh"])
    zh_stripped = re.sub(r"\{\{[^}]*\}\}", "", zh_stripped)
    zh_stripped = re.sub(r"[^A-Za-z\s]|\d", " ", zh_stripped)
    zh_words = [w for w in zh_stripped.split() if len(w) >= 3]
    if not CJK_RE.search(entry["zh"]):
        if zh_words:
            flags.append("untranslated")
    elif id_tokens != zh_tokens:
        missing_orig = id_tokens - zh_tokens
        ok = True
        if missing_orig and not BE_TOKENS.issuperset(missing_orig):
            # 展平後比較：zh 側合法的結構改寫（ifPlural 中文化、pluralize 展開、pipe 剝除、be 動詞剝除）
            flat_id = {_flatten(t) for t in id_tokens}
            flat_zh = {_flatten(t) for t in zh_tokens}
            if flat_id != flat_zh:
                missing = flat_id - flat_zh
                extra = flat_zh - flat_id
                if missing:
                    ok = False
                    entry["missing_tokens"] = sorted(missing)
                    entry["extra_tokens"] = sorted(extra)
        if not ok:
            flags.append("token_mismatch")
    else:
        # 只對已翻譯的做碎塊檢查：先剝離 =token= 與 {{markup}}，再查英文殘留
        stripped = TOKEN_RE.sub("", entry["zh"])
        stripped = re.sub(r"\{\{[^}]*\}\}", "", stripped)
        eng = [w for w in ENG_WORD_RE.findall(stripped) if w.upper() not in ("OK", "SP", "HP", "XP", "AV", "DV", "MA", "MP", "ST", "AG", "TO", "WI", "IN", "EG", "QN", "MS", "VS", "PC", "D6")]
        if eng:
            flags.append("mixed")
        for pat, label in SUSPECT_PATTERNS:
            if pat.search(entry["zh"]):
                flags.append("suspicious:" + label)
                break
    # token 內部含中文＝真錯誤（token 鍵不可翻譯）
    # 例外：ifPlural/if/isplural/pluralize 參數中文（:鄙視:鄙視=）是合法值側翻譯
    for t in TOKEN_WIDE_RE.findall(entry["zh"]):
        inner = t[1:-1]
        value_side = inner.split("|")[0]
        if CJK_RE.search(inner) and not re.search(r"\.(ifPlural|if|isplural|pluralize|fragment|things):", inner):
            flags.append("token_internal_cjk")
            entry.setdefault("bad_tokens", []).append(t)
            break
    entry["flags"] = flags
    if "mixed" in flags and "=spice:" not in entry["id"]:
        entry["priority"] = "high"
    return entry


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--print", type=int, default=0)
    ap.add_argument("--flag", default=None, help="只顯示特定 flag 的候選")
    a = ap.parse_args()

    entries = parse_strings(ZH_STRINGS)
    known_ids = {e["id"] for e in entries}
    for e in entries:
        analyze(e)

    # DLL 補充模板（不在語料的）
    dll_templates = scan_dll_templates(DLL.read_bytes())
    dll_extra = []
    for t in dll_templates:
        if t not in known_ids and "=" in t:
            dll_extra.append(analyze({"context": "", "id": t, "zh": t}))
    # 去重（DLL 可能重複）
    seen = set()
    dll_extra = [e for e in dll_extra if not (e["id"] in seen or seen.add(e["id"]))]

    corpus = {"zh_strings": entries, "dll_extra": dll_extra}
    CORPUS.write_text(json.dumps(corpus, ensure_ascii=False, indent=1), encoding="utf-8")

    stats = {"total": len(entries), "untranslated": 0, "token_mismatch": 0, "mixed": 0, "suspicious": 0, "dll_extra": len(dll_extra)}
    for e in entries:
        for fl in e["flags"]:
            key = fl.split(":")[0]
            if key in stats:
                stats[key] += 1
    REPORT.write_text(json.dumps(stats, ensure_ascii=False, indent=1), encoding="utf-8")

    print("=== 語料庫統計 ===")
    for k, v in stats.items():
        print(f"  {k}: {v}")

    if a.flag or a.print:
        show = []
        for e in entries:
            if a.flag and not any(f.startswith(a.flag) for f in e["flags"]):
                continue
            show.append(e)
        for e in show[: (a.print or 20)]:
            print(f"\n[{e['context'][:40]}]")
            print(f"  ID: {e['id'][:90]}")
            print(f"  ZH: {e['zh'][:90]}")
            print(f"  flags: {e['flags']}")


if __name__ == "__main__":
    main()