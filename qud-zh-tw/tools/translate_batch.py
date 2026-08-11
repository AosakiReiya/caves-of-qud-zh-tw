#!/usr/bin/env python3
"""
translate_batch.py — 用本機 LLM（LM Studio / llama.cpp 相容 API）批量翻譯 zh-tw 骨架。

特色：
  - 直接改寫 zh-tw/*.xml：把 ▶ 前綴的英文換成正體中文（並移除 ▶）
  - 內容去重（重複字串只翻譯一次），省 API 呼叫
  - 批次輸出 JSON 陣列，失敗自動退回逐條翻譯，並重試
  - 可選 --glossary 以術語表強制統一譯名（先於送翻譯前套用）
  - 可續跑（已翻譯的 ▶ 被移除，不會重翻）
  - 保留 placeholder（=name=、~CmdUse）與色彩標記（{{K|text}}）結構

用法範例：
  python3 tools/translate_batch.py                          # 翻譯全部骨架
  python3 tools/translate_batch.py --files "Strings*.xml"  # 只翻某些檔
  python3 tools/translate_batch.py --limit 100 --dry-run   # 只顯示前 100 條將翻譯內容
  python3 tools/translate_batch.py --workers 4 --temperature 0.2
"""
import argparse
import fnmatch
import html
import json
import re
import sys
import time
import xml.sax.saxutils as sax
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

import requests

PROJECT = Path(__file__).resolve().parents[1]
DEFAULT_SKELETON = PROJECT / "zh-tw"
DEFAULT_GLOSSARY = PROJECT / "tools" / "glossary.json"
CURATED_GLOSSARY = PROJECT / "tools" / "glossary_curated.json"

API_URL = "http://localhost:1234/api/v1/chat"
MODEL = "gemma-4-26b-a4b-it"

ATTR_RE = re.compile(r'(\w+)\s*=\s*"([^"]*)"')
TEXT_RE = re.compile(r">([^<]*▶[^<]*)<")

def _load_term_bank() -> str:
    """從策展術語表建構 prompt 的術語庫文字（名詞一致性基準）。"""
    try:
        data = json.loads(CURATED_GLOSSARY.read_text(encoding="utf-8"))
    except Exception:
        return ""
    lines = [f"  {k} → {v}" for k, v in data.items() if not k.startswith("_")]
    return "\n".join(lines)


TERM_BANK = _load_term_bank()


def _term_bank_section() -> str:
    if not TERM_BANK:
        return ""
    return (
        "Established glossary (reuse these translations verbatim; never invent a "
        "variant for a term listed here):\n" + TERM_BANK + "\n"
    )


# 共同規則：保留語法 + 專名類型判定 + 一致性
_SYSTEM_COMMON = (
    "You are a professional translator for the video game Caves of Qud, "
    "translating English into Traditional Chinese for Taiwan (zh-TW). "
    "Rules: "
    "1. Preserve placeholders exactly, e.g. =name=, ~CmdUse, @they, and counts like =num=; "
    "keep every placeholder present and in place. "
    "2. Preserve color shader syntax {{X|text}}: keep {{, the marker letter, |, and }} exactly; translate only the visible words. "
    "3. Preserve HTML markup tags exactly: <p>, </p>, <br />, <stat Name=\"CrashChance\" />, <em>, <h1>; translate only the visible English text around them. "
    "4. Keep escapes and newlines (\\n) as literal sequences. "
    "5. Use Taiwan Traditional Chinese characters and full-width punctuation. "
    "6. Proper-noun rule — decide which type the text is, then apply the matching format: "
    "   (a) FIXED proper noun (person, place, faction, artifact name, or coined term): "
    "       transliterate it phonetically into zh-TW AND append the English in parentheses, "
    "       e.g. \"Barathrum\" → \"巴拉楚姆(Barathrum)\", \"Automata Sophia\" → "
    "       \"奧托瑪塔·索菲亞(Automata Sophia)\". Keep the (English) every time it appears. "
    "   (b) DYNAMIC naming template (a template that combines placeholders like =name=, "
    "       =adjective=, =creatureTypeCap=, =rings=, =position= into a generated name): "
    "       translate only the structural/fixed words into plain Chinese and do NOT append "
    "       English, e.g. \"the =rings= Baboon =position=\" → \"戴著=rings=指環的狒狒，位於=position=\"。 "
    "   (c) COMMON noun, verb, or adjective: plain Chinese, no English, e.g. \"snapjaw\" → \"咬顎獸\". "
    "7. Consistency: consult the established glossary below and reuse its translations "
    "verbatim; never produce a variant spelling for a term already listed. "
    "8. If the line already contains Traditional Chinese text, it is a fixed translated "
    "proper noun — keep it exactly, never re-translate or alter it. "
    "9. Use the glossary below as the source of truth for settled names. "
    "10. POLYSEMY: a word may be a verb or a noun depending on context. Translate by sense, "
    "e.g. \"pets\" as a verb (to pet, in 'X pets Y') → 撫摸, but as a noun ('kept as pets') → 寵物. "
    "When unsure, choose the sense that fits the surrounding words. "
    "11. TEMPLATE KEYS: if the ID itself is a variable reference like =journalNote.the.location.of=, "
    "the VALUE must be a Chinese template that embeds the same =...= reference(s), e.g. "
    "\"=journalNote.the.location.of=\" → \"你記錄了 =journalNote.the.location.of= 的位置。\". "
    "Do not leave the value as the bare reference."
)


def BATCH_SYSTEM() -> str:
    return (
        _SYSTEM_COMMON
        + _term_bank_section()
        + "You will receive numbered lines of game text (1., 2., 3., ...). Translate EVERY "
        "line and return ONLY a JSON OBJECT keyed by the input numbers, e.g. "
        '{"1": "translation of line 1", "2": "translation of line 2"}. Each key must appear '
        "exactly once, mapped to that line's translation. Output nothing but the JSON object — "
        "no markdown fences, no commentary."
    )


def SINGLE_SYSTEM() -> str:
    return (
        _SYSTEM_COMMON
        + _term_bank_section()
        + "Output ONLY the translation, nothing else."
    )


def _text_region_units(text: str, pos: int) -> list[tuple[int, int, str, str]]:
    """文字節點 ▶：整段區域（▶ 到包含元素的結束標籤）為一個單位。

    巢狀標籤內的 ▶ 屬性（<stat DisplayName="▶...">）由這個區域單位一併處理，
    其獨立的屬性單位會在 extract_units 中被過濾掉，避免重疊。
    """
    lt = text.rfind("<", 0, pos)
    if lt == -1:
        return [(pos, 1, "▶", "marker")]
    gt = text.find(">", lt)
    tag_text = text[lt + 1 : gt].strip()
    if not tag_text or tag_text.startswith("/") or tag_text.endswith("/"):
        return [(pos, 1, "▶", "marker")]
    tag = tag_text.split()[0]
    close_pos = text.find(f"</{tag}>", pos)
    if close_pos == -1:
        return [(pos, 1, "▶", "marker")]
    return [(pos, close_pos - pos, text[pos:close_pos], "text")]


def extract_units(text: str) -> list[tuple[int, int, str, str]]:
    """回傳 (offset, length, content_after_marker, kind) 清單，依 offset 排序。

    - 屬性值（DisplayName、Values 等）：▶ 起，到引號／下一段顯示文字
    - 文字節點：移除 ▶ 標記 + 標籤間純文字段（HTML 標籤不動）
    """
    units = []
    for m in ATTR_RE.finditer(text):
        val = m.group(2)
        if "▶" not in val:
            continue
        if m.group(1) == "Values":
            # Values="v1|d1,v2|d2,..."：每個 display 獨立一條
            base = m.start(2)
            pos = 0
            for token in val.split(","):
                if "|" in token:
                    v, disp = token.split("|", 1)
                    pos += len(v) + 1
                    if "▶" in disp:
                        units.append((base + pos, len(disp), disp, "attr"))
                    pos += len(disp)
                else:
                    pos += len(token)
                pos += 1  # 逗號
        else:
            marker = val.index("▶")
            off = m.start(2) + marker
            units.append((off, len(val) - marker, val[marker:], "attr"))
    for m in re.finditer("▶", text):
        pos = m.start()
        # 若 ▶ 位於標籤內部（屬性值），跳過（已由 ATTR_RE 處理）
        lt = text.rfind("<", 0, pos)
        if lt != -1:
            gt_after = text.find(">", lt)
            if gt_after != -1 and pos < gt_after:
                continue
        units.extend(_text_region_units(text, pos))
    units.sort(key=lambda u: u[0])
    # 過濾：屬性單位若落在某個文字區域單位範圍內，移除（避免重疊，由區域單位一併處理）
    regions = [u for u in units if u[3] == "text"]
    filtered = []
    for u in units:
        if u[3] == "attr":
            uoff, ulen = u[0], u[0] + u[2].count("&#") + len(u[2])
            inside = any(r[0] <= u[0] < r[0] + len(r[2]) for r in regions if len(r[2]) > 1)
            if inside:
                continue
        filtered.append(u)
    return filtered


def load_units(skeleton: Path, file_filter: str) -> list[tuple[Path, tuple[int, int, str, str]]]:
    result = []
    for f in sorted(skeleton.glob(file_filter)):
        if not f.suffix == ".xml":
            continue
        text = f.read_text(encoding="utf-8-sig")
        for unit in extract_units(text):
            result.append((f, unit))
    return result


def load_glossary(path: Path | str | None) -> dict[str, str]:
    """讀取 LLM 術語表並疊加人工策展檔（策展優先）。"""
    if isinstance(path, str):
        path = Path(path)
    entries: dict[str, str] = {}
    if path is not None and path.exists():
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            for key, val in data.items():
                if key.startswith("_"):
                    continue
                if isinstance(val, list):
                    for v in val:
                        entries[key] = str(v)
                else:
                    entries[key] = str(val)
        except Exception as e:
            print(f"[警告] 無法讀取術語表 {path}: {e}")
    if CURATED_GLOSSARY.exists():
        try:
            data = json.loads(CURATED_GLOSSARY.read_text(encoding="utf-8"))
            for key, val in data.items():
                if not key.startswith("_"):
                    entries[key] = str(val)
        except Exception as e:
            print(f"[警告] 無法讀取策展術語表 {CURATED_GLOSSARY}: {e}")
    return entries


# 常見英文詞：即使出現在術語表（生物/物品名），也不在自由文字中盲目代換，避免誤譯動詞/一般名詞
STOPLIST = {
    "a", "an", "the", "and", "or", "of", "to", "in", "on", "at", "is", "are", "be", "was",
    "axe", "ax", "armor", "armour", "arrow", "bark", "bat", "bear", "beak", "bed", "bench",
    "bill", "bite", "boar", "book", "boot", "bow", "brick", "cage", "chair", "chest", "claw",
    "cloak", "club", "desk", "door", "ewer", "false", "fangs", "fish", "fist", "forge",
    "frond", "gate", "gaze", "globe", "goat", "gourd", "harp", "hoof", "hook", "horn",
    "hut", "item", "jaws", "kick", "kiln", "knife", "knob", "lathe", "mask", "mimic",
    "miner", "oozes", "peck", "pig", "pool", "rifle", "root", "seed", "shale", "sign",
    "slate", "slug", "sofa", "spade", "staff", "steam", "stone", "stool", "stud", "swine",
    "sword", "table", "tent", "tool", "torch", "trash", "tube", "vase", "vine", "water",
    "web", "wing", "worm", "goat", "bow", "pitch", "mash", "marl", "stone", "troll",
    "circle", "rise", "fall", "draw", "hold", "bind", "cut", "break", "spring", "wake",
    "rest", "stand", "serve", "cover", "leave", "return", "ring", "seal", "plate", "chain",
    "loop", "cloud", "glass", "iron", "steel", "bone", "smoke", "salt", "sand", "grate",
    "sling", "dart", "band", "cap", "cup", "bowl", "pot", "pan", "jar", "jug", "mug",
    "sack", "bag", "box", "barrel", "tank", "spike", "guard", "warden", "tide", "weft",
    "knot", "wheel", "gear", "shaft", "bar", "pin", "nail", "screw", "bolt", "tray",
    "crate", "cask", "vat", "keg", "flagon", "decanter", "amphora", "charge", "cleave",
    "carve", "heal", "hurt", "know", "speak", "tell", "show", "give", "take", "make",
    "come", "go", "see", "feel", "find", "move", "turn", "stand", "pass", "cast",
}


class Glossary:
    """將術語表編譯成單一正則，快速且安全的代換專有名詞。"""

    def __init__(self, entries: dict[str, str]):
        # 只代換「區別性」條目：含大寫／多詞／撇號／夠長且非停用詞
        distinct: list[tuple[str, str]] = []
        for en, zh in entries.items():
            en = en.strip()
            if not en or not zh or not en[0].isalpha():
                continue
            low = en.lower()
            if any(c.isupper() for c in en) or " " in en or "-" in en or "'" in en:
                distinct.append((en, zh))
            elif len(en) >= 4 and low not in STOPLIST and low + "s" not in STOPLIST:
                distinct.append((en, zh))
        distinct.sort(key=lambda e: -len(e[0]))
        self.is_distinct: dict[str, str] = {en.lower(): zh for en, zh in distinct}
        parts = []
        for en, zh in distinct:
            esc = re.escape(en)
            parts.append(esc)
            if not en.lower().endswith("'s"):
                parts.append(esc + r"'s")
            low = en.lower()
            if not (low.endswith(("s", "x", "z", "ch", "sh")) or low.endswith("'s")):
                parts.append(esc + "s")
        # 全部依長度遞減，長詞（含所有格/複數）優先匹配
        parts.sort(key=len, reverse=True)
        self.pattern = re.compile(
            r"(?<![A-Za-z])(?:" + "|".join(parts) + r")(?![A-Za-z])", re.IGNORECASE
        )

    def apply(self, text: str) -> str:
        if not self.is_distinct:
            return text
        # 處理 {{...}} 色彩標記（含巢狀）：只代換「顯示文字」部分，保留 id 與語法
        out: list[str] = []
        i = 0
        n = len(text)
        while i < n:
            j = text.find("{{", i)
            if j == -1:
                out.append(self._sub(text[i:]))
                break
            out.append(self._sub(text[i:j]))
            depth = 1
            k = j + 2
            while k < n and depth:
                o = text.find("{{", k)
                c = text.find("}}", k)
                if o != -1 and (c == -1 or o < c):
                    depth += 1
                    k = o + 2
                elif c != -1:
                    depth -= 1
                    k = c + 2
                else:
                    k = n
                    break
            out.append(self._shader(text[j:k]))
            i = k
        return "".join(out)

    def _shader(self, block: str) -> str:
        """block 形如 {{ID|content}}；遞迴代換 content，保留 ID 與語法。"""
        if len(block) < 4 or block[:2] != "{{" or block[-2:] != "}}":
            return block
        inner = block[2:-2]
        bar = inner.find("|")
        if bar == -1:
            return block
        return "{{" + inner[:bar] + "|" + self.apply(inner[bar + 1 :]) + "}}"

    def _sub(self, text: str) -> str:
        def repl(m: re.Match) -> str:
            token = m.group(0)
            low = token.lower()
            if low in self.is_distinct:
                return self.is_distinct[low]
            if low.endswith("'s") and low[:-2] in self.is_distinct:
                return self.is_distinct[low[:-2]] + "的"
            if low.endswith("s") and low[:-1] in self.is_distinct:
                return self.is_distinct[low[:-1]]
            return token

        return self.pattern.sub(repl, text)


def unescape(text: str) -> str:
    return html.unescape(text)


# 描述性內容中的合法 HTML 標籤（其餘 < 一律轉義）
PRESERVE_TAGS = re.compile(r"^<(/?)(p|br|stat|case|default|switch|gametext|statline|saveline|leveltext|template|em)(?=[\s>])")


def escape(unit: str, kind: str) -> str:
    if kind == "marker":
        return ""
    if kind == "attr":
        s = sax.escape(html.unescape(unit), {"\"": "&quot;"})
        return s.replace("\n", "&#xA;")
    # text：先正規化實體、還原被翻譯的標籤名，再白名單保留合法標籤
    s = html.unescape(unit)
    s = s.replace("</開關>", "</switch>").replace("<開關", "<switch")
    s = s.replace("&", "&amp;")
    out = []
    i = 0
    while i < len(s):
        if s[i] == "<":
            if PRESERVE_TAGS.match(s[i:]):
                out.append(s[i])
            else:
                out.append("&lt;")
        else:
            out.append(s[i])
        i += 1
    return "".join(out)


def call_api(url: str, model: str, system: str, user: str, temperature: float, timeout: int) -> str:
    payload = {
        "model": model,
        "system_prompt": system,
        "input": user,
        "temperature": temperature,
        "max_output_tokens": 8192,
    }
    r = requests.post(url, json=payload, timeout=timeout)
    r.raise_for_status()
    data = r.json()
    out = data.get("output")
    if not out or not isinstance(out, list) or not out[0].get("content"):
        raise ValueError(f"意外的回應結構: {str(data)[:200]}")
    return out[0]["content"]


def parse_batch_output(text: str, n: int) -> list[str] | None:
    """解析批次輸出為長度 n 的列表（依輸入編號對應，免疫順序錯位）。

    支援：
      - 陣列 ["t1","t2",...]
      - 鍵值物件 {"1":"t1","2":"t2",...}
    """
    text = text.strip()
    text = re.sub(r"^```(?:json)?\s*", "", text)
    text = re.sub(r"\s*```$", "", text)
    # 物件形式
    try:
        data = json.loads(text)
        if isinstance(data, dict):
            out = [None] * n
            for k, v in data.items():
                try:
                    idx = int(str(k)) - 1
                except ValueError:
                    continue
                if 0 <= idx < n and isinstance(v, str):
                    out[idx] = v
            if all(x is not None for x in out):
                return out
            # 部分：回傳已對應部分（None 表示缺）
            return out
    except Exception:
        pass
    m = re.search(r"\{.*\}", text, re.S)
    if m:
        try:
            data = json.loads(m.group(0))
            if isinstance(data, dict):
                out = [None] * n
                for k, v in data.items():
                    try:
                        idx = int(str(k)) - 1
                    except ValueError:
                        continue
                    if 0 <= idx < n and isinstance(v, str):
                        out[idx] = v
                return out
        except Exception:
            pass
    # 陣列形式
    try:
        data = json.loads(text)
        if isinstance(data, list) and all(isinstance(x, str) for x in data):
            return list(data) + [None] * (n - len(data))
    except Exception:
        pass
    m = re.search(r"\[.*\]", text, re.S)
    if m:
        try:
            data = json.loads(m.group(0))
            if isinstance(data, list) and all(isinstance(x, str) for x in data):
                return list(data) + [None] * (n - len(data))
        except Exception:
            pass
    return None


def translate_batch(
    items: list[str],
    url: str,
    model: str,
    temperature: float,
    timeout: int,
    retries: int,
) -> list[str]:
    n = len(items)
    # 每個項目先遮蔽 placeholder 與 HTML 標籤，翻譯後還原（保證完整）
    masked_items = []
    for t in items:
        m, ph = mask_placeholders(t)
        m, tags = mask_html_tags(m)
        masked_items.append((m, ph, tags))
    numbered = "\n".join(f"{i + 1}. {m[0]}" for i, m in enumerate(masked_items))
    last_err = None
    for attempt in range(retries + 1):
        try:
            raw = call_api(url, model, BATCH_SYSTEM(), numbered, temperature, timeout)
            parsed = parse_batch_output(raw, n)
            if parsed is None:
                raise ValueError("回應不是 JSON 物件/陣列")
            missing = [i for i, x in enumerate(parsed) if x is None]
            for i in missing:
                parsed[i] = translate_single(items[i], url, model, temperature, timeout, retries)
            # 還原 placeholder 與標籤
            out = []
            for i, t in enumerate(parsed):
                _, ph_parts, tag_parts = masked_items[i]
                restored = unmask_placeholders(t, ph_parts)
                if ph_parts:
                    missing_ph = set(ph_parts) - set(PH_MASK.findall(restored))
                    if missing_ph:
                        restored += " " + " ".join(sorted(missing_ph))
                if tag_parts:
                    restored = unmask_html_tags(restored, tag_parts)
                    if not html_balanced(restored):
                        restored = translate_single(items[i], url, model, temperature, timeout, retries)
                out.append(restored)
            return out
        except Exception as e:
            last_err = e
            if attempt < retries:
                time.sleep(2 * (attempt + 1))
    print(f"  [批次失敗，改逐條] {last_err}")
    return [translate_single(x, url, model, temperature, timeout, retries) for x in items]


PH_MASK = re.compile(r"=[A-Za-z0-9_.:;|!@/()+\-#']+=")
TAG_RE = re.compile(r"<[^>]*>")


def mask_html_tags(text: str) -> tuple[str, list[str]]:
    """把 HTML 標籤換成 TTTTiTTTT 標記，翻譯後原樣還原（保證標籤完整）。"""
    tags = list(TAG_RE.findall(text))
    counter = [0]

    def repl(m: re.Match) -> str:
        j = counter[0]
        counter[0] += 1
        return f"TTTT{j}TTTT"

    return TAG_RE.sub(repl, text), tags


def unmask_html_tags(masked: str, tags: list[str]) -> str:
    out = masked
    for i, t in enumerate(tags):
        out = out.replace(f"TTTT{i}TTTT", t)
    return out


def mask_placeholders(text: str) -> tuple[str, list[str]]:
    """把 =placeholder= 換成 PPPPiPPPP 標記（翻譯後還原，保證 placeholder 保留）。"""
    parts = list(PH_MASK.findall(text))
    counter = [0]

    def repl(m: re.Match) -> str:
        j = counter[0]
        counter[0] += 1
        return f"PPPP{j}PPPP"

    return PH_MASK.sub(repl, text), parts


def unmask_placeholders(masked: str, parts: list[str]) -> str:
    out = masked
    for i, p in enumerate(parts):
        out = out.replace(f"PPPP{i}PPPP", p)
    return out


def html_balanced(s: str) -> bool:
    """檢查 HTML 描述中的成對標籤是否平衡（p/gametext/switch/case/default/leveltext/template/em）。"""
    tag_re = re.compile(r"<(/?)(p|gametext|switch|case|default|leveltext|template|em)\b")
    stack = []
    for slash, name in tag_re.findall(s):
        if slash == "/":
            if not stack or stack[-1] != name:
                return False
            stack.pop()
        else:
            stack.append(name)
    return not stack


def translate_single(text: str, url: str, model: str, temperature: float, timeout: int, retries: int) -> str:
    # 標籤遮蔽：HTML 描述先把 <...> 換成 TTTT 標記，模型只翻譯文字
    has_html = "<" in text
    masked, ph_parts = mask_placeholders(text)
    masked, tag_parts = mask_html_tags(masked)
    last_err = None
    for attempt in range(retries + 1):
        try:
            raw = call_api(url, model, SINGLE_SYSTEM(), masked, temperature, timeout)
            raw = raw.strip()
            raw = re.sub(r"^```(?:json)?\s*", "", raw)
            raw = re.sub(r"\s*```$", "", raw)
            restored = unmask_placeholders(raw, ph_parts)
            if ph_parts:  # 有 placeholder 時驗證
                if set(PH_MASK.findall(restored)) != set(ph_parts):
                    last_err = "placeholder 驗證失敗"
                    continue
            if has_html:
                restored = unmask_html_tags(restored, tag_parts)
                if not html_balanced(restored):
                    last_err = "HTML 標籤不平衡"
                    continue
            return restored
        except Exception as e:
            last_err = e
            if attempt < retries:
                time.sleep(2 * (attempt + 1))
    if ph_parts:
        # 最後手段：把漏掉的 placeholder 補回
        restored = unmask_placeholders(raw if "raw" in dir() else masked, ph_parts)
        if has_html:
            restored = unmask_html_tags(restored, tag_parts)
        missing = set(ph_parts) - set(PH_MASK.findall(restored))
        if missing:
            restored += " " + " ".join(sorted(missing))
        return restored
    print(f"  [逐條失敗] {text[:60]!r}: {last_err}")
    return text  # 失敗保留原文（含 ▶，稍後可重跑）


def main() -> None:
    ap = argparse.ArgumentParser(description="批量翻譯 zh-tw 骨架")
    ap.add_argument("--url", default=API_URL)
    ap.add_argument("--model", default=MODEL)
    ap.add_argument("--skeleton", default=str(DEFAULT_SKELETON))
    ap.add_argument("--files", default="*.xml", help="glob，如 Strings*.xml")
    ap.add_argument("--exclude", default="", help="排除的 glob（逗號分隔），如 Naming*.xml")
    ap.add_argument("--limit", type=int, default=0, help="本次最多翻譯 N 條（0=全部）")
    ap.add_argument("--batch", type=int, default=30, help="每批字串數")
    ap.add_argument("--workers", type=int, default=1, help="並行 worker 數")
    ap.add_argument("--temperature", type=float, default=0.3)
    ap.add_argument("--timeout", type=int, default=180)
    ap.add_argument("--retries", type=int, default=3)
    ap.add_argument("--glossary", default=str(DEFAULT_GLOSSARY), help="術語表 JSON（空字串=停用）")
    ap.add_argument("--dry-run", action="store_true", help="只列出將翻譯的內容，不寫入")
    ap.add_argument("--apply", action="store_true", help="只套用已完成的 progress.json，不呼叫模型")
    ap.add_argument("--fix-bad", action="store_true", help="重翻 bad_quality.json 中的壞翻譯（placeholder 驗證迴圈）")
    args = ap.parse_args()

    units = load_units(Path(args.skeleton), args.files)
    if args.exclude:
        ex_pats = [fnmatch.translate(e.strip()) for e in args.exclude.split(",") if e.strip()]
        units = [u for u in units if not any(re.fullmatch(p, u[0].name) for p in ex_pats)]
    if not units:
        print("沒有找到含 ▶ 的待翻譯條目。")
        return
    print(f"找到 {len(units)} 條待翻譯（含重複）。")

    # 去重：相同內容只翻譯一次
    unique: dict[str, list[tuple[Path, tuple[int, int, str, str]]]] = {}
    for path, unit in units:
        unique.setdefault(unit[2], []).append((path, unit))
    unique_items = list(unique.keys())
    # 送給模型的內容去掉 ▶ 標記（那是我們自己的「未翻譯」標記，模型不該看到/保留）
    send_items = [s[1:] if s.startswith("▶") else s for s in unique_items]
    print(f"去重後 {len(unique_items)} 條需送出翻譯。")

    if args.limit:
        unique_items = unique_items[: args.limit]
        send_items = send_items[: args.limit]

    full_of = {send: full for send, full in zip(send_items, unique_items)}

    if args.dry_run:
        for s in send_items[: args.limit or 20]:
            print("  ", repr(unescape(s))[:140])
        print(f"（dry-run：共 {len(unique_items)} 條）")
        return

    glossary = Glossary(load_glossary(Path(args.glossary) if args.glossary else None))
    if glossary.is_distinct:
        print(f"套用術語表 {len(glossary.is_distinct)} 組區別性詞（先於翻譯前）")

    checkpoint = PROJECT / "tools" / "progress.json"
    results: dict[str, str] = {}
    if checkpoint.exists():
        try:
            results.update(json.loads(checkpoint.read_text(encoding="utf-8")))
            print(f"載入斷點：{len(results)} 條已完成，續跑剩餘。")
        except Exception as e:
            print(f"[警告] 斷點讀取失敗：{e}")
    # ▶ 標記單位永遠譯為空（舊斷點可能有殘留值）
    results["▶"] = ""

    if args.fix_bad:
        from translate_batch import translate_batch as _tb
        bad_file = PROJECT / "tools" / "bad_quality.json"
        bad = json.loads(bad_file.read_text(encoding="utf-8")) if bad_file.exists() else []
        print(f"品質修復：{len(bad)} 條。")
        ph_re = re.compile(r"=[A-Za-z0-9_.:;|!@/()+\-#']+=")
        fixed = 0
        for key in bad:
            source = key[1:] if key.startswith("▶") else key
            need = sorted(set(ph_re.findall(source)))
            done = False
            for _ in range(4):
                # 使用遮罩管線（translate_single 已整合 placeholder 遮罩＋驗證）
                trans = translate_single(source, args.url, args.model, args.temperature, args.timeout, 0)
                have = sorted(set(ph_re.findall(trans)))
                if have == need:
                    results[key] = trans
                    fixed += 1
                    done = True
                    break
            if not done:
                print(f"  [仍失敗] {source[:60]!r}")
        json.dump(results, open(checkpoint, "w", encoding="utf-8"), ensure_ascii=False)
        print(f"品質修復完成：{fixed}/{len(bad)} 條。")
        sys.exit(0)

    if args.apply:
        print("--apply：只套用已完成的翻譯，不呼叫模型。")
    else:
        # 過濾掉已在斷點中的條目
        pending = [(s, f) for s, f in zip(send_items, unique_items) if f not in results]
        # marker 單位（內容僅 ▶）→ 直接翻成空字串，不呼叫模型
        marker_pending = [p for p in pending if p[1] == "▶"]
        if marker_pending:
            for _, full in marker_pending:
                results[full] = ""
            pending = [p for p in pending if p[1] != "▶"]
            print(f"清除 {len(marker_pending)} 個 ▶ 標記（不呼叫模型）。")
        if len(pending) != len(unique_items):
            print(f"續跑 {len(pending)}/{len(unique_items)} 條。")
        if not pending:
            print("沒有待翻譯條目，直接套用。")
        else:
            # 依長度排序，同長度的字串同批（避免長字串拖慢整批短字串）
            pending.sort(key=lambda p: len(p[0]))
            ps, pf = zip(*pending)
            batches = [list(ps[i : i + args.batch]) for i in range(0, len(ps), args.batch)]
            done = 0
            t0 = time.time()

            def save_checkpoint() -> None:
                tmp = checkpoint.with_suffix(".tmp")
                tmp.write_text(json.dumps(results, ensure_ascii=False), encoding="utf-8")
                tmp.replace(checkpoint)

            def work(batch: list[str]) -> list[tuple[str, str]]:
                with_glossary = [glossary.apply(s) for s in batch] if glossary.is_distinct else batch
                translated = translate_batch(with_glossary, args.url, args.model, args.temperature, args.timeout, args.retries)
                return list(zip(batch, translated))

            if args.workers > 1:
                with ThreadPoolExecutor(max_workers=args.workers) as ex:
                    futs = [ex.submit(work, b) for b in batches]
                    for fut in as_completed(futs):
                        for src, out in fut.result():
                            results[full_of.get(src, src).rstrip()] = out
                        done += 1
                        save_checkpoint()
                        el = time.time() - t0
                        print(f"[{done}/{len(batches)} 批] 已翻 {len(results)}/{len(unique_items)} 條 ({el:.0f}s)", flush=True)
            else:
                for b in batches:
                    for src, out in work(b):
                        results[full_of.get(src, src).rstrip()] = out
                    done += 1
                    save_checkpoint()
                    el = time.time() - t0
                    print(f"[{done}/{len(batches)} 批] 已翻 {len(results)}/{len(unique_items)} 條 ({el:.0f}s, {el/done:.1f}s/批)", flush=True)
            print(f"\n翻譯階段完成（{len(results)} 條）。統一套用至骨架…")

    # 統一套用：每檔依 offset 反向套用
    by_file: dict[Path, list[tuple[tuple[int, int, str, str], str]]] = {}
    for path, unit in units:
        translated = results.get(unit[2].rstrip())
        if translated is not None:
            by_file.setdefault(path, []).append((unit, translated))

    written_files = 0
    total_applied = 0
    failed = 0
    for path, entries in by_file.items():
        text = path.read_text(encoding="utf-8-sig")
        for unit, translated in sorted(entries, key=lambda e: -e[0][0]):
            off, length, content, kind = unit
            if text[off : off + length] != content:
                failed += 1
                continue
            text = text[:off] + escape(translated, kind) + text[off + length :]
            total_applied += 1
        path.write_text("\ufeff" + text, encoding="utf-8")
        written_files += 1

    print(f"完成：寫入 {written_files} 檔、套用 {total_applied} 條（{failed} 條位移跳過）。")
    sys.exit(0)


if __name__ == "__main__":
    main()
