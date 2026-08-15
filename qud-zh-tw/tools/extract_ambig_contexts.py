#!/usr/bin/env python3
# 多義詞組合上下文提取（2026-08-14）
# seed 詞 × dll 全串 → 輸出「含 seed 的短片段」聚類清單，供人工標註多義場景譯法。
# 用法: python3 extract_ambig_contexts.py [--min 3] [--max 60] [seed ...]
import json, re, sys, pathlib

ROOT = pathlib.Path(__file__).resolve().parent
SEEDS = ["light", "turn", "run", "right", "left", "clear", "pass", "mark", "check",
         "post", "mean", "beam", "cut", "pick", "lead", "chair", "plant", "drill",
         "scale", "fit", "spring", "kind", "still", "watch", "graze", "buck",
         "charge", "set", "rest", "order", "press", "fine", "fast", "hard", "long"]

def main():
    args = sys.argv[1:]
    minlen, maxlen = 3, 60
    seeds = SEEDS
    if "--min" in args:
        minlen = int(args[args.index("--min") + 1])
    if "--max" in args:
        maxlen = int(args[args.index("--max") + 1])
    pos = [i for i, a in enumerate(args) if not a.startswith("--") and i > 0]
    cand = pathlib.Path(ROOT / "dll_strings.json")
    if not cand.exists():
        print("缺 dll_strings.json"); return 1
    d = json.loads(cand.read_text(encoding="utf-8"))["candidates"]
    texts = [x["text"] for x in d]
    out = {}
    for seed in seeds:
        pat = re.compile(r"\b" + re.escape(seed) + r"\b", re.I)
        # 只收「含 seed 且短」的串（太長或純技術名跳過），並記錄所在句子
        hits = []
        for t in texts:
            if not pat.search(t):
                continue
            if len(t) < minlen or len(t) > maxlen:
                continue
            if re.search(r"(Screen|Menu|Widget|Blueprint|Descript|LevelText|Popup|Template|Label|Header|Option|Setting)", t):
                continue
            hits.append(t)
        if hits:
            out[seed] = sorted(set(hits))
    res = ROOT / "ambig_contexts.json"
    res.write_text(json.dumps(out, ensure_ascii=False, indent=1, sort_keys=True), encoding="utf-8")
    total = sum(len(v) for v in out.values())
    print(f"種子 {len(out)} 個，片段總數 {total} -> {res.name}")
    for k, v in out.items():
        print(f"  {k}: {len(v)}")
    return 0

if __name__ == "__main__":
    sys.exit(main())