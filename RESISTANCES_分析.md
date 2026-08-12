# RESISTANCES / SECONDARY ATTRIBUTES 區段標題翻譯 — 調查記錄（進行中）

狀態：**待辦（來源已定位到 level0 場景，尚未修）**

## 問題
角色狀態畫面的區段大標題 `RESISTANCES`、`SECONDARY ATTRIBUTES` 仍顯示英文；
同畫面的 `ATTRIBUTES` / `MAIN ATTRIBUTES` 已翻成「屬性 / 主要屬性」。

## 已確認事實
1. **不在 DLL 字面值**：`Assembly-CSharp.dll` 的 ASCII 與 UTF-16 都搜不到
   `Resistances` / `Secondary Attributes`。只有 `MAIN ATTRIBUTES`（UTF-16，屬
   Strings._S「CharacterStatusScreen Attributes Long」）。
2. **不在 StreamingAssets 的 XML/JSON**：僅找到小寫 "Bonus resistances"（genotype extrainfo，非標題）。
3. **不在 resources.assets / sharedassets0.assets**。
4. **在 `CoQ_Data/level0`（Unity 場景）找到**：`RESISTANCES`、`SECONDARY ATTRIBUTES`、
   `MAIN ATTRIBUTES` 各 2 次。→ 這些標題很可能**烘焙在場景/prefab 的 TMP 文字元件**裡。
5. **已試的 patch 都沒抓到**：
   - console `ScreenBuffer.Write` 全多載（string/StringBuilder 開頭，patched=2）→ 無效。
   - `TMPro.TMP_Text.set_text` prefix（已確認 patched，LogAlways 有記錄）→ 無效。

## 推論（待驗證）
- 標題文字是**場景序列化資料**，Unity 反序列化時直接寫入 TMP 的 `m_text` 欄位，
  **不走 `text` property setter**，所以 `set_text` prefix 抓不到。
- `MAIN ATTRIBUTES` 能翻，可能是程式碼另外用 `Strings._S` 覆寫了那一個欄位，
  而 RESISTANCES / SECONDARY ATTRIBUTES 沒有對應的 _S 覆寫。

## 之後可嘗試的方向
1. **找場景/prefab 初始化後設定這些 TMP 文字的代碼**：patch 那個「填充角色狀態畫面」
   的方法（而非 set_text），在建立 UI 後把標題換成中文。
2. **Harmony patch TMP_Text 的 Awake/Start 或 OnEnable**：元件啟用後若 text 命中
   SectionHeaders 就替換（但要小心只跑一次、別每幀跑）。
3. **直接改場景資產**：用 Unity 編輯器/資產修改工具把 level0 裡的 TMP m_text 改掉
   （風險高、需備份，最後手段）。
4. 反編譯 `Qud.UI.CharacterStatusScreen` 找它如何填充各區段標題（需 decompiler）。

## 相關檔案
- patch 位置：`qud-zh-tw-replacers/HarmonyPatches.cs`（TMP patch 區）、
  `qud-zh-tw-replacers/UiStringsHook.cs`（`SectionHeaders` 字典、`TmpHeaderPrefix`）。
- 場景：`CoQ_Data/level0`。

---
## 狀態更新（2026-08-12）
- 按使用者決定：**暫緩處理**（不影響遊玩）。Todo 4 僅記錄，最後處理。
- 後續方向保留：level0 場景分析 / 反編譯 `Qud.UI.CharacterStatusScreen` / TMP Awake・OnEnable patch。
