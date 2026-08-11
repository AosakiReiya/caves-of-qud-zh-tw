#!/usr/bin/env python3
"""
translate_spice.py — 翻譯 HistorySpice.jsonc 模板，產出語言覆寫檔。

遊戲 HistoricSpice 會 merge mod 提供的 `historyspice.*.json`（含 lang 欄位匹配
目前語言）。本工具把英文 spice 模板翻譯成中文，保留所有 `=spice:`/`=^:`/`=var=`
等引用，產出 `historyspice.zh-tw.json`（{"lang":"zh-tw","spice":{...}}）。

用法：
  python3 tools/translate_spice.py --dry-run        # 列出將翻譯的模板數
  python3 tools/translate_spice.py                  # 背景/前台翻譯全部
  python3 tools/translate_spice.py --limit 200      # 只翻前 200 個
"""
import argparse
import json
import re
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from translate_batch import call_api, SINGLE_SYSTEM, API_URL, MODEL

GAME = Path(__file__).resolve().parents[1]
for _ in range(4):
    if (GAME / "CoQ_Data").exists():
        break
    GAME = GAME.parent
SPICE = GAME / "CoQ_Data" / "StreamingAssets" / "Base" / "HistorySpice.jsonc"
OUT = Path(__file__).resolve().parents[1] / "historyspice.zh-tw.json"
TMP = Path(__file__).resolve().parents[1] / "tools" / "spice_progress.json"

# 所有 spice/var 引用：=...=（含 ^、$、@、!、:、. 等）
SPICE_REF = re.compile(r"=[A-Za-z0-9_.:;|!@/()+\-#'\$\^\[\]]+=")
CJK = re.compile(r"[\u4e00-\u9fff]")
# 需要翻譯的門檻：含英文字母且非純引用
NEEDS = re.compile(r"[A-Za-z]{2,}")
# 排除：純引用、emote/音效、純識別字（camelCase 無空格）
EMOTE = re.compile(r"\{\{emote\|")


def is_genuine_leak(s: str) -> bool:
    """真實漏翻：剝掉所有引用後仍有實英文、無中文、非 emote、非 camelCase。"""
    if not s or not NEEDS.search(s):
        return False
    if CJK.search(s):
        return False
    if EMOTE.search(s):
        return False
    stripped = SPICE_REF.sub("", s).replace(" ", "").replace(",", "").replace(".", "")
    if not NEEDS.search(stripped):
        return False  # 全是引用，無實文字
    if " " not in stripped and re.search(r"[a-z][A-Z]", stripped):
        return False  # camelCase 識別字
    return True


def fix_overrides(args) -> None:
    """重譯覆寫檔中仍為英文的真實漏翻項（--fix）。"""
    if not OUT.exists():
        raise SystemExit(f"找不到 {OUT}，先跑正常翻譯。")
    zh = json.loads(OUT.read_text(encoding="utf-8"))
    leaked: dict[str, list] = {}
    paths = []

    def walk(o, path):
        if isinstance(o, dict):
            for k, v in o.items():
                walk(v, path + [k])
        elif isinstance(o, list):
            for i, v in enumerate(o):
                walk(v, path + [i])
        elif isinstance(o, str):
            if is_genuine_leak(o):
                leaked.setdefault(o, []).append(path)

    walk(zh["spice"], [])
    print(f"真實漏翻項：{len(leaked)} 種")
    if args.dry_run:
        for s in list(leaked)[:20]:
            print("  ", s[:60])
        return
    if not leaked:
        return
    # 翻譯
    done = 0
    t0 = time.time()
    with ThreadPoolExecutor(max_workers=args.workers) as ex:
        futs = {ex.submit(translate_one, s, args.url, args.model, args.temperature, args.timeout, args.retries): s for s in leaked}
        new_map = {}
        for fut in as_completed(futs):
            s = futs[fut]
            new_map[s] = fut.result()
            done += 1
            print(f"[{done}/{len(leaked)}] {s[:40]} -> {new_map[s][:40]}", flush=True)
    # 重建覆寫檔
    def rebuild(o):
        if isinstance(o, dict):
            return {k: rebuild(v) for k, v in o.items()}
        if isinstance(o, list):
            return [rebuild(v) for v in o]
        if isinstance(o, str):
            return new_map.get(o, o)
        return o
    zh["spice"] = rebuild(zh["spice"])
    OUT.write_text(json.dumps(zh, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n已更新 {OUT}")


def strip_comments(t: str) -> str:
    # 粗略去 // 註解（不處理字串內 //，但 spice 值少含）
    return re.sub(r"//[^\n]*", "", t)


def mask_refs(s: str) -> tuple[str, list[str]]:
    parts = SPICE_REF.findall(s)
    counter = [0]
    def repl(_m):
        j = counter[0]; counter[0] += 1
        return f"PPPP{j}PPPP"
    return SPICE_REF.sub(repl, s), parts


def unmask_refs(masked: str, parts: list[str]) -> str:
    for i, p in enumerate(parts):
        masked = masked.replace(f"PPPP{i}PPPP", p)
    return masked


def translate_one(text: str, url: str, model: str, temperature: float, timeout: int, retries: int) -> str:
    masked, parts = mask_refs(text)
    last_err = None
    for _ in range(retries + 1):
        try:
            prompt = masked
            if not parts:
                prompt = masked  # 無引用：直接翻
            raw = call_api(url, model, SINGLE_SYSTEM(), prompt, temperature, timeout).strip()
            raw = re.sub(r"^```(?:json)?\s*", "", raw)
            raw = re.sub(r"\s*```$", "", raw)
            restored = unmask_refs(raw, parts) if parts else raw.strip()
            if parts:
                have = SPICE_REF.findall(restored)
                missing = [p for p in parts if p not in have]
                if missing:
                    restored += " " + " ".join(missing)
            if CJK.search(restored):
                return restored
            last_err = "結果無中文"
        except Exception as e:
            last_err = e
            if _ < retries:
                time.sleep(2 * (_ + 1))
    print(f"  [失敗] {text[:50]!r}: {last_err}")
    return text


def collect_strings(obj) -> list[tuple[str, list]]:
    """回傳 (路徑, 原文) 清單，路徑為 spice 下的 JSON 鍵路徑。"""
    out = []

    def walk(o, path):
        if isinstance(o, dict):
            for k, v in o.items():
                walk(v, path + [k])
        elif isinstance(o, list):
            for i, v in enumerate(o):
                walk(v, path + [i])
        elif isinstance(o, str):
            out.append((path, o))
    walk(obj, [])
    return out


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default=API_URL)
    ap.add_argument("--model", default=MODEL)
    ap.add_argument("--temperature", type=float, default=0.2)
    ap.add_argument("--timeout", type=int, default=120)
    ap.add_argument("--retries", type=int, default=2)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--fix", action="store_true", help="只重譯覆寫檔中仍為英文的真實漏翻項")
    args = ap.parse_args()

    if args.fix:
        fix_overrides(args)
        return

    if not SPICE.exists():
        raise SystemExit(f"找不到 {SPICE}")
    data = json.loads(strip_comments(SPICE.read_text(encoding="utf-8")))
    spice = data["spice"]

    items = collect_strings(spice)
    # 只保留需要翻譯的：有英文且非純引用
    to_translate = [(path, s) for path, s in items if NEEDS.search(s) and not re.fullmatch(SPICE_REF.pattern, s) and not CJK.search(s)]
    print(f"spice 字串：{len(items)}，需翻譯：{len(to_translate)}")

    if args.dry_run:
        for path, s in to_translate[:20]:
            print(f"  {'.'.join(map(str,path))}: {s[:50]}")
        print(f"（dry-run：共 {len(to_translate)} 個）")
        return

    if args.limit:
        to_translate = to_translate[: args.limit]

    # 執行翻譯（依原文去重，省 API 呼叫）
    unique: dict[str, list] = {}
    for path, s in to_translate:
        unique.setdefault(s, []).append(path)
    unique_items = list(unique.keys())
    print(f"去重後需送翻譯：{len(unique_items)} 條")

    results: dict[str, str] = {}
    if TMP.exists():
        try:
            results.update(json.loads(TMP.read_text(encoding="utf-8")))
            print(f"斷點：{len(results)} 已完成")
        except Exception:
            pass

    def work(text: str) -> tuple[str, str]:
        return text, translate_one(text, args.url, args.model, args.temperature, args.timeout, args.retries)

    pending = [s for s in unique_items if s not in results]
    print(f"續跑 {len(pending)} 條…")
    done = 0
    t0 = time.time()
    with ThreadPoolExecutor(max_workers=args.workers) as ex:
        futs = [ex.submit(work, s) for s in pending]
        for fut in as_completed(futs):
            src, zh = fut.result()
            results[src] = zh
            done += 1
            if done % 50 == 0 or done == len(pending):
                TMP.write_text(json.dumps(results, ensure_ascii=False), encoding="utf-8")
                print(f"[{done}/{len(pending)}] ({time.time()-t0:.0f}s)", flush=True)
    TMP.write_text(json.dumps(results, ensure_ascii=False), encoding="utf-8")

    # 組出覆寫資料結構（保留 JSON 結構，只換字串）
    translated = dict(results)

    def rebuild(o):
        if isinstance(o, dict):
            return {k: rebuild(v) for k, v in o.items()}
        if isinstance(o, list):
            return [rebuild(v) for v in o]
        if isinstance(o, str):
            return translated.get(o, o)
        return o

    new_spice = rebuild(spice)
    out_obj = {"lang": "zh-tw", "spice": new_spice}
    OUT.write_text(json.dumps(out_obj, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n已寫入 {OUT}（放入 mod 資料夾即可覆寫）")


if __name__ == "__main__":
    main()