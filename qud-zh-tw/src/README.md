# src/ — C# 程式碼（replacers、字型）

遊戲的 mod 系統會在**執行期編譯** mod 資料夾內所有的 `.cs` 檔。因此：
- 只要資料夾內有任何 `.cs`，就必須保證全部能通過編譯，否則整個 mod 無法載入。
- 開發時用官方「Modding Utilities → Write Mods.csproj」產生 `Mods.csproj`，
  搭配 ILSpy 反編譯 `CoQ_Data/Managed/Assembly-CSharp.dll` 查閱 API。

## 目前狀態

**Phase 1（靜態字串翻譯）不需要任何 C#**。`zh-tw/*.xml` 即可完成。

需要 C# 的時機（後續階段才加入）：

1. **Variable replacer**（動態文字）：
   - 程序化命名（村莊名、歷史名、綽號）
   - 中文語法需要而英文 replacer 不支援的場合
   - 官方以 `=name=` 佔位符 + replacer 函式處理，多數英文 replacer 不通用於中文，
     需自訂（見 `TemplateReplacers.cs.example`）
2. **字型注入（選用，Chiron 字體）**：
   - 遊戲已內建 `SourceHanMono TC`，正體中文可直接顯示
   - 若要替換為 Chiron 宋體／黑體，用 Harmony 於啟動時載入 `fonts/` 下的 TTF
3. **其他語言特化邏輯**（代名詞、複數、量詞等）

## 驗證小工具（寫入 Player.log）

```csharp
XRL.Messages.MessageQueue.AddPlayerMessage("Hello world!");
UnityEngine.Debug.LogError("Hello world!");
```

## 注意

- 尚未完成的可翻譯字串一律留在 `zh-tw/*.xml`（以 `▶` 標記），不要靠 C# 硬塞。
- 加入任何 `.cs` 前，先在遊戲內用「Recompile mod」驗證能編譯。
