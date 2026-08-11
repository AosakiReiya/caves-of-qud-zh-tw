#!/usr/bin/env python3
"""
find_untranslated.py — 掃描 zh-tw 骨架的缺漏翻譯（少翻／漏翻／英文專名漏譯）。

靜態三層檢測（不啟動遊戲、不掃遊戲本體資料檔）：

  L1  ▶ 殘留標記    ：未翻譯標記，少翻／漏翻的直接證據。
  L2  英文專名漏譯   ：翻譯內容中殘留的英文專名（需音譯，輸出「中文(English)」）。
                      依「策展表 + proper_nouns.txt + 黑名單」分流，剩餘列為需音譯。
  L3  ID 覆蓋缺漏    ：ExampleLanguage 有、但 zh-tw 沒有的翻譯鍵
                      （<string ID> 與 <object Name> 兩類）。

輸出 tools/untranslated_report.json（結構化，供 expand_glossary.py 等後續工具使用）。

用法：
  python3 tools/find_untranslated.py --print          # 文字報告
  python3 tools/find_untranslated.py --report        # 寫 JSON（預設）
  python3 tools/find_untranslated.py --print --report
"""
import argparse
import glob
import json
import re
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]
SKELETON = PROJECT / "zh-tw"


def find_game_root() -> Path:
    """向上搜尋含 CoQ_Data 的遊戲根目錄。"""
    for p in Path(__file__).resolve().parents:
        if (p / "CoQ_Data").exists():
            return p
    return Path(__file__).resolve().parents[4]


GAME_ROOT = find_game_root()
GAME_EXAMPLE = GAME_ROOT / "CoQ_Data" / "StreamingAssets" / "Base" / "ExampleLanguage"
CURATED = PROJECT / "tools" / "glossary_curated.json"
PROPER_NOUNS = PROJECT / "tools" / "proper_nouns.txt"
DEFAULT_REPORT = PROJECT / "tools" / "untranslated_report.json"

# 黑名單：製作人員／公司／UI 標籤等「不應音譯」的英文 ─ 掃 L2 時跳過
# （部分沿用 fix_english.py 的 SKIP_PATTERNS）
BLACKLIST = {
    "Brian Bucklew", "Jason Grinblat", "Nick Decapua", "Corey Frang", "Craig Hamilton",
    "Autumn McDonell", "Bastia Rosen", "Caelyn Sandel", "Brandon Tanner", "Samuel Wilson",
    "Polat Yarisci", "A Shell in the Pit", "Thaumatic Systems", "Brian Reynolds",
    "Joshua Southerland", "Joshua Buckley", "Qingyao Sun", "James Canter", "Mike Sipior",
    "Kelly Joyce", "Cleo Lamb", "Shane Allcroft", "Aidan Page", "Reuben Eadon",
    "Earthly Delights Ogdo", "John Szevin", "Devon Cupo", "Big Simple", "Kevin Orren",
    "Lennart Schefe", "Beemancer", "Drew Kerrigan", "Jordan Wood", "Kasey Patrick Morgan",
    "Callum Love", "Petrichor", "Leo Durante", "Andrew Smash", "Brian Turcotte",
    "Jim Shepard", "Nic Gard", "Trevor Clack", "Gillian Eggleston", "Ivy Sly",
    "Andreas Pardeike", "Asunaro", "Byczko", "Brokilon", "Callahan", "Capuchin",
    "Coal", "Colley", "Goldkin", "Rarden", "Ray", "Wafflecopter", "Nomikos", "Magitek",
    "Vastin", "Frumple", "Sappho", "Soulwynd", "Cpbpunch", "Tarran", "Oryx",
    "Display", "Values", "Display Text", "Attribute Point",
    # UI／統計標籤（非可音譯專名）
    "Text Builder", "Page Left", "Page Right", "Double Triple", "Display Values",
    "Special Note", "Specal", "AV DV Heat Resistance Cold Resistance",
    "AV AV AV AV AV AV AV", "DV DV DV DV DV DV DV", "MA MA MA MA MA MA MA",
    "QN QN", "MS MS", "AR AR", "CR CR", "ER ER", "HR HR", "HP HP", "AP AP",
}

# L1／L3 都跳過的檔案（程序化命名模板，由 replacer 處理，不附英文）
SKIP_L3_FILES = {"Naming"}


# ---------- 工具 ----------

def _text_only_block(m: re.Match, text: str) -> str:
    """回傳 <string>/<object> 區塊的內容，剝除標籤。"""
    return m.group(0)


def strip_markup(text: str) -> str:
    """剝除影響英文專名判定的一切語法：標籤、色彩標記、placeholder、實體。

    色彩標記 {{X|content}}：只移除 {{X| 與 }}，**保留 content**（content 可能是
    英文專名/屬性名，如 {{W|Dodge Value (DV)}}，不能整段剝離否則漏偵測）。
    """
    text = re.sub(r"<[^>]*>", " ", text)              # 標籤
    text = re.sub(r"\{\{[^}|]*\|", " ", text)          # 移除 {{X| 開頭（含色碼字母）
    text = text.replace("}}", " ")                      # 移除尾巴 }}
    text = re.sub(r"=[A-Za-z0-9_.:;|!@/()+\-]+=", " ", text)  # placeholder
    text = re.sub(r"&#xA;|&#xD;|&amp;|&lt;|&gt;|&quot;|&#39;", " ", text)
    return text


# 英文專名：大寫開頭、2+ 詞的片語（含內嵌小寫介詞 a/the/of）
PROPER_PHRASE = re.compile(
    r"\b([A-Z][a-zA-Z]+(?:(?:\s+(?:of|the|a)?\s*[A-Z][a-zA-Z]+)+))\b"
)

# 單字專名：大寫開頭、4+ 字、出現在含中文的內容中（補抓 Carbide 類漏譯）
# 排除「中文(English)」混合格式中的 (English) 註解（那是刻意保留，非缺漏）
SINGLE_WORD = re.compile(r"(?<!\()\b[A-Z][a-z]{3,}\b(?!\))")
# 常見英文詞（避免把一般詞誤判為專名）
COMMON_EN = {
    "The", "This", "That", "These", "Those", "You", "Your", "They", "Their", "Theirs",
    "What", "When", "Where", "Which", "While", "With", "Without", "From", "About",
    "After", "Before", "Between", "During", "Through", "Under", "Over", "Into",
    "Than", "Then", "There", "Here", "Will", "Would", "Could", "Should", "Have",
    "Has", "Been", "Being", "Also", "Only", "Even", "Still", "Yet", "Just", "Very",
    "Much", "Many", "More", "Most", "Some", "Any", "Each", "Every", "Both", "All",
    "One", "Two", "New", "Old", "Great", "Small", "Large", "High", "Low", "Free",
    "Full", "Open", "Close", "Start", "Stop", "Back", "Next", "Continue", "Save",
    "Exit", "Quit", "Menu", "Options", "Help", "Info", "Cancel", "Apply", "Reset",
    "Clear", "Select", "Change", "Mode", "Level", "Type", "Name", "Item", "Weapon",
    "Armor", "Skill", "Attack", "Damage", "Speed", "Total", "Bonus", "Chance",
    "Range", "Base", "Main", "Other", "Left", "Right", "Front", "Behind", "Never",
    "Always", "Sometimes", "Often", "Rarely", "Normal", "Special", "Common",
    "Rare", "Epic", "Legendary", "Ancient", "Damage", "Resistance", "Attribute",
}


def load_json(path: Path) -> dict:
    if not path.exists():
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return {}


def load_proper_nouns(path: Path) -> set:
    if not path.exists():
        return set()
    return {ln.strip() for ln in path.read_text(encoding="utf-8").splitlines() if ln.strip()}


# ---------- L1：▶ 殘留 ----------

def scan_l1() -> dict:
    """回傳 {檔名: 未翻譯標記數}。"""
    result = {}
    total = 0
    for f in sorted(SKELETON.glob("*.xml")):
        n = f.read_text(encoding="utf-8-sig").count("▶")
        if n:
            result[f.name] = n
        total += n
    return {"by_file": result, "total": total}


# ---------- L2：英文專名漏譯 ----------

def scan_l2(curated: dict, proper_nouns: set) -> dict:
    """掃翻譯內容殘留英文專名，依已知名單分流。"""
    known = {k.lower() for k in curated if not k.startswith("_")}
    known |= {p.lower() for p in proper_nouns}
    black = {b.lower() for b in BLACKLIST}
    found: dict[str, dict] = {}  # 專名 -> {files:set, count}
    for f in sorted(SKELETON.glob("*.xml")):
        if "Naming" in f.name:
            continue
        text = strip_markup(f.read_text(encoding="utf-8-sig"))
        for m in PROPER_PHRASE.finditer(text):
            phrase = re.sub(r"\s+", " ", m.group(1)).strip()
            low = phrase.lower()
            if low in known or low in black:
                continue
            if len(phrase) < 5:
                continue
            # 8 詞以上多半是製作人員名單／長串公司名，非單一專名
            if len(phrase.split()) > 8:
                continue
            entry = found.setdefault(phrase, {"count": 0, "files": set()})
            entry["count"] += 1
            entry["files"].add(f.name)
        # 單字專名：只抓「該行同時含中文」者（靜態翻譯中殘留的英文單字，如 Carbide）
        if not re.search(r"[\u4e00-\u9fff]", text):
            continue
        for m2 in SINGLE_WORD.finditer(text):
            w = m2.group(0)
            if w in COMMON_EN or w.lower() in known or w.lower() in black:
                continue
            # 4+ 字且不在常見詞內才列
            entry = found.setdefault(w, {"count": 0, "files": set()})
            entry["count"] += 1
            entry["files"].add(f.name)
    # 後過濾：只出現在 Books + Manual 的英文多是製作人員名單（應保留英文），剔除
    credits_files = {"Books.zh-tw.xml", "Manual.zh-tw.xml"}
    filtered = {}
    for phrase, info in found.items():
        if info["files"] <= credits_files:
            continue
        filtered[phrase] = info
    return filtered


# ---------- L3：ID 覆蓋缺漏 ----------

def _norm_id(s: str) -> str:
    """正規化翻譯鍵：換行實體（&#xA;/&#10;/&#xD;/&#13;）統一後收斂空白。"""
    s = re.sub(r"&#(?:xA|10|13|xD);", " ", s, flags=re.IGNORECASE)
    return re.sub(r"\s+", " ", s).strip()


def _collect_keys(text: str) -> tuple[set[str], set[str]]:
    """回傳 (string ID 集合, object Name 集合)，鍵已正規化空白。"""
    string_ids = {_norm_id(m) for m in re.findall(r'<string\b[^>]*?ID="([^"]*)"', text)}
    object_names = {_norm_id(m) for m in re.findall(r'<object\b[^>]*?Name="([^"]*)"', text)}
    return string_ids, object_names


def scan_l3() -> dict:
    """比對 example 與 zh-tw 的翻譯鍵，回傳缺漏清單。"""
    missing: dict[str, dict] = {}
    ex_dir = GAME_EXAMPLE
    for ex in sorted(ex_dir.glob("*.xml")):
        base = ex.name.replace(".example.xml", "")
        if any(s in base for s in SKIP_L3_FILES):
            continue
        zh_f = SKELETON / f"{base}.zh-tw.xml"
        if not zh_f.exists():
            missing[ex.name] = {"reason": "缺少對應 zh-tw 檔", "ids": [], "objects": []}
            continue
        ex_sids, ex_oids = _collect_keys(ex.read_text(encoding="utf-8-sig"))
        zh_sids, zh_oids = _collect_keys(zh_f.read_text(encoding="utf-8-sig"))
        miss_s = ex_sids - zh_sids
        miss_o = ex_oids - zh_oids
        if miss_s or miss_o:
            missing[ex.name] = {
                "missing_string_ids": sorted(miss_s),
                "missing_object_names": sorted(miss_o),
            }
    return missing


def example_dir_resolved() -> Path:
    if GAME_EXAMPLE.exists():
        return GAME_EXAMPLE
    # 退而求其次：從 repo 內找（若使用者把 example 放在別處）
    for cand in (PROJECT / "example", PROJECT / "tools" / "example"):
        if cand.exists():
            return cand
    return GAME_EXAMPLE


# ---------- 主流程 ----------

def build_report() -> dict:
    curated = load_json(CURATED)
    proper = load_proper_nouns(PROPER_NOUNS)
    l1 = scan_l1()
    l2 = scan_l2(curated, proper)
    l3 = scan_l3()
    l2 = {k: {"count": v["count"], "files": sorted(v["files"])} for k, v in l2.items()}
    return {
        "meta": {
            "skeleton": str(SKELETON),
            "example": str(example_dir_resolved()),
            "rules": "固定專名→中文(English)；動態命名模板→純中文；一般詞→純中文",
        },
        "L1_markers": l1,
        "L2_proper_nouns": l2,
        "L3_coverage": l3,
    }


def print_report(report: dict) -> None:
    l1 = report["L1_markers"]
    l2 = report["L2_proper_nouns"]
    l3 = report["L3_coverage"]

    print("=" * 60)
    print("L1  ▶ 殘留標記（少翻／漏翻）")
    print("=" * 60)
    if l1["by_file"]:
        for name, n in sorted(l1["by_file"].items(), key=lambda x: -x[1]):
            print(f"  {n:>6}  {name}")
    else:
        print("  （無）")
    print(f"  小計：{l1['total']}")

    print()
    print("=" * 60)
    print(f"L2  英文專名漏譯（需音譯，輸出 中文(English)）  {len(l2)} 種")
    print("=" * 60)
    for phrase, info in sorted(l2.items(), key=lambda x: -x[1]["count"])[:60]:
        files = ",".join(sorted(info["files"]))
        print(f"  {phrase:<34} ×{info['count']:<4}  <- {files}")

    print()
    print("=" * 60)
    print("L3  ID 覆蓋缺漏（example 有、zh-tw 沒有）")
    print("=" * 60)
    if not l3:
        print("  （無缺漏）")
    for fname, info in l3.items():
        print(f"  [{fname}]")
        if "reason" in info:
            print(f"      {info['reason']}")
            continue
        for o in info["missing_object_names"][:15]:
            print(f"      object: {o}")
        for s in info["missing_string_ids"][:15]:
            print(f"      string: {s}")
        if len(info.get("missing_object_names", [])) + len(info.get("missing_string_ids", [])) > 30:
            print(f"      …（共 {len(info['missing_object_names']) + len(info['missing_string_ids'])} 鍵）")
    print()


def main() -> None:
    ap = argparse.ArgumentParser(description="掃描 zh-tw 骨架缺漏翻譯")
    ap.add_argument("--print", action="store_true", help="輸出文字報告")
    ap.add_argument("--report", default=str(DEFAULT_REPORT), help="JSON 報表路徑（寫入）")
    ap.add_argument("--no-report", action="store_true", help="不寫 JSON")
    args = ap.parse_args()

    report = build_report()
    if not args.no_report:
        out = Path(args.report)
        out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"報表已寫入 {out}")
    if args.print or args.no_report:
        print_report(report)


if __name__ == "__main__":
    main()