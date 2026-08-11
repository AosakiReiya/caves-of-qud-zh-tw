// TextCleanerHook.cs — Qud 繁中：文本生成層後處理（TextBuilder.ToString）
//
// 攔截遊戲「動態生成文本」組裝完成的字串，清理殘留英文：
//   - 剝離開頭冠詞（The/A/An 後接中文者）
//   - 常見英文殘留詞（don't / ones / won't 等）→ 中文
//   - 色彩/HTML 標記保留
//
// 選址：hook TextBuilder.ToString()（生成字串的組裝點，每生成一次，
// 而非全域 TMP text setter 的每幀每元件），大幅降低開銷。
// 效能優化：單趟掃描判斷「中英混雜」才處理；有界快取避免重複正則。

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;

public static class ZhTwTextCleaner
{
    private static bool Initialized;

    // 開頭冠詞：The/A/An 後 20 字元內含中文者（如 "The 村民"、"The Anna 的村民"）
    private static readonly Regex LeadingArticle = new Regex(
        @"\b(?:The|A|An)\s+(?=[^\n]{0,20}[\u4e00-\u9fff])", RegexOptions.Compiled);

    // 常見英文殘留詞 → 中文（只留明確縮寫/代名詞，避免把英文模板的好詞單獨翻譯）
    private static readonly Dictionary<string, string> Artifacts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "don't", "不" }, { "won't", "不會" }, { "can't", "不能" },
            { "doesn't", "不" }, { "isn't", "不是" }, { "aren't", "不是" },
            { "ones", "那些" }, { "its", "它的" }, { "their", "他們的" },
        };

    // 常見生成句動詞（3 人稱單數形）→ 中文。多義詞以「動詞義」優先（如 pets）
    private static readonly Dictionary<string, string> Verbs =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "pets", "撫摸" }, { "petted", "撫摸" }, { "glows", "發光" }, { "glowed", "發光" },
            { "starts", "開始" }, { "started", "開始" }, { "roars", "怒吼" }, { "roared", "怒吼" },
            { "bites", "咬" }, { "bit", "咬了" }, { "attacks", "攻擊" }, { "attacked", "攻擊了" },
            { "hits", "擊中" }, { "hit", "擊中" }, { "kills", "擊殺" }, { "killed", "擊殺了" },
            { "dies", "死亡" }, { "died", "死亡" }, { "follows", "跟隨" }, { "followed", "跟隨" },
            { "stares", "凝視" }, { "stared", "凝視" }, { "watches", "注視" }, { "watched", "注視" },
            { "examines", "檢視" }, { "examined", "檢視" }, { "touches", "觸碰" }, { "touched", "觸碰" },
            { "licks", "舔" }, { "licked", "舔" }, { "sniffs", "嗅" }, { "sniffed", "嗅" },
            { "growls", "低吼" }, { "growled", "低吼" }, { "hisses", "嘶嘶作響" }, { "hissed", "嘶嘶作響" },
            { "chitters", "嘰喳叫" }, { "wiggles", "扭動" }, { "shakes", "搖晃" }, { "shook", "搖晃" },
            { "nods", "點頭" }, { "smiles", "微笑" }, { "smiled", "微笑" },
            { "laughs", "大笑" }, { "laughed", "大笑" }, { "cries", "哭泣" }, { "cried", "哭泣" },
            { "leaps", "躍起" }, { "leaped", "躍起" }, { "jumps", "跳起" }, { "jumped", "跳起" },
            { "runs", "奔跑" }, { "ran", "奔跑" }, { "walks", "行走" }, { "walked", "行走" },
            { "crawls", "爬行" }, { "crawled", "爬行" }, { "flies", "飛翔" }, { "flew", "飛翔" },
            { "swims", "游泳" }, { "swam", "游泳" }, { "digs", "挖掘" }, { "dug", "挖掘" },
            { "howls", "嚎叫" }, { "howled", "嚎叫" }, { "screams", "尖叫" }, { "screamed", "尖叫" },
            { "shimmers", "閃爍" }, { "flickers", "閃爍" }, { "twitches", "抽動" }, { "trembles", "顫抖" },
            { "quivers", "顫動" }, { "pulses", "脈動" }, { "breathes", "呼吸" }, { "breathed", "呼吸" },
            { "sleeps", "沉睡" }, { "slept", "沉睡" }, { "wakes", "甦醒" }, { "woke", "甦醒" },
            { "eats", "進食" }, { "ate", "進食" }, { "drinks", "飲水" }, { "drank", "飲水" },
            { "gazes", "凝望" }, { "gazed", "凝望" }, { "peers", "窺視" }, { "peered", "窺視" },
            { "turns", "轉向" }, { "turned", "轉向" }, { "faces", "面向" }, { "faced", "面向" },
            { "leans", "倚靠" }, { "leaned", "倚靠" }, { "sits", "坐下" }, { "sat", "坐下" },
            { "stands", "站立" }, { "stood", "站立" }, { "kneels", "跪下" }, { "knelt", "跪下" },
            { "bows", "鞠躬" }, { "bowed", "鞠躬" }, { "waves", "揮手" }, { "waved", "揮手" },
            { "calls", "呼喚" }, { "called", "呼喚" }, { "speaks", "說話" }, { "spoke", "說話" },
            { "whispers", "低語" }, { "whispered", "低語" }, { "murmurs", "喃喃" }, { "murmured", "喃喃" },
            { "chants", "吟誦" }, { "chanted", "吟誦" }, { "prays", "祈禱" }, { "prayed", "祈禱" },
            { "offers", "提供" }, { "offered", "提供" }, { "gives", "給予" }, { "gave", "給予" },
            { "takes", "拿走" }, { "took", "拿走" }, { "holds", "握持" }, { "held", "握持" },
            { "drops", "掉落" }, { "dropped", "掉落" }, { "picks", "拾起" }, { "picked", "拾起" },
            { "throws", "投擲" }, { "threw", "投擲" }, { "catches", "接住" }, { "caught", "接住" },
            { "opens", "開啟" }, { "opened", "開啟" }, { "closes", "關閉" }, { "closed", "關閉" },
            { "pushes", "推" }, { "pushed", "推" }, { "pulls", "拉" }, { "pulled", "拉" },
        };

    private static readonly Regex VerbRegex = BuildVerbRegex();

    private static Regex BuildVerbRegex()
    {
        var keys = new List<string>(Verbs.Keys);
        keys.Sort((a, b) => b.Length.CompareTo(a.Length)); // 長詞優先
        var parts = new List<string>();
        foreach (var k in keys)
            parts.Add("(?i)" + Regex.Escape(k));
        return new Regex(@"\b(?:" + string.Join("|", parts) + @")\b", RegexOptions.Compiled);
    }

    private static string VerbMatch(Match m)
    {
        string v;
        return Verbs.TryGetValue(m.Value, out v) ? v : m.Value;
    }

    // 介詞/單位/動詞原形 → 中文（解決 of/drams/to glow 等滲入詞）
    private static readonly Dictionary<string, string> Words =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 介詞/單位（dram 採臺灣口語常見「德蘭」）
            { "drams", "德蘭" }, { "dram", "德蘭" },
            { "of", "的" }, { "ml", "毫升" }, { "tons", "噸" }, { "pounds", "磅" }, { "feet", "英尺" },
            // be 動詞（=ifPlural:are:is= 模板 token 會輸出英文 be 動詞，逐詞兜底）
            { "is", "是" }, { "are", "是" }, { "was", "曾是" }, { "were", "曾是" }, { "life", "生命" },
            // =ifPlural= 其他洩漏動詞/名詞（Faction Feeling 等模板輸出）
            { "despise", "鄙視" }, { "despises", "鄙視" }, { "dislike", "厭惡" }, { "dislikes", "厭惡" },
            { "favor", "偏愛" }, { "favors", "偏愛" }, { "consider", "認為" }, { "considers", "認為" },
            { "revere", "崇敬" }, { "reveres", "崇敬" }, { "members", "成員" },
            // 高頻顯示文字漏詞（run_tests.py LEAK_WORDS 驗證覆蓋）
            { "action", "行動" }, { "causes", "導致" }, { "costs", "花費" },
            { "increase", "增加" }, { "item", "物品" }, { "nearby", "附近的" },
            { "powered", "供能的" }, { "provides", "提供" }, { "reduction", "減少" },
            // 高頻對話/敘述常用詞（export_leak_words.py 導出；多義詞 like/well 保留待人工）
            { "time", "時間" }, { "know", "知道" }, { "now", "現在" }, { "cannot", "不能" },
            { "want", "想要" }, { "need", "需要" }, { "world", "世界" }, { "people", "人們" },
            { "something", "某事" }, { "tell", "告訴" }, { "see", "看到" }, { "come", "來" },
            { "let", "讓" }, { "welcome", "歡迎" }, { "thank", "感謝" }, { "speak", "說" },
            { "nothing", "沒什麼" }, { "feel", "感覺" }, { "place", "地方" }, { "new", "新的" },
            { "think", "認為" }, { "yes", "是" }, { "going", "正在" }, { "always", "總是" },
            { "never", "從不" }, { "here", "這裡" }, { "there", "那裡" }, { "still", "仍然" },
            { "just", "只是" },
            // 動詞原形（配合 "to X" 與獨立出現）
            { "glow", "發光" }, { "hover", "懸浮" }, { "float", "漂浮" }, { "fall", "墜落" },
            { "rise", "升起" }, { "turn", "轉向" }, { "move", "移動" }, { "walk", "行走" },
            { "grow", "成長" }, { "shrink", "縮小" }, { "burn", "燃燒" }, { "freeze", "凍結" },
            { "melt", "融化" }, { "sparkle", "閃爍" }, { "run", "奔跑" }, { "leap", "躍起" },
            { "jump", "跳起" }, { "crawl", "爬行" }, { "swim", "游泳" }, { "fly", "飛翔" },
            { "howl", "嚎叫" }, { "scream", "尖叫" }, { "breathe", "呼吸" }, { "sleep", "沉睡" },
            { "eat", "進食" }, { "drink", "飲水" }, { "gaze", "凝望" }, { "stare", "凝視" },
            // spice 生成詞組漏翻（HistorySpice 不經 mod 載入，執行期補翻）
            { "burnt", "燒毀" }, { "corroded", "腐蝕的" }, { "data", "資料" }, { "disks", "磁碟" },
            { "rife", "充斥" }, { "ruins", "遺跡" }, { "wreck", "殘骸" }, { "autarchy", "專制政體" },
            { "sultan", "蘇丹" }, { "sultanate", "蘇丹國" }, { "dynasty", "王朝" },
            // 日誌類別名（journal note 硬編碼句內）
            { "Sultan Histories", "蘇丹歷史" }, { "Historic Sites", "歷史遺址" },
            { "Named Locations", "命名地點" }, { "Natural Features", "自然地景" },
            { "Artifacts", "神器" }, { "Baetyls", "神聖石" }, { "Lairs", "巢穴" },
{ "Merchants", "商人" }, { "Oddities", "珍奇之物" },
             { "Settlements", "聚落" }, { "Becoming Nooks", "轉化之窟" },
            // 常見殘留介詞/連詞/名詞（LLM 生成，避免中英混雜殘留）
            { "about", "關於" }, { "above", "之上" }, { "across", "橫跨" }, { "after", "之後" },
            { "against", "對抗" }, { "along", "沿著" }, { "among", "之中" }, { "and", "與" },
            { "at", "在" }, { "before", "之前" }, { "behind", "後方" }, { "below", "之下" },
            { "beside", "旁" }, { "between", "之間" }, { "books", "書籍" }, { "by", "由" },
            { "den", "巢穴" }, { "down", "向下" }, { "during", "期間" }, { "for", "為了" },
            { "from", "來自" }, { "gate", "門" }, { "in", "在" }, { "inside", "內部" },
            { "into", "入" }, { "lair", "巢穴" }, { "near", "附近" }, { "off", "離開" },
            { "on", "在" }, { "onto", "到" }, { "outside", "外部" }, { "over", "越過" },
            { "spire", "尖塔" }, { "the", "" }, { "through", "穿過" }, { "to", "向" },
            { "a", "" }, { "an", "" },
            { "toward", "朝向" }, { "towards", "朝向" }, { "tower", "塔" }, { "under", "之下" },
            { "up", "向上" }, { "water", "水" }, { "with", "與" }, { "within", "之內" },
            { "without", "之外" },
            // 2026-08-09 補：戰鬥/狀態訊息常見詞（安全詞，避免中英混雜殘留）
            { "his", "他的" }, { "toggle", "切換" }, { "knocked", "擊倒" },
            { "stops", "停下" }, { "moving", "移動中" }, { "looks", "查看" }, { "out", "出" },
            { "it", "它" }, { "this", "這" }, { "that", "那" },
            // 方向/距離（LoreGenerator 硬編碼 parasangs）
            { "parasang", "帕拉桑" }, { "parasangs", "帕拉桑" },
            { "north", "北方" }, { "south", "南方" }, { "east", "東方" }, { "west", "西方" },
            { "strat", "層" }, { "strata", "層" }, { "deep", "深處" },
            // 六屬性名（意譯；放 Words 才能經 WordsRegex 大小寫不敏感地套用於所有混雜字串）
            { "strength", "力量" }, { "agility", "敏捷" }, { "toughness", "韌性" },
            { "intelligence", "智力" }, { "willpower", "意志力" }, { "ego", "心智" },
            // gemma 意譯的常見遊戲詞（219 條）
            { "acid", "酸" }, { "activate", "啟動" }, { "air", "空氣" }, { "ale", "麥酒" },
            { "amount", "數量" }, { "animal", "動物" }, { "armor", "護甲" }, { "arrow", "箭" },
            { "ash", "灰燼" }, { "attack", "攻擊" }, { "axe", "斧頭" }, { "bandit", "強盜" },
            { "battle", "戰鬥" }, { "beast", "野獸" }, { "begin", "開始" }, { "blade", "刀刃" },
            { "blood", "血液" }, { "body", "身體" }, { "bone", "骨頭" }, { "bonus", "加成" },
            { "bow", "弓" }, { "brain", "大腦" }, { "bread", "麵包" }, { "build", "建造" },
            { "buy", "購買" }, { "camp", "營地" }, { "cave", "洞窟" }, { "ceiling", "天花板" },
            { "chance", "機率" }, { "cheese", "起司" }, { "choose", "選擇" }, { "city", "城市" },
            { "clan", "氏族" }, { "claw", "爪" }, { "close", "關閉" }, { "club", "棍棒" },
            { "completed", "已完成" }, { "confirm", "確認" }, { "continue", "繼續" }, { "copper", "銅" },
            { "cost", "消耗" }, { "craft", "製作" }, { "create", "創造" }, { "creature", "生物" },
            { "cure", "治療" }, { "damage", "傷害" }, { "dark", "黑暗" }, { "deactivate", "停用" },
            { "decreased", "減少" }, { "defend", "防禦" }, { "destroyed", "已摧毀" }, { "discover", "發現" },
            { "disease", "疾病" }, { "door", "門" }, { "dust", "灰塵" }, { "earth", "大地" },
            { "elixir", "靈藥" }, { "end", "結束" }, { "energy", "能量" }, { "enter", "進入" },
            { "equip", "裝備" }, { "experience", "經驗值" }, { "explore", "探索" }, { "eye", "眼睛" },
            { "faction", "陣營" }, { "fail", "失敗" }, { "failed", "失敗了" }, { "failure", "失敗" },
            { "fang", "獠牙" }, { "farmer", "農夫" }, { "fight", "戰鬥" }, { "find", "尋找" },
            { "fire", "火焰" }, { "fix", "修理" }, { "flood", "洪水" }, { "floor", "地板" },
            { "flower", "花朵" }, { "food", "食物" }, { "force", "力量" }, { "found", "發現" },
            { "fruit", "水果" }, { "fuel", "燃料" }, { "fur", "毛皮" }, { "gain", "獲得" },
            { "gained", "已獲得" }, { "gather", "收集" }, { "gold", "黃金" }, { "grain", "穀物" },
            { "guard", "守衛" }, { "guild", "公會" }, { "hammer", "錘子" }, { "heal", "治療" },
            { "healer", "治療者" }, { "health", "生命值" }, { "heart", "心臟" }, { "herb", "草藥" },
            { "honey", "蜂蜜" }, { "horn", "角" }, { "house", "房屋" }, { "hunt", "狩獵" },
            { "hunter", "獵人" }, { "increased", "已提升" }, { "iron", "鐵" }, { "journey", "旅程" },
            { "key", "鑰匙" }, { "kill", "擊殺" }, { "king", "國王" }, { "knight", "騎士" },
            { "lady", "女士" }, { "leaf", "葉子" }, { "learn", "學習" }, { "learned", "已習得" },
            { "leave", "離開" }, { "level", "等級" }, { "light", "光" }, { "lock", "鎖定" },
            { "lord", "領主" }, { "lost", "遺失" }, { "mace", "晨星錘" }, { "mage", "法師" },
            { "mana", "魔力" }, { "market", "市場" }, { "meal", "膳食" }, { "meat", "肉" },
            { "merchant", "商人" }, { "metal", "金屬" }, { "milk", "牛奶" }, { "mind", "精神" },
            { "monster", "怪物" }, { "mud", "泥土" }, { "number", "數量" }, { "oil", "油脂" },
            { "open", "開啟" }, { "penalty", "懲罰" }, { "plant", "植物" }, { "poison", "毒素" },
            { "potion", "藥水" }, { "power", "力量" }, { "priest", "祭司" }, { "quake", "震動" },
            { "queen", "女王" }, { "rain", "降雨" }, { "range", "範圍" }, { "receive", "接收" },
            { "received", "已接收" }, { "remove", "移除" }, { "repair", "修理" }, { "repairing", "修理中" },
            { "return", "返回" }, { "reward", "獎勵" }, { "rift", "裂隙" }, { "rock", "岩石" },
            { "rogue", "流氓" }, { "room", "房間" }, { "root", "根源" }, { "ruin", "遺跡" },
            { "salt", "鹽" }, { "sand", "沙" }, { "scale", "鱗片" }, { "score", "分數" },
            { "scout", "偵察" }, { "seed", "種子" }, { "select", "選擇" }, { "sell", "出售" },
            { "shadow", "影子" }, { "share", "分享" }, { "shield", "盾牌" }, { "shrine", "神龕" },
            { "silver", "銀" }, { "skin", "皮膚" }, { "sky", "天空" }, { "smith", "鐵匠" },
            { "snow", "雪" }, { "soldier", "士兵" }, { "spear", "長矛" }, { "speed", "速度" },
            { "spice", "香料" }, { "spy", "間諜" }, { "staff", "法杖" }, { "star", "星星" },
            { "start", "開始" }, { "stone", "石頭" }, { "stop", "停止" }, { "storm", "風暴" },
            { "success", "成功" }, { "sun", "太陽" }, { "sword", "劍" }, { "tail", "尾巴" },
            { "target", "目標" }, { "temple", "寺廟" }, { "thief", "盜賊" }, { "thunder", "雷電" },
            { "tide", "潮汐" }, { "tonic", "補劑" }, { "tool", "工具" }, { "total", "總計" },
            { "town", "城鎮" }, { "trade", "交易" }, { "travel", "旅行" }, { "tree", "樹木" },
            { "tribe", "部落" }, { "unequip", "卸下" }, { "upgrade", "升級" }, { "village", "村莊" },
            { "visit", "造訪" }, { "wait", "等待" }, { "wall", "牆壁" }, { "wand", "魔杖" },
            { "warrior", "戰士" }, { "wave", "波浪" }, { "weapon", "武器" }, { "wind", "風" },
            { "window", "視窗" }, { "wine", "葡萄酒" }, { "wing", "翅膀" }, { "witch", "女巫" },
            { "wizard", "巫師" }, { "wood", "木材" }, { "xp", "經驗值" },
            // 常見技能/能力名（Active Effects 顯示）
            { "cleave", "劈砍" }, { "charge", "衝鋒" }, { "dismember", "肢解" },
            { "slam", "重擊" }, { "berserk", "狂暴" }, { "hobble", "絆倒" },
            { "shank", "刺殺" }, { "juke", "閃避" }, { "shield wall", "盾牆" },
            { "swift block", "迅捷格擋" }, { "dual wield", "雙持" },
            { "flurry", "連擊" }, { "lunge", "突刺" },
            // 玩家視角/所有格補詞（Markov 書摘、動態訊息）
            { "you", "你" }, { "your", "你的" }, { "my", "我的" }, { "him", "他" },
            { "her", "她" }, { "them", "他們" },
        };

    // spice/歷史生成整句漏翻 → 執行期整句替換（優先於逐詞）
    private static readonly Dictionary<string, string> PhraseLeaks =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "rife with burnt books and corroded data disks", "充斥著燒毀的書籍與腐蝕的數據磁碟" },
            { "rife with stray portals to other places and times", "充斥著通往其他時空的散亂傳送門" },
            { "rife with smashed rubble", "佈滿了碎裂的瓦礫" },
            { "rife with bad omens", "充斥著不祥之兆" },
            { "rife with electric arcs", "滿是電弧" },
            { "data corruption", "資料損壞" },
            { "data disks", "資料磁碟" },
            { "The Museum Autarchy of Tarchewan", "塔徹萬博物館專制政體" },
            // 專名片語（與資料 mod Factions.zh-tw.xml / historyspice 一致；字典為 OrdinalIgnoreCase，大小寫變體其一即可）
            { "Tree of Life", "生命之樹" },
            { "Chavvah, the Tree of Life", "夏瓦(Chavvah)，生命之樹" },
            // scholarship 元素漏網性質詞（spice 陣列兜底，防 MergeModJson 未取代時殘留）
            { "philosophical", "哲學性的" }, { "shrewd", "精明的" }, { "inquisitive", "好奇的" },
            { "fraying reality-edges", "崩解的現實邊緣" },
            { "glass-swept knolls", "玻璃覆蓋的丘陵" },
            { "trash heaps", "垃圾堆" },
            { "voltaic lunes", "伏特彎月" },
            { "rotten luck", "霉運" },
            // 動詞+介詞複合（XDidYToZ 的 preposition 如 "down on"）
            { "down on", "到" }, { "up on", "到" }, { "down into", "入" },
            { "out of", "出" }, { "off of", "離開" }, { "on top of", "在頂上" },
            { "stand up", "站起" }, { "stands up", "站起" }, { "stood up", "站起" },
            { "sit down", "坐下" }, { "sits down", "坐下" }, { "sat down", "坐下" },
            { "lie down", "躺下" }, { "lies down", "躺下" },
            // 硬編碼失敗訊息（Bed.cs 等）
            { "You cannot do that while sitting.", "你無法在坐著時那樣做。" },
            { "You cannot do that while flying.", "你無法在飛行時那樣做。" },
            { "You cannot do that while submerged.", "你無法在潛水時那樣做。" },
            { "You cannot do that while swimming.", "你無法在游泳時那樣做。" },
            { "You cannot do that while burrowed.", "你無法在潛地時那樣做。" },
            { "You cannot do that while enclosed.", "你無法在受困時那樣做。" },
            // 完整靜態 Popup 句子（gemma 意譯）
            { "Choose a reward", "選擇獎勵" },
            { "Select a destination", "選擇目的地" },
            { "Pick end game state", "選擇遊戲結束狀態" },
            { "That is out of range!", "超出範圍！" },
            // BookUI/Active Effects（&W...&Y 視為空格分隔）
            { "Active Effects", "主動效果" },
            { "No active effects", "沒有主動效果" },
            // 2026-08-10：XDidYToZ/DidX 動作片語（frame_missing 補翻）
            { "make camp", "架起營地" },
            { "roll on the ground", "在地上翻滾" },
            { "beat at the flames", "撲打著火焰" },
            { "gain the skill", "獲得了技能" },
            { "gain the mutation", "獲得了突變" },
            { "gain all the skills", "獲得了所有技能" },
            { "resist becoming afraid", "抵抗了恐懼" },
            { "resist being confused", "抵抗了困惑" },
            { "resist blown back by", "抵抗被吹飛" },
            { "shimmer into existence", "閃爍著現形" },
            { "project a stasis field", "投射出靜止場" },
            { "invoke a concussive blast", "引發震擊爆風" },
            { "feel a concussive blast", "感受到震擊爆風" },
            { "spot a sewage eel", "發現了一條汙水鰻" },
            { "emit a flaming ray", "發出一道烈焰射線" },
            { "emit a freezing ray", "發出一道冰凍射線" },
            { "emit a powerful magnetic pulse", "發出強力磁脈衝" },
            { "shoot a swatch of frost webs", "射出一片霜網" },
            { "spew a cloud of spores", "噴出一團孢子雲" },
            { "dematerialize out of the local region of spacetime", "從此處的時空區域中解除物質化" },
            { "shear the fiber of spacetime and burrow to another place", "撕裂時空纖維並遁往他處" },
            { "attempt to burrow a channel through the psychic aether and sunder", "試圖穿鑿心靈乙太的通道並撕裂" },
            { "wander away disinterestedly", "漠不關心地走開了" },
            { "start dashing in a plume of flame and smoke", "在烈焰與濃煙中開始衝刺" },
            { "flush with understanding of", "豁然領悟了" },
            { "start streaming ribbons of", "開始流洩出" },
            { "assume an intimidating posture", "擺出威嚇姿態" },
            { "voice a short prayer beneath", "在下方低聲祈禱" },
            { "blow into the conch of the Aji", "吹響了 Aji 的海螺" },
            { "take a puff on", "抽了一口" },
            { "reel from the force of", "因力量而搖晃" },
        };

    private static readonly Regex PhraseRegex = BuildPhraseRegex();

    private static Regex BuildPhraseRegex()
    {
        var keys = new List<string>(PhraseLeaks.Keys);
        keys.Sort((a, b) => b.Length.CompareTo(a.Length));
        var parts = new List<string>();
        foreach (var k in keys)
            parts.Add("(?i)" + Regex.Escape(k));
        // 結尾不放 \b：部分短語以「.」「」」結尾，\b 加在非字元後會永不匹配
        return new Regex("(?i)(?:" + string.Join("|", parts) + ")", RegexOptions.Compiled);
    }

    private static string PhraseMatch(Match m)
    {
        string v;
        return PhraseLeaks.TryGetValue(m.Value, out v) ? v : m.Value;
    }

    private static readonly Regex WordsRegex = BuildWordsRegex();

    private static Regex BuildWordsRegex()
    {
        var keys = new List<string>(Words.Keys);
        keys.Sort((a, b) => b.Length.CompareTo(a.Length));
        var parts = new List<string>();
        foreach (var k in keys)
            parts.Add("(?i)" + Regex.Escape(k));
        return new Regex(@"\b(?:" + string.Join("|", parts) + @")\b", RegexOptions.Compiled);
    }

    private static string WordsMatch(Match m)
    {
        string v;
        return Words.TryGetValue(m.Value, out v) ? v : m.Value;
    }

    // 「to 動詞原形」→ 直接中文（去掉 to），如 "to glow" → "發光"
    private static readonly Regex ToVerb = new Regex(
        @"\bto\s+(?i)(glow|hover|float|fall|rise|turn|move|walk|grow|shrink|burn|freeze|melt|sparkle|run|leap|jump|crawl|swim|fly|howl|scream|breathe|sleep|eat|drink|gaze|stare)\b",
        RegexOptions.Compiled);

    private static string ToVerbMatch(Match m)
    {
        string v;
        return Words.TryGetValue(m.Groups[1].Value, out v) ? v : m.Groups[1].Value;
    }

    // 有界快取：原文 -> 處理後，避免重複正則
    private static readonly Dictionary<string, string> Cache =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private const int CacheMax = 30000;

    // ===== postfix 層級結果快取（input -> 最終 result）=====
    // 翻譯具確定性（同 input 必同 output），快取安全且高命中率：
    // 物件名稱 / 常見訊息 / conversation path ID 在遊戲中反覆出現，
    // 首次處理後即可 O(1) 命中，跳過全部昂貴管線（載入 43s / 卡頓元凶）。
    private static readonly Dictionary<string, string> PostfixCache =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private const int PostfixCacheMax = 20000;

    private static string PostfixCached(string input, Func<string, string> fn)
    {
        string cached;
        if (PostfixCache.TryGetValue(input, out cached)) return cached;
        string result = fn(input);
        if (PostfixCache.Count >= PostfixCacheMax) PostfixCache.Clear();
        PostfixCache[input] = result;
        return result;
    }

    private static readonly Dictionary<string, string> ProperNounZh =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Murapur", "穆拉普爾(Murapur)" }, { "Tarchewan", "塔徹萬(Tarchewan)" },
            { "Maazoppir", "馬佐皮爾(Maazoppir)" }, { "Reshep", "雷舍夫(Reshep)" },
            { "Resheph", "雷舍夫(Resheph)" }, { "Mamon", "馬蒙(Mamon)" },
            { "Sheba", "示巴(Sheba)" },
        };

    // 常見英文詞（避免把正常英文誤當專名音譯）
    private static readonly Dictionary<string, string> CommonWords =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "the", "" }, { "of", "" }, { "and", "" }, { "in", "" }, { "to", "" },
            { "a", "" }, { "with", "" }, { "for", "" }, { "on", "" }, { "at", "" },
            { "by", "" }, { "from", "" }, { "its", "" }, { "their", "" }, { "your", "" },
            { "you", "" }, { "sultan", "蘇丹" }, { "sultanate", "蘇丹國" },
            { "kingdom", "王國" }, { "empire", "帝國" }, { "republic", "共和國" },
            { "autarchy", "專制政體" }, { "dynasty", "王朝" }, { "museum", "博物館" },
            { "wreck", "殘骸" }, { "den", "巢穴" }, { "spire", "尖塔" }, { "grotto", "洞穴" },
            { "hollow", "谷地" }, { "ruins", "遺跡" }, { "stilt", "高蹺" }, { "gate", "門戶" },
            { "grit", "礫石" }, { "water", "水" }, { "vine", "藤" }, { "watervine", "水藤" },
            { "clifftop", "懸崖之頂" }, { "moon", "月" }, { "stair", "階梯" },
        };

    // 鍵盤/UI 鍵名：不當專名音譯（=commandKey: 佔位符注入的原始鍵名）
    private static readonly Dictionary<string, string> KeyNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "backspace", "" }, { "enter", "" }, { "return", "" }, { "space", "" },
            { "spacebar", "" }, { "tab", "" }, { "escape", "" }, { "esc", "" },
            { "ctrl", "" }, { "control", "" }, { "alt", "" }, { "shift", "" },
            { "capslock", "" }, { "delete", "" }, { "del", "" }, { "insert", "" },
            { "home", "" }, { "end", "" }, { "pageup", "" }, { "pagedown", "" },
            { "up", "" }, { "down", "" }, { "left", "" }, { "right", "" },
            { "f1", "" }, { "f2" , "" }, { "f3" , "" }, { "f4" , "" }, { "f5" , "" },
            { "f6" , "" }, { "f7" , "" }, { "f8" , "" }, { "f9" , "" }, { "f10", "" },
            { "f11", "" }, { "f12", "" },
            { "left click", "" }, { "right click", "" }, { "middle click", "" },
            { "mouse", "" }, { "mousewheel", "" }, { "scroll", "" },
            { "previous ability", "" }, { "next ability", "" },
            { "highlight", "" }, { "wait", "" }, { "accept", "" }, { "cancel", "" },
        };

    // 貪婪最長匹配拆解英文專名成音節，逐一查表
    // 只在「中英混雜」文本中、且該大寫專名前不是「(」（即非「中文(English)」括號註解）時才音譯
    // 排除富文本色碼（#HEX）與維度標籤後的 token
    private static readonly Regex NameToken = new Regex(@"(?<![\u4e00-\u9fff(#\uFF08])\b([A-Z][A-Za-z'-]{2,})\b(?![-0-9])", RegexOptions.Compiled);

    // 便宜預過濾：整句 frame 觸發關鍵詞。TranslateStatusFragments 有 37 個 ^ 錨定正則，
    // 若字串不含任何 frame 動詞（純英文 path ID / 一般文字），直接跳過，省下大量載入開銷。
    // 用語幹 + \w*（非 \b 結尾）：避免漏掉屈折形（engulfed/dragged/sucking/sitting 等），
    // \b 開頭防止誤配子詞（transit 不付 \bsit）。
    private static readonly Regex FrameTrigger = new Regex(
        @"(?i)\b(hit|miss|toggle|dazed|stand|take|eat|toss|gather|sit|climb|jump|wade|swim|emerge|bump|bond|detach|slip|swap|entangle|engulf|drag|suck|impal|lying|sitting|enclosed|pilot|knock|stop|move|look|turn|fall|rise)\w*",
        RegexOptions.Compiled);

    private static string TransliterateName(string word)
    {
        if (string.IsNullOrEmpty(word)) return null;
        string zh;
        // 只翻譯人工策展的高品質專名（中文(英文) 格式）
        if (ProperNounZh.TryGetValue(word, out zh)) return zh;
        // 常見已知詞：若 CommonWords 有對應中文（非空）→ 回傳；空值 → 保留英文，不音譯
        if (CommonWords.TryGetValue(word, out zh))
            return (zh.Length > 0) ? zh : null;
        if (KeyNames.ContainsKey(word)) return null;
        // 純十六進位色碼（如 FFFFFF、4A4A4A）→ 不音譯
        if (word.Length >= 3 && Regex.IsMatch(word, @"^[0-9A-Fa-f]{3,}$")) return null;
        // 其餘一律保留英文（不再做貪婪音節拆解；LLM 音節表品質不足，寧可保留原文）
        return null;
    }

    private static string NameMatch(Match m)
    {
        string w = m.Groups[1].Value;
        string zh = TransliterateName(w);
        if (zh != null) return zh;
        return m.Value;
    }

    // 已知中文音譯專名 → 期望格式「中文(英文)」（用於 Naming 音節已生成中文名的 sultan/人物）
    private static readonly Dictionary<string, string> CjkProperNoun =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "雷舍夫", "雷舍夫(Resheph)" }, { "雷斯嘿芙", "雷舍夫(Resheph)" },
            { "雷谢夫", "雷舍夫(Resheph)" }, { "穆拉普爾", "穆拉普爾(Murapur)" },
            { "塔徹萬", "塔徹萬(Tarchewan)" }, { "馬佐皮爾", "馬佐皮爾(Maazoppir)" },
        };

    private static readonly Regex CjkNameRefmt = new Regex(
        @"(?<![\u4e00-\u9fff(\uFF08])(雷舍夫|雷斯嘿芙|雷谢夫|穆拉普爾|塔徹萬|馬佐皮爾)(?![\u4e00-\u9fff(\uFF08\s]*[\u0028\uFF08])",
        RegexOptions.Compiled);

    private static string CleanNames(string text)
    {
        // 只在「中英混雜」時處理；純英文句與純中文句不做
        bool hasCjk = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= '\u4e00' && c <= '\u9fff') { hasCjk = true; break; }
        }
        if (!hasCjk) return text;
        string result = NameToken.Replace(text, NameMatch);
        // 已知中文音譯名 → 補上英文原文括號（避免 中文(中文)）
        return CjkNameRefmt.Replace(result, new MatchEvaluator(CjkNameMatch));
    }

    private static string CjkNameMatch(Match m)
    {
        string v;
        return CjkProperNoun.TryGetValue(m.Value, out v) ? v : m.Value;
    }

    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // 單趟掃描：判斷是否「中英混雜」（只有混雜才需清理）
        bool hasEng = false, hasCjk = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) hasEng = true;
            else if (c >= '\u4e00' && c <= '\u9fff') hasCjk = true;
            if (hasEng && hasCjk) break;
        }
        if (!hasEng || !hasCjk) return text;

        string cached;
        if (Cache.TryGetValue(text, out cached)) return cached;

        string result = LeadingArticle.Replace(text, "");
        // 逐詞替換常見殘留（整詞邊界、大小寫不敏感）
        foreach (var kv in Artifacts)
        {
            result = Regex.Replace(result, @"\b(?i)" + Regex.Escape(kv.Key) + @"\b", kv.Value);
        }
        // spice/歷史生成整句漏翻（執行期補翻）
        // 先處理整句，避免逐詞替換（Words/Verbs）先把句子拆碎而匹配不到完整句
        result = PhraseRegex.Replace(result, new MatchEvaluator(PhraseMatch));
        // 「to 動詞原形」→ 中文（先處理，避免 to 被單獨處理）
        result = ToVerb.Replace(result, new MatchEvaluator(ToVerbMatch));
        // 動詞翻譯（單次正則，多義動詞以動詞義優先）
        result = VerbRegex.Replace(result, new MatchEvaluator(VerbMatch));
        // 介詞/單位/動詞原形
        result = WordsRegex.Replace(result, new MatchEvaluator(WordsMatch));
        // 程序化專名音譯（STEP 4 防漏：漏網英文專名 → 繁中音譯）
        result = CleanNames(result);
        if (result != text)
        {
            if (Cache.Count >= CacheMax) Cache.Clear();
            Cache[text] = result;
        }
        return result;
    }

    // ===== 防漏層：關鍵詞/短語（dram/data disks/of 等）在 Clean 被繞過時仍生效 =====
    private static string TranslateKeyLeaks(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // 只處理「中英混雜」：純英文（internal ID/path）不該被 Words 中文污染，且省 200 alternation
        bool hasEng, hasCjk;
        ScanLang(text, out hasEng, out hasCjk);
        if (!hasEng || !hasCjk) return text;
        text = PhraseRegex.Replace(text, new MatchEvaluator(PhraseMatch));
        text = WordsRegex.Replace(text, new MatchEvaluator(WordsMatch));
        return text;
    }

    // TextBuilder.ToString() 後綴：清理組裝後的動態生成文本（每生成字串一次，非每幀）
    // 每個階段各自 try/catch：任何一階段失敗只記 log、不中斷其他階段，
    // 徹底避免「修 A 時 B 一起壞」（單一正則炸掉整條管線 → 回傳原始英文）。
    private static string TryStage(string text, Func<string, string> fn, string name)
    {
        if (string.IsNullOrEmpty(text)) return text;
        try
        {
            string r = fn(text);
            return r ?? text;
        }
        catch (Exception e)
        {
            ZhTwReplacers.LogAlways(name + " EX: " + e.GetType().Name + " " + e.Message);
            return text;
        }
    }

    // 單趟掃描：hasEng=含英文，hasCjk=含中文。用於快速路徑守衛，
    // 避免對「純中文/純英文」字串跑貴重正則（Conversations 載入 43s 的元凶）。
    private static void ScanLang(string text, out bool hasEng, out bool hasCjk)
    {
        hasEng = false; hasCjk = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) hasEng = true;
            else if (c >= '\u4e00' && c <= '\u9fff') hasCjk = true;
            if (hasEng && hasCjk) return;
        }
    }

    public static void ToStringPostfix(ref string __result)
    {
        if (string.IsNullOrEmpty(__result)) return;
        try
        {
            __result = PostfixCached(__result, ToStringProcess);
            LogLeaks("MSG", __result);
        }
        catch (Exception e)
        {
            ZhTwReplacers.LogAlways("ToStringPostfix EX: " + e.GetType().Name + " " + e.Message);
        }
    }

    // 快速路徑：純中文 → 已翻譯，直接回傳（最大宗，零成本）
    //            純英文 → 只跑英文訊息 frame + 方向句，跳過混雜才會用到的 Clean/KeyLeaks
    private static string ToStringProcess(string text)
    {
        bool hasEng, hasCjk;
        ScanLang(text, out hasEng, out hasCjk);
        if (!hasEng) return text;
        if (!hasCjk)
        {
            text = TryStage(text, TranslateStatusFragments, "StatusFragments");
            text = TryStage(text, TranslateDirection, "Direction");
            return text;
        }
        text = TryStage(text, TranslateStatusFragments, "StatusFragments");
        text = TryStage(text, TranslateDirection, "Direction");
        text = TryStage(text, Clean, "Clean");
        return text;
    }

    // 診斷：報告已知漏網詞仍殘留的訊息/名稱（寫 replacer_log.txt）
    private static void LogLeaks(string kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.IndexOf("dram", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("data disks", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("burnt books", StringComparison.OrdinalIgnoreCase) < 0)
            return;
        ZhTwReplacers.Log("LEAK[" + kind + "]: " + text);
    }

    // ===== 純英文方向句（LoreGenerator parasang 等，純英文所以 Clean 不處理）=====
    // 1. "near X" / "near X, N strata deep"
    private static readonly Regex DirNear = new Regex(
        @"^near (.+?)(, (\d+) strata deep)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // 2. "N parasang(s) east of X"           → X 東方 N 帕拉桑
    // 3. "N parasang(s) east/west"           → 東方 N 帕拉桑
    // 4. "N parasang(s) east and M north of X" → X 東方 N 帕拉桑、北方 M 帕拉桑
    // 5. "N parasang(s) east, M north of X"   (comma 版)
    // 以上皆可帶尾綴 ", N strata deep"。
    private static readonly string StrataSuffix = @"(?:, (\d+) strata deep)?";
    private static readonly Regex DirParasang1 = new Regex(
        @"^(\d+) parasang(?:s)? (east|west|north|south) of (.+?)" + StrataSuffix + @"$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DirParasang2 = new Regex(
        @"^(\d+) parasang(?:s)? (east|west|north|south)" + StrataSuffix + @"$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DirParasangCombo = new Regex(
        @"^(\d+) parasang(?:s)? (east|west), (\d+) parasang(?:s)? (north|south) of (.+?)" + StrataSuffix + @"$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DirParasangAnd = new Regex(
        @"^(\d+) parasang(?:s)? (east|west) and (\d+) parasang(?:s)? (north|south) of (.+?)" + StrataSuffix + @"$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> DirWord =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "east", "東方" }, { "west", "西方" }, { "north", "北方" }, { "south", "南方" },
        };

    private static string TranslateDirection(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 120) return text;
        // 只處理純英文方向句（無中文）
        bool hasCjk = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] >= '\u4e00' && text[i] <= '\u9fff') { hasCjk = true; break; }
        }
        if (hasCjk) return text;

        var m = DirNear.Match(text);
        if (m.Success)
        {
            string place = StripThe(m.Groups[1].Value);
            if (m.Groups[3].Success)
                return "靠近 " + place + "，" + m.Groups[3].Value + " 層深處";
            return "靠近 " + place;
        }
        m = DirParasangCombo.Match(text);
        if (m.Success)
            return StripThe(m.Groups[5].Value) + " " + DirWord[m.Groups[2].Value] + " " + m.Groups[1].Value
                + " 帕拉桑，" + DirWord[m.Groups[4].Value] + " " + m.Groups[3].Value + " 帕拉桑"
                + StrataSuffixText(m.Groups[6].Value);
        m = DirParasangAnd.Match(text);
        if (m.Success)
            return StripThe(m.Groups[5].Value) + " " + DirWord[m.Groups[2].Value] + " " + m.Groups[1].Value
                + " 帕拉桑，" + DirWord[m.Groups[4].Value] + " " + m.Groups[3].Value + " 帕拉桑"
                + StrataSuffixText(m.Groups[6].Value);
        m = DirParasang1.Match(text);
        if (m.Success)
            return StripThe(m.Groups[3].Value) + " " + DirWord[m.Groups[2].Value] + " " + m.Groups[1].Value
                + " 帕拉桑" + StrataSuffixText(m.Groups[4].Value);
        m = DirParasang2.Match(text);
        if (m.Success)
            return DirWord[m.Groups[2].Value] + " " + m.Groups[1].Value + " 帕拉桑"
                + StrataSuffixText(m.Groups[3].Value);
        return text;
    }

    private static string StrataSuffixText(string strata)
    {
        if (string.IsNullOrEmpty(strata)) return "";
        return "，" + strata + " 層深處";
    }

    private static string StripThe(string s)
    {
        if (s.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
            return s.Substring(4);
        return s;
    }

    public static void Init()
    {
        if (Initialized) return;
        Initialized = true;
        try
        {
            Harmony harmony = new Harmony("qud_zh_tw_replacers.textgen");
            var tostr = AccessTools.Method(typeof(XRL.World.Text.TextBuilder), "ToString",
                                           new Type[] { });
            if (tostr != null)
            {
                harmony.Patch(tostr, postfix: new HarmonyMethod(typeof(ZhTwTextCleaner), nameof(ToStringPostfix)));
                ZhTwReplacers.LogAlways("TextCleaner patched TextBuilder.ToString()");
            }
            else
            {
                ZhTwReplacers.LogAlways("TextCleaner WARN: TextBuilder.ToString() target NOT FOUND");
            }
            // 狀態標籤（Prone 等）附加在 GameObject.DisplayName，不經 TextBuilder.ToString()
            var gdn = AccessTools.Method(typeof(XRL.World.GetDisplayNameEvent), "GetFor");
            if (gdn != null)
            {
                harmony.Patch(gdn, postfix: new HarmonyMethod(typeof(ZhTwTextCleaner), nameof(DisplayNamePostfix)));
                ZhTwReplacers.LogAlways("TextCleaner patched GetDisplayNameEvent.GetFor()");
            }
            else
            {
                ZhTwReplacers.LogAlways("TextCleaner WARN: GetDisplayNameEvent.GetFor() target NOT FOUND");
            }
        }
        catch (Exception e)
        {
            ZhTwReplacers.LogAlways("TextCleaner Init error: " + e.GetType().Name + " " + e.Message + "\n" + e.StackTrace);
        }
    }

    // ===== 狀態效果名翻譯（DisplayName / GetDescription / 名稱 tag 的 {{X|english}}）=====
    // 效果名是 raw 字面值（不走 Strings._S），EffectsDetails 只本地化描述不本地化名稱
    private static readonly System.Text.RegularExpressions.Regex EffectNameToken =
        new System.Text.RegularExpressions.Regex(
            @"\{\{([^}|]+)\|([^{}]*?)\}\}",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // 靜態效果名對照（62 個，LLM 意譯 2026-08-09）
    private static readonly Dictionary<string, string> EffectZh =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "frenzied", "狂暴的" }, { "mobility impaired", "行動不便" },
            { "projecting consciousness", "投射意識" }, { "pulsed", "脈動的" }, { "stressed", "壓力沉重" },
            { "hobbled", "跛行" }, { "interdicted", "被禁止的" }, { "unpiloted", "無人駕駛的" },
            { "warming up", "熱身中" }, { "En garde!", "小心！" }, { "defensive stance", "防禦姿態" },
            { "ecstatic", "狂喜的" }, { "poisoned by gas", "被瓦斯毒害" }, { "exhaustion", "精疲力竭" },
            { "unpowered", "無動力" }, { "mutating", "正在變異" }, { "stasis", "停滯" },
            { "invulnerable", "無法傷害" }, { "shimmering", "閃爍的" }, { "FURIOUS", "憤怒" },
            { "aggressive stance", "侵略姿態" }, { "crippled", "殘廢" }, { "marked", "已標記" },
            { "cardiac arrest", "心臟驟停" }, { "covered in spores", "全身佈滿孢子" },
            { "crackling", "劈啪作響的" }, { "dashing", "衝刺" }, { "dueling stance", "決鬥架勢" },
            { "floppy", "軟綿綿的" }, { "queasy", "感到噁心" }, { "wakeful", "清醒的" },
            { "omniphase", "全相位(omniphase)" }, { "tomb-tethered", "與墳墓相連的" },
            { "deep dreaming", "深層夢境" }, { "camouflaged", "偽裝的" },
            { "coated in plasma", "塗滿了電漿" }, { "cybernetic rejection syndrome", "賽博格排斥症候群" },
            { "glitching", "故障中" }, { "freezing", "結冰中" }, { "frozen", "冰凍" },
            { "illness", "疾病" }, { "nullphased", "空相(nullphased)" },
            { "phase spider venom", "相位蜘蛛毒液" }, { "vitalized", "充滿活力的" },
            { "quantum-locked", "量子鎖定" }, { "psionically cleaved", "靈能裂解" },
            { "irisdual molting", "鳶尾雙生(irisdual)蛻皮" }, { "scintillating", "閃爍的" },
            { "bleeding", "流血中" }, { "cleaved", "劈開了" }, { "disoriented", "迷失方向" },
            { "flagging", "標記中" }, { "latched onto", "緊緊咬住" }, { "nosebleed", "鼻出血" },
            { "prowling", "潛行中" }, { "rusted", "生鏽的" }, { "shamed", "感到羞愧" },
            { "war trance", "戰鬥恍惚" }, { "urban camouflage", "都市迷彩" },
            { "grounded", "落地" }, { "hampered", "受阻" },
            // 已由 Words/其他處理的常見效果（補缺）
            { "wading", "涉水" }, { "prone", "俯臥" }, { "confused", "困惑" },
            { "dazed", "暈眩" }, { "terrified", "恐懼" }, { "overburdened", "負重過度" },
            { "paralyzed", "麻痺" }, { "stunned", "暈眩" }, { "submerged", "淹沒" },
            { "swimming", "游泳" }, { "sitting", "坐著" }, { "piloting", "駕駛中" },
        };

    public static void DisplayNamePostfix(ref string __result)
    {
        try
        {
            if (string.IsNullOrEmpty(__result)) return;
            // 熱路徑（GetFor）：postfix 快取 → 重複名稱 O(1) 命中；純中文名稱（資料 mod 已翻）直接回傳
            __result = PostfixCached(__result, DisplayNameProcess);
            LogLeaks("NAME", __result);
        }
        catch (Exception e)
        {
            ZhTwReplacers.LogAlways("DisplayNamePostfix EX: " + e.GetType().Name + " " + e.Message);
        }
    }

    private static string DisplayNameProcess(string text)
    {
        bool hasEng, hasCjk;
        ScanLang(text, out hasEng, out hasCjk);
        if (!hasEng) return text;
        // 每階段各自 try/catch，任一失敗不中斷其他階段
        text = TryStage(text, TranslateDisplayNameFragments, "NameFragments");
        text = TryStage(text, TranslateKeyLeaks, "KeyLeaks");
        text = TryStage(text, Clean, "Clean");
        return text;
    }

    // 狀態標籤模板（DisplayName / GetDescription 用，熱路徑輕量）
    private static string TranslateDisplayNameFragments(string text)
    {
        // 動態模板（內嵌 X 物件名）
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\{\{(B|C)\|lying on (.+?)\}\}", "{{$1|躺在 $2 上}}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\{\{(B|C)\|sitting on (.+?)\}\}", "{{$1|坐在 $2 上}}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\{\{(B|C)\|enclosed in (.+?)\}\}", "{{$1|被困在 $2 內}}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\{\{(B|C)\|engulfed by (.+?)\}\}", "{{$1|被 $2 吞噬}}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\{\{(B|C)\|piloting (.+?)\}\}", "{{$1|駕駛 $2}}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // 靜態效果名（{{X|english}} → {{X|中文}}）
        text = EffectNameToken.Replace(text, new System.Text.RegularExpressions.MatchEvaluator(EffectNameMatch));
        return text;
    }

    // 翻譯訊息整句/片段（message 路徑，含戰鬥/烹飪/XDidYToZ frame）
    private static string TranslateStatusFragments(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // 便宜預過濾：無 {{ 模板且無任何 frame 動詞 → 37 個正則不會命中，直接跳過
        if (text.IndexOf("{{", StringComparison.Ordinal) < 0 && !FrameTrigger.IsMatch(text))
            return text;
        text = TranslateDisplayNameFragments(text);
        // ===== 戰鬥/動作整句（在 Clean 逐詞破壞前翻譯，避免詞序壞掉）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+(?:critically\s+)?hit\s+\((.+?)\)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)!?\s*\[(.+?)\]$",
            "你用 $3 擊中($1)，造成 $2 傷害[$4]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+hit\s+(.+?)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)!?\s*\[(.+?)\]$",
            "你用 $3 擊中 $1，造成 $2 傷害[$4]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+(?:critically\s+)?hit\s+\((.+?)\)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)[.!]?$",
            "你用 $3 擊中($1)，造成 $2 傷害", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^(.+?)\s+hit\s+(.+?)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)\.?\s*\[(.+?)\]$",
            "$1 用 $4 擊中 $2，造成 $3 傷害[$5]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+miss(?:ed)?\s+with\s+(.+?)[.!]?\s*\[(.+?)\]$",
            "你未擊中 $1[$2]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+miss(?:ed)?\s+(.+?)[.!]?\s*\[(.+?)\]$",
            "你未擊中 $1[$2]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+toggle\s+(.+?)\s+(on|off)[.!]?$",
            "你將 $1 切換為$2", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = text.Replace("切換為on", "切換為開啟").Replace("切換為off", "切換為關閉");
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^(.+?)\s+is\s+dazed[.!]?$",
            "$1 感到暈眩。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^(.+?)\s+stands?\s+up[.!]?$",
            "$1 站起來了。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^(.+?)\s+takes?\s+(\d+)\s+damage\s+from\s+(.+?)[.!]?$",
            "$1 因 $3 受到 $2 傷害。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+take\s+(\d+)\s+damage\s+from\s+(.+?)[.!]?$",
            "你因 $2 受到 $1 傷害。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // ===== 烹飪/香料整句（spice 未載入時的兜底）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+eat\s+the\s+meal[.!]?$",
            "你吃下了這份餐點。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+toss\s+(.+?)\s+into\s+a\s+pot\s+and\s+stir[.!]?$",
            "你將 $1 丟進鍋子裡並攪拌。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+gather\s+(.+?)\s+for\s+your\s+meal[.:]?$",
            "你收集了 $1 來當作餐點。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+toss\s+them\s+in\s+a\s+pot\s+and\s+stir[.!]?$",
            "你將它們丟進鍋子裡攪拌。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // ===== XDidYToZ frame 整句（物件名可能尚未本地化，frame 必先翻）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+sit\s+down\s+on\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你坐到 $1 上。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+sit\s+down\s+in\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你坐到 $1 裡。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+climb\s+onto\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你爬上 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+jump\s+onto\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你跳到 $1 上。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+wade\s+through\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你涉水穿過 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+swim\s+through\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你游泳穿過 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+emerge\s+from\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你從 $1 現身。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+bump\s+into\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你撞到 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+bond\s+with\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你與 $1 締結聯繫。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+detach\s+from\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你從 $1 脫離。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+slip\s+away\s+from\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你從 $1 溜走。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+swap\s+positions\s+with\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你與 $1 交換位置。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+get\s+entangled\s+in\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你被 $1 纏住。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // 被動 frame（You are X by Y）
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+are\s+engulfed\s+by\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你被 $1 吞噬。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+are\s+dragged\s+toward\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你被拖向 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+are\s+sucked\s+into\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你被吸入 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^You\s+are\s+impaled\s+by\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你被 $1 刺穿。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // 動態模板（內嵌 X 物件名）
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\{\{(B|C)\|lying on (.+?)\}\}", "{{$1|躺在 $2 上}}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\{\{(B|C)\|sitting on (.+?)\}\}", "{{$1|坐在 $2 上}}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\{\{(B|C)\|enclosed in (.+?)\}\}", "{{$1|被困在 $2 內}}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\{\{(B|C)\|engulfed by (.+?)\}\}", "{{$1|被 $2 吞噬}}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\{\{(B|C)\|piloting (.+?)\}\}", "{{$1|駕駛 $2}}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // 靜態效果名（{{X|english}} → {{X|中文}}）
        text = EffectNameToken.Replace(text, new System.Text.RegularExpressions.MatchEvaluator(EffectNameMatch));
        return text;
    }

    private static string EffectNameMatch(System.Text.RegularExpressions.Match m)
    {
        string color = m.Groups[1].Value;
        string word = m.Groups[2].Value.Trim();
        string zh;
        if (EffectZh.TryGetValue(word, out zh))
            return "{{" + color + "|" + zh + "}}";
        return m.Value;
    }
}