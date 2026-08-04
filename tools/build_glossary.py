#!/usr/bin/env python3
"""
build_glossary.py — 從遊戲資料抽取「權威名稱」並用 LLM 翻譯成 zh-tw 術語表。

術語表用途：翻譯時先於原文把英文專有名詞替換成統一譯名，確保全案一致。

來源：
  1. ObjectBlueprints/*.xml 的 DisplayName（生物、物品、家具、食物等）
  2. Factions.xml、ChiliadFactions.xml 的 DisplayName
  3. Worlds.xml / 其他 xml 的 DisplayName

用法：
  python3 tools/build_glossary.py extract            # 抽出名稱 → tools/proper_nouns.txt
  python3 tools/build_glossary.py translate          # LLM 翻譯 → tools/glossary.json
  python3 tools/build_glossary.py extract --translate
"""
import argparse
import json
import re
import sys
from pathlib import Path

from translate_batch import call_api, API_URL, MODEL

PROJECT = Path(__file__).resolve().parents[1]
BASE_DIR = Path(__file__).resolve().parents[3] / "CoQ_Data" / "StreamingAssets" / "Base"
NOUNS = PROJECT / "tools" / "proper_nouns.txt"
GLOSSARY = PROJECT / "tools" / "glossary.json"

NOISE_RE = re.compile(r"[=~{}\[\]*<>&#]")
DIGIT_RE = re.compile(r"^[\d\s\-\.]+$")
DIGIT_START = re.compile(r"^\d")

# 只從「名稱類」資料檔抽 DisplayName；排除 UI 標籤類（Options/Commands/Manual/Books/ActivatedAbilities）
NAME_FILES = {
    "ObjectBlueprints",
    "Factions.xml",
    "ChiliadFactions.xml",
    "Worlds.xml",
    "Skills.xml",
    "Mutations.xml",
    "HiddenMutations.xml",
    "Subtypes.xml",
    "Genotypes.xml",
    "Liquids.xml",
    "EmbarkModules.xml",
    "PronounSets.xml",
    "Bodies.xml",
    "HiddenConversations.xml",
    "Relics.xml",
}

GLOSSARY_SYSTEM = (
    "You are translating proper nouns from the video game Caves of Qud into "
    "Traditional Chinese (Taiwan, zh-TW). You will receive numbered lines, each a "
    "creature/item/location/faction NAME. Return ONLY a JSON array of strings, one "
    "translation per line, in order. Rules: 1. Transliterate proper nouns "
    "(e.g. keep the sound); for common nouns translate the meaning. 2. Use Taiwan "
    "Traditional Chinese characters. 3. Keep it short (1-6 characters preferred). "
    "4. Do not add the original in parentheses. 5. Output nothing but the JSON array."
)


def extract_names() -> list[str]:
    names: dict[str, str] = {}  # lower -> original
    for f in BASE_DIR.rglob("*.xml"):
        if "ExampleLanguage" in str(f):
            continue
        rel = f.relative_to(BASE_DIR)
        if not any(str(rel).startswith(p) or rel.name == p for p in NAME_FILES):
            continue
        try:
            d = f.read_text(encoding="utf-8-sig")
        except Exception:
            continue
        for m in re.finditer(r'DisplayName="([^"]*)"', d):
            n = m.group(1).strip()
            if not n or NOISE_RE.search(n) or DIGIT_RE.match(n) or len(n) < 2:
                continue
            if DIGIT_START.match(n):
                continue
            names.setdefault(n.lower(), n)
    # 去掉只有大小寫差異的重複，保留最常出現的拼法
    return sorted({v: 0 for v in names.values()}.keys())


def translate_names(names: list[str], url: str, model: str, temperature: float, batch: int) -> dict[str, str]:
    from translate_batch import translate_batch as _tb, parse_json_array

    result: dict[str, str] = {}
    for i in range(0, len(names), batch):
        chunk = names[i : i + batch]
        numbered = "\n".join(f"{j + 1}. {n}" for j, n in enumerate(chunk))
        raw = call_api(url, model, GLOSSARY_SYSTEM, numbered, temperature, 300)
        parsed = parse_json_array(raw)
        if parsed is None or len(parsed) != len(chunk):
            print(f"  [失敗] 第 {i} 批，改逐條…")
            parsed = []
            for n in chunk:
                one = call_api(url, model, GLOSSARY_SYSTEM, n, temperature, 300)
                arr = parse_json_array(one)
                parsed.append(arr[0] if arr and len(arr) == 1 else one.strip().strip('"'))
        for n, t in zip(chunk, parsed):
            result[n] = t.strip()
        print(f"  [{i + len(chunk)}/{len(names)}]", flush=True)
    return result


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", nargs="?", choices=["extract", "translate", "both"], default="both")
    ap.add_argument("--url", default=API_URL)
    ap.add_argument("--model", default=MODEL)
    ap.add_argument("--temperature", type=float, default=0.3)
    ap.add_argument("--batch", type=int, default=30)
    args = ap.parse_args()

    names = extract_names()
    print(f"抽取 {len(names)} 個權威名稱。")

    if args.mode in ("extract", "both"):
        NOUNS.write_text("\n".join(names) + "\n", encoding="utf-8")
        print(f"已寫入 {NOUNS}")

    if args.mode in ("translate", "both"):
        if NOUNS.exists():
            names = NOUNS.read_text(encoding="utf-8").splitlines()
        print(f"開始翻譯 {len(names)} 個名稱 → {GLOSSARY} …")
        result = translate_names(names, args.url, args.model, args.temperature, args.batch)
        # 合併既有 LLM 術語表（機器產出），人工策展檔最後覆蓋（最高優先）
        if GLOSSARY.exists():
            existing = json.loads(GLOSSARY.read_text(encoding="utf-8"))
            existing.update(result)
            result = existing
        GLOSSARY.write_text(
            json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )
        curated = PROJECT / "tools" / "glossary_curated.json"
        if curated.exists():
            data = json.loads(curated.read_text(encoding="utf-8"))
            c = {k: v for k, v in data.items() if not k.startswith("_")}
            result.update(c)
            GLOSSARY.write_text(
                json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
            )
            print(f"已套用人工策展 {len(c)} 組（優先）。")
        print(f"完成，共 {len(result)} 組術語。")
        print("（可在 translate_batch.py 的 --glossary 使用，並可手動修正結果）")


if __name__ == "__main__":
    main()
