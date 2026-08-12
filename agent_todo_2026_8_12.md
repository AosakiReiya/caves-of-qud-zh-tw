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

## 固定 vs 生成（調查完成 2026-08-12）
- 固定地點名（DLL/藍圖硬編碼）：Joppa/Kyakukya/Ezra/Mopango/Bey Lah/Grit Gate/Omonporch/
  Six Day Stilt/Bethesda Susa/Golgotha/Chavvah 已定名；**Agolgot/Rermadon/Shug'ruith/Brightsheol 未定名**
  → 加入 ProperNounZh（格式 中文(English)）
- 生成村名：Qudish Site 組合（30 個驗證在組合內），方案 = Naming.zh-tw Load=Replace 純中文
- 生成派系複合名（Shupparxfaundren/TonguetShumrod 等）：歷史事件動態拼接，執行期組合，
  無法窮舉 → **記錄為最後追加優化 todo**

## Phase 2：be 動詞廢字（=verb:are=/=verb:is= →「是」）
- [x] 掃描全部含 `=verb:are=` 的 Value 模板（11 條）
- [x] 逐條改 Value（移除「是」+ 調整語序）：crack 句、脫水×2、點燃、寒冷減速、充滿電、
      原生於、有價值、重、Slam、Swarm Alpha
- [x] 保留 Mutations 的「=角.verb:are= 是」（繫動詞語意必需）
- [x] 測試 + 部署 + commit

## Phase 3：動詞詞尾 s 殘留
- [x] 掃描 zh-tw 所有 `=verb:`/`=does:` 動詞參數（74 個全覆蓋）
- [x] 修 9 處 zh 重複動詞筆誤（採集 採集、壓縮 壓縮、處於 處於、似乎 似乎、看起來 看起來、
      前進 前進、收集 收集、將 將、內部 內部）
- [x] Verb/Does/ItDoes/DoesZh 改用 LookupVerbZh（剝 s/es/ies 再查表，harvests→收割）
- [x] 新增 test_verb_inflection 防回歸（53 PASS）
- [x] 測試 + 部署 + commit

## Phase 4：聲望對話模板
- [ ] 修 `=faction.ifPlural:是:是= 有興趣…`（6 條）移除「是」
- [ ] 修 `=both.andList=.` / `=sell.andList=.` 英文句號
- [ ] 配合 Phase 1 確認 `=faction.FormattedName|rules=` 村名輸出
- [ ] 測試 + 部署 + commit

## 驗證
- 每階段：`run_tests.py`(47)、`test_regex_fixes.py`(74)、XML parse、部署、commit
- 最後請使用者重啟遊戲驗證

## 追加優化（最後處理）
- [ ] 生成派系複合名（Shupparxfaundren/TonguetShumrod 等）：歷史事件動態拼接的完整名詞短語
      （「Fermented TonguetShumrod 的村民」），執行期組合無法窮舉。
      後續方向：調查 spice 的生成模板（形容詞+村名+村民），若村名已中文則新檔自動修好；
      舊檔不攔截。記錄已知 30 生成村名音譯表於 `village_zh.json`（已存 /tmp/opencode）。