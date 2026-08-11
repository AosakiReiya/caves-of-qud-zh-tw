# 2026-08-09 修復與溯源紀錄

本文件記錄本次（2026-08-09）針對三處遊戲內 bug 的修復，以及**動態前綴 LLM 意譯**的風險與溯源資訊。

測試腳本：`qud-zh-tw/tools/test_regex_fixes.py`（`python3 tools/test_regex_fixes.py`，13 項全過）。

---

## 一、Bug 1：`Philosophical` 殘留（spice 陣列未取代）

### 現象
蘇丹歷史句出現英文殘留：`...充斥著燒毀的書籍與腐蝕的數據磁碟, 以至於它被重新命名為 這份 Philosophical 殘骸。`

### 根因
遊戲 `HistoryKit/HistoricSpice.cs` 的 `MergeModJson` 對 **JArray 採「附加」而非「取代」**
（L257-262：`((JArray)SpiceRoot[item.Name]).Add(item2)`）。
因此 mod 的 `historyspice.zh-tw.json` 中 `scholarship.adjectives` 中文陣列
（`["哲學性的","精明的","好奇的"]`）被**附加**到遊戲英文陣列
（`["philosophical","shrewd","inquisitive"]`）之後。`!random` 有時抽到英文。
而字串型 `ruinReason`（L266 走 else 分支）被正確取代，故「燒毀的書籍…」已譯。

### 修法（全量 `=` 後綴）
- 將 `historyspice.zh-tw.json` 全部 **1371 個陣列 key** 加上 `=` 後綴（如 `"adjectives="`）。
  遊戲 L246-249：key 以 `=` 結尾 → `SpiceRoot[name] = item.Value`（整批取代）。
- 已同步 `qud-zh-tw/release/qud-zh-tw/historyspice.zh-tw.json`。
- **額外兜底**：`TextCleanerHook.cs` PhraseLeaks 加入
  `philosophical→哲學性的`、`shrewd→精明的`、`inquisitive→好奇的`
  （防 MergeModJson 未取代時執行期殘留）。

### 驗證
`test_regex_fixes.py --spice`：1371 陣列 key 全帶 `=`，scholarship.adjectives= 為中文。

---

## 二、Bug 2：`雷舍夫 (Resheph) (雷舍夫(Resheph))` 名稱重複

### 現象
蘇丹福音句重複輸出：`...雷舍夫 (Resheph) (雷舍夫(Resheph)) 淨化了...`（Abram 同病）。

### 根因
遊戲自身 `Strings.zh-tw.xml`（`Resheph Gospel (cleanses gyre)`）已用**全形括號**
`雷舍夫（Resheph）`、`亞伯拉罕（Abram）` 翻譯。mod 的 `NameToken` 負向 lookbehind
`(?<![\u4e00-\u9fff(#])` 只排除半形 `(`（U+0028）與 `#`，**未排除全形 `（`（U+FF08）**，
於是 `Resheph` 在 `（Resheph）` 內被再次改名 → `雷舍夫（雷舍夫(Resheph)）`。

### 修法
`TextCleanerHook.cs`：
- `NameToken` lookbehind 加入 `\uFF08`：
  `(?<![\u4e00-\u9fff(#\uFF08])`
- `CjkNameRefmt` lookbehind 加入 `\uFF08`、lookahead 排除 `\uFF08` 後接括號：
  `(?<![\u4e00-\u9fff(\uFF08])(雷舍夫|...)(?![\u4e00-\u9fff(\uFF08\s]*[\u0028\uFF08])`

### 驗證
`test_regex_fixes.py --name`：全形括號句不再重複、半形括號不受影響、純英文名仍可譯。

---

## 三、Bug 3：`lying on` / `prone` 狀態標籤未譯

### 現象
生物名稱旁狀態標籤殘留英文：`水藤農夫 [lying on 刻印 床]`。

### 根因
Prone 效果在 `GetDisplayNameEvent` 尾端附加 `[{{B|lying on X}}]` / `[{{B|prone}}]`。
此字串走 **DisplayName/nameplate 路徑，不經過 `TextBuilder.ToString()`**
（`TextCleaner` 原本只 hook 它），故 `Clean()` 不執行；且 `lying`（現在分詞）不在 Words 字典。

### 修法
`TextCleanerHook.cs`：
- 新增 `GetDisplayNameEvent.GetFor`（static、回傳 string）的 **postfix hook**
  `DisplayNamePostfix`，在 `Init()` 註冊。
- 用廉價 `IndexOf` 先篩（GetFor 極熱）再正則：
  - `{{B|lying on X}}` → `{{B|躺在 X 上}}`
  - `{{B|prone}}` → `{{B|俯臥}}`
- 效能守衛：`Length > 120` 直接返回。

### 驗證
`test_regex_fixes.py --prone`：lying on→躺在...上、prone→俯臥、無狀態不變。

---

## 四、動態前綴 LLM 意譯（149 條）風險與溯源

> 由 `gen_msg_hardening.py` 從 `msg_missing`（Fail/ShowFailure 訊息）生成。
> **這批為 LLM 片段意譯，泛用前綴有過度匹配風險，遊戲內發現誤譯可刪單條。**

### 重要發現（本次才修正）
前一階段（2026-08-09）的 `msg_dynadd_new.txt` 去重邏輯有缺陷，
導致 **149 條動態前綴實際只插入了 1 條**（"You are being watched" 多行句）。
本次已用修正後的去重邏輯重新產生 `msg_dynadd.txt` 並**完整插入 146 條**
（去重後為 146，非 149）至 `UiStringsHook.cs` DynamicPopupPatterns。

### 泛用前綴（高風險，`test_regex_fixes.py --dyn` 標記 19 條）
以下前綴過短或過度通用，可能匹配其他相鄰訊息，**若遊戲內誤譯請優先檢查/刪除**：

```
Choose\ a
Debug\ step
Debug:
DisplayName:
Generated
Talking\ to
You\ bothered
You\ discover
You\ eat
You\ eject
You\ extricate
You\ gain
You\ imbue
You\ name
You\ realize
You\ receive
You\ repair
You\ slot
Your\ companion,
```

### 完整 146 條清單
見 `qud-zh-tw/tools/msg_dynadd_new.txt`（已插入 C# 的 146 條）與
`qud-zh-tw/tools/dynamic_prefix_risks.json`（結構化風險目錄）。

### 溯源機制
- 完整前綴清單：`qud-zh-tw/tools/msg_dynadd.txt`（148 條 LLM 意譯原文）
- 已插入 C#：`qud-zh-tw-replacers/UiStringsHook.cs` `DynamicPopupPatterns`
- 驗證：`qud-zh-tw/tools/test_regex_fixes.py --dyn`
- 若需刪單條：從 `UiStringsHook.cs` DynamicPopupPatterns 移除該 `Tuple.Create` 行即可。

### 剩餘風險說明
- 泛用前綴（如上 19 條）可能誤匹配其他以相同字串開頭的訊息。
- 前綴片段（如 `You cannot slam `）執行期才組裝成完整句，原文非完整句，
  LLM 意譯可能與完整句語境有出入。
- 建議遊戲內遊玩時留意此類訊息，發現誤譯回溯 `msg_dynadd.txt` 對應行並刪除。
---

## 五、日誌畫面漏翻（tab 名 + [History of X] 標題列）

### 現象
- 日誌分頁標籤全英文：`Locations`、`Gossip and Lore`、`Sultan Histories`、
  `Village Histories`、`Chronology`、`General Notes`、`Recipes`。
- 蘇丹/村莊標題列：`[+] HISTORY 的 瑞謝夫 (RESHEPH)`（`[History of X]` wrapper 英文）。

### 根因
- tab 名來自 `JournalScreen.cs` L77-89 硬編碼 `STR_*` 常數，渲染走
  `ScreenBuffer.Write` → `SidebarLabelPostfix`，但該 postfix **只處理 `SidebarLabels`**
  （ST/AG/DV），日誌 tab 名不在其中 → 漏翻。
- `JournalCategories` 字典（`蘇丹歷史`/`閒談與傳說` 等）雖已存在，但**從未套用到日誌畫面**，
  只在 popup 的 `JournalNoteRegex` 使用。
- `[History of X]`（L303/311）字面值英文；純英文時 `Clean()` 提前返回，混雜時 `of→的`。

### 修法（`UiStringsHook.cs` `SidebarLabelPostfix`）
- 新增 `{{X|...}}` markup 剝離（JournalMarkup），再用 `JournalCategories` 精確匹配 tab 名。
- 新增 `HistoryOf` 正則：`^\[History (?:of|的) (.+?)(?:, Vol\. (.+?))?\]$`
  → `[X 的歷史]` / `[X 第 N 卷的歷史]`（相容已把 of→的 的形式）。
- 保留 `Length > 40` 效能守衛。

### 說明
- `瑞謝夫 (RESHEPH)` 是遊戲**執行期 Naming 系統**自產的蘇丹譯名，非 mod 產生；
  修 `[History of X]` 後標題列變為 `[瑞謝夫 (RESHEPH) 的歷史]`。

---

## 六、檢測英文腳本為何沒搜到日誌字串（檢測缺口）

### 根因
`extract_hardcoded_ui.py` 的 `load_ui_phrases()` 讀取 UiStringsHook.cs **所有**
`{ "key", "value" }` 對，**誤併入 `JournalCategories` 字典**。因 `JournalCategories`
的 key（`Locations`/`Sultan Histories` 等）被當成「已覆蓋」，萃取器就跳過它們，
即使它們**從未套用到日誌畫面**。

### 修法（`extract_hardcoded_ui.py`）
1. `load_ui_phrases()` 改為**只讀 `UiPhrases` 字典區塊**（`Dictionary<string,string> UiPhrases`
   到 `};`），排除 `JournalCategories`/`SidebarLabels`。
2. 新增兩種萃取模式：
   - `_FIELD_ASSIGN_RE`：欄位/常數初始化字面值（`STR_X = "Locations"`）→ `field_missing`
   - `_CONCAT_RE`：字串串接字面值片段（`"[History of " + x + "]"`）→ `concat_missing`
3. 修後：`Locations`/`Sultan Histories` 等 7 個 tab 名正確落入 `field_missing`。

### 其他檢測工具範圍限制
- `find_untranslated.py` / `check_completion.py` / `check_quality.py` 只掃 `zh-tw` XML 骨架，
  **不掃 replacer C# 或反編譯遊戲原始碼**。
- `scan_replacer_log.py` 依賴 `replacer_log.txt`（只記錄 `AddMsgPrefix` 訊息列），
  日誌畫面走 `ScreenBuffer.Write`，不會進 log。

---

## 七、追加：field 初始化訊息（飲料/食物/效果提示）

由 `field_missing` 萃取 + LLM 意譯，加入 UiPhrases（17 條）：
`That hits the spot!`、`Brightness burns your mouth.`、`You feel unsettlingly ambivalent.`、
`You hear inaudible mumbling.`、`The liquids stop reacting.`、`The poison begins to abate...`、
`There is a low, persistent hum...`、`You cooled into a block of shale.`、
`Poisonous goo burns your eyes.`、`A giant centipede crawls out of the nest.`、
`You drank the bright cream of the Palladium Reef...`、`You taste life as it was distilled...`、
`Charmed by another creature into following them.`、`Brown sludge splashes...`、
`Putrid ooze splashes...`、`You notice some strange ruins nearby...`、
`Brightness burns your mouth, but you cannot be roused any higher.`

---

## 八、2026-08-09（第二次）漏翻追查：檢測腳本為何沒檢測全 + 三處漏翻根因

### 為何「之前修了還是漏」─ 檢測腳本與 hook 的根本問題

**A. `SidebarLabelPostfix` 是 postfix 且 hook 錯 overload → 從未生效**
- `ScreenBuffer.Write(string s, ...)`（L793）在**方法體內**就把字元畫進 buffer
  （L904-911），postfix 在方法返回後才改 `s`，**對已繪製字元完全無效**。
- 且實際側欄 ST/AG 標籤走 `Write(StringBuilder)`（L936），不是被 patch 的
  `Write(string)`（L793）。→ 舊 `SidebarLabels` + `SidebarLabelPostfix` 一直沒真正生效。

**B. `lying on` 漏翻**
- `{{C|lying on X}}`（Prone `GetDescription`/`DisplayName`）走 TextCleaner，
  只把 `on→在`（Words L126），`lying` 不在字典。
- 舊 `LyingOnTag` regex 只匹配 `{{B|lying on X}}`（名稱 tag），不匹配 `{{C|...}}`
  （狀態/描述），且掛在 `GetFor` postfix（非此渲染路徑）。

**C. `You stand up.` 從未加入**
- 訊息列 `DidX("stand","up")` 走 `AddMsgPrefix`，但 `HarmonyPatches.Patterns`
  無任何 stand/sit/rise 模式（上一輪 B 未執行）。

### 修法（本次）
1. **`lying on`/`prone`**（`TextCleanerHook.cs`）：
   - 新增 `TranslateStatusFragments`，在 `ToStringPostfix` 於 `Clean()` 前執行。
   - regex 同時匹配 `{{B|lying on X}}` 與 `{{C|lying on X}}` → `{{X|躺在 X 上}}`；
     及裸形式 `lying on X` → `躺在 X 上`；`prone` → `俯臥`。
2. **`SidebarLabelPostfix` → 改 `Prefix`**（`UiStringsHook.cs` + `HarmonyPatches.cs`）：
   - 註冊改為 `prefix`（在畫進 buffer 前翻譯）。
   - 同時 patch `Write(string, ...)`（L793）與 `Write(StringBuilder, ...)`（L936）。
   - 統一 `TranslateSidebarString`：日誌 tab 名 / `[History of X]` / 側欄 ST-AG
     都處理（markup `{{W|...}}` 與 console `&W...&y` 兩種形式）。
3. **`You stand up` 家族**（`HarmonyPatches.cs` Patterns）：
   - `^You stand up\.?$` → `你站起來了。`
   - `^You stand up from (.+?)[.!]?$` → `你從 $1 站起來。`
   - `^You rise from (.+?)[.!]?$` → `你從 $1 起身。`

### 驗證
- `test_regex_fixes.py` 擴充至 **38 項**（含 lying on 兩色碼+裸形式、stand up 家族、
  prefix 註冊、StringBuilder overload）。
- syncheck 四檔全 `OK`；部署 `qud-zh-tw-replacers`，`cmp` 一致。

### 重點提醒
- 遊戲 mod 是 Source Mod（.cs 編譯成 DLL），**需重啟遊戲**才載入新 DLL。
- 先前「沒生效」很可能包含：postfix 無效 + 未重啟遊戲 兩因素疊加。

---

## 九、2026-08-09（第三次）`[躺在 {1} 上]` 與 `wading` 修復

### Bug A：`[躺在 {1} 上]` — 動態替換失效（嚴重）
- **根因**：`TextCleanerHook.cs` 舊 `DisplayNamePostfix` 用 `LyingOnTag.Replace(__result, "{{C|躺在 {1} 上}}")`。
  C# `Regex.Replace` 的回溯引用語法是 **`$1`（美元）**，不是 `{1}`；`{1}` 被當成字面文字輸出。
- **修法**：改用 `$1`；並統一改走 `TranslateStatusFragments`（已用 `$1`/`$2`）。

### Bug B：`wading, 潮濕` — Wading 效果名漏翻
- **根因**：`Wading.cs` `DisplayName`/`GetDescription`/名稱 tag 都是 `{{B|wading}}`（L19/40/145），
  純英文讓 `Clean()` 提前返回（需中英混雜才處理），且舊 `DisplayNamePostfix`/`TranslateStatusFragments`
  只覆蓋 `lying on`/`prone`。
- **修法**：`wading`→`涉水` 加入效果名表。

### Bug C（範圍）：91 個狀態效果硬編碼 `{{X|english}}` DisplayName
- **根因**：`Effect.DisplayName` 是 raw 字面值（不走 `Strings._S`），`EffectsDetails.zh-tw.xml`
  只本地化 Details（描述）不本地化 DisplayName（名稱）。
- **修法**：
  1. 建立 `EffectZh` 對照表（62 個 LLM 意譯 + 補充 `wading`/`prone`/`confused` 等）。
  2. `EffectNameToken` regex 統一翻譯 `{{X|english}}` → `{{X|中文}}`。
  3. 動態模板 regex：`lying on X`→躺在 X 上、`sitting on X`→坐在 X 上、
     `enclosed in X`→被困在 X 內、`engulfed by X`→被 X 吞噬、`piloting X`→駕駛 X。

### 驗證
- `test_regex_fixes.py` 擴充至 **42 項**（含 `$1` 無字面 `{1}`、wading、效果表、動態模板、複合狀態）。
- syncheck 四檔全 `OK`；部署 `qud-zh-tw-replacers`，`cmp` 一致。
- **提醒：需重啟遊戲**才載入新 DLL。

---

## 十、2026-08-09（第四次）Active Effects / No active effects / Sultan Histories 動態 tab

### Bug A：`Active Effects` / `No active effects.`（BookUI 獨立路徑）
- **根因**：`GameObject.ShowActiveEffects()`（L5423-5432）直接呼叫
  `BookUI.ShowBook("No active effects.", "&WActive Effects&Y - " + DisplayName)`。
  字串是 raw 字面值，不走 `Strings._S`；`BookUI.ShowBook` 是獨立渲染路徑，
  ScreenBuffer prefix 與 TextCleaner 都覆蓋不到。
- **修法**：
  - `HarmonyPatches.cs`：patch `BookUI.ShowBook(string,string,...)`（L505）加 prefix。
  - `UiStringsHook.cs` `BookShowPrefix(ref PageText, ref BookTitle)`：
    - `No active effects.` → `沒有主動效果。`
    - `&WActive Effects&Y` 開頭 → `&W主動效果&Y`（保留 `- DisplayName` 後綴）。

### Bug B：`Sultan Histories` tab（動態 tab 名）
- **根因**：`GetSultansDisplayName()` 回傳 `{sultanTerm} Histories`（如 `Resheph Histories`），
  不在 `JournalCategories` 靜態表 → 精確匹配失敗。
- **修法**：`TranslateSidebarString` 加動態 `SultanHistories` regex
  `^\s*([^|{}\[\]]+?)\s+Histories\s*$` → `$1 的歷史`（保留 markup wrapper）。

### 驗證
- `test_regex_fixes.py` 擴充至 **48 項**（含動態 Sultan tab、BookShow prefix、No active effects）。
- syncheck 四檔全 `OK`；部署 `qud-zh-tw-replacers`，`cmp` 一致。
- **重要：需完整重啟遊戲**（退出 Steam 或完全關閉）才載入新 DLL。

---

## 十一、2026-08-09（第五次）戰鬥/狀態/烹飪訊息漏翻 + 日誌決策

### 你的問題：日誌（replacer_log）要不要開？
- **結論：維持預設關閉（由 `ZH_TW_REPLACER_LOG` 環境變數控制），玩家要上報 bug 時再開。**
- 開啟時有 `LogMax=600` 上限 + `LogFlushEvery=50` 批次寫入，效能影響小。
- 玩家開 `ZH_TW_REPLACER_LOG` 後遊玩一段，`replacer_log.txt` 會記錄
  `UNTRANSLATED: '...'`（漏翻原始句）與 `TRANSLATED: ...`，方便你上報/溯源。

### 漏翻根因（三類）

**A. 戰鬥訊息走 `textBuilder`，TextCleaner 先逐詞破壞詞序**
- `Combat.cs` L1473 `event12.SetParameter("Message", textBuilder.ToString())`。
  `textBuilder.ToString()` 先觸發 TextCleaner 逐詞翻譯 → 詞序壞掉
  （`你 擊中 (x1) 為了 2 傷害 與...`），之後 AddMsgPrefix 的英文 pattern 匹配不上。
- **修法**：`TranslateStatusFragments` 加整句 pattern（在 Clean 前）：
  - `You (critically )?hit (X) for N damage with W! [R]` → `你用 W 擊中(X)，造成 N 傷害[R]`
  - `You miss with W [R]` → `你未擊中 W[R]`
  - `You toggle X on/off` → `你將 X 切換為開啟/關閉`
  - `X is dazed` → `X 感到暈眩`、`X stands up` → `X 站起來了`
  - `X takes N damage from Y` / `You take N damage from Y`
- **檢測腳本**：`extract_hardcoded_ui.py` 新增 `_TEXTBUILDER_LIT_RE` 掃
  `textBuilder.Append("...")` 鏈 → `textbuilder_missing`（408 條），未來不漏偵測。

**B. Words/連接詞缺口**
- 補 Words：`his`/`her`/`toggle`/`knocked`/`stops`/`moving`/`looks`/`out`/`it`/`this`/`that`。
- `is`/`are`/`has`/`was`/`were` 不加入（空值會誤刪英文模板），改由整句 pattern 處理。

**C. 烹飪/香料訊息**
- `cookTemplate=` 已翻譯且部署，但玩家看到英文 frame → 疑 spice 合併/載入時序。
- **修法**：`TranslateStatusFragments` 加烹飪兜底：
  - `You eat the meal.` → `你吃下了這份餐點。`
  - `You toss X into a pot and stir.` → `你將 X 丟進鍋子裡並攪拌。`
  - `You gather X for your meal.` → `你收集了 X 來當作餐點。`
  - `You toss them in a pot and stir.` → `你將它們丟進鍋子裡攪拌。`

### 驗證
- `test_regex_fixes.py` 擴充至 **61 項**（combat 整句、Words 缺口、烹飪、萃取器）。
- syncheck 四檔全 `OK`；部署 `qud-zh-tw-replacers`，`cmp` 一致。
- **需重啟遊戲**載入新 DLL。

---

## 十二、2026-08-09（第六次）`You sit down on 椅子` 深度分析 + XDidYToZ frame 修法

### 訊息流（已確認）
`Chair.cs` L432 `XDidYToZ(Actor, "sit", "down on", ParentObject)`：
- L303-308 `textBuilder.Append("You ").Append("sit").Append(" down on ")`
- L334 `textBuilder.Append(gameObject.one(...))` → 物件名
- L349 → `HandleMessage(source, textBuilder)` → L90 `Msg.ToString()` → **TextCleaner.ToStringPostfix**
- 之後 `AddPlayerMessage` → `AddMsgPrefix`（Harmony）→ 再 TextBuilder

### 根本原因分析
`Clean()` 只在「中英混雜」時執行（L423 early-return）。`XDidYToZ` frame 的翻譯依賴：
- 物件名 `gameObject.one()` 若已本地化（`椅子`）→ 訊息混雜 → Clean 逐詞翻 frame。
- 物件名若仍英文（`a chair`）→ 訊息純英文 → Clean early-return → frame 漏翻。

**`HarmonyPatches.Patterns` 從無 `You sit down on` pattern**（上面完整清單可證），
此類 frame 完全依賴 TextCleaner 逐詞，無整句兜底 → 不穩。

### 修法（本次）
1. **`TranslateStatusFragments` 加 XDidYToZ frame 整句 pattern**（在 Clean early-return 前執行，保證 frame 必翻）：
   - `You sit down on/in X` → `你坐到 X 上/裡。`
   - `You climb onto / jump onto / wade through / swim through / emerge from / bump into / bond with / detach from / slip away from / swap positions with / get entangled in X`
   - 被動：`You are engulfed by / dragged toward / sucked into / impaled by X`
   - 皆含 `(?:the|a|an)?` 冠詞剝離。
2. **`DisplayNamePostfix` 熱路徑減負**：拆出 `TranslateDisplayNameFragments`（只含狀態標籤模板 lying on/sitting on/enclosed/engulfed/piloting + EffectZh），
   `DisplayNamePostfix` 只 call 它，**不再跑戰鬥/烹飪 pattern**（排除 GetFor 熱路徑干擾）。
   `TranslateStatusFragments`（message 路徑）= `TranslateDisplayNameFragments` + 戰鬥/烹飪/XDidYToZ frame。

### 檢測掃描缺口修法
- `extract_hardcoded_ui.py` 新增 `_XDIDYTOZ_RE`/`_XDIDY_RE`/`_DIDX_RE`，
  重組 `You {verb} {prep} {object}` frame → `xdidytoz_frames` bucket（95 條）。
  未來此類 frame 不漏偵測。

### 驗證
- `test_regex_fixes.py` 擴充至 **69 項**（含 sit down on frame 中文/英文物件、DisplayNamePostfix 熱路徑減負、xdidytoz_frames 萃取）。
- syncheck 四檔全 `OK`；部署 `qud-zh-tw-replacers`，`cmp` 一致。
- **需重啟遊戲**載入新 DLL。

### 已知限制
- XDidYToZ frame 共 95 種，本次只補了 ~20 個常見 frame。其餘（如 `You lop off X`、`You shimmer into existence in X`）
  仍靠 Clean 逐詞或漏翻，可後續依 `xdidytoz_frames` 報表逐步補。

---

## 十三、2026-08-09（第七次）`You sit down on 椅子` 多層攔截修法

### 背景
上一輪只在 `TranslateStatusFragments`（TextCleaner 路徑）加 XDidYToZ frame pattern，
但使用者回報仍漏。此類 frame 依賴單一 TextCleaner 路徑（`XDidYToZ` → `Msg.ToString()` →
`ToStringPostfix`），且物件名是否已本地化影響 `Clean()` early-return，runtime 不穩。

### 修法（多層攔截，雙保險）
1. **`HarmonyPatches.Patterns`（`AddMsgPrefix` hook）新增 XDidYToZ frame**：
   - `You sit down on/in X` → `你坐到 X 上/裡。`
   - `You climb onto / jump onto / wade through / swim through / emerge from / bump into X`
   - 被動：`You are engulfed by / dragged toward / sucked into / impaled by X`
   - 皆含 `(?:the |a |an )?` 冠詞剝離。
2. **保留** `TranslateStatusFragments` 的 XDidYToZ frame（TextCleaner 路徑）。
   → `AddMsgPrefix`（每條 `AddPlayerMessage` 都跑）與 TextCleaner 雙層接住，
   即使 TextCleaner 路徑有 runtime 問題，`AddMsgPrefix` 也能翻譯。

### 驗證
- `test_regex_fixes.py` 擴充至 **72 項**（含 Harmony sit down/wade/engulfed frame）。
- syncheck 四檔全 `OK`；部署 `qud-zh-tw-replacers`，`cmp` 一致。
- **需重啟遊戲**載入新 DLL。

---

## 十二、2026-08-10（第六次）`You sit down on 椅子` 回歸 + XDidYToZ frame

### 根因
- `You sit down on {X}` 來自 `Chair.cs` L432 `XDidYToZ(Actor, "sit", "down on", ParentObject)`。
- 訊息流：`XDidYToZ` → textBuilder 組裝 `You {Verb} {Preposition} {X}` → `TextCleaner.ToStringPostfix`。
- **根本問題**：frame（`You sit down on`）靠 `Clean()` 逐詞翻譯（`sit down→坐下`、`on→在`），
  但 `Clean()` **只在「中英混雜」時執行**（L423 early-return）。若物件名尚未本地化
  （`one()` 回傳英文 `a chair`），訊息是純英文 → `Clean()` early-return → frame 不翻；
  之後遊戲**另外**把物件名本地化成 `椅子` 顯示 → 使用者看到 `You sit down on 椅子`
  （frame 英文 + 物件中文）。

### 修法（`TextCleanerHook.cs`）
- `TranslateStatusFragments` 加 **XDidYToZ frame 整句 pattern**（在 Clean 前，保證 frame 正確）：
  - `You sit down on X` → 你坐到 X 上。/ `You sit down in X` → 你坐到 X 裡。
  - `You climb onto X` → 你爬上 X。/ `You jump onto X` → 你跳到 X 上。
  - `You wade through X` → 你涉水穿過 X。/ `You swim through X` → 你游泳穿過 X。
  - `You emerge from X` → 你從 X 現身。/ `You bump into X` → 你撞到 X。
  - `You bond with X` → 你與 X 締結聯繫。/ `You detach from X` → 你從 X 脫離。
  - `You slip away from X` → 你從 X 溜走。/ `You swap positions with X` → 你與 X 交換位置。
  - `You get entangled in X` → 你被 X 纏住。
  - 被動：`You are engulfed by X` → 你被 X 吞噬。/ `You are dragged toward X` → 你被拖向 X。
  - `You are sucked into X` → 你被吸入 X。/ `You are impaled by X` → 你被 X 刺穿。
- frame 內含 `(?:the |a |an )?` 冠詞處理。
- `DisplayNamePostfix`（GetFor 熱路徑）維持只 call 輕量 `TranslateDisplayNameFragments`，
  不跑戰鬥/烹飪/XDidYToZ pattern（排除熱路徑干擾）。

### 檢測掃描缺口（`extract_hardcoded_ui.py`）
- **根因**：`_MSG_RE` 只掃 `.EmitMessage("...")`/`.Fail(...)`，漏 `XDidYToZ`/`DidX` frame。
- **修法**：新增 `_FRAME_RE` 掃 `XDidYToZ/XDidY/DidXToY/DidX` 的 Verb+Preposition →
  `frame_missing` bucket（如 `sit down on`、`rise from`）。
- 其他既有缺口：`find_untranslated.py`/`check_completion.py` 只掃 zh-tw XML，
  不掃 replacer C#；`scan_replacer_log.py` 依賴 `replacer_log.txt`。

### 驗證
- `test_regex_fixes.py` 擴充至 **72 項**（含 sit down on/wade through/are engulfed frame）。
- 文字 `ap` 語法 OK；C# 括號平衡 OK。
- 部署 `qud-zh-tw-replacers`（TextCleanerHook.cs），`cmp` 一致。
- **需重啟遊戲**；遊戲啟動時會自行編譯 mod（build_log 顯示 Compiling 4 files）。

---

## 十三、2026-08-10（第七次）DisplayName/描述路徑回歸根治

### 根因（大面積漏翻的單一根源）
`DisplayNamePostfix`（`GetDisplayNameEvent.GetFor` hook，L619-629）有兩個問題：
1. **`Length > 120` 直接返回**：雕刻品福音等長描述（238 字元）完全跳過翻譯。
2. **只做 `TranslateDisplayNameFragments`（輕量效果名）**：不呼叫 `Clean()`，
   導致 Words/PhraseRegex（`of→的`、`dram→德蘭`、`sultan→蘇丹`、
   `the Museum Autarchy of Tarchewan→塔徹萬博物館專制政體`、`rife with burnt books→...`、
   `Philosophical→哲學性的`）全部不套用於 DisplayName/描述字串。

這解釋了：雕刻品描述、地形/液體描述、物件名 全部走 `GetFor` → `DisplayNamePostfix`，
而我上一版為效能拆成輕量 → 大面積回歸。

### 修法
`DisplayNamePostfix` 現在：
```csharp
__result = TranslateDisplayNameFragments(__result);
string cleaned = Clean(__result);   // 補跑 Clean()
if (cleaned != __result) __result = cleaned;
```
- 移除了 `Length > 120` 直接返回。
- 保留 `Clean()` 內建 `Cache`（CacheMax=4000）+ 純英/純中 early-return 效能守護。

### 驗證（模擬完整 pipeline）
- `the Museum Autarchy of Tarchewan` → `塔徹萬博物館專制政體` ✅
- `Philosophical Wreck` → `哲學性的 殘骸` ✅
- `rife with burnt books and corroded data disks` → `充斥著燒毀的書籍與腐蝕的數據磁碟` ✅
- `of 1 dram` → `的 1 德蘭` ✅
- `sultan Murapur I` → `蘇丹 Murapur I` ✅

### Phase 3：全面清查環境重建
- `/tmp/opencode` 先前被清空 → 重新安裝 .NET 6 runtime（`/tmp/opencode/dotnet`）+ 重跑 ilspycmd
  反編譯遊戲 DLL → `/tmp/opencode/decompY`（5454 個 .cs 檔）。
- 重跑 `extract_hardcoded_ui.py`：
  - frame_missing=144（XDidYToZ 多詞 frame）
  - msg_missing=70（Fail/ShowFailure 訊息片段，多數已由 DynamicPopupPatterns 覆蓋）
- 待續：批次翻譯 frame + 補字典（Phase 3 迭代）。
