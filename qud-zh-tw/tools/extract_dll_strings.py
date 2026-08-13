#!/usr/bin/env python3
"""
extract_dll_strings.py — 離線提取遊戲障礙 dll 的硬編碼字串（#US user-string heap），
分類「可譯 UI 文本 vs 技術 ID」，輸出候選清單供 run_pipeline translate。

不載入 dll、不掛 hook、不影響遊戲性能。

用法：
  python3 tools/extract_dll_strings.py            # 全部 dll 字串 + 候選
  python3 tools/extract_dll_strings.py --top 40   # 只看統計與前 40 條候選
"""
import argparse
import json
import re
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROJ = ROOT.parent
REPL = PROJ.parent / "qud-zh-tw-replacers"


def find_dll():
    for base in ("/mnt/g/SteamLibrary/steamapps/common/Caves of Qud",):
        p = Path(base) / "CoQ_Data/Managed/Assembly-CSharp.dll"
        if p.exists():
            return p
    return None


def parse_us_heap(data, meta_offset, size):
    """解析 #US heap（ECMA-335）：[compressed len][utf16][00/01]..."""
    out = []
    pos = 0
    while pos < size:
        b0 = data[meta_offset + pos]
        if b0 < 0x80:
            ln, hdr = b0, 1
        elif b0 < 0xC0:
            ln = ((b0 & 0x3F) << 8) | data[meta_offset + pos + 1]
            hdr = 2
        elif b0 < 0xE0:
            ln = ((b0 & 0x1F) << 24) | (data[meta_offset + pos + 1] << 16) | (data[meta_offset + pos + 2] << 8) | data[meta_offset + pos + 3]
            hdr = 4
        else:
            pos += 1
            continue
        raw = data[meta_offset + pos + hdr: meta_offset + pos + hdr + ln]
        if ln > 1 and raw[-1] in (0x00, 0x01):
            raw = raw[:-1]
        try:
            s = raw.decode("utf-16-le")
        except Exception:
            s = ""
        out.append(s)
        total = hdr + ln
        pos += total if total > 0 else 1
    return out


TECH_RE = re.compile(
    r"^(XRL\.|Mono\.|Unity|System\.|Ansi|UTF|\./|\./[\w/]+|[\w]+\\[\w\\]+|[A-Z0-9_]{6,}$|[\w./-]+\.(dll|cs|png|jpg|ogg|txt|xml|wav|json|asset|prefab)$|^0x[0-9A-Fa-f]+$|^[A-Fa-f0-9]{6}$|^[\d.]+$|^#\w+$|^_|\w+_\w+$|^\{.*\}$|^\s)")
IMMEDIATE_SHORT = re.compile(r"^[A-Z]{1,3}\d*$")  # DV / HP / XP 等縮寫

# 系統/除錯開啟字（句子類過濾）：非玩家可見文本
SYS_STARTS = (
    "time taken", "file:", "line:", "expected", "unknown ", "attempting", "duplicate",
    "should be", "can't find", "not found", "bad ", "invalid", "error", "exception",
    "missing", "unsupported", "null", "entered", "shutdown", "starting", "found ",
    "see logs", "halting", "ready for", "is not a", "with different", "none",
    "platform", "main camera", "thread", "canvas host", "phase", "noise",
    "overlay", "coeffs", "scanlines", "distortion", "amount", "constant",
    "texture", "material", "shader", "mesh", "prefab", "dll", "xml", "json",
    "loading ", "initializ", "init ", "setup", "cleanup", "register",
    "getcomponent", "addcomponent", "destroy", "instantiate", "serialize",
    "deserialize", "is null", "has been", "will be", "went ", "while ", "when ",
    "the ", "a ", "an ", "in ", "on ", "of ", "to ", "for ", "with ", "as ",
    "ssss", "colons", "clamp", "ping", "raycast", "navmesh", "aabb",
)


def classify(s):
    if len(s) < 3 or len(s) > 200:
        return "skip"
    if not all(0x20 <= ord(c) < 0x7f or c in "–—…“”‘’" for c in s):
        return "skip"  # 非純 ASCII（含中文/特殊排版）→ 語料層處理
    if TECH_RE.match(s):
        return "tech"
    if IMMEDIATE_SHORT.match(s):
        return "tech"
    if s.startswith("{{"):
        return "tech"  # 除錯 dump 模板
    # 可譯 UI 文本：含空格（句子/片語）或佔位符（模板標籤）
    has_ws = " " in s
    has_ph = "{0}" in s or "=name=" in s
    low = s.lower()
    if has_ws:
        if any(low.startswith(x) for x in SYS_STARTS):
            return "sys"
        if " " in s and len(s) > 120:
            return "skip"
        if "{" in s and "}" in s and len(s) < 60:
            return "skip"  # 除錯格式模板
        return "candidate"
    if has_ph:
        return "candidate"
    # 單詞級（無空格）：UI 標籤/短語，長度≥5 且非全大寫
    if 5 <= len(s) <= 40 and not s.isupper():
        return "candidate_word"
    return "tech"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--top", type=int, default=30)
    ap.add_argument("--out", default=str(ROOT / "dll_strings.json"))
    a = ap.parse_args()

    dll = find_dll()
    if dll is None:
        print("找不到 Assembly-CSharp.dll")
        sys.exit(1)
    data = Path(dll).read_bytes()
    # 找 metadata root 與 #US stream（搜子串「#US」於 CLI header 之後）
    cli = data.find(b"BSJB")
    if cli < 0:
        print("無 .NET 元資料（非托管 dll？）")
        sys.exit(1)
    # 元資料 root：#~ 前 4 字節 magic；#US 一般緊隨 #Strings/#GUID/#Blob
    meta = cli + 69 if False else data.find(b"#Strings")
    if meta < 0:
        meta = cli + 72
    # 在 meta root 後找 #~ ... 的 stream 表，再找 #US 的 offset/size
    try:
        import dnfile
        pe = dnfile.dnPE(str(dll))
        us = pe.net.user_strings
        size = int(us.struct.Size)
        raw = us.get_data_at_offset(0, size)
        strings = parse_us_heap(raw, 0, len(raw))
    except Exception as e:
        print("dnfile 解析失敗：", e)
        sys.exit(1)

    seen = {}
    for s in strings:
        if not s:
            continue
        cat = classify(s)
        if cat in ("candidate", "candidate_word"):
            seen.setdefault(s, 0)
            seen[s] += 1

    candidates = sorted(seen.items(), key=lambda kv: -kv[1])
    report = {
        "total_strings": len(strings),
        "candidates": [{"text": s, "freq": f} for s, f in candidates],
    }
    out = Path(a.out)
    out.write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"dll 字串總數: {len(strings)} ; 可譯候選: {len(candidates)}")
    print(f"輸出: {out}")
    for s, f in candidates[:a.top]:
        print(f"  x{f}  {s[:90]}")

    # ---- UI 標籤子集（高價值首批翻譯）：句子類且以 UI 操作詞開頭 ----
    ui_heads = ("previous", "next", "use ", "toggle", "select", "open ", "close ", "view ",
                "save", "load", "new ", "add ", "remove", "edit", "search", "character",
                "ability", "page ", "navigate", "take a step", "map pin", "continue",
                "quit", "start", "stop ", "enable", "disable", "rename", "delete",
                "extract", "recover", "repair", "refill", "sort", "filter", "confirm",
                "cancel", "reset", "restore", "equip", "unequip", "examine", "inspect")
    ui = []
    for s, f in candidates:
        low = s.lower()
        if " " not in s:
            continue
        if not any(low.startswith(h) for h in ui_heads):
            continue
        if any(t in low for t in ("is null", "shutdown", "initialized", "platform",
                                  "device", "region", "thread", "camera", "leaderboard",
                                  "navcategory", "defaulting", "complete", "logger")):
            continue
        if len(s) > 90:
            continue
        ui.append({"text": s, "freq": f})
    ui_out = Path(a.out).with_name("ui_labels.json")
    ui_out.write_text(json.dumps(ui, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\nUI 標籤子集: {len(ui)} 條 → {ui_out}")
    for x in ui[:a.top]:
        print("  U:", x["text"][:80])


if __name__ == "__main__":
    main()