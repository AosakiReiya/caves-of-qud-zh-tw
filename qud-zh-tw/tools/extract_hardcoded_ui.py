#!/usr/bin/env python3
"""
extract_hardcoded_ui.py — 掃描遊戲 DLL 中所有「非語料涵蓋」的硬編碼 UI 字串。

方法（靜態，離線，不影響 runtime）：
  1. 反編譯 Assembly-CSharp.dll（用 ilspycmd，需 dotnet 環境）；或給 --source-dir 現成反編譯原始碼
  2. 解析所有 .cs：
     - Strings._S("ID") / _S("Context","ID") 呼叫 → (context, id)
     - Popup.*（Show/ShowYesNoCancel/PickOption/AskString/ShowBlock 等）的第一個字串字面值
  3. 交叉比對 zh-tw 語料：
     - _S(Context,ID)：語料有 → 已涵蓋；無 → 待翻譯
     - Popup 字面值：與 UiStringsHook.cs 的 UiPhrases 比對 → 未含 → 待翻譯
  4. 輸出 hardcoded_ui_report.json；可 --translate 餵 gemma 生成中文建議

用法：
  python3 tools/extract_hardcoded_ui.py --print
  python3 tools/extract_hardcoded_ui.py --source-dir /tmp/decomp --print   # 跳過反編譯
  python3 tools/extract_hardcoded_ui.py --translate --limit 50             # 生成中文建議
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]
SKELETON = PROJECT / "zh-tw"
GAME = Path(__file__).resolve().parents[1]
for _ in range(4):
    if (GAME / "CoQ_Data").exists():
        break
    GAME = GAME.parent
DLL = GAME / "CoQ_Data" / "Managed" / "Assembly-CSharp.dll"
UI_HOOK = PROJECT.parent / "qud-zh-tw-replacers" / "UiStringsHook.cs"
DEFAULT_REPORT = PROJECT / "tools" / "hardcoded_ui_report.json"

CJK = re.compile(r"[\u4e00-\u9fff]")

# _S 呼叫：. _S("id") 或 . _S("context", "id")（第一、二個字串引數）
_S_CALL = re.compile(r'\._S\(\s*"((?:[^"\\]|\\.)*)"\s*(?:,\s*"((?:[^"\\]|\\.)*)")?')

# Popup 方法第一個字串引數（Title / Message）
POPUP_METHODS = (
    "Show", "ShowAsync", "ShowYesNoCancel", "PickOption", "AskString", "AskStringAsync",
    "ShowBlock", "ShowBlockPrompt", "ShowBlockSpace", "ShowBlockWithCopy", "ShowFail",
    "ShowFailAsync", "ShowSpace", "ShowKeybindAsync", "ShowProgress", "ShowConversation",
)
_POPUP_RE = re.compile(r'Popup\.(' + "|".join(POPUP_METHODS) + r')\(\s*"((?:[^"\\]|\\.)*)"')

# 非 Popup 的動態訊息呼叫（Fail/ShowFailure/DidX/XDidY 等）的第一個字串引數
_MSG_CALLS = (
    ".Fail(", ".FailAsync(", ".ShowFailure(", ".DidX(", ".XDidY(", ".XDidYToZ(",
    ".WDidXToYWithZ(", ".EmitMessage(", ".Toast(", ".FailPopup(", ".ShowFailPopup(",
)
_MSG_RE = re.compile(r'(' + "|".join(re.escape(c) for c in _MSG_CALLS) + r')\s*"((?:[^"\\]|\\.)*)"')

# XDidYToZ / XDidY / DidXToY / DidX 的 Verb + Preposition frame（"sit", "down on" 等）
# 這些 frame 組裝成 "You {Verb} {Preposition} {X}"，是 TextCleaner 逐詞翻不完整的漏翻源
_FRAME_RE = re.compile(
    r'\.(XDidYToZ|XDidY|DidXToY|DidX)\(\s*[^,]+,\s*"((?:[^"\\]|\\.)*)"\s*(?:,\s*"((?:[^"\\]|\\.)*)")?',
)

# StringBuilder.Append("literal" + var) —— 純字面值片段（parasang 類）
_APPEND_RE = re.compile(r'\.Append\("((?:[^"\\]|\\.)*)"\)')

# TextBuilder.Append("...") 連續鏈的字面值片段（Combat.cs 等組裝句）
# 這些句子用 textBuilder.ToString() → TextCleaner 逐詞翻譯，需要整句 pattern 才能正確翻
_TEXTBUILDER_LIT_RE = re.compile(
    r'\.Append\("((?:[^"\\]|\\.)*)"\)(?:[\s\S]{0,120}?\.Append\("((?:[^"\\]|\\.)*)"\))*',
)

# 欄位/常數初始化字面值（public static readonly string STR_X = "Locations" 等）
# 觸發點：= "literal" 結尾分號，且左側有 string 欄位宣告
_FIELD_ASSIGN_RE = re.compile(r'(?:static\s+)?(?:readonly\s+)?string\s+\w+\s*=\s*"((?:[^"\\]|\\.)*)"\s*;')

# 字串串接的純字面值片段（"History of " + x + "]" 等）
_CONCAT_RE = re.compile(r'"((?:[^"\\]|\\.)*)"\s*\+\s*[^;]+')

# XDidYToZ/XDidY/DidX frame —— 重組 "You {verb} {prep} {object}"（物件名為變數）
# 這些 frame 用 textBuilder 組裝 → TextCleaner，物件名可能未本地化，需整句 pattern
_XDIDYTOZ_RE = re.compile(r'\.XDidYToZ\(\s*\w+\s*,\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"')
_XDIDY_RE = re.compile(r'\.XDidY\(\s*\w+\s*,\s*"((?:[^"\\]|\\.)*)"(?:\s*,\s*"((?:[^"\\]|\\.)*)")?')
_DIDX_RE = re.compile(r'\.DidX\(\s*"((?:[^"\\]|\\.)*)"(?:\s*,\s*"((?:[^"\\]|\\.)*)")?')

# 過濾：無意義 / 純變數 / 純色彩標記
def meaningful(s: str) -> bool:
    s = s.strip()
    if not s or len(s) < 3:
        return False
    if CJK.search(s):
        return False  # 已中文
    stripped = re.sub(r"\{\{[^}]*\}\}", "", s)      # 色彩標記
    stripped = re.sub(r"=[A-Za-z0-9_.:;|!@/()+\-]+=", "", stripped)  # placeholder
    letters = re.sub(r"[^A-Za-z]", "", stripped)
    if len(letters) < 2:
        return False
    return True


def unescape(s: str) -> str:
    return s.replace(r'\"', '"').replace(r"\n", "\n").replace(r"\\", "\\")


def norm_key(s: str) -> str:
    """正規化對比鍵：新行實體(&#xA;/&#10;/&#xD;)統一為 \n，收斂多餘空白。"""
    s = s.replace("&#xA;", "\n").replace("&#10;", "\n").replace("&#xD;", "\r").replace("&#13;", "\r")
    s = s.replace("\r\n", "\n").replace("\r", "\n")
    return s


# ---------- 反編譯 ----------

def find_ilspycmd() -> str | None:
    for cand in (
        shutil.which("ilspycmd"),
        str(Path.home() / ".dotnet/tools/ilspycmd"),
        str(Path.home() / ".local/bin/ilspycmd"),
    ):
        if cand and Path(cand).exists():
            return cand
    return None


def find_dotnet_root() -> str | None:
    """找含 libhostfxr.so 的 dotnet 根目錄（供 ilspycmd 子程序）。"""
    cands = [
        os.environ.get("DOTNET_ROOT"),
        os.environ.get("DOTNET_HOST_PATH"),
        str(Path.home() / ".dotnet"),
        "/tmp/opencode/dotnet",
        "/usr/share/dotnet",
        "/usr/lib/dotnet",
    ]
    for c in cands:
        if c and (Path(c) / "host" / "fxr").is_dir():
            return c
    return None


def decompile(dll: Path, out_dir: Path) -> bool:
    cmd = find_ilspycmd()
    if not cmd:
        print("[警告] 找不到 ilspycmd。請先安裝 dotnet + `dotnet tool install -g ilspycmd`，或用 --source-dir 提供反編譯原始碼。")
        return False
    env = dict(os.environ)
    root = find_dotnet_root()
    if root:
        env["DOTNET_ROOT"] = root
        env["PATH"] = root + os.pathsep + env.get("PATH", "")
    r = subprocess.run([cmd, "-p", "-o", str(out_dir), str(dll)],
                       capture_output=True, text=True, timeout=1200, env=env)
    if r.returncode != 0:
        print("[失敗] ilspycmd:", r.stderr[-500:])
        return False
    return True


# ---------- 語料載入 ----------

def load_corpus() -> tuple[set, set]:
    """回傳 (context_id_set, id_set)。"""
    ctx_ids: set[tuple] = set()
    ids: set[str] = set()
    for f in SKELETON.glob("*.xml"):
        t = f.read_text(encoding="utf-8-sig")
        for m in re.finditer(r"<string\b([^>]*?)>", t):
            attrs = m.group(1)
            c = re.search(r'Context="([^"]*)"', attrs)
            i = re.search(r'ID="([^"]*)"', attrs)
            if i:
                iid = norm_key(i.group(1))
                ids.add(iid)
                ctx_ids.add((c.group(1) if c else None, iid))
    return ctx_ids, ids


def load_ui_phrases() -> dict[str, str]:
    if not UI_HOOK.exists():
        return {}
    t = UI_HOOK.read_text(encoding="utf-8")
    # 只取「UiPhrases」字典（套用到 Popup 的），排除 JournalCategories / SidebarLabels
    # 那些字典未 wire 進日誌畫面，若併入會誤判為「已覆蓋」而漏偵測。
    start = t.find("Dictionary<string, string> UiPhrases")
    if start == -1:
        return {}
    close = t.find("};", start)
    if close == -1:
        return {}
    block = t[start:close]
    phrases = {}
    for m in re.finditer(r'\{\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\}', block):
        key, val = m.group(1), m.group(2)
        if CJK.search(val):
            phrases[key] = val
    return phrases


# ---------- 主流程 ----------

def build_report(source_dir: Path | None) -> dict:
    dll = DLL
    cleanup = None
    if source_dir is None:
        if not dll.exists():
            raise SystemExit(f"找不到 DLL {dll}")
        cleanup = tempfile.mkdtemp(prefix="qud_decomp_")
        print(f"反編譯中…（{dll}）")
        if not decompile(dll, Path(cleanup)):
            raise SystemExit("反編譯失敗")
        source_dir = Path(cleanup)
    else:
        source_dir = Path(source_dir)

    ctx_ids, ids = load_corpus()
    ui_phrases = load_ui_phrases()

    s_missing: dict[str, dict] = {}
    popup_missing: dict[str, dict] = {}
    popup_covered: dict[str, str] = {}
    msg_missing: dict[str, dict] = {}
    frame_missing: dict[str, dict] = {}
    append_missing: dict[str, dict] = {}
    field_missing: dict[str, dict] = {}
    concat_missing: dict[str, dict] = {}
    textbuilder_missing: dict[str, dict] = {}
    xdidytoz_frames: dict[str, dict] = {}

    for f in source_dir.rglob("*.cs"):
        try:
            t = f.read_text(encoding="utf-8", errors="ignore")
        except Exception:
            continue
        for m in _S_CALL.finditer(t):
            if m.group(2):
                ctx = unescape(m.group(1))
                idv = norm_key(unescape(m.group(2)))
            else:
                ctx = None
                idv = norm_key(unescape(m.group(1)))
            if not meaningful(idv):
                continue
            if ctx is not None:
                covered = (ctx, idv) in ctx_ids
                key = f"{ctx}\u0001{idv}"
            else:
                covered = idv in ids
                key = f"\u0001{idv}"
            if not covered:
                entry = s_missing.setdefault(key, {"context": ctx, "id": idv, "count": 0, "files": set()})
                entry["count"] += 1
                entry["files"].add(f.name)
        for m in _POPUP_RE.finditer(t):
            lit = unescape(m.group(2))
            if not meaningful(lit):
                continue
            if lit in ui_phrases:
                popup_covered[lit] = ui_phrases[lit]
                continue
            entry = popup_missing.setdefault(lit, {"count": 0, "files": set(), "interpolated": False})
            entry["count"] += 1
            entry["files"].add(f.name)
            if "{" in lit:
                entry["interpolated"] = True
        for m in _MSG_RE.finditer(t):
            lit = unescape(m.group(2) or "")
            if not meaningful(lit):
                continue
            if lit in ui_phrases:
                popup_covered[lit] = ui_phrases[lit]
                continue
            entry = msg_missing.setdefault(lit, {"count": 0, "files": set(), "interpolated": False})
            entry["count"] += 1
            entry["files"].add(f.name)
            if "{" in lit:
                entry["interpolated"] = True
        for m in _FRAME_RE.finditer(t):
            verb = unescape(m.group(2) or "")
            prep = unescape(m.group(3) or "")
            if not meaningful(verb) or len(verb) < 3:
                continue
            frame = (verb + " " + prep).strip() if prep else verb
            entry = frame_missing.setdefault(frame, {"verb": verb, "prep": prep, "count": 0, "files": set()})
            entry["count"] += 1
            entry["files"].add(f.name)
        for m in _APPEND_RE.finditer(t):
            lit = unescape(m.group(1))
            if not meaningful(lit):
                continue
            if len(lit) < 3:
                continue
            entry = append_missing.setdefault(lit, {"count": 0, "files": set()})
            entry["count"] += 1
            entry["files"].add(f.name)
        # TextBuilder.Append 連續鏈的字面值片段（Combat.cs 組裝句，需整句 pattern）
        for m in _TEXTBUILDER_LIT_RE.finditer(t):
            frags = ",".join(x for x in m.groups() if x)
            if len(frags) < 3:
                continue
            entry = textbuilder_missing.setdefault(frags, {"count": 0, "files": set()})
            entry["count"] += 1
            entry["files"].add(f.name)
        # 欄位/常數初始化字面值（如 JournalScreen 的 STR_LOCATIONS = "Locations"）
        for m in _FIELD_ASSIGN_RE.finditer(t):
            lit = unescape(m.group(1))
            if not meaningful(lit):
                continue
            if lit in ui_phrases:
                continue
            entry = field_missing.setdefault(lit, {"count": 0, "files": set()})
            entry["count"] += 1
            entry["files"].add(f.name)
        # 字串串接字面值片段（如 "[History of " + x + "]"）
        for m in _CONCAT_RE.finditer(t):
            lit = unescape(m.group(1))
            if not meaningful(lit):
                continue
            if len(lit) < 3:
                continue
            entry = concat_missing.setdefault(lit, {"count": 0, "files": set()})
            entry["count"] += 1
            entry["files"].add(f.name)
        # XDidYToZ/XDidY/DidX frame（重組 "You {verb} {prep} {object}"，物件為變數）
        for m in _XDIDYTOZ_RE.finditer(t):
            verb, prep = unescape(m.group(1)), unescape(m.group(2))
            frame = f"You {verb} {prep} {{object}}".strip()
            entry = xdidytoz_frames.setdefault(frame, {"count": 0, "files": set(), "verb": verb, "prep": prep})
            entry["count"] += 1
            entry["files"].add(f.name)
        for m in _XDIDY_RE.finditer(t):
            verb = unescape(m.group(1))
            extra = unescape(m.group(2)) if m.group(2) else ""
            frame = f"You {verb} {extra}".strip()
            entry = xdidytoz_frames.setdefault(frame, {"count": 0, "files": set(), "verb": verb, "prep": extra})
            entry["count"] += 1
            entry["files"].add(f.name)
        for m in _DIDX_RE.finditer(t):
            verb = unescape(m.group(1))
            extra = unescape(m.group(2)) if m.group(2) else ""
            frame = f"You {verb} {extra}".strip()
            entry = xdidytoz_frames.setdefault(frame, {"count": 0, "files": set(), "verb": verb, "prep": extra})
            entry["count"] += 1
            entry["files"].add(f.name)

    if cleanup:
        shutil.rmtree(cleanup, ignore_errors=True)

    def norm(d: dict) -> dict:
        return {k: {**{kk: (sorted(vv) if isinstance(vv, set) else vv) for kk, vv in v.items()}} for k, v in d.items()}

    return {
        "meta": {"dll": str(dll), "source_dir": str(source_dir)},
        "_S_missing": norm(s_missing),
        "popup_missing": norm(popup_missing),
        "popup_covered": popup_covered,
        "msg_missing": norm(msg_missing),
        "append_missing": norm(append_missing),
        "field_missing": norm(field_missing),
        "concat_missing": norm(concat_missing),
        "textbuilder_missing": norm(textbuilder_missing),
        "frame_missing": norm(frame_missing),
        "xdidytoz_frames": norm(xdidytoz_frames),
    }


def print_report(r: dict) -> None:
    sm = r["_S_missing"]
    pm = r["popup_missing"]
    pc = r["popup_covered"]
    print("=" * 62)
    print(f"_S 語料外硬編碼字串：{len(sm)} 個")
    print("=" * 62)
    for key, info in list(sm.items())[:40]:
        ctx = info["context"] or "(無context)"
        print(f"  [{ctx}] {info['id'][:60]}  ×{info['count']}")
    print()
    print("=" * 62)
    print(f"Popup 原始字面值（未收錄）：{len(pm)} 個")
    print("=" * 62)
    for lit, info in list(pm.items())[:40]:
        print(f"  {lit[:60]:<62} ×{info['count']}")
    print()
    print(f"Popup 已收錄（UiPhrases）：{len(pc)} 個")
    for lit, zh in list(pc.items()):
        print(f"  ✓ {lit[:40]:<42} → {zh}")


def main() -> None:
    ap = argparse.ArgumentParser(description="掃描 DLL 硬編碼 UI 字串")
    ap.add_argument("--source-dir", default=None, help="現成反編譯原始碼目錄（跳過 ilspycmd）")
    ap.add_argument("--print", action="store_true")
    ap.add_argument("--report", default=str(DEFAULT_REPORT))
    ap.add_argument("--no-report", action="store_true")
    ap.add_argument("--translate", action="store_true", help="把 popup_missing 餵 gemma 生成中文（需本機 LLM API）")
    ap.add_argument("--limit", type=int, default=0)
    args = ap.parse_args()

    report = build_report(Path(args.source_dir) if args.source_dir else None)
    if not args.no_report:
        Path(args.report).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"報表已寫入 {args.report}")
    if args.print or args.no_report:
        print_report(report)

    if args.translate:
        from translate_batch import translate_single, API_URL, MODEL
        # 合併 popup_missing 與 msg_missing（皆為完整字面值/動態片段）
        items = {}
        for bucket in ("popup_missing", "msg_missing"):
            for lit, info in report.get(bucket, {}).items():
                items.setdefault(lit, info)
        if args.limit:
            items = dict(list(items.items())[: args.limit])
        print(f"\n--translate：依序翻譯 {len(items)} 個硬編碼字串…")
        out = {}
        for lit, info in items.items():
            zh = translate_single(lit, API_URL, MODEL, 0.2, 120, 2)
            if zh and CJK.search(zh) and zh != lit:
                out[lit] = {"zh": zh, "count": info["count"]}
                print(f"  ✓ {lit[:44]:<46} → {zh}")
            else:
                out[lit] = {"zh": None, "count": info["count"]}
                print(f"  ✗ 失敗 {lit[:50]}")
        sugg = PROJECT / "tools" / "hardcoded_ui_suggestions.json"
        sugg.write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"\n建議已寫入 {sugg}，審核後填入 UiStringsHook.cs 的 UiPhrases。")


if __name__ == "__main__":
    main()