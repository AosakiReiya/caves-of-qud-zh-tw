#!/usr/bin/env python3
"""
translate_pending.py — 翻譯語料庫中「真未翻譯」的模板（pending_untranslated.json）。

管線：讀清單 → 批次送本地 LLM（LM Studio）→ token/標記完整性校驗 → 按 ID 寫回
Strings.zh-tw.xml → 重跑 extract_templates.py 驗證。

用法：
  python3 tools/translate_pending.py --limit 10 --dry-run
  python3 tools/translate_pending.py            # 翻譯全部
"""
import argparse
import json
import re
import sys
import time
from pathlib import Path

import requests

ROOT = Path(__file__).resolve().parent
GHOST = ROOT.parent / "qud-zh-tw" if (ROOT.parent / "qud-zh-tw").exists() else ROOT.parent
ZH_STRINGS = GHOST / "zh-tw" / "Strings.zh-tw.xml"
PENDING = ROOT / "pending_untranslated.json"

API_URL = "http://localhost:1234/api/v1/chat"
MODEL = "gemma-4-26b-a4b-it"

SYSTEM_PROMPT = """你是《Caves of Qud》（卡德洞窟）繁中化的翻譯員。以下是遊戲訊息/生成模板的英中文對照（中國文目前為空待翻）。
任務：將每條 English 翻譯成繁體中文。
嚴格規則（違反任何一條都是錯誤）：
1. 所有 =...= token 與 {{...}} markup、HTML 標籤必須逐字原樣保留，一個都不能少、不能改順序、不能改內容
2. 「中文(English)」括號格式是既定規範（如 卡德(Qud)），專有名詞沿用既有譯名；動態生成模板只翻結構詞、不追加英文括號
3. 生成器詞根（地名/姓名的後綴構詞如 abad、grad）意譯中文，但要保留 =seed|wordRoot|capitalize= 等 token 原樣
4. 僅縮寫/字母標籤（如 A-Z）維持原樣
5. 輸出必須是合法 JSON 陣列，每項格式：{"i": 序號, "zh": "繁體中文翻譯"}"""


def call_llm(user_prompt):
    r = requests.post(API_URL, json={
        "model": MODEL,
        "input": SYSTEM_PROMPT + "\n\n" + user_prompt,
        "temperature": 0.2,
        "max_output_tokens": 6000,
    }, timeout=600)
    r.raise_for_status()
    return r.json()["output"][0]["content"]


def build_prompt(batch):
    out = []
    for i, e in enumerate(batch):
        out.append(f"{i}. Context: {e['context'][:60]}")
        out.append(f"   EN: {e['id']}")
    out.append("\n請輸出 JSON 陣列。")
    return "\n".join(out)


TOKEN_RE = re.compile(r"=[^=\s][^=]{0,80}=")


def verify(item, zh):
    id_tokens = set(TOKEN_RE.findall(item['id']))
    zh_tokens = set(TOKEN_RE.findall(zh))
    if id_tokens != zh_tokens:
        return False, f"token 集不同: 缺 {sorted(id_tokens - zh_tokens)} 多 {sorted(zh_tokens - id_tokens)}"
    for m in re.finditer(r"\{\{[^}]*\}\}", item['id']):
        if m.group(0) not in zh:
            return False, f"markup 缺失: {m.group(0)}"
    if not re.search(r"[\u4e00-\u9fff]", zh):
        return False, "無中文"
    return True, ""


def parse_resp(text, n):
    m = re.search(r"\[.*\]", text, re.S)
    if not m:
        return None
    try:
        data = json.loads(m.group(0))
        if isinstance(data, list):
            out = [None] * n
            for i, it in enumerate(data):
                if isinstance(it, dict) and "zh" in it and i < n:
                    out[i] = it["zh"]
            return out
    except Exception:
        pass
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--batch", type=int, default=10)
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()

    items = json.loads(PENDING.read_text(encoding="utf-8"))
    if a.limit:
        items = items[:a.limit]
    print(f"待翻譯：{len(items)} 條（batch={a.batch}）")

    results = {}
    for start in range(0, len(items), a.batch):
        batch = items[start:start + a.batch]
        if a.dry_run:
            for e in batch:
                print(f"  [{e['context'][:40]}] {e['id'][:70]}")
            continue
        text = call_llm(build_prompt(batch))
        parsed = parse_resp(text, len(batch))
        if parsed is None:
            print(f"  @{start}: 解析失敗，重灌批次…")
            time.sleep(2)
            continue
        for i, e in enumerate(batch):
            zh = parsed[i]
            if not zh:
                print(f"  @{start}+{i} 缺翻譯: {e['context'][:40]}")
                continue
            ok, why = verify(e, zh)
            if ok:
                results[e['id']] = zh
                print(f"  ✓ {e['context'][:44]} → {zh[:48]}")
            else:
                print(f"  ✗ {e['context'][:44]}: {why} | 建議: {zh[:60]}")
        time.sleep(1)

    if a.dry_run:
        return
    print(f"\n通過校驗：{len(results)}/{len(items)}")

    zh_xml = open(GHOST / "zh-tw" / "Strings.zh-tw.xml", encoding="utf-8-sig").read()
    applied = missed = 0
    for eid, zh in results.items():
        m = re.search(r'(<string\s+Context="[^"]*"\s+ID="[^"]*">)([^<]*)(</string>)',
                      zh_xml, re.S)
        # 直接找該 ID 的 <string> 元素
        pat = re.compile(r'(<string\s+Context="[^"]*"\s+ID="' + re.escape(eid) + r'">)([^<]*)(</string>)')
        mm = pat.search(zh_xml)
        if not mm:
            missed += 1
            print(f"  ID 未找到: {eid[:60]}")
            continue
        zh_xml = zh_xml[:mm.start(2)] + zh + zh_xml[mm.end(2):]
        applied += 1
    (GHOST / "zh-tw" / "Strings.zh-tw.xml").write_text(zh_xml, encoding="utf-8")
    print(f"寫回 XML：套用 {applied}，未找到 {missed}")
    print("完成後請執行: python3 tools/extract_templates.py && python3 tools/run_tests.py")


if __name__ == "__main__":
    main()