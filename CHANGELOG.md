# Changelog

## v0.2.2 — 2026-08-04

- 修復殘留英文句子（HTML 能力描述、對話）：以本地 AI 翻譯 25 句
  - 含中英混雜句子（模型保留既有中文）
  - 古英文詩（Shomer、Krka 方言）人工潤飾
- 剩餘英文皆為該保留者：人名/公司名（製作人員）、程式註解、ID 原文、括號原文註解
- 更新已安裝 mod（samso）；保留 workshop.json（WorkshopId 3777400827）與 preview.png

## v0.2.1 — 2026-08-04

- 品質修復：placeholder 全數通過、HTML 描述標籤完整、翻譯 98%+
- 批次對齊（編號鍵值）、placeholder/標籤遮蔽機制
- 修復斷點鍵錯配、重疊單位過濾

- **完成約 18,500 條翻譯**（非 Naming 內容 98% 完成，40 檔 XML 全部有效）
- 本機 LLM（gemma-4-26b-a4b-it）批量翻譯，術語表強制統一專名
- 建立 `build_glossary.py`：從遊戲資料抽取 1,839 個權威名稱並翻譯成術語表
  （`glossary.json` LLM 生成 1,853 組 + `glossary_curated.json` 人工策展 34 組）
- `translate_batch.py`：斷點續跑、部分回收、按長度分批、`--exclude`、
  `--apply` 模式、`Glossary` 單一正則高效代換（所有格/複數/shader 處理）
- `fix_consistency.py`：統一專名變體拼法（約帕/巴拉楚姆/恰庫恰等）
- 剩餘：`Naming.zh-tw.xml`（3,428 條程序化命名音節，留待 Phase 4 replacer）

## v0.1.0 — 2026-08-04

- 確立專案：官方 `lang-experimental` 本地化系統、純 mod 做法
- 確認遊戲已內建 `SourceHanMono TC` 正體中文字型
- 建立 manifest.json、Languages.xml（zh-tw）
- 產生 40 檔翻譯骨架（約 25,000 筆待翻譯）
- 建置工具：generate_skeleton.py、check_completion.py
- 建立 GLOSSARY 術語表草案

## v0.3.0 — 2026-08-05

- 命名政策確立：專名（人名/地名/造詞）顯示「音譯(原文)」，如 約帕(Joppa)、莎娜(Shayna)；一般名詞維持純中文
- 補譯 68 個漏譯物品/生物名（chem cell→化學電池、banana→香蕉 等）
- 修復 placeholder 錯字：pronons→pronouns（31 處）、possive→possessive
- PronounSets 一致化（你/你們/他中性）
- 術語表與 fix_consistency 擴充為「音譯(原文)」並強制一致
