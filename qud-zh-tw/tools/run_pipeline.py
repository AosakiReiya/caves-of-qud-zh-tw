#!/usr/bin/env python3
"""
run_pipeline.py — 離線「提取 → 翻譯 → 回填 → 驗證」一鍵管線。

場景：
  extract   離線提取遊戲文本缺口（dll 硬編碼 UI 標籤 → ui_labels.json）
  translate 用本機 LLM（LM Studio / llama.cpp，localhost:1234）批量翻譯
  backfill  自動回填字典（Words/PhraseLeaks）
  verify    跑 run_tests 全量驗證（含紅線測試）
  all       一鍵全跑（預設）

不掛遊戲 hook、不影響遊戲性能；dll 字串由腳本直接讀取，模型與
translate_batch.py 同協議（不改動其配置）。

用法：
  python3 tools/run_pipeline.py               # all
  python3 tools/run_pipeline.py translate     # 只翻譯
  python3 tools/run_pipeline.py translate --limit 200
"""
import argparse
import json
import re
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

import requests

ROOT = Path(__file__).resolve().parent
PROJ = ROOT.parent
UI_LABELS = ROOT / "ui_labels.json"
TRANSLATED = ROOT / "translated_ui.json"
TRANSLATED_MSG = ROOT / "translated_msg.json"

API_URL = "http://localhost:1234/api/v1/chat"
MODEL = "gemma-4-26b-a4b-it"

SYS_PROMPT = (
    "You are a professional translator for the video game Caves of Qud, "
    "translating English UI labels/tooltips into Traditional Chinese (zh-TW). "
    "Rules: "
    "1. UI label terms: keep them short and natural, e.g. 'Previous Ability' → '上一個能力', "
    "'Use Ability' → '使用能力'. "
    "2. Preserve placeholders exactly ({0}, =name=, ~CmdUse). "
    "3. Proper nouns (Barathrum, Mopango, Snapjaw...) → transliterate zh-TW plus append English "
    "in parentheses, e.g. 'Barathrum' → '巴拉楚姆(Barathrum)'. "
    "4. Use Taiwan Traditional Chinese and full-width punctuation. "
    "Output ONLY the translation, nothing else."
)


def _call(text, temperature=0.2, timeout=180):
    payload = {
        "model": MODEL,
        "system_prompt": SYS_PROMPT,
        "input": text,
        "temperature": temperature,
        "max_output_tokens": 2048,
    }
    r = requests.post(API_URL, json=payload, timeout=timeout)
    r.raise_for_status()
    data = r.json()
    out = data.get("output")
    if not out or not isinstance(out, list) or not out[0].get("content"):
        raise ValueError("回應結構異常")
    return out[0]["content"].strip()


def extract_step():
    print("== extract ==")
    r = subprocess.run([sys.executable, str(ROOT / "extract_dll_strings.py")], capture_output=True, text=True)
    print(r.stdout[-800:])
    if r.returncode != 0:
        print(r.stderr[-500:])
        sys.exit(1)


def translate_step(limit, src_name=None):
    src = ROOT / (src_name or "ui_labels.json")
    if not src.exists():
        print(f"{src.name} 不存在，先跑 extract")
        sys.exit(1)
    items = json.loads(src.read_text(encoding="utf-8"))
    pending = items[:limit] if limit else items
    print(f"== translate ==（{len(pending)} 條 → {MODEL}）")

    out_path = TRANSLATED_MSG if src.name.startswith("msg") else TRANSLATED
    done = {}
    if out_path.exists():
        try:
            done = {x["text"]: x for x in json.loads(out_path.read_text(encoding="utf-8"))}
        except Exception:
            done = {}

    def work(item):
        text = item["text"]
        if text in done:
            return done[text]
        try:
            zh = _call(text)
            return {"text": text, "zh": zh}
        except Exception as e:
            print(f"  [失敗] {text[:50]!r}: {type(e).__name__} {str(e)[:80]}")
            return {"text": text, "zh": None}

    results = []
    with ThreadPoolExecutor(max_workers=4) as ex:
        futs = {ex.submit(work, it): it for it in pending}
        for f in as_completed(futs):
            results.append(f.result())
    merged = dict(done)
    for x in results:
        merged[x["text"]] = x
    out = sorted(merged.values(), key=lambda x: x["text"])
    out_path.write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    ok = sum(1 for x in out if x.get("zh"))
    print(f"翻譯完成：{ok}/{len(out)} → {out_path}")


def backfill_step():
    print("== backfill ==")
    r = subprocess.run([sys.executable, str(ROOT / "backfill.py")], capture_output=True, text=True)
    print(r.stdout[-600:])
    if r.returncode != 0:
        print(r.stderr[-400:])
    r2 = subprocess.run([sys.executable, str(ROOT / "backfill_sentences.py")], capture_output=True, text=True)
    print(r2.stdout[-600:])
    if r2.returncode != 0:
        print(r2.stderr[-400:])


def verify_step():
    print("== verify ==")
    r = subprocess.run([sys.executable, str(ROOT / "run_tests.py")], capture_output=True, text=True)
    print(r.stdout[-900:])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("step", nargs="?", default="all",
                    choices=["all", "extract", "translate", "backfill", "verify"])
    ap.add_argument("--limit", type=int, default=None)
    ap.add_argument("--src", default=None, help="待譯來源 json 檔名（預設 ui_labels.json；msg_templates.json → translated_msg.json）")
    a = ap.parse_args()

    if a.step in ("all", "extract"):
        extract_step()
    if a.step in ("all", "translate"):
        translate_step(a.limit, a.src)
    if a.step in ("all", "backfill"):
        backfill_step()
    if a.step in ("all", "verify"):
        verify_step()


if __name__ == "__main__":
    main()