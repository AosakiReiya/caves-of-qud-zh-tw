#!/usr/bin/env python3
"""
classify_items_parens.py — LLM 分類 Items 純中文 DisplayName：
「重要/可辨識物品 → 附 (English) 括號」 vs 「日常通用（貓/鳥/水 → 保持」。
輸出 tools/items_paren_suggestions.json：[{"zh","enkey","action":"keep|add","suggest":..., "reason":...}]
"""
import json
import re
from pathlib import Path

import requests

ROOT = Path(__file__).resolve().parent
API_URL = "http://localhost:1234/api/v1/chat"
MODEL = "gemma-4-26b-a4b-it"

SYSTEM = """你是《Caves of Qud》繁中化的譯名審校。以下是物品的中文顯示名與對應英文名稱。
任務：判斷每個物品是否應附英文括號「中文(English)」。
規則：
1. 重要/可辨識的實體物品（武器、工具、零件、神器、科技品、容器、裝備、原物料、可識別動植物）→ 建議「中文(English)」
2. 純日常通用字（水、門、石頭、食物、普通自然物、普通動物如貓/鳥/魚）→ 保持不加括號
3. 後綴型生成名（=X= 模板、簡單性質+名詞的組合）可不加
4. 輸出 JSON 陣列：[{"i":序號,"action":"add"|"keep","suggest":"中文(English) 或 null","reason":"一句話"}]"""


def call(prompt):
    r = requests.post(API_URL, json={
        "model": MODEL,
        "input": SYSTEM + "\n\n" + prompt,
        "temperature": 0.1,
        "max_output_tokens": 8000,
    }, timeout=600)
    r.raise_for_status()
    return r.json()["output"][0]["content"]


def main():
    items = json.loads((ROOT / "items_paren_candidates.json").read_text(encoding="utf-8"))
    print(f"共 {len(items)} 條")
    results = {}
    B = 30
    for start in range(0, len(items), B):
        batch = items[start:start + B]
        lines = "\n".join(f"{i}. EN={e['enkey']} ZH={e['zh']}" for i, e in enumerate(batch))
        resp = call(lines + "\n輸出 JSON 陣列。")
        m = re.search(r"\[.*\]", resp, re.S)
        if not m:
            print(f"@{start} 解析失敗"); continue
        try:
            data = json.loads(m.group(0))
        except Exception as e:
            print(f"@{start} JSON 錯誤 {e}"); continue
        for it in data:
            i = it.get("i")
            if i is None or i >= len(batch):
                continue
            e = batch[i]
            key = e["zh"]
            results[key] = {
                "zh": e["zh"], "enkey": e["enkey"],
                "action": it.get("action"), "suggest": it.get("suggest"),
                "reason": it.get("reason", ""),
            }
        print(f"  批次 {start//B+1} 完成")
    out = ROOT / "items_paren_suggestions.json"
    out.write_text(json.dumps(list(results.values()), ensure_ascii=False, indent=1), encoding="utf-8")
    adds = [v for v in results.values() if v.get("action") == "add"]
    keeps = [v for v in results.values() if v.get("action") == "keep"]
    print(f"完成: add={len(adds)} keep={len(keeps)} → {out}")


if __name__ == "__main__":
    main()