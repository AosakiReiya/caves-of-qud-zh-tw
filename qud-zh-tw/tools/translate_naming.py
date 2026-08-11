#!/usr/bin/env python3
"""
translate_naming.py — 程序化專名音節音譯（STEP 4 主體）。

遊戲的 Naming.xml 用 prefix/infix/postfix 音節在執行期拼出村名/歷史名/實體名。
本工具把 Naming.zh-tw.xml 中仍為英文的音節（純字母 1333 個 + 含符號 19 個）
用本機 LLM 音譯成台灣正體中文，產出對照表並直接套用回 Naming.zh-tw.xml。

已翻譯（含中文）的音節與純符號/數字的音節一律保留不動。

用法：
  python3 tools/translate_naming.py --dry-run        # 只列出待翻譯音節
  python3 tools/translate_naming.py --limit 50       # 只翻前 50 個（試跑）
  python3 tools/translate_naming.py --apply          # 翻譯並套用回 Naming.zh-tw.xml
  python3 tools/translate_naming.py --apply-only     # 只用已完成進度重新套用（不呼叫 API）
  python3 tools/translate_naming.py --review         # 列出低置信/需人工審核項
"""
import argparse
import json
import re
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from translate_batch import call_api, API_URL, MODEL

PROJECT = Path(__file__).resolve().parents[1]
NAMING = PROJECT / "zh-tw" / "Naming.zh-tw.xml"
TMP = PROJECT / "tools" / "naming_translation.json"
SKIP = set("·．·")  # 預留

CJK = re.compile(r"[\u4e00-\u9fff]")
ELEM = re.compile(r'<(prefix|infix|postfix) Name="([^">]*)"')

SYSTEM = (
    "You transliterate the invented Qudish syllables of the game Caves of Qud into "
    "Traditional Chinese for Taiwan (zh-TW). These are single random sound-units used to "
    "procedurally compose invented proper names (village names, sultan names, historical "
    "places, creature names).\n"
    "Rules:\n"
    "1. Output ONLY the Chinese transliteration, nothing else, no English in parentheses.\n"
    "2. Use 1-2 (occasionally 3) Traditional Chinese characters that phonetically match the sound.\n"
    "3. Use natural, common transliteration characters and keep them consistent: the same sound "
    "must always map to the same characters (e.g. ka→卡, shi→希, ra→拉, ru→魯, moo→姆).\n"
    "4. Single letters: transliterate by sound (a→阿, e→伊, k→克, n→恩, q→庫, j→傑).\n"
    "5. If the token contains symbols like apostrophes or hyphens (e.g. k'na, s'tya, m-goni), "
    "transliterate the whole thing as one sound cluster, dropping or keeping the separator "
    "naturally (e.g. k'na→克娜, s'tya→斯蒂亞).\n"
    "6. If the token is a repeated or emphatic sound (aaaaah, RG!, OO!), render an emphatic "
    "Chinese echo (aaaaah→啊啊啊啊).\n"
    "7. Never translate meaning; transliterate sound only.\n"
    "8. Do not output spaces between the Chinese characters.\n"
    "9. If the token is clearly not a name-sound (e.g. an error string), leave it as-is."
)


def collect_untranslated() -> list[str]:
    """回傳含字母且未翻（無中文）的音節，去重，依字母排序。

    鍵以 FullUpper 正規化：遊戲 Naming 音節大小寫不敏感（C# 字典用
    OrdinalIgnoreCase），'A' 與 'a'、'CAAAW' 與 'caaaw' 視為同一音節，
    統一用大寫鍵，避免產生大小寫重複導致 C# 靜態字典初始化衝突。
    """
    t = NAMING.read_text(encoding="utf-8-sig")
    un = set()
    for m in ELEM.finditer(t):
        n = m.group(2)
        if not n or n == "DisplayText":
            continue
        if CJK.search(n):
            continue
        if re.search(r"[A-Za-z]", n):
            un.add(n.upper())
    return sorted(un)


def has_cjk(s: str) -> bool:
    return bool(CJK.search(s))


def keep_ASCII_token(s: str) -> bool:
    # 錯誤字串/長程式碼狀 token 保留
    return s.startswith("[ERROR") or s.startswith("{")


def translate_one(text: str, url: str, model: str, temperature: float, timeout: int, retries: int) -> str:
    last_err = None
    for _ in range(retries + 1):
        try:
            raw = call_api(url, model, SYSTEM, text, temperature, timeout).strip()
            raw = re.sub(r"^```(?:json)?\s*", "", raw)
            raw = re.sub(r"\s*```$", "", raw)
            raw = raw.strip().strip("\"'")
            if has_cjk(raw) and not re.search(r"[A-Za-z]{2,}", raw):
                return raw
            last_err = f"結果無中文: {raw[:40]!r}"
        except Exception as e:
            last_err = str(e)
            if _ < retries:
                time.sleep(2 * (_ + 1))
    print(f"  [失敗] {text[:40]!r}: {last_err}")
    return text


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default=API_URL)
    ap.add_argument("--model", default=MODEL)
    ap.add_argument("--temperature", type=float, default=0.2)
    ap.add_argument("--timeout", type=int, default=90)
    ap.add_argument("--retries", type=int, default=2)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--apply", action="store_true", help="翻譯並套用回 XML")
    ap.add_argument("--apply-only", action="store_true", help="只用進度重新套用，不呼叫 API")
    args = ap.parse_args()

    all_un = collect_untranslated()
    # 排除保留 token
    to_translate = [s for s in all_un if not keep_ASCII_token(s)]
    kept = [s for s in all_un if keep_ASCII_token(s)]
    print(f"未翻音節（含字母去重）：{len(all_un)}；將翻譯：{len(to_translate)}；保留：{len(kept)}")

    if args.dry_run:
        for s in to_translate[:40]:
            print("  ", s)
        print(f"（dry-run：共 {len(to_translate)} 個）")
        return

    # 進度快取
    results: dict[str, str] = {}
    if TMP.exists():
        try:
            results.update(json.loads(TMP.read_text(encoding="utf-8")))
            print(f"斷點：{len(results)} 已完成")
        except Exception:
            pass

    if args.apply_only:
        print("--apply-only：不呼叫 API，直接套用已完成對照表。")
        args.apply = True

    if not args.apply_only:
        pending = [s for s in to_translate if s not in results]
        if args.limit:
            pending = pending[: args.limit]
        print(f"續跑 {len(pending)} 條…")

        def work(text: str) -> tuple[str, str]:
            return text, translate_one(text, args.url, args.model, args.temperature, args.timeout, args.retries)

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
        print(f"進度已存：{TMP}")

    if not args.apply:
        print("未 --apply，僅存進度。可稍後以 --apply 套用。")
        return

    # 套用回 XML（只替換含字母且仍未翻、且對照表有中文的項）
    # 對照表鍵已正規化為大寫，這裡以 .upper() 查詢（遊戲 Naming 大小寫不敏感）
    t = NAMING.read_text(encoding="utf-8-sig")
    applied = 0
    left = 0
    def repl(m):
        nonlocal applied, left
        k = m.group(1)
        n = m.group(2)
        if not n or n == "DisplayText" or CJK.search(n) or not re.search(r"[A-Za-z]", n):
            return m.group(0)
        zh = results.get(n.upper())
        if zh and CJK.search(zh) and zh != n:
            applied += 1
            return f'<{k} Name="{zh}"'
        left += 1
        return m.group(0)
    new = ELEM.sub(repl, t)
    NAMING.write_text("\ufeff" + new, encoding="utf-8")
    print(f"已套用 {applied} 個音節翻譯；仍剩 {left} 個英文音節（無對照或失敗）。")


if __name__ == "__main__":
    main()