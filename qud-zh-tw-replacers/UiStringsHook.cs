// UiStringsHook.cs — Qud 繁中：硬編碼 UI 字串補丁
//
// 遊戲 UI 文字來源分兩種，需兩層 Harmony 攔截：
//   1. 走官方本地化查詢 `XRL.Language.Strings._S(Context, ID)`：
//      語料內的字串官方系統已翻譯；此處補「語料外但走 _S」的 (Context,ID) 字典。
//   2. 原始英文字面值直接塞給 Popup（如暫停選單 "Save and Quit"、確認彈窗）：
//      此處以 UIPhrases 字典在 Popup 顯示層翻譯。
//
// 診斷：設環境變數 ZH_TW_REPLACER_LOG=1 時，把「_S 查不到回退英文」的鍵記到
// replacer_log.txt（LIMIT: STRING_MISS ...），可據此列舉所有硬編碼 UI。

using System;
using System.Collections.Generic;
using HarmonyLib;
using XRL.Language;

public static class ZhTwUiStrings
{
    // ============ 1) _S 補充字典：(Context, ID) -> 中文（語料外、走 _S 的字串）============
    // 主要靠 STRING_MISS 記錄列舉後補齊；此處先放已知項。
    private static readonly Dictionary<string, string> UiOverrides =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // { "Context\u0001ID", "中文" },
        };

    // ============ 2) Popup 原始字面值字典：英文 -> 中文 ============
    private static readonly Dictionary<string, string> UiPhrases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Save and Quit", "儲存並退出" },
            { "Are you sure you want to save and quit?", "你確定要儲存並退出嗎？" },
            { "Set Checkpoint", "設定檢查點" },
            { "Restore Checkpoint", "還原檢查點" },
            { "Control Mapping", "按鍵設定" },
            { "Game Info", "遊戲資訊" },
            { "End Test", "結束測試" },
            { "Go to which point of interest?", "要前往哪個興趣點？" },
            { "Interact with which companion?", "要與哪個傢伙對話？" },
            // ESC 暫停選單（XRLCore 原始字面值）
            { "Options", "設定" },
            { "&KRestore Checkpoint", "&K還原檢查點" },
            { "&KSet Checkpoint", "&K設定檢查點" },
            // ===== 硬編碼 UI 詞庫（extract_hardcoded_ui.py --translate 生成，審核後可增刪）=====
            { "No mound of scrap and clay was found to complete.", "未發現可供完成的廢料與黏土堆。" },
            { "There's nothing there you can shank.", "那裡沒有你可以用尖刺攻擊的目標。" },
            { "There's nothing there to shank.", "那裡沒有可以刺殺的東西。" },
            { "You must have a short blade equipped to shank.", "你必須裝備短刃才能進行刺殺。" },
            { "You do not have a bow or rifle equipped!", "你沒有裝備弓或步槍！" },
            { "You must have a short blade equipped in your primary hand to hobble.", "你必須在主手中裝備一把短刃才能進行跛行。" },
            { "There's nothing there to hobble.", "那裡沒有任何可以絆倒的東西。" },
            { "You must have a cudgel equipped in your primary hand to demolish things.", "你必須在主手中裝備棍棒才能拆除物品。" },
            { "You can't Demolish until Slam is off cooldown.", "在「重擊(Slam)」冷卻結束前，你無法進行「拆除(Demolish)」。" },
            { "You have no explosives to deploy!", "你沒有可以使用的爆炸物！" },
            { "You can't deploy there!", "你不能在那裡部署！" },
            { "You must have an axe equipped in your primary hand to go berserk.", "你必須在主手中裝備一把斧頭才能進入狂暴狀態。" },
            { "You can't go berserk until Dismember is off cooldown.", "在「肢解(Dismember)」冷卻結束前，你無法進入狂暴狀態。" },
            { "There is nothing for you to submerge in here.", "這裡沒有任何可以讓你潛入的東西。" },
            { "You cannot do that right now.", "你現在無法執行該操作。" },
            { "That name is already in use.", "該名稱已被使用。" },
            { "You don't have a missile weapon equipped that uses that ammunition.", "你沒有裝備使用該種彈藥的飛彈武器。" },
            { "Matching quest target not found.", "找不到匹配的任務目標。" },
            { "There was a duplication glitch involving your player character. It'd be helpful to send the save folder and Player.log to support@freeholdgames.com along with what you were currently doing.", "發生了一個涉及您的玩家角色的重複物品錯誤。若能將存檔資料夾與 Player.log 寄送到 support@freeholdgames.com，並附上您當時正在進行的操作，將會對我們很有幫助。" },
            { "You don't have enough skill points.", "您的技能點數不足。" },
            { "End game?", "遊戲結束？" },
            { "The limb rejects the infection!", "肢體排斥了感染！" },
            { "You can't be mutated.", "你無法被突變。" },
            { "You can't scintillate again so soon.", "你不能這麼快又再次閃爍。" },
            { "You're not under enough stress to scintillate.", "你的壓力還不夠大，無法閃爍。" },
            { "You are already burrowed.", "你已經在地底挖掘了。" },
            { "You are already sitting down.", "你已經坐下了。" },
            { "You are already standing.", "你已經站著了。" },
            { "You are already lying down.", "你已經躺下了。" },
            { "You don't have a tongue!", "你沒有舌頭！" },
            { "There are no creatures in range.", "範圍內沒有生物。" },
            { "You feel drowsy.", "你感到昏昏欲睡。" },
            { "You cannot do that here.", "你不能在這裡這樣做。" },
            { "Your bilge sphincter is missing.", "你的汙水括約肌失蹤了。" },
            { "There is no liquid here for you to spew.", "這裡沒有任何液體讓你噴吐。" },
            { "That wouldn't leave you with NEARLY enough junk! You can't drop that!", "那樣會讓你幾乎拿不到足夠的垃圾！你不能丟掉那個！" },
            { "You are startled!", "你嚇了一跳！" },
            { "You do not budge.", "你紋絲不動。" },
            { "Your psyche is too exhausted.", "你的精神過於疲憊了。" },
            { "There is nothing you can telekinetically manipulate there.", "那裡沒有任何你可以用念力操控的東西。" },
            { "Your wish is my command!", "您的願望就是我的命令！" },
            { "You can only set your checkpoint in settlements.", "你只能在定居點中設置檢查點。" },
            { "You can only restore your checkpoint outside settlements.", "你只能在定居點之外恢復你的存檔點。" },
            { "Are you sure you want to restore your checkpoint?", "您確定要還原您的存檔點嗎？" },
            { "This saved game predates world seed info.", "此存檔早於世界種子資訊。" },
            { "You haven't found any points of interest nearby.", "您在附近沒有發現任何興趣點。" },
            { "There are hostiles nearby!", "附近有敵對生物！" },
            { "Are you sure you want to go to the world map?", "您確定要前往世界地圖嗎？" },
            { "Would you like to walk to the nearest stairway up?", "您想要走向最近的向上樓梯嗎？" },
            { "Would you like to walk to the nearest stairway down?", "您想要走向最近的向下樓梯嗎？" },
            { "It is already daytime.", "現在已經是白天了。" },
            { "You cannot examine things while you are enraged.", "你在狂怒狀態下無法檢查物品。" },
            { "You cannot examine things while you are confused.", "你在混亂狀態下無法檢查物品。" },
            { "Are you sure you want to quit?", "您確定要退出嗎？" },
            { "Do you want to save first?", "您要先存檔嗎？" },
            { "You are nut currently in a landmark location.", "你目前不在地標地點。" },
            { "No skill by that name could be found.", "找不到該名稱的技能。" },
            { "Access diodes flash in the affirmative.", "存取二極體閃爍，表示肯定。" },
            { "You may only select a visible square!", "你只能選擇可見的方格！" },
            { "You may only select an explored square!", "你只能選擇已探索過的方格！" },
            { "You cannot use disassemble all with hostiles nearby.", "當附近有敵對目標時，你無法使用「拆解全部(disassemble all)」。" },
            { "You are not enclosed.", "你未被包圍。" },
            { "You can't exit here.", "你無法在此離開。" },
            { "You can't imprint yet.", "你還不能進行印記。" },
            { "Your genome enters an excited state!", "您的基因組進入了興奮狀態！" },
            { "You are transported!", "你被傳送了！" },
            { "You're lost! Regain your bearings by exploring your surroundings.", "你迷路了！透過探索周遭環境來重新找回方向。" },
            { "You maintain your balance and kick the eel away.", "你保持平衡，並將那條鰻魚踢開。" },
            { "You maintain your balance and shake the eel off.", "你保持平衡，並甩掉了那條鰻魚。" },
            { "You can't recoil yet.", "你還不能後退。" },
            { "You cannot cross into Brightsheol from this place.", "你無法從此處進入布萊特希歐(Brightsheol)。" },
            { "Crossing into Brightsheol will retire your character. Are you sure you want to do it? Type 'CROSS' to confirm.", "進入布萊特希奧爾(Brightsheol)將會讓你的角色退役。你確定要這樣做嗎？請輸入「CROSS」以確認。" },
            { "The darkness recedes, and a new light breaks on the shores of your mind.", "黑暗退去，一道新的光芒在你的心靈岸邊綻放。" },
            { "The colossal lid slams shut. Darkness engulfs you.", "巨大的蓋子砰地一聲關上了。黑暗將你吞噬。" },
            { "You died.\n\nEntombed in the burial chamber of Resheph, the Last Sultan.", "你死了。\n\n被埋葬在最後蘇丹雷謝夫(Resheph)的墓室中。" },
            { "Something went wrong.", "發生錯誤。" },
            { "The matter of your new body starts to thicken and clot. You're inlaid with ribbons of bone, tissue, nerve, and flesh.", "你新身體的物質開始變得濃稠並凝結。你的體內鑲嵌著骨骼、組織、神經與肌肉的帶狀物。" },
            { "The neoteric body is differently charged. Tying inside you is another of the secret knots that bind the world.", "新穎的軀體帶有不同的電荷。繫於你體內的，是另一道束縛世界的秘密結節。" },
            { "Notes added.", "已新增筆記。" },
            { "Notes removed.", "筆記已移除。" },
            { "No notes found.", "未找到筆記。" },
            { "Launch spaceship and end game?", "發射太空船並結束遊戲？" },
            { "The moors rattle and a marble brick crumbles as a starship takes off.", "隨著一艘星際飛船起飛，荒原震動，一塊大理石磚瓦崩裂。" },
            { "Ugh, you feel sick.", "呃，你感到很不舒服。" },
            { "You lose your way beneath a dense canopy of spores.", "你在茂密的孢子樹冠下迷失了方向。" },
            { "You must have a long blade equipped to switch stances.", "你必須裝備長刃才能切換架勢。" },
            { "There's nothing there to lunge at.", "那裡沒有任何可以撲擊的目標。" },
            { "There's nothing there you can lunge at.", "那裡沒有你可以撲向的目標。" },
            { "Your lunge is interrupted.", "您的突刺被中斷了。" },
            { "There's nothing there to lunge away from.", "那裡沒有任何東西可以讓你衝開。" },
            { "You must be in a long blade stance to use that ability.", "你必須處於長劍架勢才能使用該能力。" },
            { "There's nothing there to swipe at.", "那裡沒有可以揮擊的目標。" },
            { "There's nothing there you can swipe at.", "那裡沒有你可以揮擊的目標。" },
            { "How long would you like to sleep?", "你想睡多久？" },
            { "Are you sure you want to ascend the Spindle? You know of no means to descend again.\n\nType 'ASCEND' to confirm.", "您確定要升上紡錘(Spindle)嗎？您目前沒有任何方法可以再次下降。\n\n請輸入「ASCEND」以確認。" },
            { "Are you sure you want to return to Qud? \n\nType 'RETURN' to confirm.", "您確定要返回卡德(Qud)嗎？\n\n請輸入「RETURN」以確認。" },
            { "The gates are sealed for eternity.", "大門已永遠封閉。" },
            { "The gates swing wide.", "大門敞開了。" },
            { "You are covered in sticky goop!", "你全身沾滿了黏糊糊的黏液！" },
            { "You hear a shloop, and the world around you warps and shifts violently.", "你聽到一聲「咻嚕」，隨後周遭的世界劇烈地扭曲與變形。" },
            { "In the midst of your disorientation, you find a passageway to another dimension.", "在妳感到迷失方向之際，妳發現了一條通往另一個維度的通道。" },
            { "You find a passageway back to your home dimension.", "你發現了一條通往你原本維度的通道。" },
            { "That was awful!", "那太糟糕了！" },
            { "Your friends may lease the Spindle, as we agreed.", "正如我們所約定的，你的朋友們可以租用紡錘(Spindle)。" },
            { "The pact is struck. The Barathrumites may lease control of the Spindle, and all the attending factions owe a debt to Asphodel.", "契約已達成。巴拉楚姆信徒(Barathrumites)可以租用紡錘(Spindle)的控制權，所有參與的派系都欠阿斯福德爾(Asphodel)一份債。" },
            { "The pact is struck. The Barathrumites may lease control of the Spindle, and the chosen factions owe a debt to Asphodel.", "契約已達成。巴拉楚姆信徒(Barathrumites)可以租用紡錘(Spindle)的控制權，而選定的派系則欠下了阿斯福德爾(Asphodel)一份債。" },
            { "The pact is struck. The Barathrumites may lease control the Spindle.", "契約已達成。巴拉楚姆信徒(Barathrumites)可以租賃並控制紡錘(Spindle)。" },
            { "You ponder how best to sow chaos with your words.", "你沉思著如何用你的言語來播種混亂。" },
            { "A loud buzz is emitted. The unauthorized glyph flashes on the side of the applicator.", "發出了一聲巨大的嗡鳴聲。未經授權的符文在塗抹器的側面閃爍著。" },
            { "Choose a model faction for your facial reconstruction.", "請為您的面部重建選擇一個模型派系。" },
            { "Choose a model faction for your holographic glamour.", "為您的全息幻象選擇一個模型派系。" },
            { "The sprayer head won't move.", "噴頭無法移動。" },
            { "There's nothing viable to animate here.", "這裡沒有任何可供製作動畫的內容。" },
            { "You can't animate an object that already has a brain.", "你無法賦予一個已經擁有大腦的物體動畫效果。" },
            { "The sarcophagus is inert.", "石棺是靜止的。" },
            { "You mind swerves from afar, and a force of repulsion from inside the sarcophagus prevents your entry.", "你的心智在遠處偏離了軌道，而來自石棺內部的排斥力阻止了你的進入。" },
            { "A force of repulsion from inside the sarcophagus prevents your entry.\n\nYou do not bear the Mark of Death.", "來自石棺內部的排斥力阻止了你的進入。\n\n你並未帶著死亡印記。" },
            { "You climb into the sarcophagus.", "你爬進了石棺。" },
            { "You melt through the floor and descend with your meal.", "你融穿了地板，帶著你的餐點一同降落。" },
            { "Your cloning capacity is refreshed.", "您的複製能力已重置。" },
            { "You have no bodily tether to recoil.", "你沒有肉體束縛可供後退。" },
            { "You are stuck in a remote pocket dimension and cannot recoil out.", "你受困於一個偏遠的口袋維度，無法退回。" },
            { "Nothing happens.", "什麼事也沒發生。" },
            { "The whole compound rumbles around you.", "整個建築群在你周圍震動。" },
            { "The walls creak, loose objects skid about, and dust is stirred up in iridescent clouds.", "牆壁嘎吱作響，鬆動的物品四處滑動，塵土被揚起，形成虹彩般的雲霧。" },
            { "Enter text to filter inventory by item name.", "輸入文字以透過物品名稱篩選清單。" },
            { "You can't delete automatically recorded chronology entries.", "您無法刪除自動記錄的編年史條目。" },
            { "This place already has a name.", "這個地方已經有名字了。" },
            { "You have no abilities to manage!", "你沒有任何能力可以管理！" },
            { "You don't have anything to use in that slot.", "您在該欄位中沒有任何可使用的物品。" },
            { "You have no inventory!", "你沒有任何物品！" },
            { "You ask about your location and are no longer lost.", "你詢問了你的位置，不再迷路了。" },
            { "Select your maker's mark.", "選擇您的製造者印記。" },
            { "No problems found.", "未發現任何問題。" },
            { "Zone has null parts list.", "區域的部件列表為空。" },
            { "Zone has no parts.", "區域沒有任何部分。" },
            { "Done.", "完成。" },
            { "Not found.", "找不到。" },
            { "No adjacent empty squares to create your wish!", "沒有相鄰的空格來實現你的願望！" },
            { "You swell with the inspiration to name an item.", "你湧現出為物品命名的靈感。" },
            { "You have no hands to beat at the flames with!", "你沒有手可以拍打火焰！" },
            { "You have no hands to beat at the flames with, and cannot roll on the ground because you are flying!", "你沒有手可以拍打火焰，而且因為你正在飛行，所以無法在地面上翻滾！" },
            { "You have no hands to beat at the flames with, and cannot roll on the ground because you are phased out!", "你沒有手可以拍打火焰，而且因為你處於相位偏移狀態，無法在地面上翻滾！" },
            { "You have the feeling of waking from a dream.", "你有種從夢中醒來的感覺。" },
            { "You feel a soothing tingle in your chest as your wounds start to close.", "當你的傷口開始癒合時，你感覺到胸口有一股舒緩的刺痛感。" },
            { "The soothing tingle fades.", "舒緩的刺痛感逐漸消失。" },
            { "Your mutant physiology reacts adversely to the tonic. The soothing tingle fades.", "你的突變生理機能對這劑補劑產生了不良反應。那股舒緩的刺痛感逐漸消退。" },
            { "The tonics you ingested react adversely to each other. The soothing tingle fades.", "你服下的補劑產生了不良反應。舒緩的刺痛感正在消退。" },
            { "Your skin shrivels and dimples.", "你的皮膚萎縮並凹陷。" },
            { "Your skin flattens out and stretches tautly around your body once again.", "你的皮膚再次變得平坦，並緊繃地包裹著你的身體。" },
            { "Your mutant physiology reacts adversely to the tonic. Your skin starts to knot and misshape.", "你的突變生理機能對該補劑產生了不良反應。你的皮膚開始結塊並變形。" },
            { "The tonics you ingested react adversely to each other. Your skin starts to knot and misshape.", "你服用的補劑產生了不良反應。你的皮膚開始結塊並變形。" },
            { "You have wilted! You'll move and regenerate slower until you eat or bask in the sunlight again.", "你枯萎了！在再次進食或沐浴陽光之前，你的移動速度與再生速度都會變慢。" },
            { "You are famished! You'll act more slowly until you eat again.", "你餓極了！在再次進食之前，你的行動速度會變慢。" },
            { "A torrent of life rushes over you.", "一股生命洪流向你席捲而來。" },
            { "The torrent of life sweeps away.", "生命的洪流正席捲而去。" },
            { "Your mutant physiology reacts adversely to the tonic. The torrent of life sweeps away.", "你的突變生理對該補劑產生了不良反應。生命之流正消逝而去。" },
            { "The tonics you ingested react adversely to each other. The torrent of life sweeps away.", "你服下的補劑產生了不良反應。生命之流正隨之逝去。" },
            { "You cannot do that while submerged.", "你無法在淹沒狀態下執行此動作。" },
            { "Your mutant physiology reacts adversely to the tonic. You flicker through spacetime uncontrollably.", "你的突變生理機能對該補劑產生了不良反應。你在時空之中失控地閃爍。" },
            { "The tonics you ingested react adversely to each other. You flicker through spacetime uncontrollably.", "你服下的補劑產生了不良反應。你在時空之中不受控制地閃爍。" },
            { "Your muscles bulge grotesquely.", "你的肌肉怪異地隆起。" },
            { "Your muscles deflate to their usual size.", "你的肌肉萎縮回原本的大小。" },
            { "Your mutant physiology reacts adversely to the tonic. Aaaaaaaaargh!", "你的突變生理機能對這種補劑產生了不良反應。啊啊啊啊啊啊啊！" },
            { "The tonics you ingested react adversely to each other. Aaaaaaaaargh!", "你服下的補劑產生了不良反應。啊啊啊啊啊啊啊！" },
            { "You recognize the area and stop being lost!", "你認出了這片區域，不再迷路了！" },
            { "You regain your bearings.", "你重新找回了方向。" },
            { "Your hearts begin to beat faster and your pupils dilate.", "你的心跳開始加快，瞳孔也隨之放大。" },
            { "Your heart begins to beat faster and your pupils dilate.", "你的心跳開始加快，瞳孔也隨之放大。" },
            { "Your heart rate returns to normal and your pupils shrink.", "你的心率恢復正常，瞳孔收縮。" },
            { "Your mutant physiology reacts adversely to the tonic. Your field of vision erupts into a plane of blinding, white light.", "你的突變生理機能對該補劑產生了不良反應。你的視野爆裂成一片令人目眩的白光平面。" },
            { "The tonics you ingested react adversely to each other. Your field of vision erupts into a plane of blinding, white light.", "你服下的補劑產生了不良反應。你的視野爆裂成一片令人目眩的白光。" },
            { "You are too exhausted to act!", "你太過疲憊，無法行動！" },
            { "You are too exhausted to do that.", "你太過疲憊，無法做到那件事。" },
            { "You feel a cool swelling as your organs start to glow through your skin.", "你感覺到一股涼爽的腫脹感，你的器官開始透過皮膚發出光芒。" },
            { "The cool swelling deflates as your organs dim.", "隨著你的器官功能衰退，那股冰涼的腫脹感也隨之消退。" },
            { "Your mutant physiology reacts adversely to the tonic. You feel awfully frigid.", "你的突變生理機能對這種補劑產生了不良反應。你感到極度寒冷。" },
            { "The tonics you ingested react adversely to each other. You feel awfully frigid.", "你服下的補藥產生了不良反應。你感到極度寒冷。" },
            { "The clouds part in your mind and a ray of clarity strikes through.", "思緒中的雲霧散去，一道清明之光穿透而入。" },
            { "Your mind clouds over once again.", "你的思緒再次變得模糊不清。" },
            { "Your mutant physiology reacts adversely to the tonic. You cannot see to see -- your mind cracks as a bell struck by a hammer.", "你的突變生理對這種補劑產生了不良反應。你無法看清任何事物——你的心智如同被鐵鎚敲擊的鐘聲般破碎。" },
            { "The tonics you ingested react adversely to each other. You cannot see to see -- your mind cracks as a bell struck by a hammer.", "你服下的補藥產生了不良反應。你無法看清——你的心智如同被鐵鎚敲擊的鐘聲般碎裂。" },
            { "Your heart swells with a burning sensation.", "你的心臟因灼熱感而膨脹。" },
            { "Your heart rate slows again.", "你的心率再次減慢。" },
            { "Your mutant physiology reacts adversely to the tonic. You erupt into flames!", "你的突變生理機能對該補劑產生了不良反應。你全身燃起了火焰！" },
            { "The tonics you ingested react adversely to each other. You erupt into flames!", "你服下的補劑產生了不良反應。你全身燃起了火焰！" },
            // ---- 自動固化：Fail/ShowFailure 訊息（gemma 意譯，2026-08-09）----
            { "It doesn't seem to do anything.", "它似乎沒有起任何作用。" },
            // ---- 2026-08-09 追加：field 初始化訊息（field_missing 萃取）----
            { "That hits the spot!", "這真是太痛快了！" },
            { "Brightness burns your mouth.", "強光灼傷了你的嘴。" },
            { "Brightness burns your mouth, but you cannot be roused any higher.", "光芒灼燒著你的口腔，但你無法再被喚醒得更高了。" },
            { "You feel unsettlingly ambivalent.", "你感到一種令人不安的矛盾情緒。" },
            { "You hear inaudible mumbling.", "你聽到模糊不清的喃喃自語。" },
            { "The liquids stop reacting.", "液體停止反應了。" },
            { "The poison begins to abate, but you still feel nauseous.", "毒素開始消退，但你仍感到噁心。" },
            { "There is a low, persistent hum emanating outward.", "有一股低沉且持續的嗡嗡聲向外擴散。" },
            { "You cooled into a block of shale.", "你冷卻成了一塊頁岩。" },
            { "Poisonous goo burns your eyes.", "有毒的黏液灼傷了你的眼睛。" },
            { "A giant centipede crawls out of the nest.", "一隻巨大的蜈蚣從巢穴中爬了出來。" },
            { "You drank the bright cream of the Palladium Reef and were quickened.", "你喝下了帕拉迪姆礁(Palladium Reef)的明亮奶油，並獲得了加速效果。" },
            { "You taste life as it was distilled by the Eaters, Qud's primordial masons.", "你品嚐到了生命的味道，那是經由卡德(Qud)的原始石匠——食者(Eaters)所萃取的滋味。" },
            { "Charmed by another creature into following them.", "被另一名生物魅惑，進而跟隨牠。" },
            { "Brown sludge splashes into your mouth. You wince at the metallic taste.", "棕色的黏液濺入你的口中。你因那股金屬味而皺起眉頭。" },
            { "Putrid ooze splashes into your mouth. You gag at the awful taste.", "腐爛的黏液濺入你的口中。你被那股惡心的味道嗆到了。" },
            { "You notice some strange ruins nearby. Do you want to investigate?", "你注意到附近有些奇怪的遺跡。要調查看看嗎？" },
            { "One dram of {{neutronic|neutron}} flux evaporates from your inventory.", "一單位{{neutronic|中子}}通量從你的物品欄中蒸發了。" },
            { "Should not be called!", "不應被召喚！" },
            { "The gates are secured shut until the threat to Omonporch is eliminated.", "在威脅消除之前，通往歐蒙波奇(Omonporch) 的門扉都已安全鎖閉。" },
            { "The spaceship can't launch from here.", "太空船無法從這裡發射。" },
            { "The spaceship is already traversing the void.", "太空船已在虛空中穿梭。" },
            { "The spaceship's launch sequence has already begun.", "太空船的發射程序已經開始。" },
            { "There are no places to escape to safely!", "沒有任何可以安全逃脫的地方！" },
            { "There is no one there for you to entangle.", "那裡沒有你可以糾纏的人。" },
            { "There is nobody there to perform Death From Above on.", "那裡沒有人可以對其施展「從天而降的死亡」。" },
            { "There is nothing there for you to clone.", "那裡沒有你可以複製的東西。" },
            { "There's nothing there to slam.", "那裡沒有可以撞擊的東西。" },
            { "There's nothing there you can conk.", "那裡沒有任何你可以敲擊的東西。" },
            { "There's nothing there you can shield slam.", "那裡沒有你可以進行盾擊的目標。" },
            { "While flying, you can only perform Death From Above on nearby targets.", "在飛行時，你只能對附近的目標執行「從天而降(Death From Above)」。" },
            { "You are lost!", "你迷路了！" },
            { "You are not sitting down.", "你沒有坐下。" },
            { "You are out of turrets to place.", "你沒有多餘的砲塔可以放置了。" },
            { "You are paralyzed!", "你癱瘓了！" },
            { "You are unable to consume food.", "你無法食用食物。" },
            { "You are {{C|paralyzed}}!", "你{{C|全身麻痺}}了！" },
            { "You can only teleport to a place you have seen before!", "你只能傳送至你曾經去過的地方！" },
            { "You can't do that here.", "你不能在這裡這樣做。" },
            { "You can't fly right now.", "你現在無法飛行。" },
            { "You can't fly underground!", "你不能在地下飛行！" },
            { "You can't fly while overburdened.", "負重過重時無法飛行。" },
            { "You can't go to sleep right now.", "你現在不能睡覺。" },
            { "You can't leave the golem while it is ascending.", "你不能在魔像上升期間離開它。" },
            { "You can't pilot the golem while it is ascending.", "你無法在魔像上升時操控它。" },
            { "You can't recoil with hostiles nearby!", "附近有敵對生物時，你無法後退！" },
            { "You can't repair that.", "你無法修理那個。" },
            { "You can't repair with hostile creatures nearby.", "附近有敵對生物時，你無法進行修理。" },
            { "You cannot berate without a tongue.", "沒有舌頭，你就無法謾罵。" },
            { "You cannot charge a flying target.", "你無法衝向飛行中的目標。" },
            { "You cannot charge while flying.", "飛行時無法進行充能。" },
            { "You cannot charge while in melee combat.", "你在近戰狀態下無法衝鋒。" },
            { "You cannot charge while overburdened.", "負重過重時無法衝刺。" },
            { "You cannot do that while enclosed.", "你無法在被包圍的狀態下執行該動作。" },
            { "You cannot do that while flying.", "你在飛行時無法執行此動作。" },
            { "You cannot do that while sitting.", "你不能在坐著的時候做那件事。" },
            { "You cannot do that while swimming.", "你無法在游泳時執行該動作。" },
            { "You cannot do that while wading.", "你在涉水時無法執行此動作。" },
            { "You cannot mark a target while you are confused.", "當你處於混亂狀態時，無法標記目標。" },
            { "You cannot perform Death From Above on a door.", "你無法對著一扇門使用「從天而降(Death From Above)」。" },
            { "You cannot perform Death From Above on a wall.", "你無法對牆壁使用從天而降(Death From Above)。" },
            { "You cannot perform Death From Above while overburdened.", "你在負重過重時無法執行從天而降(Death From Above)。" },
            { "You do not have a thrown weapon equipped.", "你沒有裝備投擲武器。" },
            { "You do not have any recoilers.", "你沒有任何回彈器。" },
            { "You don't have enough allied factions. Come back when you're favored by {{C|4}} or more factions.", "您沒有足夠的盟友派系。請在受到 {{C|4}} 個或更多派系青睞時再回來。" },
            { "You gain an additional {{rules|Floating Nearby}} slot!", "你獲得了一個額外的 {{rules|Floating Nearby}} 欄位！" },
            { "You have no devices that use energy cells.", "你沒有任何使用能量電池的裝置。" },
            { "You have no items that can be magnetized.", "你沒有任何可以被磁化的物品。" },
            { "You have no missile weapons to deploy.", "你沒有可部署的飛彈武器。" },
            { "You have no tonics to load.", "你沒有可載入的補劑。" },
            { "You have no weapon!", "你沒有武器！" },
            { "You lack a liquid to spit!", "你缺乏可以吐出的液體！" },
            { "You lack the means to do that.", "你缺乏執行該動作的手段。" },
            { "You may only teleport into an empty square!", "你只能傳送至空格！" },
            { "You must charge at a target!", "你必須衝向目標！" },
            { "You must have a cudgel equipped in order to use slam.", "你必須裝備棍棒才能使用重擊。" },
            { "You must have a cudgel equipped in your primary hand to conk.", "你必須在主手中裝備棍棒才能敲擊。" },
            { "You must have a long blade equipped in your primary hand to lunge.", "你必須在主手中裝備長刃才能進行突刺。" },
            { "You must have a long blade equipped in your primary hand to swipe.", "你必須在主手中裝備一把長刃才能進行揮砍。" },
            { "You must have a shield equipped to perform a shield slam.", "你必須裝備盾牌才能執行盾牌重擊。" },
            { "You're too confused to do that.", "你太困惑了，無法做到那件事。" },
            { "Your onboard cloning systems are offline.", "您的船載複製系統已離線。" },
        };

    private const char SEP = '\u0001';

    private static string LookupOverride(string context, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        string zh;
        if (UiOverrides.TryGetValue((context ?? "") + SEP + id, out zh)) return zh;
        return null;
    }

    private static string LookupPhrase(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        string zh;
        if (UiPhrases.TryGetValue(s, out zh)) return zh;
        return null;
    }

    // ---------- Strings._S 前綴（官方 API） ----------

    public static bool _SHook3(ref string __result, string Context, string ID)
    {
        try { string zh = LookupOverride(Context, ID); if (zh != null) { __result = zh; return false; } } catch { }
        return true;
    }

    public static bool _SHook2(ref string __result, string Context, string ID)
    {
        try { string zh = LookupOverride(Context, ID); if (zh != null) { __result = zh; return false; } } catch { }
        return true;
    }

    public static bool _SHook1(ref string __result, string ID)
    {
        try { string zh = LookupOverride(null, ID); if (zh != null) { __result = zh; return false; } } catch { }
        return true;
    }

    // ---------- Strings._S 後綴：記錄「回退英文」的缺漏 ----------
    // 僅在 ZH_TW_REPLACER_LOG 開啟時有效（Log 內部有 total cap；miss 為列舉用）
    public static void _SMissPostfix(string Context, string ID, string __result)
    {
        try
        {
            if (string.IsNullOrEmpty(ID)) return;
            if (__result == ID)
                ZhTwReplacers.Log("STRING_MISS: [" + (Context ?? "") + "] " + ID);
        }
        catch { }
    }

    // ---------- Popup 顯示層：原始字面值 ----------

    // ShowYesNoCancel(string Message, ...)
    public static bool PopupYesNoPrefix(ref string Message)
    {
        try { string zh = LookupPhrase(Message); if (zh != null) { Message = zh; } } catch { }
        return true;
    }

    // PickOption(string Title, string Intro, ..., IReadOnlyList<string> Options, ...)
    public static bool PopupPickPrefix(ref string Title, ref string Intro, ref IReadOnlyList<string> Options)
    {
        try
        {
            string zhTitle = LookupPhrase(Title);
            if (zhTitle != null) Title = zhTitle;
            string zhIntro = LookupPhrase(Intro);
            if (zhIntro != null) Intro = zhIntro;
            if (Options == null) return true;
            bool changed = false;
            var list = new List<string>(Options.Count);
            foreach (var o in Options)
            {
                string zh = LookupPhrase(o);
                if (zh != null) { list.Add(zh); changed = true; }
                else list.Add(o);
            }
            if (changed) Options = list;
        }
        catch { }
        return true;
    }

    // ShowOptionList(string Title = "", IReadOnlyList<string> Options = null, ...)
    public static bool PopupOptionListPrefix(ref string Title, ref IReadOnlyList<string> Options)
    {
        try
        {
            string zhTitle = LookupPhrase(Title);
            if (zhTitle != null) Title = zhTitle;
            if (Options == null) return true;
            bool changed = false;
            var list = new List<string>(Options.Count);
            foreach (var o in Options)
            {
                string zh = LookupPhrase(o);
                if (zh != null) { list.Add(zh); changed = true; }
                else list.Add(o);
            }
            if (changed) Options = list;
        }
        catch { }
        return true;
    }

    // ---------- Popup.Show(string Message, ...) 前綴：日誌筆記等動態訊息 ----------
    // 日誌類別名（JournalScreen 硬編碼 STR_*）
    private static readonly Dictionary<string, string> JournalCategories =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Sultan Histories", "蘇丹歷史" },
            { "Village Histories", "村莊歷史" },
            { "Gossip and Lore", "閒談與傳說" },
            { "General Notes", "一般筆記" },
            { "Recipes", "食譜" },
            { "Locations", "地點" },
            { "Chronology", "編年史" },
            { "Observations", "觀察" },
            { "Artifacts", "神器" },
            { "Historic Sites", "歷史遺址" },
            { "Lairs", "巢穴" },
            { "Merchants", "商人" },
            { "Named Locations", "命名地點" },
            { "Natural Features", "自然地景" },
            { "Oddities", "珍奇之物" },
            { "Ruins", "遺跡" },
            { "Baetyls", "神聖石" },
            { "Settlements", "聚落" },
            { "Ruins with Becoming Nooks", "帶轉化之窟的遺跡" },
        };

    private static readonly System.Text.RegularExpressions.Regex JournalNoteRegex =
        new System.Text.RegularExpressions.Regex(
            @"^You note this piece of information in the \{\{W\|(.+?)\}\} section of your journal\.?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex JournalLocationRegex =
        new System.Text.RegularExpressions.Regex(
            @"^You note the location of (.+?) in the \{\{W\|(.+?)\}\} section of your journal\.?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Quest.cs 硬編碼 Popup 動態模板（任務名/步驟名在括號內保留）
    private static readonly System.Text.RegularExpressions.Regex QuestReceivedRegex =
        new System.Text.RegularExpressions.Regex(
            @"^You have received a new quest, (.+?)!$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex QuestFailedRegex =
        new System.Text.RegularExpressions.Regex(
            @"^You have failed the quest (.+?)!$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex QuestCompletedRegex =
        new System.Text.RegularExpressions.Regex(
            @"^You have completed the quest (.+?)!$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex QuestFailedStepRegex =
        new System.Text.RegularExpressions.Regex(
            @"^You have failed the step, \{\{R\|(.+?)\}\}, of the quest (.+?)!$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex QuestFinishedStepRegex =
        new System.Text.RegularExpressions.Regex(
            @"^You have finished the step, \{\{G\|(.+?)\}\}, of the quest (.+?)!$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string TranslateJournalCategory(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        string zh;
        if (JournalCategories.TryGetValue(s, out zh)) return zh;
        // 形如 "Sultan Histories > 瑞謝夫(Reshep)"：分段翻譯類別名
        var segs = s.Split('>');
        for (int i = 0; i < segs.Length; i++)
        {
            string seg = segs[i].Trim();
            if (JournalCategories.TryGetValue(seg, out zh))
                segs[i] = segs[i].Replace(seg, zh);
        }
        return string.Join(">", segs);
    }

    // ===== 動態 Popup 意譯（gemma 生成 + 審核）=====
    private static readonly System.Collections.Generic.List<System.Tuple<System.Text.RegularExpressions.Regex, string>> DynamicPopupPatterns =
        new System.Collections.Generic.List<System.Tuple<System.Text.RegularExpressions.Regex, string>>
        {
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^No blueprint by ID '(.+?)' found\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "找不到 ID 為「{1}」的藍圖。"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You have received a new quest, (.+?)!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你收到了一個新任務：{1}！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You have failed the quest (.+?)!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你失敗了任務：{1}！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You have completed the quest (.+?)!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你已完成任務 {1}！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^No blueprint named ""(.+?)"" found\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "找不到名為「{1}」的藍圖。"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You gain \{\{C\|(.+?)\}\} skill points!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你獲得了 {{C|{1}}} 點技能點數！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Your Ego is increased by \{\{G\|(.+?)\}\}!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你的心智增加了 {{G|{1}}}！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Your Ego is decreased by \{\{R\|(.+?)\}\}!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你的心智降低了 {{R|{1}}}！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Your Intelligence is increased by \{\{G\|(.+?)\}\}!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你的智力增加了 {{G|{1}}}！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Your Willpower is increased by \{\{G\|(.+?)\}\}!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你的意志力增加了 {{G|{1}}}！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You start calling (.+?) by the name '(.+?)'\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你開始稱呼 {1} 為「{2}」。"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You are already (.+?) $", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你已經是 {1} 了"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^No members found for '(.+?)'\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "找不到「{1}」的成員。"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You gain \{\{C\|(.+?)\}\} XP\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你獲得了 {{C|{1}}} 經驗值。"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You share your (.+?) with $", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你與 {1} 分享了"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You gained \{\{C\|(.+?)\}\} skill points!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你獲得了 {{C|{1}}} 點技能點數！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^No biome by name '(.+?)' found\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "找不到名為「{1}」的生物群系。"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^The dance ended in failure! \[(.+?)\]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "舞蹈以失敗告終！[{1}]"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^The dance ended in success! \[(.+?)\]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "舞蹈圓滿結束！[{1}]"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Your (.+?) is increased by \{\{G\|1\}\}!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你的 {1} 增加了 {{G|1}}！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You try to (.+?) $", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你試著要 {1}"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You (.+?) $", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你 {1}"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^A sewage eel wraps itself(.+?)!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "一條污水鰻纏繞住了你{1}！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You don't have any (.+?) to clean $", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你沒有任何需要清理的 {1}"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^The council will be convened! Come back in (.+?) $", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "議會即將召開！請在 {1} 後回來"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^A sphere of light in the chord of (.+?) radiates away\.\n\nYou feel it absorbed elsewhere\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "一團位於 {1} 和弦中的光球向外擴散。\n\n你感覺到它在其他地方被吸收了。"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You discover (.+?)!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你發現了 {1}！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You traveled to (.+?)!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你前往了 {1}！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You stop calling this location '(.+?)' and start calling it '(.+?)'\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你不再稱呼此處為「{1}」，而是開始稱之為「{2}」。"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You start calling this location '(.+?)'\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你開始稱呼這個地點為「{1}」。"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^no string property '(.+?)' found$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "找不到字串屬性「{1}」"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^no int property '(.+?)' found$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "找不到整數屬性「{1}」"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Could not generate turret from blueprint ""(.+?)""\n\n$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "無法從藍圖「{1}」生成砲塔。"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^The infected crust of skin on your (.+?) loosens and breaks away\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你{1}上受感染的皮膚結痂鬆脫並脫落了。"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You are (.+?)!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你就是 {1}！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You are \{\{\|(.+?)\}\}!$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你是 {{|{1}}}！"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Error generating population:(.+?)\n\n, please report this error to support@freeholdgames\.com$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "生成人口時發生錯誤：{1}\n\n，請將此錯誤回報至 support@freeholdgames.com"),
            System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^No table by the name '(.+?)' could be resolved\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "無法解析名為「{1}」的資料表。"),
            // ---- 自動固化：Fail/ShowFailure 前綴片段（gemma 意譯，2026-08-09）----
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^A\ sphere\ of\ light\ in\ the\ chord\ of(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "在...的和弦中一個光球{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Accessing\ the\ pilot\ console\ requires\ the\ permanent\ insertion\ of(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "存取駕駛控制台需要永久插入{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Are\ you\ sure\ you\ want\ to\ disassemble(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "您確定要拆解嗎？{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Are\ you\ sure\ you\ want\ to\ target(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "您確定要瞄準嗎？{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^As\ the\ prism\ shatters,\ a\ reflection\ of(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "隨著稜鏡破碎，映照出的是{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^As\ the\ prism\ shatters,\ reflections\ of(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "隨著稜鏡破碎，反射出的...{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Can\ not\ remove\ the\ last\ binding\ for(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "無法移除最後一個束縛，用於{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Choose\ a(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "選擇一個{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Choose\ a\ physical(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "選擇一個物理{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Could\ not\ find\ body\ part\ by\ name\ or\ description:(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "找不到以名稱或描述定義的身體部位：{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Debug\ step(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "除錯步驟{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Debug:(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "除錯：{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Despite\ your\ genetic\ limitations,(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "儘管有著基因上的限制，{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^DisplayName:(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "顯示名稱：{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Due\ to\ your\ revelation,(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "由於你的啟示，{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Enter\ a\ new\ name\ for(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "輸入新的名稱給予{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Enter\ notes\ for(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "輸入筆記：{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Failed\ to\ rebuild\ body\ as(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "無法重建身體，身為{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Generated(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "已生成{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Have\ ambient\ stabilization\ at\ strength(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "在力量時擁有環境穩定化能力{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^If\ you\ want\ to\ apply(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "如果你想要申請{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^If\ you\ want\ to\ eat(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "如果你想吃{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Just\ before\ your\ demise,\ you\ are\ transported\ to\ safety!(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "就在你臨終前，你被傳送到了安全地帶！{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^No\ mod\ found\ for(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "找不到用於以下項目的模組：{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^No\ valid\ targets\ for(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "沒有有效的目標用於{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Pick\ step\ from(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "從以下選擇步驟{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Should\ not\ be\ called:\ testPhase(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "不應稱為：testPhase{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Some\ sticky\ goop\ mixes\ in\ with(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "一些黏稠的黏液混入了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Some\ sticky\ goop\ passes\ through(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "一些黏稠的黏液流了過來{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Somehow\ there\ seems\ to\ be\ no\ location\ for(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "不知為何，似乎沒有 =name= 的位置{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Talking\ to(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "正在與之交談{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^The\ council\ will\ be\ convened!\ Come\ back\ in(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "會議即將召開！請稍後再回來{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^The\ delegate\ for(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "...的代表{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^The\ infected\ crust\ of\ skin\ on\ your(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你身上的感染性皮屑{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^The\ liquid\ mixture\ inside(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "內部的液體混合物{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^The\ polygel\ morphs\ into\ another(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "聚合物凝膠變形成另一種形態{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^The\ sealing\ mechanisms\ inside\ this\ sarcophagus\ will\ certainly\ kill\ you\ if\ you\ close(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "這具石棺內部的密封機制，如果你將其關閉，肯定會殺死你{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^There\ is\ no(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "沒有任何{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^There\ is\ no\ one\ there\ to\ use(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "那裡沒有人可以被使用{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^There\ is\ no\ one\ there\ you\ can(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "那裡沒有任何人可以讓你{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^There\ is\ no\ one\ there\ you\ can\ use(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "那裡沒有你可以利用的人{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^There\ is\ nowhere\ for\ the(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "沒有任何地方可以讓這個{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^There\ was\ an\ error\ saving:(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "儲存時發生錯誤：{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^To\ perform\ Death\ From\ Above\ from\ the\ ground,\ you\ must\ select\ a\ target\ at\ least(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "若要在地面執行「從天而降(Death From Above)」，你必須選擇至少一個目標{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^To\ perform\ Death\ From\ Above\ from\ the\ ground,\ you\ must\ select\ a\ target\ no\ more\ than(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "若要在地面執行「從天而降(Death From Above)」，你必須選擇一個距離不超過的目標{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^What\ name\ should\ be\ used\ for\ your(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "您的名字應該使用什麼？{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^What\ objective\ pronoun\ \(him,\ her,\ them,\ etc\.\)\ should\ be\ used\ for\ this(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "這句話應該使用哪種受格代名詞（他、她、他們等）？{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^What\ possessive\ adjective\ \(his,\ her,\ their,\ etc\.\)\ should\ be\ used\ for\ this(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "這句話應該使用哪種所有格形容詞（他的、她的、他們的等）？{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^What\ reflexive\ pronoun\ \(himself,\ herself,\ themself,\ themselves,\ etc\.\)\ should\ be\ used\ for\ this(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "這句話應該使用哪種反身代名詞（himself、herself、themself、themselves 等）？{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^What\ subjective\ pronoun\ \(he,\ she,\ they,\ etc\.\)\ should\ be\ used\ for\ this(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "應該使用哪種主格代名詞（他、她、他們等）？{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^What\ substantive\ possessive\ \(his,\ hers,\ theirs,\ etc\.\)\ should\ be\ used\ for\ this(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "應該使用哪種實質性所有格（他的、她的、他們的等）？{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^What\ term\ should\ be\ used\ for\ a\ mature\ person\ of\ this(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "對於這種成熟的人，應該使用哪個術語？{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^What\ term\ should\ be\ used\ for\ an\ immature\ person\ of\ this(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "對於這種不成熟的人，應該使用哪個術語？{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^What\ term\ should\ be\ used\ to\ address\ a\ person\ of\ this(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "應該使用什麼稱呼來指代這種人？{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^What\ term\ should\ be\ used\ to\ formally\ address\ a\ person\ of\ this(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "應該使用什麼稱謂來正式稱呼此類人士？{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ are\ already(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你已經是{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ are\ already\ in(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你已經在裡面了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ are\ out\ of\ phase\ with(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你與以下對象相位不同：{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ are\ promoted\ to\ the(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你被晉升為{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ are\ too\ far\ from(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你離以下地點太遠了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ are\ too\ large\ to\ enter(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你體型太大，無法進入{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ aren't\ strong\ enough\ to\ slam\ through(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你的力量不足以撞開它{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ bothered(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你被打擾了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ can't\ aggressively\ lunge\ through(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法強力衝刺穿過{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ can't\ charge\ more\ than(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你不能收取的費用超過{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ can't\ figure\ out\ how\ to\ fix(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法找出修復的方法{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ can't\ gain(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法獲得{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ can't\ safely\ ascend(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法安全地向上攀升{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ can't\ safely\ descend(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法安全地下降{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ can't\ use(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法使用{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ clone(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法複製{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ do\ that\ while(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法在ขณะ進行時做到那樣{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ do\ that\ while\ enclosed\ by(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法在被以下對象包圍時執行此操作{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ do\ that\ while\ engulfed\ by(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你在被 {{color|text}} 包圍時無法執行該動作{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ enter(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法進入{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ fly\ while\ engulfing(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "在吞噬狀態下你無法飛行{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ give(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法給予{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ juke(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法閃躲{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ juke\ both(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法同時閃避兩者{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ juke\ into(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法閃避進入{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ make\ contact\ with(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法與之取得聯繫{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ perform\ Death\ From\ Above\ on(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法對 =name= 使用從天而降(Death From Above){1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ reach(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法觸及{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ repair(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法修理{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ seem\ to\ affect(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你似乎無法影響{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ set(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法設定{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ shield\ slam(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法使用盾牌重擊{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ sit\ on(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法坐在上面{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ slam(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法進行猛擊{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ sleep\ on(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法在以下地點睡覺：{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ cannot\ unequip(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你無法卸下裝備{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ discover(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你發現了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ do\ not\ have(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你沒有{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ do\ not\ have\ 1\ dram\ of(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你沒有 1 德蘭的{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ don't\ have\ any(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你沒有任何物品{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ don't\ have\ enough(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "您的數量不足{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ don't\ have\ the\ capacity\ to\ ascend(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你沒有昇華的能力{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ don't\ have\ the\ capacity\ to\ descend(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你沒有下降的能力{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ don't\ know\ how\ to\ use(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你不知道如何使用{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ eat(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你吃下{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ eject(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你彈出了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ encode\ the\ psyche\ of(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你編碼了...的靈魂{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ extricate(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你解救了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ fail\ to\ clone(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你複製失敗{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ fail\ to\ engulf(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你未能吞噬{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ fail\ to\ extricate(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你未能脫身{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ fail\ to\ get(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你未能獲得{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ gain(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你獲得了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ have\ all\ available(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你擁有所有可用的{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ have\ already\ proselytized(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你已經進行過傳教了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ have\ completed\ the\ quest(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你已完成任務{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ have\ failed\ the\ quest(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你任務失敗了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ have\ increased\ your(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你已提升了你的{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ have\ no(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你沒有任何{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ have\ no\ ammunition\ to\ supply(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "您沒有可提供的彈藥{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ have\ no\ followers\ that\ can\ enter(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你沒有可以進入的追隨者{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ have\ no\ physical(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你沒有實體{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ have\ no\ supplies\ that(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你沒有任何可以{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ have\ rapidly\ advanced(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你快速地進步了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ have\ received\ a\ new\ quest,(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你收到了一個新任務，{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ imbue(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你灌注{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ make\ some\ progress\ repairing(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你在修理方面取得了一些進展{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ must\ charge\ at\ least(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你必須至少衝鋒{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ must\ wait(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你必須等待{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ name(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你的名字{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ need\ be\ near(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你必須靠近{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ pause\ as\ the\ psyche\ of(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你停頓了一下，因為...的靈魂{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ realize(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你意識到{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ receive(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你收到{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ repair(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你修理了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ share\ your(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你分享你的{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ slot(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你插槽{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ start\ calling(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你開始呼喚{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ think\ you\ broke(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你以為你壞掉了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ traveled\ to(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你前往了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You\ try\ to(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你試圖{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You're\ not\ hungry\ enough\ to\ bring(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你還不夠餓，無法帶走{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^You've\ contracted(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你感染了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Your\ companion,(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你的夥伴，{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Your\ genome\ destabilizes\ and\ you\ gain(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "您的基因組變得不穩定，您獲得了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Your\ lunge\ passes\ through(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "你的突刺穿過了{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^Your\ onboard\ systems\ are\ out\ of(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "您的機載系統已失效{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^\{\{K\|You\ are\ being\ watched\.\
\
It's\ a\ familiar\ feeling\.\ When\ someone\ has\ watched\ you\ in\ the\ past,\ when\ it's\ light\ that's\ betrayed\ your\ presence,\ you\ made\ a\ friend\ of\ the\ darkness\.\ You\ pulled\ your\ hat\ brim\ low\ over\ your\ eyes\.\ You\ stepped\ behind\ the\ cover\ of\ a\ thatched\ wall\.\ But\ those\ who\ watch\ you\ now\ watch\ in\ spite\ of\ such\ simple\ obstructions\.\ Their\ sight\ isn't\ mediated\ by\ the\ rays\ of\ a\ gleaming\ star\ or\ torch\ but\ by\ something\ much\ older\.\ If\ there\ are\ ways\ to\ conceal(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "{{K|你正被注視著。\n\n這是一種熟悉的感覺。當過去有人注視著你時，當光線背叛了你的行蹤，你會與黑暗結為朋友。你會將帽簷壓低遮住雙眼，或是躲在茅草牆的掩護後。但現在注視著你的人，無視這些簡單的障礙物。他們的視線並非透過閃爍星辰或火炬的光芒來傳遞，而是透過某種更古老的事物。如果還有隱藏的方法...{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^\{\{K\|You've\ discovered\ a\ way\ to\ conceal(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "{{K|你發現了一種隱藏的方法{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^\{\{r\|You\ cannot\ berate(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "{{r|你無法責罵{1}"),
System.Tuple.Create(new System.Text.RegularExpressions.Regex(@"^\{\{r\|You\ cannot\ make\ telepathic\ contact\ with(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "{{r|你無法與以下對象建立心靈感應：{1}"),
        };

    private static string ApplyDynamicPopup(string msg)
    {
        foreach (var t in DynamicPopupPatterns)
        {
            var m = t.Item1.Match(msg);
            if (!m.Success) continue;
            string r = t.Item2;
            for (int gi = 1; gi < m.Groups.Count; gi++)
                r = r.Replace("{" + gi + "}", m.Groups[gi].Value);
            return r;
        }
        return null;
    }

    public static bool PopupShowPrefix(ref string Message)
    {
        try
        {
            if (string.IsNullOrEmpty(Message)) return true;
            var m = JournalNoteRegex.Match(Message);
            if (m.Success)
            {
                Message = "你在日誌的 {{W|" + TranslateJournalCategory(m.Groups[1].Value) + "}} 區段中記下這項資訊。";
                return true;
            }
            var ml = JournalLocationRegex.Match(Message);
            if (ml.Success)
            {
                Message = "你在日誌的 {{W|" + TranslateJournalCategory(ml.Groups[2].Value) + "}} 區段中記下 " + ml.Groups[1].Value + " 的位置。";
                return true;
            }
            var mQr = QuestReceivedRegex.Match(Message);
            if (mQr.Success) { Message = "你接受了新任務：" + mQr.Groups[1].Value + "！"; return true; }
            var mQf = QuestFailedRegex.Match(Message);
            if (mQf.Success) { Message = "你未能完成任務：" + mQf.Groups[1].Value + "！"; return true; }
            var mQc = QuestCompletedRegex.Match(Message);
            if (mQc.Success) { Message = "你已完成任務：" + mQc.Groups[1].Value + "！"; return true; }
            var mQfs = QuestFailedStepRegex.Match(Message);
            if (mQfs.Success) { Message = "你未能完成任務 " + mQfs.Groups[2].Value + " 的步驟：{{R|" + mQfs.Groups[1].Value + "}}！"; return true; }
            var mQfin = QuestFinishedStepRegex.Match(Message);
            if (mQfin.Success) { Message = "你已完成任務 " + mQfin.Groups[2].Value + " 的步驟：{{G|" + mQfin.Groups[1].Value + "}}！"; return true; }
            // 動態 Popup 意譯（gemma 生成：38 條）
            string dyn = ApplyDynamicPopup(Message);
            if (dyn != null) { Message = dyn; return true; }
            string p = LookupPhrase(Message);
            if (p != null) Message = p;
        }
        catch { }
        return true;
    }

    // ---------- 側欄/狀態畫面屬性標籤（console 渲染，ScreenBuffer.Write 後綴）----------
    // 只在「整字串即標籤」時翻譯（避免誤換數值行）；AV/DV/MA 保留英文縮寫（Qud 慣用）
    private static readonly Dictionary<string, string> SidebarLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "ST", "力量" }, { "AG", "敏捷" }, { "QN", "速度" }, { "MS", "移動" },
            { "TO", "耐力" }, { "WI", "意志" }, { "IN", "智力" }, { "EG", "心智" },
            { "T", "溫度" }, { "XP", "經驗" },
            { "Acid Resist", "抗酸" }, { "Cold Resist", "寒冷抗性" },
            { "Electrical Resist", "電力抗性" }, { "Heat Resist", "熱力抗性" },
        };

    // 角色狀態畫面區段大標題（CharacterStatusScreen；非 Strings._S、執行期渲染，
    // 只能在此 console 繪製路徑攔截。精確匹配整串。）
    private static readonly Dictionary<string, string> SectionHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "RESISTANCES", "抗性" },
            { "SECONDARY ATTRIBUTES", "次要屬性" },
            { "MAIN ATTRIBUTES", "主要屬性" },
            { "ATTRIBUTES", "屬性" },
            { "STATISTICS", "統計" },
            { "SKILLS", "技能" },
            { "INFOS", "資訊" },
        };

    // 通用：對「所有」ScreenBuffer.Write 多載做區段標題精確翻譯（角色狀態畫面 RESISTANCES 等，
    // 不限側欄那 2 個多載）。只精確匹配 SectionHeaders，短字串，低開銷。
    // 註：參數名 s 須與 ScreenBuffer.Write 的第一個參數同名（Harmony 依名匹配）。
    public static void SectionHeaderWritePrefix(ref string s)
    {
        try
        {
            if (string.IsNullOrEmpty(s) || s.Length > 40) return;
            string zh;
            if (SectionHeaders.TryGetValue(s, out zh)) { s = zh; return; }
            string t = s.Trim();
            if (t.Length > 0 && t != s && SectionHeaders.TryGetValue(t, out zh)) s = zh;
        }
        catch { }
    }

    public static void SectionHeaderSBufWritePrefix(System.Text.StringBuilder s)
    {
        try
        {
            if (s == null || s.Length == 0 || s.Length > 40) return;
            string str = s.ToString();
            string zh;
            if (SectionHeaders.TryGetValue(str, out zh)) { s.Clear().Append(zh); return; }
            string t = str.Trim();
            if (t.Length > 0 && t != str && SectionHeaders.TryGetValue(t, out zh)) { s.Clear().Append(zh); }
        }
        catch { }
    }

    // Unity UI（TMP）區段標題：角色狀態畫面走 TextMeshPro，不走 console。
    // 掛在 TMP_Text.set_text；只精確匹配 SectionHeaders，短字串守衛，低開銷。
    public static void TmpHeaderPrefix(ref string value)
    {
        try
        {
            if (string.IsNullOrEmpty(value) || value.Length > 40) return;
            string zh;
            if (SectionHeaders.TryGetValue(value, out zh)) { value = zh; return; }
            string t = value.Trim();
            if (t.Length > 0 && t != value && SectionHeaders.TryGetValue(t, out zh)) value = zh;
        }
        catch { }
    }

    public static void SidebarLabelPrefix(ref string s)
    {
        try
        {
            if (string.IsNullOrEmpty(s)) return;
            // 效能：只處理短字串（側欄標籤 / 日誌 tab 名 / [History of X] 都很短），避免在熱渲染路徑拖慢
            if (s.Length > 40) return;
            string replaced = TranslateSidebarString(s);
            if (replaced != s) s = replaced;
        }
        catch { }
    }

    public static void SidebarLabelSBufPrefix(System.Text.StringBuilder s)
    {
        try
        {
            if (s == null || s.Length == 0 || s.Length > 40) return;
            string cur = s.ToString();
            string replaced = TranslateSidebarString(cur);
            if (replaced != cur)
            {
                s.Length = 0;
                s.Append(replaced);
            }
        }
        catch { }
    }

    // 統一翻譯：日誌 tab 名 / [History of X] / 側欄屬性標籤
    private static string TranslateSidebarString(string s)
    {
        string stripped = SidebarColorRegex.Replace(s, "");
        string wrapOpen = null, wrapClose = null;
        string inner = stripped;
        // 富文本 markup 形式（{{W|Locations}}，Markup.Transform 處理前）
        var wm = JournalMarkup.Match(stripped);
        if (wm.Success)
        {
            wrapOpen = "{{" + wm.Groups[1].Value + "|";
            wrapClose = "}}";
            inner = wm.Groups[2].Value;
        }
        // 日誌 tab 名（JournalScreen STR_*）精確匹配
        string journalZh;
        if (JournalCategories.TryGetValue(inner, out journalZh))
        {
            return (wrapOpen != null ? wrapOpen : "") + journalZh + (wrapClose != null ? wrapClose : "");
        }
        // 角色狀態畫面區段大標題（RESISTANCES / SECONDARY ATTRIBUTES 等）精確匹配
        string headerZh;
        if (SectionHeaders.TryGetValue(inner.Trim(), out headerZh))
        {
            return (wrapOpen != null ? wrapOpen : "") + headerZh + (wrapClose != null ? wrapClose : "");
        }        // 動態蘇丹 tab：「{sultanName} Histories」（GetSultansDisplayName 組裝）
        var shm = SultanHistories.Match(inner);
        if (shm.Success)
        {
            string x = shm.Groups[1].Value;
            string zh = x + " 的歷史";
            return (wrapOpen != null ? wrapOpen : "") + zh + (wrapClose != null ? wrapClose : "");
        }
        // [History of X] / [History of X, Vol. N] 標題列
        var hm = HistoryOf.Match(inner);
        if (hm.Success)
        {
            string x = hm.Groups[1].Value;
            string vol = hm.Groups[2].Value;
            string zh = (vol != null) ? "[" + x + " 第 " + vol + " 卷的歷史]" : "[" + x + " 的歷史]";
            return (wrapOpen != null ? wrapOpen : "") + zh + (wrapClose != null ? wrapClose : "");
        }
        // 側欄屬性標籤（ST/AG/DV 等，&X 已剝）
        foreach (var kv in SidebarLabels)
        {
            string lab = kv.Key;
            if (inner.Length < lab.Length) continue;
            if (string.CompareOrdinal(inner, 0, lab, 0, lab.Length) == 0)
            {
                // lab 後必須是 :、空格或行尾（避免誤換 "ST" 開頭的其他詞）
                if (inner.Length == lab.Length || inner[lab.Length] == ':' || inner[lab.Length] == ' ')
                {
                    // 標籤中文化（保留後續數值與格式）
                    return kv.Value + inner.Substring(lab.Length);
                }
            }
        }
        return s;
    }

    private static readonly System.Text.RegularExpressions.Regex SidebarColorRegex =
        new System.Text.RegularExpressions.Regex(@"&[A-Za-z]", System.Text.RegularExpressions.RegexOptions.Compiled);
    // {{X|inner}} 富文本 markup
    private static readonly System.Text.RegularExpressions.Regex JournalMarkup =
        new System.Text.RegularExpressions.Regex(@"^\{\{([^}|]+)\|([^{}]*)\}\}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    // [History of X] / [History of X, Vol. N]；亦相容已把 of→的 的「[History 的 X]」
    private static readonly System.Text.RegularExpressions.Regex HistoryOf =
        new System.Text.RegularExpressions.Regex(@"^\[History (?:of|的) (.+?)(?:, Vol\. (.+?))?\]$", System.Text.RegularExpressions.RegexOptions.Compiled);
    // 動態蘇丹 tab：「{sultanName} Histories」
    private static readonly System.Text.RegularExpressions.Regex SultanHistories =
        new System.Text.RegularExpressions.Regex(@"^\s*([^|{}\[\]]+?)\s+Histories\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ===== BookUI prefix（Active Effects / No active effects. 等）=====
    public static void BookShowPrefix(ref string PageText, ref string BookTitle)
    {
        try
        {
            if (PageText != null && PageText.Trim() == "No active effects.")
                PageText = "沒有主動效果。";
            if (BookTitle != null && BookTitle.StartsWith("&WActive Effects&Y", System.StringComparison.Ordinal))
                BookTitle = "&W主動效果&Y" + BookTitle.Substring("&WActive Effects&Y".Length);
        }
        catch { }
    }
}