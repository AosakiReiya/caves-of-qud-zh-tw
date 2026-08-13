#!/usr/bin/env python3
"""
extract_msg_templates.py — 提取「訊息模板」（動態組裝句子）供 SentenceDict 離線翻譯。

來源：dll 候選中的句子類（含動詞骨架，非 UI 標籤、非技術/系統字串）。
特徵：含 2+ 空格、含高頻訊息動詞（gain/lose/receive/emits/waits/must/stops/
begins/attempts/create/breaks/dies/fall/eat/throw/use 等）、無技術特徵。

輸出：msg_templates.json（僅「無變量」完整句優先）+ msg_templates_all.json
用法：python3 tools/extract_msg_templates.py
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent

MSG_VERBS = re.compile(
    r"\b(gain|gains|gained|lose|loses|lost|receive|receives|received|emits|emitted|emit|"
    r"waits|wait|must|stops|stopped|stop|begins|begin|attempts|attempted|create|creates|"
    r"breaks|broke|dies|died|falls|fall|eats|eats|throw|throws|use|uses|used|enters|"
    r"leaves|leaves|cracks|shatters|collapses|awakes|wakes|rises|rises|bursts|splashes|"
    r"burns|freezes|cools|heats|chills|stuns|dazes|blinds|clutches|breaks off|shatters)\w*",
    re.I)

TECH_HINT = re.compile(
    r"is null|shutdown|initialized|platform|device|region|thread|camera|leaderboard|"
    r"navcategory|defaulting|logger|sound|prefab|texture|material|shader|mesh|dll|xml|"
    r"json|exception|error|debug|trace|log(ged|ging|ger)?\b|config|setting|option|"
    r"keycode|input|axis|button|scr.", re.I)


def main():
    src = ROOT / "dll_strings.json"
    if not src.exists():
        print("需要先跑 extract_dll_strings.py 產生 dll_strings.json")
        sys.exit(1)
    data = json.loads(src.read_text(encoding="utf-8"))
    cands = data["candidates"]
    found = {}
    for c in cands:
        s = c["text"]
        if not s or " " not in s:
            continue
        if len(s) > 160 or len(s) < 12:
            continue
        if TECH_HINT.search(s):
            continue
        if not MSG_VERBS.search(s):
            continue
        if "{" in s and "}" in s and "{}" not in s.replace("{0}", "").replace("{1}", "").replace("{2}", "").replace("{3}", ""):
            continue
        # 系統/框架訊息黑名單（非玩家文本）
        if re.search(
                r"lean|index ?buffer|steam workshop|accessibility|tts|singleton|autoscope|"
                r"treeview|filemode|file ?didn|could not create file|create?directory|"
                r"vector path|prefer using|must be overridden|not supported for|"
                r"example text|begin-end|macos|windows|failed to create|"
                r"created ?:|submitting update|requesting a new|standalonefilebrowser|"
                r"ascendanimation|cpuboost|lambda|delegate|method|class|interface", s, re.I):
            continue
        found[s] = found.get(s, 0) + 1

    items = sorted(found.items(), key=lambda kv: -kv[1])
    # 全量 + 無變量優先子集
    all_out = [{"text": s, "freq": f} for s, f in items]
    simple = [x for x in all_out if not re.search(r"\{(0|1|2|3|4)|=name=|\.name=", x["text"])]
    (ROOT / "msg_templates_all.json").write_text(
        json.dumps(all_out, ensure_ascii=False, indent=1), encoding="utf-8")
    (ROOT / "msg_templates.json").write_text(
        json.dumps(simple, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"訊息模板候選: {len(all_out)}（無變量優先: {len(simple)}）")
    for x in simple[:25]:
        print("  M:", x["text"][:90])


if __name__ == "__main__":
    main()