#!/usr/bin/env python3
"""
audit_templates.py — 批次送本地 LLM（LM Studio）審核語料庫候選，輸出修正建議。

候選來源：templates_corpus.json（extract_templates.py 產出）
  - 預設：priority=high（非 spice 的中英混雜）
  - --all-mixed：全部 mixed
  - --untranslated：未翻譯的（生成翻譯建議）
  - --flag token_mismatch：token 缺失的

輸出：tools/audit_suggestions.json — [{"context","id","zh","suggestion","reason"}]

規則（寫入 prompt）：=token= 與 {{markup}} 原樣保留；只修中文語病；無需修正輸出 null。

用法：
  python3 tools/audit_templates.py --limit 50            # 審核前 50 條 high-priority
  python3 tools/audit_templates.py --untranslated --limit 30
  python3 tools/audit_templates.py --apply               # 套用審核建議到 Strings.zh-tw.xml
"""
import argparse
import json
import re
import sys
import time
from pathlib import Path

import requests

ROOT = Path(__file__).resolve().parent
ZH = ROOT.parent / "zh-tw"
CORPUS = ROOT / "templates_corpus.json"
OUT = ROOT / "audit_suggestions.json"
ZH_STRINGS = ZH / "Strings.zh-tw.xml"

API_URL = "http://localhost:1234/api/v1/chat"
MODEL = "gemma-4-26b-a4b-it"

SYSTEM_PROMPT = """你是《Caves of Qud》（卡德洞窟）繁中化的翻譯審校。以下是遊戲訊息模板的英中文對照。
任務：檢查中文翻譯的語病（中英混雜殘留、重複詞、不自然語序、漏字、多餘空格）。
嚴格規則（違反任何一條都是錯誤）：
1. 所有 =...= token 與 {{...}} markup 必須逐字原樣保留，一個都不能少、不能改順序、不能改內容
2. 「中文(English)」括號格式是既定規範（如 卡德(Qud)、巴拉楚姆(Barathrum)），絕不能移除括號內的英文
3. 專有名詞（生物/物品/地點/技能名）採用既有翻譯，不重新發明
4. 只修正中文部分的語病（重複詞、多餘空格、不自然語序、殘留英文）；若中文已自然流暢，輸出 null
5. 輸出必須是合法 JSON 陣列，每項格式：{"i": 序號, "suggestion": "修正後中文或 null", "reason": "一句話原因"}"""


def load_corpus():
    return json.loads(CORPUS.read_text(encoding="utf-8"))


def pick_candidates(corpus, flag, untranslated, limit):
    entries = corpus["zh_strings"]
    cands = []
    for e in entries:
        fs = e["flags"]
        if untranslated and "untranslated" in fs:
            cands.append(e)
            continue
        if flag and any(f.startswith(flag) for f in fs):
            cands.append(e)
            continue
        if e.get("priority") == "high":
            cands.append(e)
    return cands[:limit]


def call_llm(user_prompt):
    # LM Studio 新版 API：input 純字串（system 規則合併進 prompt）
    r = requests.post(API_URL, json={
        "model": MODEL,
        "input": SYSTEM_PROMPT + "\n\n" + user_prompt,
        "temperature": 0.1,
        "max_output_tokens": 6000,
    }, timeout=600)
    r.raise_for_status()
    return r.json()["output"][0]["content"]


def build_user_prompt(batch):
    lines = []
    for i, e in enumerate(batch):
        lines.append(f"{i}. Context: {e['context'][:50]}")
        lines.append(f"   EN: {e['id'][:200]}")
        lines.append(f"   ZH: {e['zh'][:200]}")
    lines.append("\n請輸出 JSON 陣列（每項 i 對應上面序號）。")
    return "\n".join(lines)


def parse_response(text):
    text = text.strip()
    m = re.search(r"\[.*\]", text, re.S)
    if not m:
        return []
    try:
        return json.loads(m.group(0))
    except Exception:
        return []


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=50)
    ap.add_argument("--batch", type=int, default=20)
    ap.add_argument("--flag", default=None)
    ap.add_argument("--untranslated", action="store_true")
    ap.add_argument("--print-input", action="store_true", help="只印 prompt 不送 LLM")
    ap.add_argument("--apply", action="store_true", help="套用審核建議到 Strings.zh-tw.xml")
    a = ap.parse_args()

    if a.apply:
        return apply_suggestions()

    corpus = load_corpus()
    cands = pick_candidates(corpus, a.flag, a.untranslated, a.limit)
    print(f"候選：{len(cands)} 條（batch={a.batch}）")

    suggestions = []
    for start in range(0, len(cands), a.batch):
        batch = cands[start:start + a.batch]
        prompt = build_user_prompt(batch)
        if a.print_input:
            print(prompt)
            continue
        try:
            resp = call_llm(prompt)
        except Exception as e:
            print(f"LLM 失敗 @{start}: {e}")
            break
        parsed = parse_response(resp)
        for item in parsed:
            i = item.get("i")
            if i is None or i >= len(batch):
                continue
            e = batch[i]
            suggestions.append({
                "context": e["context"],
                "id": e["id"],
                "zh": e["zh"],
                "suggestion": item.get("suggestion"),
                "reason": item.get("reason", ""),
            })
        print(f"  批次 {start // a.batch + 1} 完成（{len(parsed)} 建議）")
        time.sleep(1)

    OUT.write_text(json.dumps(suggestions, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"已寫入 {OUT}（{len(suggestions)} 條建議）")


def apply_suggestions():
    """套用審核建議：suggestion 非 null 的逐條替換 zh（值精確匹配）。"""
    if not OUT.exists():
        print("無建議檔")
        return
    sugg = json.loads(OUT.read_text(encoding="utf-8"))
    path = ZH_STRINGS
    s = path.read_text(encoding="utf-8-sig")
    applied = skipped = 0
    for item in sugg:
        if not item.get("suggestion"):
            continue
        # 保守：跳過「移除英文/括號」類建議（=token= 完整性與專名格式優先）
        reason = item.get("reason", "")
        if re.search(r"移除|刪除", reason) and re.search(r"括號|英文", reason):
            print(f"  跳過（破壞專名格式）：{item['context'][:40]} | {reason[:50]}")
            skipped += 1
            continue
        old, new = item["zh"], item["suggestion"]
        if old not in s:
            print(f"  跳過（值未找到）：{item['context'][:40]} | {old[:50]}")
            skipped += 1
            continue
        s = s.replace(old, new, 1)
        applied += 1
    path.write_text(s, encoding="utf-8")
    print(f"套用 {applied} 條，跳過 {skipped} 條 → {path}")


if __name__ == "__main__":
    main()