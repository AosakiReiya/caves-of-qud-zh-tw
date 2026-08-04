# 卡德洞窟繁中化

《Caves of Qud》的正體中文（zh-TW）翻譯。用官方本地化系統做成的純 mod，不碰遊戲本體檔案，Steam 更新也不怕壞。

## 這個 mod 做什麼

把《Caves of Qud》的界面、對話、物品、生物、書籍和世界觀文本翻成正體中文，目前翻了 40 個本地化檔、超過 19,000 條。地名人名（約帕、巴拉楚姆、咬顎獸這些）全案統一，不會同一地方出現兩種譯名。

遊戲本身已內建正體中文字型（SourceHanMono TC），裝好就能直接顯示，不用另外處理字型。

## 怎麼裝

1. Steam 裡把 Caves of Qud 切到 `lang-experimental` 測試版分支
2. 遊戲內選項打開 **Enable mods**
3. 把 `qud-zh-tw` 資料夾放進 Mods 目錄：
   - Windows：`%USERPROFILE%\AppData\LocalLow\Freehold Games\CavesOfQud\Mods\`
   - Linux：`~/.config/unity3d/Freehold Games/CavesOfQud/Mods/`
4. 主選單左下角語言選單選「正體中文」，遊戲重啟後生效

也可以直接上 Steam Workshop 訂閱。

## 資料夾結構

```
manifest.json      mod 識別檔
Languages.xml      語言宣告（zh-tw）
workshop.json      Steam Workshop 設定
zh-tw/             翻譯資料（40 個 XML，根元素帶 Lang="zh-tw"）
tools/             翻譯、檢查、打包用的腳本
.github/           GitHub Actions 自動發布
```

## 開發

翻譯和品質工具用 Python 3 寫的，都在 `tools/` 裡：

```bash
# 從官方 ExampleLanguage 重新產生翻譯骨架
python3 tools/generate_skeleton.py

# 檢查翻譯完成度
python3 tools/check_completion.py

# 檢查 placeholder 有沒有被翻譯弄壞、XML 是否有效
python3 tools/check_quality.py

# 用本地 LLM 批量翻譯（需要本機跑 LLM API）
python3 tools/translate_batch.py

# 統一專名譯名
python3 tools/fix_consistency.py

# 打包成可安裝的 mod 資料夾
python3 tools/package_mod.py --zip
```

## 其他文件

- [翻譯計劃（PLAN）](PLAN.md)
- [術語表（GLOSSARY）](GLOSSARY.md)
- [更新紀錄（CHANGELOG）](CHANGELOG.md)

## 授權

遊戲內容與翻譯文本版權歸 Freehold Games 所有。本專案的翻譯與工具歡迎參考，請勿用於商業用途。
