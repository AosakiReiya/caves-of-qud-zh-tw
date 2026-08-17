# Changelog

## v0.5.0 — 2026-08-17（大更新）

- **角色創建頁「兩棲的(」斷裂根治**：RestoreAll 交替循環還原嵌套佔位符（`\x00`/`\x01`
  四種組合），「=mutationName= ({{r|D}})」缺陷標記完整顯示
- **翻譯審計管道**（`tools/audit_translation.py`，接入 run_tests 全量鎖死）：
  - C1 括號空格統一（語料 109 處 + replacer 115 處）
  - C2 月名 13 條全「中文(原文)」無空格（含補譯 Ut yara Ux）
  - C3 種族級「中文(原文)」：山羊族(Goatfolk)、咬顎獸(Snapjaw)、岩民(Cragmensch)、
    巨魔(Troll)、食人族(Cannibal)
  - C5 語料層 vs replacer 譯名衝突：火炬→火把、樹木→樹、牆壁→牆、尾刺→刺針
  - C7 中文條目殘留英文巡檢（emote/聖地句等 7 處補譯）
  - C9 跨字典重複 key 衝突：29 個技能名雙軌統一（以語料層為準）
  - C10 replacer 括號空格 84 處；C11 專名缺 ProperNounZh 風險清單
- **動態生成專名整段保護**：activate-in-ivory / Ivory-in-Motion 類引擎生成名不再
  被逐詞拆成「啟動-在-象牙」（RestoreAll 第三路 `\x02` 交替還原）
- **殘留英文清洗**：「14th」→「14 日」（排除 1/10th、3rd dimension 誤傷）、
  「stratum/strata」→「層」、「Eq」→「裝備」
- **術語統一**：顎骨獸→咬顎獸（17 處）、史托普斯瓦林/快顎/快咬者系列→止濤者/
  咬顎獸(Snapjaw)、心臟停搏→驟停、frozen→凍結、Baetyls→貝提爾
- **ProperNounZh 補專名**：Kindrish、Bey Lah、Eskhind、Baetyl、Lithofex、
  Decarbonizer、Timereaver、Golgotha、Stopsvaalinn
- **裝備欄 7 槽位名補中**：臉部/頭部/身體/右手/左手/左臂/右臂（僅入 TmpWords 白名單）
- run_tests 220 PASS / 0 FAIL

## v0.2.3 — 2026-08-12

- 還原被改壞的 string ID：
  - `Strings.zh-tw.xml`：79 → 0（含自閉合 `Value=` 格式）
  - `Strings.Conversations.zh-tw.xml`：593 → 0（正規化 `&#10;`/`&#xA;` 換行實體、
    放寬專名 regex、動詞/詞序定向映射）
- `does:` 主詞消失修復；blocker 擋路句去多餘「是」；does:are 贅詞優化
- 殘留英文詞修復（`fix_residual_english.py`）：parasangs→帕拉桑、glotrot→舌腐症、
  glotrot markup、ironshank、drame 等
- ProperNounZh 新增執行期程序生名（本地 LLM 音譯）：Uppar→厄帕、Naalil→納利爾、
  Cherubim、Girsh、Mechanimists、cragmensch、Baetyls、dromad merchants
- RESISTANCES / SECONDARY ATTRIBUTES 標記暫緩（來源在 level0 場景，不影響遊玩）

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
