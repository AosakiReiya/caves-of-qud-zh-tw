# Agent TODO — 2026-08-12

## 調查已完成（2026-08-12 上午）
- 村名 = `Qudish Site` namestyle 組合生成：prefix(23) × (0-1 infix,16) × postfix(32) ≈ 1.2 萬組合
- `Naming.zh-tw.xml` 351 個中文片段中，部分被「直譯成字面意思」（如 infix `by`→「由」），
  導致新生成村名中英混雜（`Na由lil`）；舊存檔英文村名（Mirappar/Qappir/Sakesh 等 30+）
  靠 ProperNounZh 攔截但目前只覆蓋 8 個
- be 動詞廢字：`=subject.verb:are=`→「是」影響 10+ 模板（RevealedSomewhere、Fully Charged、
  Dehydrated、native to、set on fire 等）
- 動詞詞尾 s：`=verb:harvests=` → VerbZh 無 `harvests` → 回傳原文 →「收割s」
- 聲望模板：`=faction.ifPlural:是:是= 有興趣…` 中文「是」廢字；`=both.andList=.` 英文句號殘留

## 決策（使用者 2026-08-12）
- 村名方案：**通用**（不綁定存檔）→ 修 `Naming.zh-tw` 片段讓遊戲直接生成正確中文名
  - 已移除 ProperNounZh 對執行期村名的攔截（測試存檔綁定無價值）
- 執行順序：Phase 1（村名）→ Phase 2（be 動詞廢字）→ Phase 3（動詞 s）→ Phase 4（聲望模板）

## Phase 1：執行期村名通用化
- [x] 解析 base `Naming.xml` 與 `Naming.zh-tw.xml` 全部 namestyle 片段對照
- [x] 確認根因：namestyle 層 `Load="Merge"` 讓中文片段附加到英文池 → 中英混雜（Na由lil）
- [x] `Qudish Site` → `Load="Replace"`（純中文片段，23 pre × 15 inf × 32 pos）
- [x] 17 個衍生 Site（Qudish X Site）→ `Load="Replace"`（templates 已是中文）
- [x] 修 infix `by→由`（直譯錯誤）→ `拜`（音譯）
- [x] 驗證：中文村名生成正常（娜巴舒爾、米拉普爾 等）
- [x] ProperNounZh 移除執行期村名攔截（通用方案取代）
- [ ] **待使用者重啟驗證**：新存檔村名為純中文、無中英混雜

## Phase 2：be 動詞廢字（=verb:are=/=verb:is= →「是」）
- [ ] 掃描全部含 `=verb:are=`/`=verb:is=` 的 Value 模板
- [ ] 逐條改 Value（中文不需要「是」的移除）
- [ ] 評估 Verb replacer 對 are/is/was/were 的架空處理
- [ ] 測試 + 部署 + commit

## Phase 3：動詞詞尾 s 殘留
- [ ] 掃描 zh-tw 所有 `=verb:`/`=does:` 動詞參數
- [ ] Verb/Does replacer：ReadVerb 後剝英文屈折尾（s/es/ies/ing/ed）再查 VerbZh
- [ ] 測試 + 部署 + commit

## Phase 4：聲望對話模板
- [ ] 修 `=faction.ifPlural:是:是= 有興趣…`（6 條）移除「是」
- [ ] 修 `=both.andList=.` / `=sell.andList=.` 英文句號
- [ ] 配合 Phase 1 確認 `=faction.FormattedName|rules=` 村名輸出
- [ ] 測試 + 部署 + commit

## 驗證
- 每階段：`run_tests.py`(47)、`test_regex_fixes.py`(74)、XML parse、部署、commit
- 最後請使用者重啟遊戲驗證