#!/usr/bin/env python3
"""
expand_glossary.py — 為缺漏英文專名生成「中文(English)」音譯建議，供人工審核後合併。

資料來源：tools/untranslated_report.json 的 L2_proper_nouns（find_untranslated.py 產出）。
用改進後的 SINGLE_SYSTEM prompt 對每個專名逐條翻譯（只翻專名，量小、可控）。

輸出 tools/glossary_proposed.json：
  {
    "英文專名": {"zh": "建議譯名(English)", "files": ["...xml", ...], "count": N},
    "_說明": "..."
  }

此工具**不自動改寫** glossary_curated.json；審核後用 --merge 才會合併（仍會備份）。

用法：
  python3 tools/expand_glossary.py --dry-run        # 只列出將處理的專名
  python3 tools/expand_glossary.py --limit 20       # 只處理前 20 個
  python3 tools/expand_glossary.py --merge          # 審核後合併進策展表（需先審核 proposed）
"""
import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from translate_batch import SINGLE_SYSTEM, call_api, API_URL, MODEL

PROJECT = Path(__file__).resolve().parents[1]
REPORT = PROJECT / "tools" / "untranslated_report.json"
CURATED = PROJECT / "tools" / "glossary_curated.json"
PROPOSED = PROJECT / "tools" / "glossary_proposed.json"

# 翻譯結果必須含中文 + 附 (英文)，否則視為失敗
_VALID = re.compile(r"[\u4e00-\u9fff]")


def load_proper_nouns(report_path: Path) -> dict[str, dict]:
    """從 L2 報表讀專名清單：{專名: {files, count}}。"""
    if not report_path.exists():
        raise SystemExit(f"找不到報表 {report_path}，請先跑 tools/find_untranslated.py")
    data = json.loads(report_path.read_text(encoding="utf-8"))
    l2 = data.get("L2_proper_nouns", {})
    return {k: {"files": v.get("files", []), "count": v.get("count", 1)} for k, v in l2.items()}


def transliterate(name: str, url: str, model: str, temperature: float, timeout: int, retries: int) -> str | None:
    """對單一專名生成「中文(English)」。"""
    prompt = (
        f"Transliterate this proper noun from Caves of Qud into Traditional Chinese.\n"
        f"Output ONLY the result in the exact format 中文(English): keep the English in "
        f"parentheses, e.g. \"Barathrum\" → \"巴拉楚姆(Barathrum)\".\n"
        f"Proper noun: {name}"
    )
    last_err = None
    for _ in range(retries + 1):
        try:
            raw = call_api(url, model, SINGLE_SYSTEM(), prompt, temperature, timeout).strip()
            raw = re.sub(r"^```(?:json)?\s*", "", raw)
            raw = re.sub(r"\s*```$", "", raw)
            if not _VALID.search(raw):
                raise ValueError("結果不含中文")
            if "(" not in raw or ")" not in raw:
                raise ValueError("結果未附 (English)")
            zh = raw.split("(", 1)[0].strip()
            en = raw.split("(", 1)[1].split(")", 1)[0].strip()
            if not zh or not en:
                raise ValueError("中文或英文為空")
            return f"{zh}({en})"
        except Exception as e:
            last_err = e
    print(f"  [失敗] {name!r}: {last_err}")
    return None


def main() -> None:
    ap = argparse.ArgumentParser(description="為缺漏英文專名生成音譯建議")
    ap.add_argument("--report", default=str(REPORT))
    ap.add_argument("--dry-run", action="store_true", help="只列出專名，不呼叫模型")
    ap.add_argument("--limit", type=int, default=0, help="只處理前 N 個（0=全部）")
    ap.add_argument("--url", default=API_URL)
    ap.add_argument("--model", default=MODEL)
    ap.add_argument("--temperature", type=float, default=0.2)
    ap.add_argument("--timeout", type=int, default=120)
    ap.add_argument("--retries", type=int, default=2)
    ap.add_argument("--merge", action="store_true", help="把 proposed 合併進策展表（會先備份）")
    args = ap.parse_args()

    if args.merge:
        if not PROPOSED.exists():
            raise SystemExit(f"沒有 {PROPOSED} 可合併。先跑本工具（不帶 --merge）生成建議。")
        proposed = json.loads(PROPOSED.read_text(encoding="utf-8"))
        curated = json.loads(CURATED.read_text(encoding="utf-8")) if CURATED.exists() else {}
        curated.pop("_說明", None)
        added = 0
        for en, info in proposed.items():
            if en.startswith("_"):
                continue
            zh = info.get("zh") if isinstance(info, dict) else info
            if not zh or en in curated:
                continue
            curated[en] = zh
            added += 1
        # 備份舊表
        backup = CURATED.with_suffix(".json.bak")
        if CURATED.exists():
            backup.write_text(CURATED.read_text(encoding="utf-8"), encoding="utf-8")
        CURATED.write_text(json.dumps(curated, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"已合併 {added} 條至 {CURATED}（舊表備份於 {backup.name}）。")
        return

    nouns = load_proper_nouns(Path(args.report))
    if not nouns:
        raise SystemExit("L2 沒有待處理專名（可能已全部處理或報表為空）。")
    print(f"待處理專名：{len(nouns)} 個。")

    items = list(nouns.items())
    if args.limit:
        items = items[: args.limit]

    if args.dry_run:
        for name, info in items:
            files = ",".join(info["files"])
            print(f"  {name:<32} ×{info['count']:<3} <- {files}")
        print(f"（dry-run：共 {len(items)} 個）")
        return

    output: dict = {}
    for name, info in items:
        zh = transliterate(name, args.url, args.model, args.temperature, args.timeout, args.retries)
        if zh:
            output[name] = {"zh": zh, "files": info["files"], "count": info["count"]}
            print(f"  ✓ {name:<32} → {zh}")
        else:
            output[name] = {"zh": None, "files": info["files"], "count": info["count"]}

    output["_說明"] = (
        "find_untranslated.py 掃出的缺漏英文專名音譯建議。審核後用 "
        "python3 tools/expand_glossary.py --merge 合併進 glossary_curated.json。"
    )
    PROPOSED.write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n已寫入 {PROPOSED}。審核後執行 --merge 合併。")


if __name__ == "__main__":
    main()