#!/usr/bin/env python3
"""gen_popup_cs.py — 從 popup_leaks.json 生成 UiStringsHook 的動態 Popup regex 表。"""
import json, re
from pathlib import Path

LEAKS = Path(__file__).resolve().parent / "popup_leaks.json"
OUT = Path(__file__).resolve().parent / "popup_leaks_generated.cs"


def to_regex(template):
    # 解 C# 跳脫：\" → "、\\\\ → \
    template = template.replace('\\\\', '\\').replace('\\"', '"')
    parts = re.split(r'\{(\d+)\}', template)
    out = []
    for i, p in enumerate(parts):
        if i % 2 == 1:
            out.append('(.+?)')
        else:
            # 保護 \n 不被 re.escape 弄成 \\n，暫存後還原
            p = p.replace('\\n', '\x00N')
            esc = re.escape(p).replace('\\ ', ' ')
            esc = esc.replace('\x00N', '\\n')
            out.append(esc)
    return ''.join(out)


def main():
    d = json.loads(LEAKS.read_text(encoding="utf-8"))
    entries = []
    for key, v in d.items():
        zh = v.get("zh")
        if not zh:
            continue
        regex = to_regex(key)
        # regex 用 C# verbatim 字串 @"..."（\ 為字面），只需處理 " 與注音號
        rc = regex.replace('"', '""')
        # replacement 用一般 "" 字串：\n 保留為 C# 跳脫，其餘 \ 雙寫
        zc = (zh
              .replace('\\n', '\x00N')   # 先保護 \n
              .replace('\\', '\\\\')     # 其他 \ 雙寫
              .replace('"', '\\"')
              .replace('\x00N', '\\n'))  # 還原 \n
        entries.append((rc, zc))

    lines = []
    lines.append("    // ===== 動態 Popup 意譯（gemma 生成 + 審核）=====")
    lines.append("    private static readonly System.Collections.Generic.List<System.Tuple<System.Text.RegularExpressions.Regex, string>> DynamicPopupPatterns =")
    lines.append("        new System.Collections.Generic.List<System.Tuple<System.Text.RegularExpressions.Regex, string>>")
    lines.append("        {")
    for i, (rc, zc) in enumerate(entries):
        lines.append(f'            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^{rc}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "{zc}"),')
    lines.append("        };")
    lines.append("")
    lines.append("    private static string ApplyDynamicPopup(string msg)")
    lines.append("    {")
    lines.append("        foreach (var t in DynamicPopupPatterns)")
    lines.append("        {")
    lines.append("            var m = t.Item1.Match(msg);")
    lines.append("            if (!m.Success) continue;")
    lines.append("            string r = t.Item2;")
    lines.append("            for (int gi = 1; gi < m.Groups.Count; gi++)")
    lines.append("                r = r.Replace(" + '"' + "{" + '"' + " + gi + " + '"' + "}" + '"' + ", m.Groups[gi].Value);")
    lines.append("            return r;")
    lines.append("        }")
    lines.append("        return null;")
    lines.append("    }")
    OUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"已生成 {len(entries)} 條 → {OUT}")


if __name__ == "__main__":
    main()