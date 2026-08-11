# 《卡德洞窟》繁中在地化 — 專案計劃與開發計劃

> 目標版本：官方 `lang-experimental` 分支（官方本地化系統）
> 目標語言：臺灣正體中文（zh-TW，BCP 47：`zh-tw`）
> 本專案為**全新專案**，不抄襲 `lofucc/caves-of-qud-chinese`（簡中民間漢化），改採官方本地化系統。

---

## 1. 專案概述

### 1.1 目標

將《Caves of Qud》（卡德洞窟）完整翻譯為臺灣正體中文，涵蓋：

- UI／選單／戰鬥紀錄等**硬編碼字串**（約 9,900 筆 `<string>`）
- 物品、生物、建築、食物等**資料檔**（約 10,000 筆物件條目）
- **書籍、對話、世界歷史**等長文內容
- **程序化生成文字**（村莊名、歷史名、綽號、複數規則）
- **中文字型渲染**（Chiron 黑體／宋體注入）

### 1.2 原則

| 原則 | 說明 |
|---|---|
| 純 mod | 永不修改遊戲本體檔案（DLL／assets），Steam 更新不衝突 |
| 官方系統 | 使用官方本地化系統（`Languages.xml` + `Lang` 屬性 + XML 覆寫） |
| 繁中特化 | 用語、標點、數字格式、專有名詞以臺灣繁體為準 |
| 工具化 | 翻譯骨架自動生成、完成度檢查、版本差異比對 |
| 版本追蹤 | 對齊官方 `gnarf/caves-of-qud-example-language` 的版本 tag |

---

## 2. 背景分析

### 2.1 遊戲文字架構

卡德洞窟的文字來源有三類，本地化策略各異：

| 來源 | 佔比 | 處理方式 |
|---|---|---|
| XML 資料檔（`StreamingAssets/Base/*.xml`） | 大宗 | 官方 mod 系統以 `Load="Merge"` 覆寫即可 |
| DLL 硬編碼字串（UI、戰鬥文字） | 約 9,900 筆 | 官方本地化系統以 `Strings.xml` 提供 `ID`→翻譯 對應 |
| 程序化生成（Naming／HistorySpice） | 特殊 | 需 C# replacer + 翻譯名詞庫 |

### 2.2 版本現況（2026-08 調查）

| 項目 | 狀態 |
|---|---|
| 本機遊戲（穩定版） | **214.66**，尚未包含官方本地化系統 |
| 官方本地化系統 | 在 `lang-experimental` beta 分支（開發中） |
| 官方範例字串 repo | `gnarf/caves-of-qud-example-language`，最新 tag `212.29`（2026-07-15），40 檔、約 3.9MB |
| 參考 mod（簡中） | 目標 210.20，舊式改 DLL+assets 做法（本專案**不採用**） |

> **結論**：須將 Steam 遊戲切換至 `lang-experimental` 分支。切換後遊戲會隨附該分支版本的 `ExampleLanguage/` 字串清單，以確保與遊戲版本完全對齊。

### 2.3 參考專案分析（為何不抄）

`lofucc/caves-of-qud-chinese` 的作法：

- 直接改寫 `Assembly-CSharp.dll`（混淆過的 DLL，每版更新都要逆向重做）
- 替換 `sharedassets0.assets`（內嵌 CJK 字形，59MB，更新即破壞）
- 整包覆蓋 `StreamingAssets/Base/*.xml`

本專案改用官方系統的**資料覆寫**機制，完全不碰上述檔案。

---

## 3. 技術架構

### 3.1 Mod 結構（官方本地化 mod）

```
qud-zh-tw/
├── manifest.json          # mod 識別（ID、版本、作者）
├── Languages.xml          # 宣告 zh-tw 語言
└── <任何名字>.xml          # 翻譯資料（根元素帶 Lang="zh-tw"）
```

### 3.2 檔案格式重點

```xml
<?xml version="1.0" encoding="utf-8"?>
<strings Lang="zh-tw" Encoding="utf-8">
  <string Context="MainMenu Main" ID="New Game">開始新遊戲</string>
</strings>
```

- **根元素必帶 `Lang="zh-tw"`**：否則該檔在英文模式也會載入
- **`ID` 是原文**、內容是翻譯；同名同詞不同處用 `Context` 區分
- 物件改名：`<object Name="Ctesiphus" Load="Merge"><part Name="Render" DisplayName="泰斯菲斯"/></object>`
- **Placeholder**：`=num=`、`=commandKey:CmdUse=` 等不可刪、可移位
- **色彩標記**：`{{K|text}}` 的 `{{`、`K`、`|`、`}}` 是語法，只翻 `text`

### 3.3 字型方案

**重大發現（2026-08-04 調查）**：`lang-experimental` 分支已內建 **SourceHanMono**（思源等寬體）
多區域變體 SDF 字型，包含 **`SourceHanMono TC`（正體中文）**、`SC`、`HC`、`K`。
亦即**官方本地化系統本身即可渲染正體中文**，不需要額外注入字型即可開始翻譯。

字型策略分兩層：

1. **基底（現成可用）**：直接用遊戲內建的 `SourceHanMono TC SDF` 渲染繁體中文。
   先以它完成翻譯與驗證，零成本。
2. **增強（選用，Chiron）**：遊戲內建為「等寬體」，字型美感有限。待翻譯上軌道後，
   以 Harmony 於啟動時載入 `fonts/` 下的 Chiron TTF 替換字型物件：
   - `ChironSungHK-Text-R.ttf`（宋體，正文／書籍）→ 主字型
   - `ChironHeiHK-R.ttf`（黑體，UI／標題）→ 次要字型
   - `ChironSungHK-B.ttf`（宋體粗體）→ 強調
   - 技術上以 `Font.CreateDynamicFontFromOSFont`／Unity Font API 產生 Font 物件，替換引用。
   - 此為**可選**，不阻塞翻譯主線。

### 3.4 工具鏈（`tools/`）

| 工具 | 功能 |
|---|---|
| `generate_skeleton.py` | 從 example-language 產生 zh-tw 翻譯骨架（ID/Context 保留、內容清空標記） |
| `check_completion.py` | 掃描未翻譯／殘留 `▶` 的條目，輸出完成度報告 |
| `diff_versions.py` | 對比 example-language tag，列出新增／刪除／變更的翻譯鍵 |
| `glossary.py` | 術語表與翻譯字串的一致性檢查 |
| `find_untranslated.py` | 掃描缺漏：L1 `▶` 殘留、L2 英文專名漏譯、L3 ID 覆蓋缺漏 → `untranslated_report.json` |
| `expand_glossary.py` | 依 L2 報表為英文專名生成「中文(原文)」音譯建議，審核後 `--merge` 進策展表 |
| `fix_consistency.py` | 統一專名譯名、殘留英文代換、全數出現處補 `(原文)` |
| `scan_replacer_log.py` | 解析 `replacer_log.txt`，搜尋動態替換側「未替換」的英文訊息 |
| `report_gaps.py` | 整合缺漏報告：L1▶殘留／L2 專名／L3 ID／L4 資料檔／L5 句子／L6 Naming |

---

## 4. 翻譯範圍與規模

來源：`caves-of-qud-example-language` 40 個檔案（212.29）

### 4.1 字串類（`Strings*.xml`，9,887 筆）

| 檔案 | 筆數 | 難度 |
|---|---|---|
| `Strings.example.xml` | 5,304 | 中（UI、提示、戰鬥文字，含 placeholder） |
| `Strings.Conversations.example.xml` | 4,569 | 中高（對話、文化梗） |
| `Strings.appendix.example.xml` | 14 | 低 |

### 4.2 資料類（物件／書籍，約 10,000 筆）

| 檔案 | 筆數 | 難度 |
|---|---|---|
| `Items` | 2,933 | 中（物品名＋描述，含詩性文字） |
| `Creatures` | 2,884 | 中（生物名＋描述） |
| `Furniture` | 1,275 | 中 |
| `HiddenObjects` | 903 | 中 |
| `Foods` | 446 | 低中 |
| `Walls` | 451 | 低 |
| `Books` | 54（長篇） | **高**（詩體、仿古語） |

### 4.3 特化內容

- **程序化命名**（`Naming`）：村莊、歷史人物、隨機綽號 → 需 replacer + 名詞庫
- **世界歷史**（`HistorySpice`、`LibraryCorpus`）：長文、文化內涵 → 高難度
- **代名詞系統**：遊戲有 they/xe/ey 等多種代名詞 → 中文無性別代名詞，較易處理，但仍需統一策略

---

## 5. 術語策略

### 5.1 名稱定案原則

- 先建立 **GLOSSARY.md**，確立專有名詞譯名，全案統一
- 與簡中版區隔（例：簡中「卡德洞窟／咬颚兽」，繁中待定，見 GLOSSARY）
- 名稱翻譯以**音譯為主**，保留原文氛圍（Qud 語源為阿拉伯語/希伯來語）

### 5.2 待定案核心名詞（初稿）

| 原文 | 簡中版（參考） | 繁中建議 | 狀態 |
|---|---|---|---|
| Caves of Qud | 卡德洞窟 | 卡德洞窟 | 草案 |
| Joppa | 約帕 | 約帕 | 草案 |
| Moghra'yi, the Great Salt Desert | 莫格拉伊大鹽漠 | 待定 | 草案 |
| snapjaw | 咬颚兽 | 咬顎獸 | 草案 |
| Spindle | 纺锤 | 紡錘 | 草案 |

---

## 6. 開發階段（Roadmap）

### Phase 0 — 前置（本週）
- [x] 技術可行性調查、路線決策
- [ ] 切換 Steam 至 `lang-experimental` 分支並啟動遊戲驗證
- [ ] 同步官方 `ExampleLanguage/`（以該分支隨附版本為準）
- [ ] 產生 zh-tw 翻譯骨架（`tools/generate_skeleton.py`）
- [ ] 建立 GLOSSARY 術語表草案

### Phase 1 — 基礎設施
- [ ] manifest.json、Languages.xml 完成，遊戲內可切換 zh-tw
- [ ] 字型 Harmony 注入 prototype（畫面出現中文）
- [ ] `check_completion.py` 完成度報告跑通
- [ ] 翻譯「高頻短字串」（`Options`、`Manual`、UI 常用語）

### Phase 2 — 資料檔大批翻譯（工作量最大）
- [ ] `Items`／`Creatures`／`Foods`／`Walls` 等物件名
- [ ] `Furniture`／`HiddenObjects`／`ZoneTerrain`
- [ ] 每個檔案完成 → 遊戲內走查驗證

### Phase 3 — 對話與字串
- [ ] `Strings.Conversations`（4,569 筆）
- [ ] `Strings.example`（5,304 筆）
- [ ] placeholder／色彩標記正確性驗證

### Phase 4 — 長文與特化
- [ ] `Books`（詩體，需人工精修）
- [ ] `HistorySpice`／`LibraryCorpus`
- [ ] 程序化命名 replacer（C#）

### Phase 5 — 品質與發布
- [ ] 全量完成度檢查、術語一致性檢查
- [ ] 遊玩走查（新手教學→約帕→第一張圖）
- [ ] Steam Workshop 上傳、版本 tag

### 版本對齊流程
1. 官方釋出新的 example-language tag → `git diff` 找出新字串
2. 翻譯新增條目 → 更新 `Version` → 釋出

---

## 7. 目錄結構

```
qud-zh-tw/
├── PLAN.md               # 本文件
├── README.md             # 安裝與使用
├── GLOSSARY.md           # 繁中術語表
├── CHANGELOG.md          # 翻譯進度記錄
├── manifest.json         # mod 識別檔
├── Languages.xml         # 語言宣告
├── zh-tw/                # 翻譯資料（.xml，根元素 Lang="zh-tw"）
├── src/                  # C# replacer、字型 Harmony 注入
├── tools/                # 骨架生成／完成度檢查／版本比對
└── fonts/                # Chiron 字型（不進 git，見 .gitignore）
```

---

## 8. 風險與對策

| 風險 | 影響 | 對策 |
|---|---|---|
| `lang-experimental` 為 beta 分支，系統可能變動 | 高 | 對齊官方 tag；核心架構小步迭代 |
| 官方可能直接出繁中 | 中 | 專案獨立、可與官方互補；屆時評估 |
| 字型渲染 | **低** | 遊戲已內建 `SourceHanMono TC`，翻譯即可顯示；Chiron 為選用增強 |
| 程序化文字（replacer）技術門檻 | 中高 | 官方 dev 可協助；先做靜態字串再攻動態 |
| 程序化文字（replacer）技術門檻 | 中高 | 官方 dev 可協助；先做靜態字串再攻動態 |
| 遊戲更新頻繁 | 中 | 以 diff 工具追蹤新增字串 |
| 20,000 條翻譯工作量 | 高 | 工具化＋分工（翻譯者／程式員分離） |

---

## 9. 立即行動清單

1. Steam 遊戲切換 `lang-experimental` 分支（Steam → 內容 → 測試版 → `lang-experimental`）
2. 啟動遊戲一次，確認 Mod 選項「Enable mods」與「Allow scripting mods」開啟
3. 從 `lang-experimental` 的遊戲資料夾取回該分支的 `ExampleLanguage/` 字串清單
4. 跑 `tools/generate_skeleton.py` 產生 zh-tw 骨架
5. 驗證字型注入 prototype
