# LIQUIDS_INCIDENT — 液體初始化 NRE 崩潰記錄

## 結論（一句話）
遊戲 2.0.212.29 一旦載入 mod 的 `Liquids.zh-tw.xml`（純文本或合成版皆同），
液體初始化即對每個液體拋 NRE，最後 `primary liquid "wine" unknown`，無法進遊戲。
**目前修復：移除液體檔（中文液體名暫以英文顯示）。**

## 時間線與證據

| 時間 | 版本/事件 | 結果 |
|---|---|---|
| 8/13 12:20 | game_log.112（92B） | 啟動即停，無液體行 |
| 8/13 12:32 | game_log.113（純文本版 369 行已部署） | 7 液體 NRE |
| 8/13 12:35 | Player-prev.log（純文本版） | 7 液體 NRE，堆疊 `BaseLiquid.Initialize @0x68` |
| 8/13 12:42 | commit d38fdbe：合成版（1382 行，塞入 slug/class/combustibility/render/part） | — |
| 8/13 12:47 | Player.log（合成版已部署） | 同時 7 液體 NRE，堆疊**與 12:35 一字不差** |

NRE 堆疊（game_log.113 / Player.log 相同）：
```
Initializing liquid xml Name Acid - TargetInvocationException ---> NullReferenceException
  at XRL.Liquids.BaseLiquid.Initialize (System.String ID) [0x00068]
  at XRL.Liquids.BaseLiquid..ctor (System.String ID) [0x00032]
  at XRL.Liquids.LiquidAcid..ctor ()
  at ... Activator.CreateInstance ...
  at XRL.World.Parts.LiquidVolume.Init ()
```
失敗液體：Acid、Blood、Honey、Oil、Salt、Water、Wine（等 7 個先被初始化的）；
隨後 `RunGame: System.Exception: primary liquid "wine" unknown` → 中止。

## 上一個修復做了什麼？（d38fdbe）
把 `Liquids.zh-tw.xml` 從「官方 Generated Localizable XML 純文本格式」改為
「合成版」：把 base Liquids.xml 的全部欄位（slug/class/flameTemperature/combustibility
/thermalConductivity/evaporativity/staining/cleansing/valuePerDram/vaporObject/render
(含 paint/frozen/blips)/tag/soupSludgeProperties/part）逐液體併入。
提交訊息聲稱「缺 slug/class/part 藍圖欄位（遊戲新液體架構）→ 修復」。

**它沒有「導致」崩潰**：12:35（合成前）與 12:47（合成後）堆疊一字不差。
**它是無效修復**：當時對根因的判斷（缺藍圖欄位）方向錯誤。

## 為什麼會崩（假說，證據位階 2/3）
1. 「No liquid blueprint found for {id} {class} - consider switching to liquids xml.」
   （WARN，每液體一條）只出現在 mod 液體檔存在時——遊戲偵測到「mod 提供液體」，
   對每個液體走 `LiquidVolume.Init → Activator.CreateInstance(Liquid*)`。
2. 純文本版與合成版兩條路都走同一 ctor 鏈，都在 `BaseLiquid.Initialize` [0x68]
   訪問空引用。即：**與檔案欄位形式無關，與「mod 液體檔存在」本身相關**。
3. replacers mod 只 patch `MessageQueue.AddPlayerMessage/Add`（訊息佇列），
   無涉液體初始化（已逐行核對 HarmonyPatches.cs）→ 排除。
4. 其後 `Variable replacer =liquid.name= not found` 是 NRE 的**後果**
   （液體物件未建立，文本變數缺 liquid 引數），非成因。

## 已採取的修復（本輪）
| 位置 | 動作 |
|---|---|
| `Mods/qud-zh-tw/zh-tw/Liquids.zh-tw.xml` | 移出 zh-tw/ → `Liquids.zh-tw.xml.bak`（遊戲不再載入） |
| repo `qud-zh-tw/qud-zh-tw/zh-tw/Liquids.zh-tw.xml` | 移出 → `Liquids.zh-tw.xml.bak` |
| repo `.../release/qud-zh-tw/zh-tw/Liquids.zh-tw.xml` | 移出 → `.../release/qud-zh-tw/Liquids.zh-tw.xml.bak` |
| `tools/deploy_mods.py` | 新增 `LIQUID_BLOCKLIST` 封鎖 + 目標端殘留清理（防呆，防再踩雷） |

## 驗證
啟動一次遊戲：
- 正常進入標題/角色創建 → 根因確認=液體檔存在；液體名暫英文。
- 仍崩 → 把疑點轉向其他 zh-tw 檔或 replacers，繼續二分（見下）。

## 恢復液體中文（後續選項，未實施）
A. 逐液體二分：縮小 `Liquids.zh-tw.xml` 到 1 個液體試跑，找出格局性觸發條件。
B. 繞過 xml 合併：改用 replacers 在運行時替換液體顯示名（LiquidInfo 已建之後），
   代價=液體名/形容詞等文本在後台替換，與現有 TextCleaner 管道整合。
C. 上報官方（等官方修復 mod 液體合併 NRE）。