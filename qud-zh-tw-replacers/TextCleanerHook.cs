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

    // ===== token 保護 =====
    // Clean/KeyLeaks 的逐詞替換（Words/Verbs/Artifacts）會誤傷 =spice:...= 等 token 內部的
    // 英文關鍵字（spice→香料、item→物品、of→的…），導致遊戲查變數失敗（
    // "No variable replacer by key '香料' found"）。處理前把 token 換成佔位符，之後還原。
    private static readonly Regex TokenGuard = new Regex(
        @"=[A-Za-z0-9_.:;|!@/()+\-#'%]+=", RegexOptions.Compiled);

    // {{X|…}} 色彩/模板標記整體保護（2026-08-13）：任何 regex（含整句 pattern 的
    // 前綴 consume）都不得吞咬 {{ 標記，避免「:: Freezing|冰凍}…」「R|…}}」式殘留。
    private static readonly Regex MarkupToken = new Regex(
        @"\{\{(?:[A-Za-z0-9_%]+|[^{}|]+)\|[^{}]*\}\}", RegexOptions.Compiled);

    private static string ProtectTokens(string text, List<string> box)
    {
        string r = TokenGuard.Replace(text, delegate(Match m)
        {
            box.Add(m.Value);
            return "\x00" + (box.Count - 1) + "\x00";
        });
        return MarkupToken.Replace(r, delegate(Match m)
        {
            box.Add(m.Value);
            return "\x00" + (box.Count - 1) + "\x00";
        });
    }

    private static string RestoreTokens(string text, List<string> box)
    {
        if (box.Count == 0) return text;
        return Regex.Replace(text, @"\x00(\d+)\x00", delegate(Match m)
        {
            int idx;
            if (int.TryParse(m.Groups[1].Value, out idx) && idx >= 0 && idx < box.Count)
                return box[idx];
            return m.Value;
        });
    }

    // ===== ProperNoun 括號英文保護 =====
    // 「中文(English)」的括號英文是規範格式（農民公會(Farmers' Guild)），
    // 逐詞替換（Words/ProperNounZh）若套用到括號內會污染成
    // 「農民公會(Farmers' 公會)」「瑪門之子(Children 的 馬蒙(Mamon))」。
    // 只在「括號前的非空白字元是中文」時保護（其餘 (…) 仍走逐詞替換）。
    private static readonly Regex ParenGuard = new Regex(
        @"\([^()]*?(?:[A-Za-z][^()]*?)*?\)|（[^（）]*?(?:[A-Za-z][^（）]*?)*?）", RegexOptions.Compiled);

    private static string ProtectParens(string text, List<string> box)
    {
        return ParenGuard.Replace(text, delegate(Match m)
        {
            string content = m.Value;
            int start = m.Index;
            // 括號前的非空白字元是否為中文（緊鄰即「中文(English)」規範）
            int p = start - 1;
            while (p >= 0 && (text[p] == ' ' || text[p] == '\t')) p--;
            if (p < 0 || text[p] < '\u4e00' || text[p] > '\u9fff') return content;
            box.Add(content);
            return "\x01" + (box.Count - 1) + "\x01";
        });
    }

    private static string RestoreParens(string text, List<string> box)
    {
        if (box.Count == 0) return text;
        return Regex.Replace(text, @"\x01(\d+)\x01", delegate(Match m)
        {
            int idx;
            if (int.TryParse(m.Groups[1].Value, out idx) && idx >= 0 && idx < box.Count)
                return box[idx];
            return m.Value;
        });
    }

    // ===== 所有格 's（後接中文時中文化：哈爾's 丈夫 → 哈爾的丈夫）=====
    // 括號英文（Farmers' Guild）因 ProtectParens 已保護，不會被此規則誤傷。
    private static readonly Regex PossessiveZh = new Regex(
        @"\b([A-Za-z]+)'s\s+(?=[\u4e00-\u9fff])", RegexOptions.Compiled);

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
            { "sultan", "蘇丹" }, { "sultanate", "蘇丹國" }, { "dynasty", "王朝" }, { "nephilim", "尼腓利姆" }, { "nephal", "尼腓爾" },
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
            { "stops", "停下" }, { "moving", "移動" }, { "looks", "查看" }, { "out", "出" },
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
            { "leave", "離開" }, { "level", "等級" }, { "light", "光" }, { "lock", "鎖定" }, { "torch", "火炬" },
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
            { "spy", "間諜" }, { "staff", "法杖" }, { "star", "星星" },
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
            // 漏網詞兜底（運行時殘英掃描回饋：critically/開始常見殘留）
            { "critically", "爆擊" }, { "begins", "開始" }, { "began", "開始" },
            { "bleed", "流血" }, { "bleeds", "流血" }, { "bleeding", "流血中" },
            { "rounds", "回合" }, { "round", "回合" },
            // combat weapon 段補詞（=subject.its.item#weapon= 未翻時逐詞兜底）
            { "bite", "咬" }, { "bites", "咬" }, { "claws", "爪" },
            { "paw", "爪掌" }, { "paws", "爪掌" }, { "hoof", "蹄" }, { "hooves", "蹄" },
            { "tusk", "獠牙" }, { "tusks", "獠牙" }, { "fangs", "獠牙" },
            { "beak", "喙" }, { "stinger", "尾刺" }, { "horns", "角" },
            { "fist", "拳頭" }, { "fists", "拳頭" }, { "scratch", "抓傷" }, { "scratches", "抓傷" },
            { "bronze", "青銅" }, { "steel", "鋼" }, { "dagger", "匕首" },
            // 2026-08-13 補：Faction Interest/Secret 系統漏網兜底（sultanTerm 複數、動詞片語等）
            { "sultans", "蘇丹" }, { "interested", "感興趣" }, { "resources", "資源" },
            { "necessary", "必需的" }, { "building", "建造" }, { "societies", "社會" },
            { "worship", "崇拜" }, { "worships", "崇拜" }, { "trading", "交易" },
            { "sharing", "分享" }, { "learning", "了解" }, { "breeding", "繁殖" },
            { "husband", "丈夫" }, { "gossip", "八卦" },
            // 2026-08-13 補：硬編碼 combat/UI 殘詞
            { "suppressive", "壓制" }, { "draw", "瞄準" }, { "bead", "準星" },
            { "marked", "已標記" }, { "targets", "目標" }, { "aim", "瞄準" },
            { "guardians", "守護者" }, { "proficient", "精通" },
        
            { "Page", "頁面" },
            { "Rename", "重新命名" },
            { "loaded", "已載入" },};

    // spice/歷史生成整句漏翻 → 執行期整句替換（優先於逐詞）
    private static readonly Dictionary<string, string> PhraseLeaks =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 電池/充能狀態標籤（顯示名後綴 (Full) 等，遊戲動態附加）
            { "(Full)", "(滿電)" }, { "(Empty)", "(空)" },
            { "(Partially charged)", "(部分充電)" },
            // History Worships/Despises faction 名 fragment（=faction.FormattedName= 槽位）
            { "%Worships.LegendaryCreature.SacredThing", "崇拜傳說生物的聖物" },
            { "%Worships.LegendaryCreature", "傳說生物的崇拜者" },
            { "%Despises.LegendaryCreature.ProfaneThing", "鄙視傳說生物的褻瀆物" },
            { "%Despises.LegendaryCreature", "傳說生物的鄙視者" },
            { "rife with burnt books and corroded data disks", "充斥著燒毀的書籍與腐蝕的數據磁碟" },
            { "rife with stray portals to other places and times", "充斥著通往其他時空的散亂傳送門" },
            { "rife with smashed rubble", "佈滿了碎裂的瓦礫" },
            { "rife with bad omens", "充斥著不祥之兆" },
            { "rife with electric arcs", "滿是電弧" },
            { "data corruption", "資料損壞" },
            { "data disks", "資料磁碟" },
            // 殘英掃描回饋短語（combat 句未命中整句 pattern 時的兜底）
            { "with all of your", "用你的全部" }, { "all of your", "你的全部" },
            { "begins bleeding", "開始流血" }, { "begin bleeding", "開始流血" },
            { "starts bleeding", "開始流血" }, { "start bleeding", "開始流血" },
            { "You begin bleeding", "你開始流血了" },
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
            { "You cannot do that while burrowed.", "你無法在潛地時那樣做。" },
            // 完整靜態 Popup 句子（gemma 意譯）
            { "Choose a reward", "選擇獎勵" },
            { "Select a destination", "選擇目的地" },
            { "Pick end game state", "選擇遊戲結束狀態" },
            { "That is out of range!", "超出範圍！" },
            // BookUI/Active Effects（&W...&Y 視為空格分隔）
            { "Active Effects", "主動效果" },
            { "No active effects", "沒有主動效果" },
            // 2026-08-10：XDidYToZ/DidX 動作片語（frame_missing 補翻）
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
        
            // 2026-08-13 補：全技能/技能樹/統計名（需求串等 Clean 語境整詞組匹配）
            { "Swift Reflexes", "迅捷反射" },
            { "Spry", "靈活" },
            { "Jump", "跳躍" },
            { "Tumble", "翻滾" },
            { "Axe Proficiency", "斧頭精通" },
            { "Cleave", "劈砍" },
            { "Charging Strike", "衝鋒打擊" },
            { "Dismember", "肢解" },
            { "Hook and Drag", "勾引與拖曳" },
            { "Decapitate", "斬首" },
            { "Berserk!", "狂暴！" },
            { "Steady Hands", "穩定的手" },
            { "Draw a Bead", "繪製珠飾" },
            { "Suppressive Fire", "壓制射擊" },
            { "Flattening Fire", "壓平火焰" },
            { "Wounding Fire", "傷口灼燒" },
            { "Disorienting Fire", "令人迷失方向的火焰" },
            { "Sure Fire", "萬無一失" },
            { "Beacon Fire", "信標之火" },
            { "Ultra Fire", "超火" },
            { "Meal Preparation", "膳食準備" },
            { "Harvestry", "收穫術" },
            { "Butchery", "屠殺" },
            { "Spicer", "香料商" },
            { "Carbide Chef", "碳化物廚師(Carbide Chef)" },
            { "Cudgel Proficiency", "棍棒熟練度" },
            { "Bludgeon", "鈍器打擊" },
            { "Conk", "撞擊" },
            { "Backswing", "後擺" },
            { "Slam", "猛擊" },
            { "Demolish", "拆除" },
            { "Tactful", "圓滑" },
            { "Trash Divining", "垃圾占卜" },
            { "Opportune Attacks", "時機恰當的攻擊" },
            { "Weapon Expertise", "武器專精" },
            { "Penetrating Strikes", "穿透打擊" },
            { "Weapon Mastery", "武器精通" },
            { "Flurry", "狂亂" },
            { "Multiweapon Proficiency", "多武器熟練度" },
            { "Multiweapon Expertise", "多武器專精" },
            { "Multiweapon Mastery", "多武器精通" },
            { "Shake It Off", "擺脫負面狀態" },
            { "Swimming", "游泳" },
            { "Poison Tolerance", "毒素耐性" },
            { "Weathered", "飽經風霜" },
            { "Juicer", "榨汁者" },
            { "Calloused", "繭化" },
            { "Longstrider", "長步者" },
            { "Staunch Wounds", "頑強傷口" },
            { "Nostrums", "藥劑" },
            { "Amputate Limb", "截肢" },
            { "Apothecary", "藥劑師" },
            { "Strapping Shoulders", "束帶肩甲" },
            { "Tank", "坦克" },
            { "Sweep", "橫掃" },
            { "Long Blade Proficiency", "長刃精通" },
            { "Lunge", "突刺" },
            { "Swipe", "揮擊" },
            { "Dueling Stance", "決鬥架勢" },
            { "Improved Aggressive Stance", "改良型侵略姿態" },
            { "Improved Defensive Stance", "改良防禦姿態" },
            { "Improved Dueling Stance", "改良型決鬥架勢" },
            { "En Garde!", "小心！" },
            { "Menacing Stare", "威脅凝視" },
            { "Intimidate", "威嚇" },
            { "Berate", "斥責" },
            { "Snake Oiler", "蛇油商" },
            { "Proselytize", "勸誘信徒" },
            { "Inspiring Presence", "鼓舞人心的存在感" },
            { "Steady Hand", "穩定的手" },
            { "Akimbo", "雙持" },
            { "Weak Spotter", "弱點偵測者" },
            { "Sling and Run", "投石與奔跑" },
            { "Disarming Shot", "卸力射擊" },
            { "Dead Shot", "神射手" },
            { "Empty the Clips", "清空彈匣" },
            { "Fastest Gun in the Rust", "鏽蝕之地最快槍手" },
            { "Meditate", "冥想" },
            { "Fasting Way", "禁食之道" },
            { "Iron Mind", "鋼鐵意志" },
            { "Lionheart", "獅心" },
            { "Mind over Body", "心勝於身" },
            { "Block", "格擋" },
            { "Shield Slam", "盾牌 撞擊" },
            { "Deft Blocking", "靈巧格擋" },
            { "Swift Blocking", "迅捷格擋" },
            { "Staggering Block", "踉蹌格擋" },
            { "Shield Wall", "盾牌 牆壁" },
            { "Short Blade Expertise", "短刃專精" },
            { "Bloodletter", "血刃" },
            { "Jab", "刺擊" },
            { "Hobble", "跛行" },
            { "Pointed Circle", "尖銳圓環" },
            { "Rejoinder", "反擊" },
            { "Shank", "刺殺" },
            { "Hurdle", "跨越" },
            { "Deft Throwing", "靈巧投擲" },
            { "Charge", "衝鋒" },
            { "Kickback", "反作用力" },
            { "Juke", "閃躲" },
            { "Gadget Inspector", "裝置檢查員" },
            { "Disassemble", "拆解" },
            { "Reverse Engineer", "逆向工程師" },
            { "Scavenger", "拾荒者" },
            { "Repair", "修理" },
            { "Deploy Turret", "部署砲塔" },
            { "Lay Mine / Set Bomb", "佈雷 / 設置炸彈" },
            { "Tinker I", "修補匠 I" },
            { "Tinker II", "修補匠 II" },
            { "Tinker III", "修補匠 III" },
            { "Mind's Compass", "心靈指南針" },
            { "Wilderness Lore: Flower Fields", "荒野知識：花田" },
            { "Wilderness Lore: Marshes", "荒野知識：沼澤" },
            { "Wilderness Lore: Hills and Mountains", "荒野知識：丘陵 與 山脈" },
            { "Wilderness Lore: Canyons", "荒野知識：峽谷" },
            { "Wilderness Lore: Salt Dunes", "荒野知識：鹽丘群" },
            { "Wilderness Lore: Jungles", "荒野知識：叢林" },
            { "Wilderness Lore: Rivers and Lakes", "荒野知識：河流與湖泊" },
            { "Wilderness Lore: Ruins", "荒野知識：遺跡" },
            { "Tomorrowful", "明日之光" },
            { "Acrobatics", "特技" },
            { "Axe", "斧頭" },
            { "Bow and Rifle", "弓與步槍" },
            { "Cooking and Gathering", "烹飪與採集" },
            { "Cudgel", "槌棒" },
            { "Customs and Folklore", "習俗與民俗" },
            { "Single Weapon Fighting", "單手武器戰鬥" },
            { "Multiweapon Fighting", "多武器戰鬥" },
            { "Endurance", "耐力" },
            { "Physic", "醫學" },
            { "Heavy Weapon", "重型武器" },
            { "Long Blade", "長刃" },
            { "Persuasion", "說服" },
            { "Pistol", "手槍" },
            { "Self-discipline", "自律" },
            { "Shield", "盾牌" },
            { "Short Blade", "短劍" },
            { "Tactics", "戰術" },
            { "Tinkering", "修補" },
            { "Wayfaring", "旅者" },
            { "Nonlinearity", "非線性" },
            { "Strength", "力量" },
            { "Agility", "敏捷" },
            { "Toughness", "韌性" },
            { "Willpower", "意志力" },
            { "Intelligence", "智力" },
            { "Ego", "心智" },
            { "Speed", "速度" },
            { "MoveSpeed", "移動速度" },
            { "Quickness", "敏捷度" },
            { "Level", "等級" },
            { "Third", "第三" },

            { "Make Camp", "紮營" },
            { "Conatus", "內驅力" },

            { "Ability bar, skill cooldown update", "能力列，技能冷卻更新" },
            { "Character Sheet", "角色狀態欄" },
            { "Navigate Down", "向下移動" },
            { "Navigate Horizontal", "水平移動" },
            { "Navigate Left", "向左移動" },
            { "Navigate Right", "向右移動" },
            { "Navigate Up", "向上移動" },
            { "Navigate Vertical", "垂直移動" },
            { "New Game", "新遊戲" },
            { "New Map Pin", "新增地圖標記" },
            { "Next Ability", "下一個能力" },
            { "Open File", "開啟檔案" },
            { "Open File Async", "非同步開啟檔案" },
            { "Open File Directory", "開啟檔案目錄" },
            { "Open File Extension", "開啟檔案副檔名" },
            { "Open File Multiple", "開啟多個檔案" },
            { "Page Down", "向下翻頁" },
            { "Page Left", "向左翻頁" },
            { "Page Right", "向右翻頁" },
            { "Page Up", "向上翻頁" },
            { "Previous Ability", "上一個能力" },
            { "SaveManagementRow - Line 1", "存檔管理列 — 第 1 行" },
            { "SaveManagementRow - Line 2", "存檔管理列 — 第 2 行" },
            { "SaveManagementRow - Line 3", "儲存管理列 - 第 3 行" },
            { "SaveManagementRow - Line 4", "儲存管理列 - 第 4 行" },
            { "Select Workshop Image", "選擇工作坊圖像" },
            { "Selected item was updated", "已更新所選項目" },
            { "Take A Step", "踏出一步" },
            { "Toggle Message Log", "切換訊息日誌" },
            { "Use Ability", "使用能力" },
            { "Ability Bar effects line", "能力列效果行" },
            { "Ability Bar multi page", "能力列多頁面" },
            { "Ability Bar no target message", "能力列無目標訊息" },
            { "Ability Bar single page", "能力列單頁面" },
            { "Ability Bar target descriptors", "能力列目標描述符" },
            { "Ability Bar target details", "能力列目標詳情" },
            { "Ability Clone", "複製能力 (Clone Ability)" },
            { "Ability Page Down", "能力 下一頁" },
            { "Ability Page Up", "能力頁向上翻頁" },
            { "Ability Use", "使用能力" },
            { "AbilityBar AbilityName", "能力列 {0} 能力名稱" },
            { "AbilityBar ToggleState tag off", "能力列切換狀態標籤關閉" },
            { "AbilityBar ToggleState tag on", "能力列切換狀態：開啟" },
            { "AbilityBar cooldownrounds tag", "能力列冷卻回合標籤" },
            { "AbilityBar disabled tag", "能力列已停用" },
            { "AbilityBar hotkeydescription", "能力列快捷鍵說明" },
            { "AbilityManagerScreen Cooldown Remaining Turns", "能力管理器介面 冷卻剩餘回合數" },
            { "AbilityManagerScreen HandleRebind AlreadyAbilityPicker", "能力管理器介面處理重新綁定 已在能力選擇器中" },
            { "AbilityManagerScreen HandleRebind AlreadySystemMenu", "能力管理器介面處理重新綁定已在系統選單中" },
            { "AbilityManagerScreen HandleRebind PressTheKeyToBind", "能力管理器介面處理重新綁定按下用於綁定的按鍵{0}" },
            { "AbilityManagerScreen HandleRebind PressTheKeyToBind v2", "能力管理器介面 按下重新綁定按鍵 {0}" },
            { "AbilityManagerScreen HandleRemoveBindAsync AreYouSure", "能力管理器介面處理移除非同步綁定：你確定嗎？" },
            { "AbilityManagerScreen MenuOption ActivateSelectedAbility", "啟動所選能力 (ActivateSelectedAbility)" },
            { "AbilityManagerScreen MenuOption Search", "能力管理介面 選單選項 搜尋" },
            { "AbilityManagerScreen MenuOption Search w/ Text", "能力管理介面 選單選項 文字搜尋" },
            { "AbilityManagerScreen NoAbilitiesMatchSeach", "找不到符合的技能" },
            { "AbilityManagerScreen SearchText Prompt", "能力管理器介面搜尋文字提示" },
            { "AbilityManagerScreen Type: ClassDisplayName", "能力管理介面 類型：{0}顯示名稱" },
            { "AbilityManagerScreen choose sort mode", "能力管理介面選擇排序模式" },
            { "AbilityManagerScreen no activated abilities", "能力管理介面：無已啟用的能力" },
            { "AbilityManagerScreen sort mode setting", "能力管理介面排序模式設定" },
            { "AbilityManagerScreen sort name By Class", "依職業排序能力管理器畫面" },
            { "AbilityManagerScreen sort name Custom", "排序名稱：自訂" },
            { "Add =", "新增 =" },
            { "Add One", "增加一個" },
            { "Add a new build code", "新增新的建構代碼" },
            { "Add how many =subject.name= to trade.", "新增多少個 {0}=subject.name= 到交易中。" },
            { "Add int property", "新增整數屬性" },
            { "Add pulldowns", "新增下拉選單" },
            { "Add string property", "新增字串屬性" },
            { "Add to favorite recipes", "加入收藏食譜" },
            { "Add to favorites", "加入收藏" },
            { "Add to trade", "加入交易" },
            { "CancelRangedAttacks RulePostfix ComputeDecrease", "取消遠程攻擊規則後綴計算減少量" },
            { "CancelRangedAttacks RulePostfix ComputeIncrease", "取消遠程攻擊規則後綴計算增加" },
            { "CancelRangedAttacks RulesPostfix NotPowerSensitive", "取消遠程攻擊規則後綴非威力敏感" },
            { "CancelRangedAttacks RulesPostfix PowerSensitive", "取消遠程攻擊規則後綴力量敏感度" },
            { "Character Status Screen Tab Name (Long)", "角色狀態畫面" },
            { "Character Status Screen Tab Name (Short)", "角色狀態" },
            { "CharacterAttributeLine modifiertext", "角色屬性行修正文本" },
            { "CharacterMutationLine (level)", "角色突變線（等級）" },
            { "CharacterMutationLine mutation no level", "角色突變：線性突變（無等級）" },
            { "CharacterMutationLine mutation with level", "角色突變：隨等級提升的突變線" },
            { "CharacterStatusScreen Attributes Long", "角色狀態畫面 屬性 長度" },
            { "CharacterStatusScreen Attributes Short", "角色狀態畫面屬性簡稱" },
            { "CharacterStatusScreen Buy Mutation description", "購買突變 (Buy Mutation)" },
            { "CharacterStatusScreen MutationDetails", "角色狀態畫面 突變詳情" },
            { "CharacterStatusScreen MutationDetailsShort", "角色狀態畫面變異詳情簡短版" },
            { "CharacterStatusScreen MutationRankText", "角色狀態畫面 突變 等級 文本" },
            { "CharacterStatusScreen MutationType Mental", "角色狀態畫面 突變類型 精神" },
            { "CharacterStatusScreen MutationType Mental Defect", "角色狀態畫面 突變類型 精神缺陷" },
            { "CharacterStatusScreen MutationType Morphotype", "角色狀態畫面 突變類型 形態類型" },
            { "CharacterStatusScreen MutationType Physical", "角色狀態畫面 突變類型 物理" },
            { "CharacterStatusScreen MutationType Physical Defect", "角色狀態畫面 突變類型 身體缺陷" },
            { "CharacterStatusScreen Show Effects description", "{0} 的效果：{1}" },
            { "CharacterStatusScreen Statistic HelpText BaseValue", "角色狀態畫面 統計 說明文字 基礎值" },
            { "CharacterStatusScreen attribute points", "角色狀態畫面屬性點數" },
            { "CharacterStatusScreen classtext line", "職業：{0}" },
            { "CharacterStatusScreen highlight cp", "角色狀態畫面高亮 CP" },
            { "CharacterStatusScreen mutation points long", "突變點 (Mutation Points)" },
            { "CharacterStatusScreen mutation points short", "突變點" },
            { "CharacterStatusScreen stats line level hp xp weight", "等級：{0}　生命值：{0}　經驗值：{0}　負重：{0}" },
            { "Close Book", "關閉書籍" },
            { "Close Menu", "關閉選單" },
            { "Close the Loop", "閉合迴路" },
            { "ConfirmMoveInto LiquidPool", "確認進入液體池(LiquidPool)" },
            { "ConfirmMoveInto LiquidPool Up or Down", "確認進入液體池向上或向下" },
            { "Delete Build", "刪除配置" },
            { "Delete Folder?", "確定要刪除資料夾嗎？" },
            { "Delete Save", "刪除存檔" },
            { "Delete file?", "確定要刪除檔案嗎？" },
            { "Disable the Spindle's Magnetic Field", "停用紡錘(Spindle)的磁場" },
            { "Edit custom ability: =abilityname=", "編輯自定義能力：=abilityname=" },
            { "Edit custom ability: =abilityname= (=originalname=)", "編輯自訂能力：=abilityname= (=originalname=)" },
            { "Enabled mods:", "已啟用的模組：" },
            { "Equip Item", "裝備物品" },
            { "Equip the Flume-Flier of the Sky-Bear.", "裝備天熊(Sky-Bear)的流體飛行者(Flume-Flier)。" },
            { "Equip the battle axe since it's better than your dagger.", "裝備戰斧，因為它比你的匕首更好用。" },
            { "Equip the battle axe.", "裝備戰斧。" },
            { "Equip the dagger too.", "也裝備匕首。" },
            { "Equipment", "裝備" },
            { "Equipment Rack", "裝備架" },
            { "Equipment View: =modes.textSelector#selected=", "裝備檢視：=modes.textSelector#selected=" },
            { "EquipmentLine primary hand glyph", "裝備主手符文" },
            { "Equipped with", "裝備著" },
            { "Equipped: =equipItems.join:, =", "已裝備：=equipItems.join:, =" },
            { "Examiner CriticalFail", "審查者大失敗 (Examiner CriticalFail)" },
            { "Examiner CriticalFail puzzled", "審查者 (Examiner) 關鍵失敗 (CriticalFail) 感到困惑 (puzzled)" },
            { "Examiner Failure", "審查者失敗 (Examiner Failure)" },
            { "Examiner FakeConfusionFailure", "審查者 偽裝混亂失敗 (Examiner FakeConfusionFailure)" },
            { "Examiner GetIdentifyMessage", "審查者取得識別訊息 (Examiner GetIdentifyMessage)" },
            { "Examiner Identify Message", "審查者辨識訊息" },
            { "Examiner Identify Message - Snapshot Matched", "審查者鑑定訊息 — 快照匹配成功" },
            { "Examiner Identify Snapshot Name", "審查者辨識快照名稱" },
            { "Examiner Partial Success snapshotDoesSeem", "審查者部分成功快照 DoesSeem" },
            { "Examiner Result Partial Snapshot Match Variant", "審查員結果部分快照匹配變體" },
            { "Examiner Result Partial Snapshot Mismatch Variant", "審查員結果部分快照不匹配變體 (Examiner Result Partial Snapshot Mismatch Variant)" },
            { "Examiner ResultExceptionalSuccess", "審查結果 卓越 成功" },
            { "Examiner ResultPartialSuccess Snapshot Match", "審查者結果部分成功快照比對" },
            { "Examiner ResultPartialSuccess Snapshot Mismatch", "檢查員結果：部分成功，快照不匹配" },
            { "Examiner ResultSuccess", "審查結果：成功" },
            { "Examiner already broken", "審查者(Examiner)已經損壞" },
            { "Examiner damage contained risk warning", "審查者(Examiner)傷害包含風險警告" },
            { "Examiner damage contents risk warning", "審查者(Examiner)傷害內容風險警告" },
            { "Examiner damage risk warning", "審查者(Examiner)傷害風險警告" },
            { "Inspect the bear.", "檢查熊。" },
            { "Inspect the snapjaw.", "檢查快顎(Snapjaw)。" },
            { "Load :", "載入：" },
            { "Load File Test", "載入檔案測試" },
            { "Load Map", "載入地圖" },
            { "Load game object...", "正在載入遊戲物件..." },
            { "Load globals...", "正在載入全域變數..." },
            { "Load keeping current mod configuration", "載入目前的模組設定" },
            { "Load keymap2.json", "載入按鍵配置 keymap2.json" },
            { "Load map...", "載入地圖中..." },
            { "Load player...", "正在載入玩家..." },
            { "Load systems...", "正在載入系統..." },
            { "Load your last quick save?", "載入最後一次快速存檔？" },
            { "Load zone manager...", "正在載入區域管理器..." },
            { "Loaded translator provider from", "已從以下來源載入翻譯提供者" },
            { "New File Name:", "新檔案名稱：" },
            { "New Folder", "新資料夾" },
            { "New Folder Name:", "新資料夾名稱：" },
            { "New historic relic:", "新的歷史遺物：" },
            { "New map", "新地圖" },
            { "New owner:", "新持有者：" },
            { "New part:", "Please provide the text you would like me to translate. I am ready to begin once you input the English strings." },
            { "Next Day", "下一日" },
            { "Next Page", "下一頁" },
            { "Next command string seems to be very far down the list: ${Base} ${count}", "下一個指令字串似乎位於列表非常靠下的位置：${Base} ${count}" },
            { "NextWindChange: +", "下次風向變化：+" },
            { "OPEN ARK", "開啟方舟 (OPEN ARK)" },
            { "Open File Filter", "開啟檔案篩選器" },
            { "Open Folder", "開啟資料夾" },
            { "Open Folder Async", "非同步開啟資料夾" },
            { "Open Folder Directory", "開啟資料夾目錄" },
            { "Open Save Folder", "開啟存檔資料夾" },
            { "Open Your Mind", "敞開心智" },
            { "Previous Day", "前一日" },
            { "Previous Page", "上一頁" },
            { "Quit Without Saving", "不儲存並退出" },
            { "Quit Without Saving No Checkpoint", "不儲存並退出（無存檔點）" },
            { "Quit Without Saving With Checkpoint", "不儲存並使用檢查點退出" },
            { "Quit to main menu", "返回主選單" },
            { "Recover", "恢復" },
            { "Recover =relic|strip=", "回收 =relic|剝離=" },
            { "Recover =relic|strip= at =location=.", "在 =location= 取得 =relic|strip=。" },
            { "Recover the Mark of Death", "找回死亡印記 (Mark of Death)" },
            { "Recovers 0.6 hit points per level (minimum 3) each turn.", "每回合恢復 0.6 點生命值（最低 3 點），每級增加。" },
            { "Recovers 0.9 hit points per level (minimum 5) each turn.", "每回合恢復 0.9 點生命值（最低 5 點），每級增加 0.9 點。" },
            { "Remove Cell v2", "移除細胞 v2 (Remove Cell v2)" },
            { "Remove Keybind", "移除按鍵綁定" },
            { "Remove One", "移除一個" },
            { "Remove custom ability", "移除自定義能力" },
            { "Remove from favorite recipes", "從收藏食譜中移除" },
            { "Remove from favorites", "從收藏中移除" },
            { "Remove property", "移除屬性" },
            { "Removed custom inventory ability: =abilityname=", "已移除自定義物品欄能力：=abilityname=" },
            { "Removed everyone but the player from the action queue.", "從動作隊列中移除了除玩家以外的所有人。" },
            { "Removed part", "已移除部分" },
            { "Rename Build", "重新命名配置" },
            { "Rename custom ability", "重新命名自定義能力" },
            { "Rename recipe", "重新命名配方" },
            { "Rename your companion", "重新命名你的夥伴" },
            { "Rename yourself", "重新命名自己" },
            { "Repair CriticalFailure", "修復嚴重失敗 (CriticalFailure)" },
            { "Repair CriticalFailure Broke", "修復嚴重失敗(CriticalFailure)損壞(Broke)" },
            { "Repair CriticalFailure Destroy", "修理　嚴重失敗　摧毀" },
            { "Repair ExceptionalSuccess", "修復卓越成功 (ExceptionalSuccess)" },
            { "Repair Failure", "維修失敗" },
            { "Repair IncompatibleFlyingState", "修復不相容的飛行狀態 (IncompatibleFlyingState)" },
            { "Repair NotNearHostiles", "修復非敵對目標 (NotNearHostiles)" },
            { "Repair OutofPhase", "修復 OutofPhase(OutofPhase)" },
            { "Repair Success", "修理成功" },
            { "Repair TargetNotAllowedToBeRepaired", "無法修理目標" },
            { "Repair TooConfused", "修復 TooConfused" },
            { "Repair the Waydroid", "修理 Waydroid (Waydroid)" },
            { "Reset Selection", "重置選擇" },
            { "Reset is not supported on this enumerator.", "此列舉器不支援重設。" },
            { "Resetting", "正在重置" },
            { "Restore Checkpoint", "還原檢查點" },
            { "Restore Defaults", "恢復預設值" },
            { "RestoreOnDeath OtherRestored", "死亡時恢復其他已恢復項目" },
            { "RestoreOnDeath PlayerRestored", "死後恢復 PlayerRestored" },
            { "RestoreOnDeath, AppendEffect", "死亡時恢復 (RestoreOnDeath)，附加效果 (AppendEffect)" },
            { "RestoreOnDeath, AppendEffect v2", "死亡時恢復 (RestoreOnDeath)，附加效果 v2 (AppendEffect v2)" },
            { "Restored backup has invalid objects, removing.", "已還原的備份包含無效物件，正在移除。" },
            { "Save :", "儲存：" },
            { "Save As", "另存新檔" },
            { "Save As...", "另存新檔..." },
            { "Save Build To Library", "將配置儲存至收藏庫" },
            { "Save File", "存檔檔案" },
            { "Save File Async", "非同步存檔檔案" },
            { "Save File Default Name", "存檔預設名稱" },
            { "Save File Default Name Ext", "存檔預設名稱副檔名" },
            { "Save File Directory", "存檔目錄" },
            { "Save File Filter", "存檔檔案篩選器" },
            { "Save File Test", "存檔測試" },
            { "Save Map", "儲存地圖" },
            { "Save Map As", "另存地圖為" },
            { "Save Tombstone File", "儲存墓碑檔案" },
            { "Save and Quit", "儲存並退出" },
            { "Save file is less than two bytes.", "存檔檔案小於兩個位元組。" },
            { "Save file is missing gzip header.", "存檔檔案缺少 gzip 標頭。" },
            { "Save file is the incorrect version.", "存檔版本不正確。" },
            { "Save path:", "儲存路徑：" },
            { "SaveBonus No Effect", "存檔加成無效" },
            { "SaveManagement Screen Title", "存檔管理" },
            { "SaveManagment - Delete Game Popup Message", "你確定要刪除存檔嗎？此操作無法復原。" },
            { "SaveManagment - Delete Game Popup Title", "刪除遊戲存檔" },
            { "SaveManagment - Deleted Game Popup", "儲存管理 — 已刪除遊戲彈出視窗" },
            { "SaveManagment - Incompatible Version Update (older version)", "存檔管理 — 版本不相容更新（舊版本）" },
            { "SaveModifierVs attribute obsolete, should use <savemodifiers> instead.", "SaveModifierVs 屬性已過時，應改用 <savemodifiers>。" },
            { "Search Input - Placeholder text", "搜尋輸入框 — 提示文字" },
            { "Search Input - Popup Title", "搜尋輸入 — 彈出視窗標題" },
            { "Search Mode: =modes.textSelector#selected=", "搜尋模式：=modes.textSelector#selected=" },
            { "Search text:", "Please provide the English text you would like me to translate." },
            { "Search: =searchtext.color:w=", "搜尋：=searchtext.color:w=" },
            { "Select All", "全選" },
            { "Select Base Gender", "選擇基礎性別" },
            { "Select Base Set", "選擇基礎組合" },
            { "Select Character Option", "選擇角色選項" },
            { "Select Controller", "選擇控制器" },
            { "Select Fire Mode", "選擇火焰模式" },
            { "Select Folder", "選擇資料夾" },
            { "Select Language", "選擇語言" },
            { "Select Move Style", "選擇移動風格" },
            { "Select Starting Location", "選擇起始地點" },
            { "Select Wait Style", "選擇等待風格" },
            { "Select an ingredient to use.", "選擇要使用的材料。" },
            { "Select an item to charge.", "選擇一個物品進行充能。" },
            { "Select how many?", "選擇數量？" },
            { "Select primary limb", "選擇主要肢體" },
            { "Select your maker's mark.", "選擇你的造物者印記。" },
            { "Selected Bind Set", "已選取的綁定組合" },
            { "Selected Cell: none", "選取的儲存格：無" },
            { "Selected active screen", "已選擇的活動畫面" },
            { "Sort Mode: =modes.textSelector#selected=", "排序模式：=modes.textSelector#selected=" },
            { "Sort Options", "排序選項" },
            { "Sort by", "排序方式：" },
            { "Sort: =modes.textSelector#selected=", "排序：=modes.textSelector#selected=" },
            { "Start Running", "開始奔跑" },
            { "Start a new game with one button.", "一鍵開啟新遊戲。" },
            { "Startup time:", "啟動時間：" },
            { "Stop Burrowing", "停止挖掘" },
            { "Stop Running", "停止奔跑" },
            { "Take a step toward the snapjaw.", "向快咬者(Snapjaw)邁出一步。" },
            { "Toggle All", "全部切換" },
            { "Toggle Cybernetics", "切換義體化" },
            { "Toggle Favorite", "切換收藏" },
            { "Toggle NorthSheva Overlay", "切換 NorthSheva (NorthSheva) 覆蓋層" },
            { "Toggle Option", "切換選項" },
            { "Toggle Sort", "切換排序" },
            { "Toggle Visibility", "切換顯示狀態" },
            { "UnequipPartAndChildren drop", "卸下裝備部位及其子項目掉落" },
            { "UnequipPartAndChildren unequip", "卸下該部位及其子裝備" },
            { "Use =commandKey:CmdMoveD= to descend.", "使用 =commandKey:CmdMoveD= 下樓。" },
            { "Use =commandKey:CmdMoveU= to ascend.", "使用 =commandKey:CmdMoveU= 來上升。" },
            { "Use =commandKey:LookDirection= to look at the bear.", "使用 =commandKey:LookDirection= 來注視熊。" },
            { "Use =commandKey:LookDirection= to look at the snapjaw.", "使用 =commandKey:LookDirection= 來注視快咬獸(Snapjaw)。" },
            { "Use Bare Indicative:", "使用裸露指示(Bare Indicative)：" },
            { "Use the campfire.", "使用營火。" },
            { "View final messages", "查看最終訊息" },
            { "ability", "能力" },
            { "cancelled:", "已取消：" },
            { "open air", "露天 (Open Air)" },
            { "remove cell:", "移除儲存格：" },
            { "remove cell: =cell.long.name=", "移除儲存格：=cell.long.name=" },
            { "removebodyparttype tag had no Name attribute", "removebodyparttype 標籤缺少 Name 屬性" },
            { "removebodyparttypevariant tag had no Name attribute", "removebodyparttypevariant 標籤缺少 Name 屬性" },
            { "removegender tag had no Name attribute", "removegender 標籤缺少 Name 屬性" },
            { "resetting field", "重置領域" },
            { "restored backup", "已還原備份" },
            { "save upgrade backup", "儲存、升級、備份" },
            { "search:", "搜尋：" },
            { "select an action", "選擇動作" },
            { "selected", "已選擇" },
            { "selectionPrefab was null", "selectionPrefab 為空值 (null)" },
            { "started:", "已開始：" },
            { "use psychometry on =object.the.name:single=", "對 =object.the.name:single= 使用靈媒感應(Psychometry)" },
            // ===== 動態層批 1a｜dll 完整句 2026-08-13（P2.1）=====
            { "Aaaaaaaaargh!", "啊啊啊啊啊啊啊啊！" },
            { "Accede to Resheph's plan for purging the world of higher life.", "同意雷舍夫(Resheph)淨化世上高階生命的計畫。" },
            { "Access denied. Continue on your path through the Thick World.", "存取被拒。繼續你穿越厚界之路。" },
            { "Access diodes flash in the affirmative.", "存取二極體閃爍出肯定的訊號。" },
            { "Achievement unlocked!", "成就解鎖！" },
            { "Acting against the prohibition on the practice of <spice.elements.", "違反了施作 <spice.elements. 的禁令。" },
            { "Activate a time cube.", "啟動一個時間方塊。" },
            { "ActivatedAbility.", "已啟動的能力。" },
            { "Added cooking knowledge.", "已獲得烹飪知識。" },
            { "Additionally, you sometimes scare creatures passively.", "此外，你會不時被動嚇跑生物。" },
            { "Advance a mutation to level 15.", "將一項突變提升至 15 級。" },
            { "Ahh, refreshing!", "啊，真是清爽！" },
            { "Alarms blare across the enclave.", "警報聲響徹整個聚居地。" },
            { "Annul the plagues of the Gyre.", "消除環流(Gyre)的瘟疫。" },
            { "Apparently, the Hindriarch isolated herself to work on a mystery project, possibly to hide Kindrish.", "顯然，欣德里亞克(Hindriarch)為了進行一項神秘計畫而自我隔離，可能是為了隱藏 Kindrish。" },
            { "Applying Harmony patches...", "正在套用 Harmony 修補……" },
            { "Are you sapient?", "你有知性嗎？" },
            { "Are you sure you want to RESET ALL YOUR ACHIEVEMENTS?", "你確定要重置所有成就嗎？" },
            { "Are you sure you want to delete this entry?", "你確定要刪除這個條目嗎？" },
            { "Are you sure you want to discard your changes?", "你確定要放棄變更嗎？" },
            { "Are you sure you want to go back and lose any unsaved changes?", "你確定要返回並失去未儲存的變更嗎？" },
            { "Are you sure you want to go to the world map?", "你確定要前往世界地圖嗎？" },
            { "Are you sure you want to proceed?", "你確定要繼續嗎？" },
            { "Are you sure you want to quit?", "你確定要退出嗎？" },
            { "Are you sure you want to restore your checkpoint?", "你確定要恢復檢查點嗎？" },
            { "Are you sure you want to save and quit?", "你確定要儲存並退出嗎？" },
            { "Ascend the Spindle.", "登上紡錘(Spindle)。" },
            { "Asynchronous exception has occurred.", "發生非同步例外。" },
            { "At a remote <spice.professions.", "在偏遠的 <spice.professions. " },
            { "Attack in what direction?", "向哪個方向攻擊？" },
            { "Attack where?", "攻擊哪裡？" },
            { "Attacked me.", "攻擊了我。" },
            { "Attain 100 psychic glimmer.", "獲得 100 點心靈微光。" },
            { "Attain 20 psychic glimmer as a true kin.", "以真族身分獲得 20 點心靈微光。" },
            { "Attain 200 psychic glimmer.", "獲得 200 點心靈微光。" },
            { "Attain 40 psychic glimmer and discover the vast psychic aether.", "獲得 40 點心靈微光並發現浩瀚的心靈以太。" },
            { "AutoAct Wait Resting until morning...", "自動行動：等待休息至清晨……" },
            { "Baetyl.", "拜提爾(Baetyl)。" },
            { "Barathrum is not in the golem. Ascend anyway?", "巴拉特魯姆(Barathrum)不在石魔像中。仍然要登上嗎？" },
            { "Basic user access grant. What do you wish from the Thin World?", "授予基本使用者權限。你希望從薄界得到什麼？" },
            { "Be crushed under the weight of a thousand suns.", "被千陽之重壓碎。" },
            { "Be gifted a 10-pointed asterisk.", "獲得一枚十角星飾。" },
            { "Be killed by a companion on accident.", "意外被同伴殺死。" },
            { "Be stoned to death by a baboon on the surface of Red Rock.", "在紅岩表面被狒狒以石塊砸死。" },
            { "Be swallowed whole.", "被整隻吞下。" },
            { "Be transmuted into a gemstone.", "被轉化為寶石。" },
            { "Be turned to stone.", "變成石頭。" },
            { "Beat the game.", "通關遊戲。" },
            { "Become auroral.", "化為極光。" },
            { "Become hated by the villagers of Joppa.", "被約帕(Joppa)的村民所憎恨。" },
            { "Become loved by a faction.", "獲得一個派系的喜愛。" },
            { "Become loved by newly sentient beings.", "獲得新生知性存在的喜愛。" },
            { "Before you move on, you should equip yourself from the nearby chest.", "在繼續前進之前，你應從附近的箱子武裝自己。" },
            { "Beginning Pathfider Benchmark...", "正在開始 Pathfider 基準測試……" },
            { "Beguiled me.", "魅惑了我。" },
            { "Berate whom?", "責罵誰？" },
            { "Bestow life to 20 inanimate objects.", "賦予 20 個無生命之物生命。" },
            { "Blocking because the overflow action is Block...", "因溢出行動為格擋而阻擋……" },
            { "Blocks all incoming melee attacks.", "擋下所有來襲的近戰攻擊。" },
            { "Bonus reward for completing this quest by level &C=level=&y.", "在 &C=level=&y 級之前完成此任務的額外獎勵。" },
            { "Brightness burns your mouth, but you cannot be roused any higher.", "光芒灼燒你的嘴，但你無法被喚醒到更高的境界。" },
            { "Brightness burns your mouth.", "光芒灼燒你的嘴。" },
            { "Bump attack the bear.", "撞擊攻擊那隻熊。" },
            { "Can I uninstall implants?", "我能卸除植入物嗎？" },
            { "Can carry more.", "可以攜帶更多。" },
            { "Can see things at normal distances.", "能以正常的距離看清事物。" },
            { "Can walk over webs without getting stuck.", "能走過蛛網而不被黏住。" },
            { "Can't be grabbed.", "無法被抓取。" },
            { "Can't fast travel.", "無法快速旅行。" },
            { "Can't move or stand up.", "無法移動或起身。" },
            { "Can't move.", "無法移動。" },
            { "Can't physically interact with creatures and objects.", "無法與生物和物體進行物理互動。" },
            { "Can't take actions.", "無法採取行動。" },
            { "Can't take physical actions.", "無法採取物理行動。" },
            { "Cannot be put in sleep mode.", "無法進入休眠模式。" },
            { "Cannot fall asleep.", "無法入睡。" },
            { "Cannot fly.", "無法飛行。" },
            { "Cast down your artifacts! You are not worthy of their make!", "放下你的神器！你配不上它們的工藝！" },
            { "Charmed by another creature into following them.", "被另一隻生物魅惑，跟隨其行動。" },
            { "Choose a color for your maker's mark.", "為你的製作者印記選擇顏色。" },
            { "Choose a model faction for your holographic glamour.", "為你的全息偽裝選擇典範派系。" },
            { "Choose a piece of furniture or other viable object to animate.", "選擇一件家具或其他可行的物體加以活化。" },
            { "Choose a random name from your own culture.", "從你自己的文化中選擇一個隨機名字。" },
            { "Choose an item to slot a cell into.", "選擇要裝入電池匣的物品。" },
            { "Choose books to give.", "選擇要送出的書籍。" },
            { "Clone Ctesiphus.", "複製泰西封(Ctesiphus)。" },
            { "Companion attacked me.", "同伴攻擊了我。" },
            { "Complete the quest What's Eating the Watervine?", "完成任務「什麼在吃水藤？」" },
            { "Complete the trade.", "完成交易。" },
            { "Compliment Q Girl.", "稱讚 Q 女孩。" },
            { "Compute power on the local lattice increases this item's range.", "本地晶格上的運算力會增加此物品的射程。" },
            { "Compute power on the local lattice reduces this item's cooldown.", "本地晶格上的運算力會縮短此物品的冷卻時間。" },
            { "Conspiring with its eponymous mushroom scientist, you spread Klanq throughout Qud.", "與其同名蘑菇科學家密謀，你將克蘭克(Klanq)傳播到整個 Qud。" },
            { "Consume 10 drams of sunslag in a single playthrough.", "於單次遊玩中消耗 10 德蘭(drams)的太陽渣。" },
            { "Contract glotrot.", "染上舌腐症。" },
            { "Contract ironshank.", "染上鐵腳病。" },
            { "Contract monochrome.", "染上單色症。" },
            { "Convince a frog to teach you how to jump.", "說服一隻青蛙教你跳躍。" },
            { "Convince the High Priest of the Stilt to join your party.", "說服高蹺的大祭司加入你的隊伍。" },
            { "Cook a meal with an extradimensional limb as an ingredient.", "以異次元肢體為食材烹飪一頓。" },
            { "Coruscating with the dim light of a trillion shattered suns.", "閃爍著萬億破碎太陽的幽光。" },
            { "Couldn't find a site!", "找不到地點！" },
            { "Create a new map?", "建立新地圖？" },
            { "Creatures are pushed away from center of blast.", "生物會被從爆炸中心推開。" },
            { "Cross into Brightsheol.", "跨越進入布萊特希歐(Brightsheol)。" },
            { "Crossing into Brightsheol will retire your character. Are you sure you want to do it? Type 'CROSS' to confirm.", "跨越進入布萊特希歐(Brightsheol)將使你的角色退休。你確定要這樣做嗎？輸入「CROSS」以確認。" },
            { "Cure glotrot via traditional methods.", "以傳統方法治癒舌腐症。" },
            { "Cure ironshank via traditional methods.", "以傳統方法治癒鐵腳病。" },
            { "Cure monochrome via traditional methods.", "以傳統方法治癒單色症。" },
            { "Currently occupying a tile safe from the tolling teleportation of the Bell of Rest.", "正佔據一個能避開安息之鐘催鳴傳送的格子。" },
            { "Dashes to the nearest obstacle when moving in a direction.", "朝某個方向移動時會衝向最近的障礙物。" },
            { "Decreased temperature.", "溫度下降。" },
            { "Defeat all seven of the Girsh nephilim in a single playthrough.", "於單次遊玩中擊敗全部七位吉什(Girsh)尼菲林(nephilim)。" },
            { "Defeat five of the Girsh nephilim in a single playthrough.", "於單次遊玩中擊敗五位吉什(Girsh)尼菲林(nephilim)。" },
            { "Defeat four of the Girsh nephilim in a single playthrough.", "於單次遊玩中擊敗四位吉什(Girsh)尼菲林(nephilim)。" },
            { "Defeat one of the Girsh nephilim.", "擊敗一位吉什(Girsh)尼菲林(nephilim)。" },
            { "Defeat six of the Girsh nephilim in a single playthrough.", "於單次遊玩中擊敗六位吉什(Girsh)尼菲林(nephilim)。" },
            { "Defeat three of the Girsh nephilim in a single playthrough.", "於單次遊玩中擊敗三位吉什(Girsh)尼菲林(nephilim)。" },
            { "Defeat two of the Girsh nephilim in a single playthrough.", "於單次遊玩中擊敗兩位吉什(Girsh)尼菲林(nephilim)。" },
            { "Defend Grit Gate from the Putus Templar.", "抵禦普圖斯聖殿騎士團，守衛礫門(Grit Gate)。" },
            { "Deliver a message to the mushroom progidy.", "將訊息送達蘑菇神童。" },
            { "Deliver me a bit of knowledge from the Thin World.", "從薄界給我捎來一點知識。" },
            { "Descend 50 strata deep.", "向下深入 50 層地層。" },
            { "Did something to my mind.", "對我的心智做了什麼。" },
            { "Die of thirst.", "渴死。" },
            { "Dies when any attribute reaches zero.", "任一屬性歸零時死亡。" },
            { "Disappoint a highly entropic being.", "讓一個高熵存在感到失望。" },
            { "Discover 100 lairs.", "發現 100 處巢穴。" },
            { "Discover psychic glimmer.", "發現心靈微光。" },
            { "Displacing corpse mode!", "屍體位移模式開啟！" },
            { "Do you want to download it?", "你想要下載它嗎？" },
            { "Do you want to examine these errors in the Player.log?", "你想要在 Player.log 中檢視這些錯誤嗎？" },
            { "Do you want to save first?", "你要先儲存嗎？" },
            { "Do you want to wait until the ascent is done?", "你想要等到上升完成為止嗎？" },
            { "Doesn't know how to rejoin leader.", "不知道如何重新與隊長會合。" },
            { "Doesn't regenerate hit points.", "不會回復生命值。" },
            { "Done.", "完成。" },
            { "Drink 20 drams of brain brine.", "喝下 20 德蘭(drams)的腦鹵。" },
            { "Drink lava.", "喝下熔岩。" },
            { "Easier to hit with bows and rifles.", "使用弓與步槍時更容易命中。" },
            { "Eat a serving of humble pie.", "吃一份謙卑派。" },
            { "Eat one of your own limbs.", "吃掉自己的一隻肢體。" },
            { "Eat the Cloaca Surprise.", "吃下「泄殖腔驚喜」。" },
            { "Eat the Crystal Delight.", "吃下「水晶美味」。" },
            { "Effects that make non-biological clones of you produce twice as many.", "使你的非生物複製品產生加倍的效果。" },
            { "Electrical damage has a percentage chance to cause recovery equal to the damage taken.", "電擊傷害有一定百分比機率使你回復與所受傷害相等的量。" },
            { "Electromagnetically unstable.", "電磁不穩定。" },
            { "Encode the psionic bits of someone's psyche onto the holographic boundary of your own psyche.", "將某人心靈的靈能位元編碼到你自身心靈的全息邊界上。" },
            { "End game?", "結束遊戲？" },
            { "Enter 100 giant clams.", "進入 100 隻巨蛤。" },
            { "Enter 25 spacetime vortices.", "進入 25 個時空渦流。" },
            { "Enter Mak's clam in the Yd Freehold.", "進入 Yd 自由邦中麥克(Mak)的巨蛤。" },
            { "Enter a name for this location.", "為此地點輸入名稱。" },
            { "Enter a name.", "輸入名稱。" },
            { "Enter a waking dream from within a waking dream.", "從清醒的夢中進入另一個清醒的夢。" },
            { "Enter into a covenant with Resheph and help prepare Qud for the Coven's return.", "與雷舍夫(Resheph)締結聖約，並協助為 Qud 準備迎接女巫集會(Coven)的回歸。" },
            { "Enter text to filter inventory by item name.", "輸入文字以按物品名稱篩選物品欄。" },
            { "Entering inflicts damage.", "進入會造成傷害。" },
            { "Entering may inflict damage.", "進入可能造成傷害。" },
            { "Entering may not succeed.", "進入可能不會成功。" },
            { "Exit.", "退出。" },
            { "Exiting inflicts damage.", "離開會造成傷害。" },
            { "Exiting may inflict damage.", "離開可能造成傷害。" },
            { "Fall to your death.", "墜落身亡。" },
            { "Finally, a staircase. Walk over to it.", "終於有樓梯了。走過去看看。" },
            { "Find one of Argyve's old apprentices.", "找到阿吉夫(Argyve)的一位老學徒。" },
            { "Find the oddly-hued glowpad.", "找到色彩奇異的螢光菌墊。" },
            { "Fires pistols twice as quickly.", "手槍射速加倍。" },
            { "Fleeing in panic.", "恐慌逃竄中。" },
            { "Foresee your own death.", "預見自己的死亡。" },
            { "Form large creature.", "組成大型生物。" },
            { "Friendly or neutral in the way, I'll try a different weapon...", "友方或中立者擋路，我會改用其他武器……" },
            { "Gain a mutation from a gamma moth's mutating gaze.", "從伽瑪蛾的突變凝視中獲得一項突變。" },
            { "Get decapitated.", "被斬首。" },
            { "Get killed by a chute crab.", "被滑道蟹殺死。" },
            { "Get killed by your evil twin.", "被你的邪惡雙胞胎殺死。" },
            { "Get the books.", "取得書籍。" },
            { "Get vaporized.", "被汽化。" },
            { "Getting ready to act.", "準備行動中。" },
            { "Give the repulsive device to all four children of the Tomb.", "將厭惡裝置交給陵墓的四位子嗣。" },
            { "Glimmering mind.", "心靈微光閃爍。" },
            { "Go on. Do it.", "動手吧。照做。" },
            { "Go to which point of interest?", "前往哪個地標？" },
            { "Grants you =skillName=.", "賦予你 =skillName=。" },
            { "Ground material splinters and opens onto a void.", "地面材料碎裂，敞開通向虛空。" },
            { "Have 10 mutations.", "擁有 10 項突變。" },
            { "Have a cybernetic implant installed on every implantable body part.", "在每個可植入的肢體部位安裝植入物。" },
            { "Have six arms.", "擁有六隻手臂。" },
            { "Have your head explode.", "讓你的頭爆炸。" },
            { "Healing effects are only half as effective.", "治療效果只有一半效力。" },
            { "Heh heh heh!", "嘿嘿嘿！" },
            { "Heh, heh!", "嘿，嘿！" },
            { "Help Argyve build the contraption.", "幫助阿吉夫(Argyve)建造那台裝置。" },
            { "Help Chavvah complete the ritual of -elseing and sever Tau at Taproot.", "幫助查瓦(Chavvah)完成「-elseing」儀式，並在源根(Taproot)切斷陶(Tau)的連結。" },
            { "Help the slynth find a new home.", "幫助斯林斯(slynth)尋找新家。" },
            { "Here is a summary of your attributes and mutations, which grant your character unique abilities.", "這是你的屬性與突變摘要，它們賦予你的角色獨特的能力。" },
            { "Hey you do damage!", "嘿，你真的造成傷害！" },
            { "Hillfolk dug wells deep into the earth and drew spring water to honor the sultan =name= in sacred ritual.", "山地居民深掘大地開鑿水井，汲取泉水，以神聖儀式尊崇蘇丹 =name=。" },
            { "Ho, ho!", "吼，吼！" },
            { "Hold on, we want to show you the tooltip compare feature.", "等等，我們要向你展示提示框的比較功能。" },
            { "Horns jut out of your head.", "角從你的頭上突出。" },
            { "Host three different fungal infections at once.", "同時感染三種不同的真菌感染。" },
            { "How long would you like to sleep?", "你想睡多久？" },
            { "How many implants can I install?", "我可以安裝多少植入物？" },
            { "I am Ereshkigal, your liaison with the Thin World. Ask of me.", "我是艾蕾什基伽勒(Ereshkigal)，你與薄界之間的聯絡人。儘管問我。" },
            { "I am being piloted.", "我正在被人操控。" },
            { "I can no longer clone my target!", "我再也無法複製我的目標了！" },
            { "I can't figure out where my target might be.", "我想不出我的目標可能在哪裡。" },
            { "I can't find a creature to flock with.", "我找不到可以結群的生物。" },
            { "I can't find a path to my target!", "我找不到通往目標的路徑！" },
            { "I can't find a path.", "我找不到路徑。" },
            { "I can't find any trash.", "我找不到任何垃圾。" },
            { "I can't find anyplace to flee!", "我找不到任何可以逃離的地方！" },
            { "I can't find my target...", "我找不到我的目標……" },
            { "I can't get to my target!", "我無法到達我的目標！" },
            { "I can't move there!", "我無法移動到那裡！" },
            { "I can't move!", "我無法移動！" },
            { "I can't see my target any more!", "我再也看不到我的目標了！" },
            { "I discovered a footprint from a great saltback on the paths, a sign of recent trade.", "我在小徑上發現了一隻巨大鹽背獸的足跡，這是近期貿易的跡象。" },
            { "I don't have a location!", "我沒有位置！" },
            { "I don't have a target any more!", "我再也沒有目標了！" },
            { "I don't have a target anymore!", "我再也沒有目標了！" },
            { "I don't have a target!", "我沒有目標！" },
            { "I don't have a turret type to place!", "我沒有要放置的砲塔類型！" },
            { "I found a few scraps from some kind of leatherworking project, a sign of recent craft.", "我找到了一些皮革加工計畫的碎料，這是近期工藝的跡象。" },
            { "I found a small leatherworking hammer on the paths, a sign of recent craft.", "我在小徑上發現了一把小皮革錘，這是近期工藝的跡象。" },
            { "I found a step to take toward my target, I'm going to try it.", "我找到了朝目標邁進的一步，我要試試看。" },
            { "I found a weapon haft, broken from use, on the paths, a sign of recent violence.", "我在小徑上發現一截因使用而折斷的武器握柄，這是近期暴力的跡象。" },
            { "I found the discarded husks of the curative yuckwheat plant, a sign of recent illness.", "我發現了被丟棄的解毒穢草莢殼，這是近期疾病的跡象。" },
            { "I looked for a target but didn't find one.", "我找了目標，卻一個也沒找到。" },
            { "I looked for the player to target but didn't find them.", "我尋找可鎖定的玩家，卻找不到。" },
            { "I lost my target.", "我失去了目標。" },
            { "I no longer have a location!", "我不再有位置了！" },
            { "I should get some distance from my target.", "我應該和目標拉開一些距離。" },
            { "I shouldn't kill my party leader.", "我不該殺死我的小隊隊長。" },
            { "I shouldn't kill other members of the player's party.", "我不該殺死玩家小隊的其他成員。" },
            { "Kill a pentasludge.", "擊殺一隻五泥怪。" },
            { "Kill a trisludge.", "擊殺一隻三泥怪。" },
            { "Launch spaceship and end game?", "發射太空船並結束遊戲？" },
            { "Leader directed me to attack.", "隊長指示我發動攻擊。" },
            { "Leap, Frog.", "跳吧，青蛙。" },
            { "Learn a secret from the microbiome.", "從微生物群中學到一個祕密。" },
            { "Learn the complete history of a single sultan.", "了解某位蘇丹的完整歷史。" },
            { "Leave the solar system on a starship.", "乘星艦離開太陽系。" },
            { "Let's equip it.", "我們來裝備它吧。" },
            { "Let's explore this cave we've found ourselves in.", "我們來探索這座我們身處的洞窟吧。" },
            { "Let's not be rude. Talk to the beetle.", "別失禮。跟甲蟲說話吧。" },
            { "Loading...", "載入中……" },
            { "Looks like there was some sort of battle to the north. Let's grab the gear that was left behind.", "看起來北方發生過某種戰鬥。我們把留下的裝備撿走吧。" },
            { "Lose your will to live.", "失去求生的意志。" },
            { "Lunge and Swipe have no cooldown.", "突刺與揮砍沒有冷卻時間。" },
            { "Make an offering at the Argent Well! Pay homage to your Fathers!", "在白銀之井獻祭吧！向你的父輩致敬！" },
            { "Make an offering at the Sacred Well of an artifact worth at least 200 reputation.", "在聖井獻上價值至少 200 聲望的神器。" },
            { "Map loaded!", "地圖已載入！" },
            { "Map saved!", "地圖已儲存！" },
            { "May inflict periodic damage to occupant.", "可能對佔據者造成週期性傷害。" },
            { "Mobility impaired due to missing or broken limbs.", "因肢體缺失或損壞而行動受限。" },
            { "Mollified.", "被安撫了。" },
            { "Molting beams of prismatic light.", "蛻皮煥發出稜鏡般的彩光。" },
            { "More likely to attack things.", "更容易攻擊事物。" },
            { "Move in what direction?", "往哪個方向移動？" },
            { "Move where?", "移動到哪裡？" },
            { "Moving at full speed.", "全速移動中。" },
            { "Must spend a turn exiting before moving.", "移動前必須花費一回合先離開。" },
            { "Mutants cannot digest this meal.", "變種人無法消化這頓餐。" },
            { "Mutation.", "突變。" },
            { "My cloning ability is no longer usable!", "我的複製能力不再可用！" },
            { "My target has been destroyed!", "我的目標已被摧毀！" },
            { "My target has no location!", "我的目標沒有位置！" },
            { "My target is already grafted...", "我的目標已被嫁接……" },
            { "My target is dead!", "我的目標死了！" },
            { "My target is in another zone!", "我的目標在另一個區域！" },
            { "My target is too far and I'm immobile.", "我的目標太遠了，而我無法移動。" },
            { "My target no longer exists!", "我的目標已不存在！" },
            { "No active effects.", "沒有進行中的效果。" },
            { "No adjacent empty squares to create your wish!", "沒有相鄰的空白格可以實現你的願望！" },
            { "No damage.", "無傷害。" },
            { "No faction found by that name.", "找不到該名稱的派系。" },
            { "No high scores!", "沒有高分紀錄！" },
            { "No mound of scrap and clay was found to complete.", "找不到可以完成它的廢料與黏土堆。" },
            { "No notes found.", "沒有找到筆記。" },
            { "No problems found.", "沒有發現問題。" },
            { "No skill by that name could be found.", "找不到該名稱的技能。" },
            { "Notes added.", "筆記已新增。" },
            { "Notes removed.", "筆記已移除。" },
            { "Nowhere to move element to!", "沒有可以移動元素的地方！" },
            { "O Glorious Shekhinah!", "噢，光榮的舍金納(Shekhinah)！" },
            { "Obliterate which object?", "要湮滅哪個物體？" },
            { "One of your flight capabilities fails.", "你的其中一項飛行能力失效了。" },
            { "One villager was frightened by sounds of fighting at night, indicating recent violence.", "一名村民被夜晚的打鬥聲嚇到，顯示近期發生過暴力。" },
                        { "Only mutants can digest this meal.", "只有變種人能消化這頓餐。" },
            { "Only suffers 25% damage from bludgeoning attacks.", "只會承受鈍擊攻擊 25% 的傷害。" },  
            { "Only suffers 25% damage from falling.", "只會承受墜落 25% 的傷害。" },  
            { "Only suffers 50% damage from bludgeoning attacks.", "只會承受鈍擊攻擊 50% 的傷害。" },  
            { "Only suffers 50% damage from falling.", "只會承受墜落 50% 的傷害。" },  
            { "Only true kin can digest this meal.", "只有真族能消化這頓餐。" },  
            { "Open..", "開啟……" },  
            { "Opponent died!", "對手死了！" },  
            { "Our respective ways of reckoning that inquiry are irreconcilable. I have no answer.", "我們對這項詢問的思量方式無法調和。我沒有答案。" },  
            { "Overdose on a hulk honey tonic.", "因過量服用巨魁蜂蜜補劑而中毒。" },  
            { "Overwrite file?", "覆寫檔案？" },  
            { "Pacify all seven of the Girsh nephilim in a single playthrough.", "於單次遊玩中安撫全部七位吉什(Girsh)尼菲林(nephilim)。" },  
            { "Pax Klanq, I Presume?", "帕克斯·克蘭克(Pax Klanq)，我想是吧？" },  
            { "Perform the introductory water ritual 100 times.", "進行 100 次入門水之儀式。" },  
            { "Perform the water ritual with Mamon Souldrinker.", "與噬魂者瑪蒙(Mamon Souldrinker)進行水之儀式。" },  
            { "Perform the water ritual with Oboroqoru, Ape God.", "與猿神奧博羅科魯(Oboroqoru)進行水之儀式。" },  
            { "Physically interactive with both in-phase and out-of-phase creatures and objects.", "能與同相與異相的生物和物體進行物理互動。" },  
            { "Piety compels you to deliver your sacred relics to the priests in the cathedral! Cleanse them of your filth!", "虔誠之心驅使你把神聖遺物送到大教堂的祭司手中！洗淨你身上的污穢吧！" },  
            { "Please choose a target body part.", "請選擇目標肢體部位。" },  
            { "Poisonous goo burns your eyes.", "毒膠灼燒著你的眼睛。" },  
            { "Poked around where they shouldn't.", "在他們不該去的地方窺探。" },  
            { "Poor reasoning.", "推理拙劣。" },  
            { "Pour a dram of pure warm static on yourself.", "將一德蘭(dram)純淨的溫熱靜電澆在自己身上。" },  
            { "Project your mind into a goat's body.", "將你的心智投射進山羊的身體。" },  
            { "Proselytized me.", "向我傳教了。" },  
            { "Prove the information paradox by causing your surroundings to glitch.", "讓周圍環境故障，證明資訊悖論。" },  
            { "Provides =min.range#max=% of resistance to mental intrusion.", "提供 =min.range#max=% 的心智侵入抗性。" },  
            { "Provides =min.range#max=% of resistance to most forms of mental intrusion.", "提供 =min.range#max=% 的多數心智侵入抗性。" },  
            { "Provides =min.range#max=% of resistance to some forms of mental intrusion.", "提供 =min.range#max=% 的部分心智侵入抗性。" },  
            { "Psst.", "嘶嘶。" },  
            { "Puff Klanq spores at a depth of at least 20 levels.", "在至少 20 層深處噴出克蘭克(Klanq)孢子。" },  
            { "Putrid ooze splashes into your mouth. You gag at the awful taste.", "腐臭黏液濺進你的嘴裡。那可怕的味道讓你乾嘔。" },  
            { "Radiates light in radius 3.", "半徑 3 內散發光芒。" },  
            { "Read 10 books.", "閱讀 10 本書。" },  
            { "Read 100 books.", "閱讀 100 本書。" },  
            { "Rebuked me.", "斥責了我。" },  
            { "Recover Kindrish.", "取回金德里什(Kindrish)。" },  
            { "Recover Stopsvalinn.", "取回斯托普斯瓦林(Stopsvalinn)。" },  
            { "Recover a relic from a historic site.", "從歷史遺址取回一件遺物。" },  
            { "Recover the Ruin of House Isner.", "取回伊斯納之家的遺墟。" },  
            { "Recover the Spindlegrounds from the Putus Templar war party.", "從普圖斯聖殿騎士團的戰隊手中奪回紡錘遺跡(Spindlegrounds)。" },  
            { "Recruited by another creature via proselytization.", "被另一隻生物透過傳教招募。" },  
            { "Regenerate a limb.", "再生一隻肢體。" },  
            { "Reload complete!", "重載完成！" },  
            { "Rematerialize in the corporeal realm.", "在實體界域重新物質化。" },  
            { "Restarting application...", "重新啟動應用程式……" },  
            { "Resting until healed...", "休息直到治癒……" },  
            { "Resting until party healed...", "休息直到小隊治癒……" },  
            { "Retreat point is invalid!", "撤離點無效！" },  
            { "Retreat point is on the world map, can't go there!", "撤離點在世界地圖上，無法前往！" },  
            { "Return from Golgotha with a repaired waydroid and gain admittance to Grit Gate.", "帶著修好的導路機(Waydroid)從髑髏地(Golgotha)返回，並獲准進入礫門(Grit Gate)。" },  
            { "Return the crumpled sheet of paper to its intended recipient or its author.", "把這張皺巴巴的紙還給它的收件人或作者。" },  
            { "Ruined the First Council of Omonporch.", "毀了歐蒙波奇(Omonporch)的第一議會。" },  
            { "SUCCESS! Item submitted!", "成功！物品已提交！" },  
            { "Scan surroundings, Ereshkigal.", "掃描四周，艾蕾什基伽勒(Ereshkigal)。" },  
            { "Sensor access granted. The Thin World is open to you. What do you wish?", "感測器存取已授予。薄界向你敞開。你希望什麼？" },  
            { "Settle the dispute in Bey Lah and doom the village.", "解決貝·拉(Bey Lah)的紛爭，並毀滅整個村莊。" },  
            { "Settle the dispute in Bey Lah in Eskhind's favor.", "解決貝·拉(Bey Lah)的紛爭，讓埃斯欣德(Eskhind)得利。" },  
            { "Settle the dispute in Bey Lah in Hindriarch Keh's favor.", "解決貝·拉(Bey Lah)的紛爭，讓牧母凱(Keh)得利。" },  
            { "Several horns jut out of your head.", "好幾支角從你的頭上突出。" },  
            { "Severity of heating effects reduced by =amount=%. Heat damage reduced by =amount=%.", "加熱效果的強度降低 =amount=%。熱傷害降低 =amount=%。" },  
            { "Share the map with 10 clones of yourself.", "與 10 個自己的複製品共享地圖。" },  
            { "Share the map with 30 clones of yourself.", "與 30 個自己的複製品共享地圖。" },  
            { "Shoot.", "開火。" },  
            { "Shot me by accident.", "意外射中了我。" },  
            { "Slam what?", "猛擊什麼？" },  
            { "Smoke from a hookah.", "抽一鬥水煙。" },  
            { "Someone discarded a worn but still functional leather bracer on the paths, a sign of recent craft.", "有人在路上丟棄了一個磨損但仍堪用的皮革護腕，這是近期工藝的跡象。" },  
            { "Someone dropped a copper nugget on the paths, a sign of recent trade.", "有人在小徑上掉落了一塊銅塊，這是近期貿易的跡象。" },  
            { "Someone left a sealed merchant's waterskin on the paths, a sign of recent trade.", "有人在小徑上留下了一個密封的商人水袋，這是近期貿易的跡象。" },  
            { "Something went wrong.", "出了點問題。" },  
            { "Speak to the alchemist.", "與煉金術師交談。" },  
            { "Spread Klanq in space.", "在太空中散播克蘭克(Klanq)。" },  
            { "Sprints or power skates at thrice move speed.", "以三倍移動速度衝刺或動力滑行。" },  
            { "Spurn Resheph and return to Qud.", "唾棄雷舍夫(Resheph)並返回 Qud。" },  
            { "Started!", "開始了！" },  
            { "Staying underwater.", "待在水下。" },  
            { "Stops cardiac arrest.", "停止心臟停搏。" },  
            { "Storage space is full. Please make more space to avoid loss of data.", "儲存空間已滿。請騰出更多空間以避免資料遺失。" },  
            { "Storage space is running low. Please make more space to avoid loss of data.", "儲存空間不足。請騰出更多空間以避免資料遺失。" },  
            { "Strike a deal with Asphodel the Lovely for control of the Spindlegrounds.", "與美麗的阿福黛爾(Asphodel the Lovely)達成控制紡錘遺跡(Spindlegrounds)的交易。" },  
            { "Strong =name= trekked to the City of Bones to breathe the tarry vapor and bathe in the black blood of the earth.", "強大的 =name= 長途跋涉到白骨之城，呼吸焦油煙霧，並在土之黑血中沐浴。" },  
            { "Strong =name= trekked to the Great Asphalt Sea to breathe the tarry vapor and bathe in the black blood of the earth.", "強大的 =name= 長途跋涉到瀝青海，呼吸焦油煙霧，並在土之黑血中沐浴。" },  
            { "Strong =name= trekked to the Swilling Vast to breathe the tarry vapor and bathe in the black blood of the earth.", "強大的 =name= 長途跋涉到漫湧曠野(Swilling Vast)，呼吸焦油煙霧，並在土之黑血中沐浴。" },  
            { "Strong =name= trekked to the Tunnels of Ur to breathe the tarry vapor and bathe in the black blood of the earth.", "強大的 =name= 長途跋涉到烏爾隧道(Tunnels of Ur)，呼吸焦油煙霧，並在土之黑血中沐浴。" },  
            { "Strong =name= trekked to the asphalt mines to breathe the tarry vapor and bathe in the black blood of the earth.", "強大的 =name= 長途跋涉到瀝青礦，呼吸焦油煙霧，並在土之黑血中沐浴。" },  
            { "Successfully cook a meal with neutron flux as an ingredient.", "成功以中子通量為食材烹飪一頓。" },  
            { "Summoned me.", "召喚了我。" },  
            { "Surprise!", "驚喜！" },  
            { "Suspended in spacetime. Cannot act or be interacted with.", "懸浮在時空中。無法行動，也無法與之互動。" },  
            { "Swap canceled.", "交換已取消。" },  
            { "Take time to weigh the options.", "花點時間權衡選項。" },  
            { "Tattoo yourself.", "為自己紋身。" },  
            { "Thank you, Ereshkigal.", "謝謝你，艾蕾什基伽勒(Ereshkigal)。" },  
            { "That hits the spot!", "正中要害！" },  
            { "That name is already in use.", "那個名稱已被使用。" },  
            { "That wouldn't leave you with NEARLY enough junk! You can't drop that!", "那樣不會讓你剩下足夠的「垃圾」！你不能丟棄它！" },  
            { "That's too dangerous!", "那太危險了！" },  
            { "There =pool.is= a dangerous-looking =pool.short= that way. Do you want to go =direction.toward= and start swimming?", "那裡 =pool.is= 一片看似危險的 =pool.short=。你想要向 =direction.toward= 前進並開始游泳嗎？" },  
            { "There are hostiles nearby!", "附近有敵對者！" },  
            { "There are no creatures in range.", "範圍內沒有生物。" },  
            { "There are no places to escape to safely!", "沒有可以安全逃離的地方！" },  
            { "There is no liquid here for you to spew.", "這裡沒有你可以噴吐的液體。" },  
            { "There is no one there for you to entangle.", "那裡沒有你可以糾纏的對象。" },  
            { "There is no starship to enter. The docking bay is empty.", "沒有可以進入的星艦。停靠艙是空的。" },  
            { "There is nobody there to perform Death From Above on.", "那裡沒有可以施展「死亡從天而降」的目標。" },  
            { "There is nothing for you to submerge in here.", "這裡沒有可以讓你潛入的東西。" },  
            { "There is nothing there for you to clone.", "那裡沒有你可以複製的東西。" },  
            { "There is nothing you can telekinetically manipulate there.", "那裡沒有你能用念力操控的東西。" },
            { "There's nothing there to hobble.", "那裡沒有可以絆跛的對象。" },  
            { "There's nothing there to lunge at.", "那裡沒有可以突刺的目標。" },  
            { "There's nothing there to lunge away from.", "那裡沒有可以突離的目標。" },  
            { "There's nothing there to shank.", "那裡沒有可以捅刺的對象。" },  
            { "There's nothing there to slam.", "那裡沒有可以猛擊的對象。" },  
            { "There's nothing there to swipe at.", "那裡沒有可以揮斬的目標。" },  
            { "There's nothing there you can conk.", "那裡沒有你能砸暈的對象。" },  
            { "There's nothing there you can lunge at.", "那裡沒有你能突刺的目標。" },  
            { "There's nothing there you can shank.", "那裡沒有你能捅刺的對象。" },  
            { "There's nothing there you can shield slam.", "那裡沒有你能以盾猛擊的對象。" },  
            { "There's nothing there you can swipe at.", "那裡沒有你能揮斬的目標。" },  
            { "There's something else over here.", "這裡還有其他東西。" },  
            { "There's something in my way!", "有什麼擋住了我！" },  
            { "This creature is unresponsive.", "這隻生物沒有反應。" },  
            { "This is your equipment and inventory screen. Equipped items on the left, and inventory on the right.", "這是你的裝備與物品欄畫面。左邊是已裝備物品，右邊是物品欄。" },  
            { "This item is a tonic. Applying one tonic while under the effects of another may produce undesired results.", "此物品是補劑。在一種補劑的效力下再施用另一種補劑，可能產生不良後果。" },  
            { "This item's AV and DV bonuses are being averaged across all body parts of the same type.", "此物品的 AV 與 DV 加成會依所有同類型肢體部位平均計算。" },  
            { "This item's AV and DV modifiers are being averaged across all body parts of the same type.", "此物品的 AV 與 DV 修正值會依所有同類型肢體部位平均計算。" },  
            { "This item's AV and DV penalties are being averaged across all body parts of the same type.", "此物品的 AV 與 DV 懲罰會依所有同類型肢體部位平均計算。" },  
            { "This item's AV bonus is being averaged across all body parts of the same type.", "此物品的 AV 加成會依所有同類型肢體部位平均計算。" },  
            { "This item's AV penalty is being averaged across all body parts of the same type.", "此物品的 AV 懲罰會依所有同類型肢體部位平均計算。" },  
            { "This item's DV bonus is being averaged across all body parts of the same type.", "此物品的 DV 加成會依所有同類型肢體部位平均計算。" },  
            { "This item's DV penalty is being averaged across all body parts of the same type.", "此物品的 DV 懲罰會依所有同類型肢體部位平均計算。" },  
            { "This place already has a name.", "這個地方已經有名字了。" },  
            { "Timeout.", "逾時。" },  
            { "Travel to Brightsheol and disable the Spindle's magnetic field.", "前往布萊特希歐(Brightsheol)並停用紡錘塔(Spindle)的磁場。" },  
            { "Travel to the Six Day Stilt.", "前往六日高蹺(Six Day Stilt)。" },  
            { "Travel to the Temple of the Rock and decrypt the mysterious signal.", "前往巖石神殿並解密神秘的訊號。" },  
            { "Travel to the pocket dimension Tzimtzlum.", "前往口袋次元席姆茨魯姆(Tzimtzlum)。" },  
            { "Tried to beguile me.", "試圖魅惑我。" },  
            { "True kin cannot digest this meal.", "真族無法消化這頓餐。" },  
            { "Unable to move.", "無法移動。" },  
            { "Under someone else's control.", "受他人控制。" },  
            { "Use which recoiler?", "使用哪個回彈器？" },  
            { "Violate the Pauli exclusion principle.", "違反包立不相容原理。" },  
            { "Violate the sacred covenant of the water ritual.", "違反水之儀式的神聖誓約。" },  
            { "Visit 100 generated villages.", "造訪 100 座生成的村莊。" },  
            { "Wait for the bear to take a step towards you.", "等待熊朝你踏出一步。" },  
            { "Waiting 100 turns...", "等待 100 回合……" },  
            { "Waiting...", "等待中……" },  
            { "Wake peacefully from twenty dreams.", "從二十場夢中安然醒來。" },  
            { "Was It Something I Said?", "是我說了什麼嗎？" },  
            { "Wear each of the six Faces of the sultanate.", "佩戴蘇丹國六張面容各一。" },  
            { "Wear the Otherpearl.", "佩戴異界珍珠(Otherpearl)。" },  
            { "Wear your own severed face on your face.", "把自己割下的臉戴在自己的臉上。" },  
            { "Weirdwire Conduit... Eureka!", "異線導管……啊哈！" },  
            { "What Are Directions on a Space That Cannot Be Ordered?", "什麼是無法排序空間上的方向？" },  
            { "What is the Thin World?", "薄界是什麼？" },  
            { "What would you like to name?", "你想要命名什麼？" },  
            { "What's Eating the Watervine?", "什麼在吃水藤？" },  
            { "Whenever you drink freshwater, there's a 25% chance you become immune to fear for 6 hours.", "每當你喝淡水，有 25% 機率對恐懼免疫 6 小時。" },  
            { "Whenever you would have taken cold damage, you heal for 0.", "每當你本會受到寒冷傷害時，改為治療 0 點。" },  
            { "Whimsy must yield to necessity, Aristocrat.", "任性必須向必然屈服，貴族大人。" },  
            { "Wield Caslainard and Polluxus.", "裝備卡斯萊納德(Caslainard)與波魯克斯(Polluxus)。" },  
            { "Wield the amaranthine prism.", "裝備不朽稜鏡(amaranthine prism)。" },  
            { "Will fall asleep soon.", "很快會睡著。" },  
            { "Will fall in love with the first thing examined.", "會愛上第一個檢查的東西。" },
            { "Will prefer to attack creatures who are bleeding.", "會傾向攻擊正在流血的生物。" },  
            { "Working...", "處理中……" },  
            { "World seed copied to your clipboard.", "世界種子已複製到剪貼簿。" },  
            { "Would you like to walk to the nearest stairway down?", "你想要走到最近的向下的樓梯嗎？" },  
            { "Would you like to walk to the nearest stairway up?", "你想要走到最近的向上的樓梯嗎？" },  
            { "Yes. I will =verb= =item.the.basename|strip= as you ask.", "好的。我會照你吩咐 =verb= =item.the.basename|strip=。" },  
            { "You abandoned all hope.", "你放棄了所有希望。" },  
            { "You acceded to Resheph's plan to purge the world of higher life in preparation for the Coven's return.", "你同意了雷舍夫(Resheph)淨化世上高階生命的計畫，為女巫集會(Coven)的回歸做準備。" },  
            { "You accrue electrical charge that you can use and discharge to deal damage.", "你積累了可用於施放以造成傷害的電荷。" },  
            { "You activate the recoiler.", "你啟動了回彈器。" },  
            { "You aggressively swipe your blade in the air.", "你用力揮劍劃過空中。" },  
            { "You are Becoming, Aristocrat.", "你正在「成為」，貴族大人。" },  
            { "You are a crystalline being.", "你是水晶型存在。" },  
            { "You are a wall-dwelling creature and may only move onto walls.", "你是棲牆生物，只能移動到牆上。" },  
            { "You are already sitting down.", "你已經坐下了。" },  
            { "You are an aquatic creature and may not move onto land!", "你是水生生物，不能移動到陸地上！" },  
            { "You are becoming, aristocrat. Choose an implant to install.", "你正在「成為」，貴族大人。選擇要安裝的植入物。" },  
            { "You are becoming.", "你正在「成為」。" },  
            { "You are carrying too much to move!", "你身上物品太重，動彈不得！" },  
            { "You are covered in sticky goop!", "你全身沾滿黏稠膠狀物！" },  
            { "You are famished! You'll act more slowly until you eat again.", "你餓壞了！在再次進食前，你的行動會變慢。" },  
            { "You are frenzied by the howl!", "你被嚎叫搞得狂亂了！" },  
            { "You are given to whimsy, Aristocrat. Choose an implant to uninstall.", "你本性任性，貴族大人。選擇要卸除的植入物。" },  
            { "You are hooked!", "你被鉤住了！" },  
            { "You are immune to getting stuck.", "你免疫於被黏住。" },  
            { "You are in no shape to start a conversation.", "你現在的狀態不適合展開對話。" },  
            { "You are leaving the ambient broadcast power grid and transitioning to backup power. Are you sure?", "你正要離開環境廣播電網並切換到後備電力。你確定嗎？" },  
            { "You are lost!", "你迷路了！" },  
            { "You are no aristocrat. Goodbye.", "你不是貴族大人。再見。" },  
            { "You are not a mobile creature.", "你不是可移動的生物。" },  
            { "You are not enclosed.", "你沒有被包圍。" },  
            { "You are not sitting down.", "你沒有坐著。" },  
            { "You are nut currently in a landmark location.", "你目前並非位於地標地點。" },  
            { "You are out of turrets to place.", "你沒有可以放置的砲塔了。" },  
            { "You are paralyzed!", "你麻痺了！" },  
            { "You are possessed of an indefatigable focus which every so often manifests itself as stubbornness.", "你擁有不知疲倦的專注力，時不時表現為固執。" },  
            { "You are possessed of exceptionally acute smell.", "你擁有異常靈敏的嗅覺。" },  
            { "You are possessed of hulking strength.", "你擁有魁梧的力氣。" },  
            { "You are startled!", "你嚇了一跳！" },  
            { "You are stuck in a remote pocket dimension and cannot recoil out.", "你被困在偏遠的口袋次元中，無法用回彈器離開。" },  
            { "You are teleported to an exit.", "你被傳送到一個出口。" },  
            { "You are too exhausted to act!", "你太疲憊，無法行動！" },  
            { "You are too exhausted to do that.", "你太疲憊，無法那樣做。" },  
            { "You are unable to consume food.", "你無法進食。" },  
            { "You are unusually large.", "你異常地巨大。" },  
            { "You ask about your location and are no longer lost.", "你詢問了所在位置，不再迷路。" },  
            { "You assume the form of any creature you touch.", "你會變成任何你觸碰到的生物的形態。" },  
            { "You ate it!", "你把它吃掉了！" },  
            { "You begin flying!", "你開始飛翔！" },  
            { "You begin itching for a trigger.", "你開始渴望按一下扳機。" },  
            { "You begin using an additional flight capability.", "你開始啟用一項額外的飛行能力。" },  
            { "You beguile a nearby creature into serving you loyally.", "你魅惑了一隻附近的生物，使其忠誠地為你效勞。" },  
            { "You belch forth various urchins.", "你嘔出各種海膽來。" },  
            { "You bond with a nearby organic creature and leech its life force.", "你與附近的有機生物連結，吸取它的生命力。" },  
            { "You breathe confusion gas.", "你呼出困惑氣體。" },  
            { "You breathe corrosive gas.", "你呼出腐蝕氣體。" },  
            { "You breathe fire.", "你噴吐火焰。" },  
            { "You breathe ice.", "你噴吐寒冰。" },  
            { "You breathe normality gas.", "你呼出常態氣體。" },  
            { "You breathe poison gas.", "你呼出毒氣。" },  
            { "You breathe shame gas.", "你呼出羞恥氣體。" },  
            { "You breathe sleep gas.", "你呼出催眠氣體。" },  
            { "You breathe stun gas.", "你呼出暈眩氣體。" },  
            { "You can move across walls, and only across walls.", "你能穿越牆壁，而且只能穿越牆壁。" },  
            { "You can move things with your mind.", "你能用念力移動東西。" },  
            { "You can only restore your checkpoint outside settlements.", "你只能在聚落外恢復儲存點。" },  
            { "You can only set your checkpoint in settlements.", "你只能在聚落中設定儲存點。" },  
            { "You can spin sticky silk webs.", "你能吐織黏稠的絲網。" },  
            { "You can walk over slime without slipping.", "你能走過黏液而不滑倒。" },  
            { "You can't Demolish until Slam is off cooldown.", "在猛擊結束冷卻前，你無法使用「粉碎」。" },  
            { "You can't be mutated.", "你無法被突變。" },  
            { "You can't delete automatically recorded chronology entries.", "你無法刪除自動記錄的事件表條目。" },  
            { "You can't deploy there!", "你不能在那裡部署！" },  
            { "You can't do that here.", "你不能在這裡做這件事。" },  
            { "You can't exit here.", "你不能從這裡離開。" },  
            { "You can't fly right now.", "你現在無法飛行。" },
            { "You can't fly underground!", "你無法在地下飛行！" },  
            { "You can't fly while overburdened.", "超載時你無法飛行。" },  
            { "You can't go berserk until Dismember is off cooldown.", "在肢解結束冷卻前，你無法進入狂暴。" },  
            { "You can't go to sleep right now.", "你現在無法入睡。" },  
            { "You can't imprint yet.", "你暫時無法銘印。" },  
            { "You can't leave the golem while it is ascending.", "石魔像上升期間你不能離開。" },  
            { "You can't pilot the golem while it is ascending.", "石魔像上升期間你不能駕駛它。" },  
            { "You can't recoil with hostiles nearby!", "附近有敵對者時你不能使用回彈器！" },  
            { "You can't recoil yet.", "你現在還不能用回彈器。" },  
            { "You can't remove =cell.the.name=!", "你無法卸下 =cell.the.name=！" },  
            { "You can't scintillate again so soon.", "你不能那麼快再度閃爍。" },  
            { "You cannot be seen.", "你無法被看見。" },  
            { "You cannot berate without a tongue.", "沒有舌頭你無法斥責。" },  
            { "You cannot charge a flying target.", "你無法衝撞飛行中的目標。" },  
            { "You cannot charge while flying.", "飛行中你無法衝撞。" },  
            { "You cannot charge while in melee combat.", "近戰交戰中你無法衝撞。" },  
            { "You cannot charge while overburdened.", "超載時你無法衝撞。" },  
            { "You cannot cross into Brightsheol from this place.", "你無法從這個地方跨越進入布萊特希歐(Brightsheol)。" },  
            { "You cannot do that right now.", "你現在不能那樣做。" },  
            { "You cannot do that while enclosed.", "被包圍時你無法那樣做。" },  
            { "You cannot do that while flying.", "飛行中你無法那樣做。" },  
            { "You cannot do that while sitting.", "坐著時你無法那樣做。" },  
            { "You cannot do that while submerged.", "潛入水中時你無法那樣做。" },  
            { "You cannot do that while swimming.", "游泳時你無法那樣做。" },  
            { "You cannot do that while wading.", "涉水時你無法那樣做。" },  
            { "You cannot do that.", "你不能那樣做。" },  
            { "You cannot examine things while you are confused.", "困惑時你無法檢視事物。" },  
            { "You cannot examine things while you are enraged.", "狂怒時你無法檢視事物。" },  
            { "You cannot go that way.", "你不能往那個方向走。" },  
            { "You cannot mark a target while you are confused.", "困惑時你無法標記目標。" },  
            { "You cannot perform Death From Above on a door.", "你無法對門施展「死亡從天而降」。" },  
            { "You cannot perform Death From Above on a wall.", "你無法對牆施展「死亡從天而降」。" },  
            { "You cannot perform Death From Above while overburdened.", "超載時你無法施展「死亡從天而降」。" },  
            { "You cannot use disassemble all with hostiles nearby.", "附近有敵對者時，你無法使用「全部拆解」。" },  
            { "You capture prey with your sticky tongue.", "你用黏滑的舌頭捕獲獵物。" },  
            { "You cease using one of your flight capabilities.", "你停止了其中一項飛行能力。" },  
            { "You climb into the sarcophagus.", "你爬進石棺。" },  
            { "You cooled into a block of shale.", "你冷卻成了一塊頁岩。" },  
            { "You crossed into Brightsheol.", "你跨越進入了布萊特希歐(Brightsheol)。" },  
            { "You crossed into the Thin World, rarefied, and stood before the gate to Brightsheol.", "你跨越進入薄界，變得稀薄，並站在通往布萊特希歐(Brightsheol)的大門前。" },  
            { "You dash along a waveform.", "你沿著波函數衝刺。" },  
            { "You destroyed Resheph and launched yourself into the dusted cosmos to ply the stars with Barathrum.", "你摧毀了雷舍夫(Resheph)，把自己送入蒙塵的宇宙，與巴拉特魯姆(Barathrum)一起縱橫星海。" },  
            { "You destroyed Resheph and launched yourself into the dusted cosmos to ply the stars.", "你摧毀了雷舍夫(Resheph)，把自己送入蒙塵的宇宙，縱橫星海。" },  
            { "You destroyed Resheph and returned to Qud to help garden the burgeoning world.", "你摧毀了雷舍夫(Resheph)，返回 Qud 幫忙耕耘這個蓬勃成長的世界。" },  
            { "You destroyed Resheph to save the burgeoning world but marooned yourself at the North Sheva.", "你摧毀了雷舍夫(Resheph)以拯救蓬勃成長的世界，卻將自己困在北舍瓦(North Sheva)。" },  
            { "You died and were entombed in the burial chamber of Resheph, the Last Sultan.", "你死了，並被安葬在「最後的蘇丹」雷舍夫(Resheph)的墓室中。" },  
            { "You discover an abandoned village. Would you like to investigate?", "你發現了一座廢棄村莊。你想要調查嗎？" },  
            { "You discover some historic ruins. Would you like to investigate?", "你發現了一些歷史遺跡。你想要調查嗎？" },  
            { "You discovered the Hydropon.", "你發現了水培生機塔(Hydropon)。" },  
            { "You discovered the hidden village of Bey Lah.", "你發現了隱藏的村莊貝·拉(Bey Lah)。" },  
            { "You dissipated.", "你消散了。" },  
            { "You do not budge.", "你一動不動。" },  
            { "You do not have a bow or rifle equipped!", "你沒有裝備弓或步槍！" },  
            { "You do not have any recoilers.", "你沒有任何回彈器。" },  
            { "You don't have a missile weapon equipped that uses that ammunition.", "你沒有裝備使用那種彈藥的遠程武器。" },  
            { "You don't have a tongue!", "你沒有舌頭！" },  
            { "You don't have anything to use in that slot.", "那個欄位沒有可用的東西。" },  
            { "You don't have enough skill points.", "你沒有足夠的技能點數。" },  
            { "You drank the bright cream of the Palladium Reef and were quickened.", "你喝下鈀礁(Palladium Reef)的明亮乳膏，變得更有活力。" },  
            { "You drank the raw entropy of the universe and your genome fluctuated in and out of coherence.", "你喝下宇宙的原始熵，你的基因組在有序與無序之間波動。" },  
            { "You drank the raw entropy of the universe and your mind fluctuated in and out of coherence.", "你喝下宇宙的原始熵，你的心智在清晰與混沌之間波動。" },  
            { "You emit a ray of flame.", "你射出一道火焰射線。" },  
            { "You emit a ray of frost.", "你射出一道冰霜射線。" },  
            { "You emit jets of frost from your mouth.", "你從嘴裡噴出冰霜射流。" },  
            { "You emit powerful magnetic pulses.", "你發射出強力磁脈衝。" },  
            { "You ended the game.", "你結束了遊戲。" },  
            { "You entered into a covenant with Resheph to help prepare Qud for the Coven's return.", "你與雷舍夫(Resheph)締結聖約，協助為 Qud 準備迎接女巫集會(Coven)的回歸。" },  
            { "You experienced a lack of existential support.", "你體驗到存在根基的缺乏。" },  
            { "You exploded.", "你爆炸了。" },  
            { "You extract carbon from living material.", "你從活體材料中萃取碳。" },  
            { "You fall to the ground!", "你摔倒在地！" },  
            { "You feel a cool swelling as your organs start to glow through your skin.", "你感到一陣冰涼的膨脹，你的器官開始穿透皮膚發出光芒。" },  
            { "You feel a sense of holiness here.", "你在這裡感到一股神聖感。" },  
            { "You feel a soothing tingle in your chest as your wounds start to close.", "你感到胸口一陣舒適的刺痛，傷口開始癒合。" },  
            { "You feel feverish.", "你覺得發燒了。" },  
            { "You feel increasingly unstable.", "你覺得越來越不穩定。" },  
            { "You feel less feverish.", "你覺得燒退了一點。" },  
            { "You feel shaken and infirm.", "你覺得動搖虛弱。" },  
            { "You feel uneasy.", "你感到不安。" },
            { "You feel unsettlingly ambivalent.", "你感到不安的矛盾。" },  
            { "You fell apart.", "你四分五裂了。" },  
            { "You fell from a great height.", "你從高處墜落。" },  
            { "You find a passageway back to your home dimension.", "你找到了一條通往家鄉次元的通道。" },  
            { "You found a new home for a young people.", "你為一個年輕的族群找到了新家園。" },  
            { "You freed the Spindle for another's ascent and crossed into Brightsheol.", "你解放了紡錘(Spindle)供他人攀登，並跨越進入布萊特希歐(Brightsheol)。" },  
            { "You froze.", "你凍結了。" },  
            { "You gain +6 agility and +10 movespeed for 100 turns. Can only be activated at night.", "你在 100 回合內獲得 +6 敏捷與 +10 移動速度。只能在夜間啟動。" },  
            { "You garrote an adjacent creature's mind and control its actions while your own body lies dormant.", "你用絞索絞住鄰近生物的心靈並控制其行動，此時你自己的身體沉眠不語。" },  
            { "You got it!", "你得到了！" },  
            { "You had the feeling of being watched, and learned that there's a sight older than sight.", "你有種被注視的感覺，並領悟到存在著比視覺更古老的凝視。" },  
            { "You have an extra set of arms.", "你多了一雙手臂。" },  
            { "You have an extra set of legs.", "你多了一雙腿。" },  
            { "You have no abilities to manage!", "你沒有可以管理的能力！" },  
            { "You have no artifacts to offer that are not important.", "你沒有可以獻出且不重要的神器。" },  
            { "You have no artifacts to offer.", "你沒有可以獻出的神器。" },  
            { "You have no attribute points to raise this attribute.", "你沒有可以提升此屬性的屬性點數。" },  
            { "You have no bodily tether to recoil.", "你沒有可以歸返的身體繫帶。" },  
            { "You have no books to give.", "你沒有可以送出的書籍。" },  
            { "You have no devices that use energy cells.", "你沒有使用能量電池的裝置。" },  
            { "You have no explosives to deploy!", "你沒有可以部署的爆裂物！" },  
            { "You have no hands to beat at the flames with!", "你沒有手可以拍打火焰！" },  
            { "You have no hands to beat at the flames with, and cannot roll on the ground because you are flying!", "你沒有手可以拍打火焰，而且因為你在飛行，無法在地上翻滾！" },  
            { "You have no hands to beat at the flames with, and cannot roll on the ground because you are phased out!", "你沒有手可以拍打火焰，而且因為你已脫出相位，無法在地上翻滾！" },  
            { "You have no hands to beat at the flames with. Do you want to roll on the ground to try to put them out?", "你沒有手可以拍打火焰。你想要在地上翻滾試圖撲滅它們嗎？" },  
            { "You have no inventory!", "你沒有物品欄！" },  
            { "You have no items that can be magnetized.", "你沒有可以磁化的物品。" },  
            { "You have no missile weapons to deploy.", "你沒有可以部署的遠程武器。" },  
            { "You have no tonics to load.", "你沒有可以裝填的補劑。" },  
            { "You have no weapon!", "你沒有武器！" },  
            { "You have the feeling of waking from a dream.", "你有種從夢中醒來的感覺。" },  
            { "You have two heads.", "你有兩顆頭。" },  
            { "You have two hearts.", "你有兩顆心臟。" },  
            { "You have wilted! You'll move and regenerate slower until you eat or bask in the sunlight again.", "你萎蔫了！在再次進食或沐浴陽光之前，你的移動與再生會變慢。" },  
            { "You haven't found any points of interest nearby.", "你沒有在附近找到任何地標。" },  
            { "You hear a shloop and the world around you shifts.", "你聽到一聲「唰嚕」，四周的世界移了位。" },  
            { "You hear a shloop and then a hitch. Nothing happens.", "你聽到一聲「唰嚕」，接著一個停頓。什麼事都沒發生。" },  
            { "You hear a shloop, and the world around you warps and shifts violently.", "你聽到一聲「唰嚕」，四周的世界劇烈扭曲移位。" },  
            { "You hear inaudible mumbling.", "你聽到無聲的喃喃自語。" },  
            { "You journeyed to Bethesda Susa.", "你造訪了貝塞斯達·蘇薩(Bethesda Susa)。" },  
            { "You journeyed to Golgotha.", "你造訪了髑髏地(Golgotha)。" },  
            { "You journeyed to Grit Gate.", "你造訪了礫門(Grit Gate)。" },  
            { "You journeyed to Kyakukya.", "你造訪了卡亞庫亞(Kyakukya)。" },  
            { "You journeyed to Omonporch.", "你造訪了歐蒙波奇(Omonporch)。" },  
            { "You journeyed to Red Rock.", "你造訪了紅岩(Red Rock)。" },  
            { "You journeyed to a rusted archway.", "你造訪了一座鏽蝕的拱門。" },  
            { "You journeyed to the City of Bones.", "你造訪了白骨之城(City of Bones)。" },  
            { "You journeyed to the Great Asphalt Sea.", "你造訪了瀝青海(Great Asphalt Sea)。" },  
            { "You journeyed to the Six Day Stilt.", "你造訪了六日高蹺(Six Day Stilt)。" },  
            { "You journeyed to the Swilling Vast.", "你造訪了漫湧曠野(Swilling Vast)。" },  
            { "You journeyed to the Tomb of the Eaters.", "你造訪了食者之墓(Tomb of the Eaters)。" },  
            { "You journeyed to the Tunnels of Ur.", "你造訪了烏爾隧道(Tunnels of Ur)。" },  
            { "You journeyed to the asphalt mines.", "你造訪了瀝青礦山。" },  
            { "You journeyed to the rust wells.", "你造訪了鏽井。" },  
            { "You lack a liquid to spit!", "你沒有可以吐出的液體！" },  
            { "You lack the means to do that.", "你缺乏這樣做的手段。" },  
            { "You launched yourself into the dusted cosmos to ply the stars with Barathrum.", "你把自己送入蒙塵的宇宙，與巴拉特魯姆(Barathrum)一起縱橫星海。" },  
            { "You launched yourself into the dusted cosmos to ply the stars.", "你把自己送入蒙塵的宇宙，縱橫星海。" },  
            { "You left the dance early!", "你提早離開舞會！" },  
            { "You live in an alternate plane of reality.", "你生活在另一個現實平面。" },  
            { "You lose sight of your mark.", "你失去了目標的蹤影。" },  
            { "You lose your way beneath a dense canopy of spores.", "你在濃密的孢子穹蓋下迷失了方向。" },  
            { "You maintain your balance and kick the eel away.", "你穩住平衡，把電鰻踢開。" },  
            { "You maintain your balance and shake the eel off.", "你穩住平衡，把電鰻甩掉。" },  
            { "You may fire missile weapons through the forcefield.", "你可以透過力場發射遠程武器。" },  
            { "You may not learn this skill if you already have =skill.name=.", "如果你已經擁有 =skill.name=，你不能再學習此技能。" },  
            { "You may not raise an attribute above 100.", "你不能將屬性提升到 100 以上。" },  
            { "You may open security doors and use some secure devices by touching them.", "你可以透過觸碰來開啟保全門並使用部分保全裝置。" },  
            { "You may phase through solid objects for brief periods of time.", "你可以短暫地穿越固體物體。" },  
            { "You may walk freely over webs.", "你可以自由地走過蛛網。" },  
            { "You melt through the floor and descend with your meal.", "你融化穿過地板，連同你的食物一起下降。" },  
            { "You mind swerves from afar, and a force of repulsion from inside the sarcophagus prevents your entry.", "你的心靈在遠處偏轉，石棺內部傳出的排斥力阻止你進入。" },  
            { "You molt powerful beams across the spectrum of light and matter.", "你蛻皮放射出橫跨光譜與物質的強力光束。" },  
            { "You must be in a long blade stance to use that ability.", "你必須處於長刃架式才能使用該能力。" },
            { "You must have a long blade equipped in your primary hand to swipe.", "你必須在主手裝備長刃才能使用「揮斬」。" },  
            { "You must have a long blade equipped to switch stances.", "你必須裝備長刃才能切換架式。" },  
            { "You must have a shield equipped to perform a shield slam.", "你必須裝備盾牌才能施展「盾牌猛擊」。" },  
            { "You must have a short blade equipped in your primary hand to hobble.", "你必須在主手裝備短刃才能使用「絆跛」。" },  
            { "You must have a short blade equipped to shank.", "你必須裝備短刃才能使用「捅刺」。" },  
            { "You must have an axe equipped in your primary hand to go berserk.", "你必須在主手裝備斧頭才能進入狂暴。" },  
            { "You no longer feel ill.", "你不再覺得不適。" },  
            { "You notice some strange ruins nearby. Do you want to investigate?", "你注意到附近有一些奇怪的遺跡。你想要調查嗎？" },  
            { "You only have books you've marked important. Unmark any you wish to donate.", "你手上只剩下標記為重要的書籍。想捐贈的就取消標記。" },  
            { "You peer into your near future.", "你窺視自己不久的未來。" },  
            { "You ponder how best to sow chaos with your words.", "你思索如何用言語撒下混亂。" },  
            { "You possess a towering vision of self that you project onto the minds of nearby creatures.", "你擁有一個巍峨的自我形象，並將其投射到附近生物的心靈中。" },  
            { "You provoke waking dreams with your gaze.", "你用凝視勾起清醒之夢。" },  
            { "You quit.", "你退出了。" },  
            { "You read =title=.", "你閱讀了 =title=。" },  
            { "You rebuked Resheph and returned to Qud to help garden the burgeoning world.", "你斥責了雷舍夫(Resheph)，返回 Qud 幫忙耕耘這個蓬勃成長的世界。" },  
            { "You receive tinkering bits <=bits.bits=>.", "你獲得 <=bits.bits=> 個修理碎料。" },  
            { "You recognize the area and stop being lost!", "你認出了這個區域，不再迷路！" },  
            { "You reflect mental attacks back at your attackers.", "你將心靈攻擊反彈回攻擊者身上。" },  
            { "You regain your bearings.", "你恢復了方向感。" },  
            { "You regenerate by absorbing cold.", "你藉由吸收寒冷來再生。" },  
            { "You regenerate by absorbing heat.", "你藉由吸收熱量來再生。" },  
            { "You regulate your body's release of adrenaline.", "你調節身體腎上腺素的釋放。" },  
            { "You release a gaseous burst around yourself.", "你在自己周圍釋放出氣體爆噴。" },  
            { "You return to the ground.", "你回到地面。" },  
            { "You scare creatures around you.", "你嚇跑了周圍的生物。" },  
            { "You see in the dark.", "你能在黑暗中視物。" },  
            { "You slurp up nearby liquids and spew them with your bilge sphincter, occasionally knocking enemies prone.", "你吸起附近的液體，用你的艙底括約肌噴射出去，偶爾把敵人擊倒在地。" },  
            { "You spit a puddle of corrosive acid.", "你吐出一灘腐蝕性酸液。" },  
            { "You start to feel unstable.", "你開始覺得不穩定。" },  
            { "You startle easily and engage your defense mechanisms.", "你容易受驚，並啟動防禦機制。" },  
            { "You stop meditating and feel refreshed.", "你停止冥想，覺得神清氣爽。" },  
            { "You stop meditating.", "你停止冥想。" },  
            { "You sunder spacetime, sending things nearby careening through a tear in the cosmic fabric.", "你撕裂時空，讓附近的東西衝撞著穿過宇宙織物上的裂口。" },  
            { "You sunder the mind of an enemy, leaving them reeling in pain.", "你撕裂敵人的心靈，使他們在痛苦中搖晃。" },  
            { "You swell with the inspiration to name an item.", "你湧起為一件物品命名的靈感。" },  
            { "You swipe your blade in the air, pushing your enemies backward.", "你揮刃劃過空中，把敵人往後推。" },  
            { "You tap into the aggregate mind and steal power from other espers.", "你接入聚合之心，從其他靈能者身上竊取力量。" },  
            { "You taste life as it was distilled by the Eaters, Qud's primordial masons.", "你品嘗到食者——Qud 的原始工匠——所蒸餾的生命本質。" },  
            { "You teleport and bring creatures along with you.", "你傳送離開，並帶上附近的生物。" },  
            { "You turn things to stone with your gaze.", "你用凝視將事物變成石頭。" },  
            { "You were cancelled from existence.", "你被從存在中抹除。" },  
            { "You were crushed by falling rocks.", "你被墜落的岩石壓碎。" },  
            { "You were crushed under the weight of a thousand suns.", "你被千陽之重壓碎。" },  
            { "You were entombed in the burial chamber of Resheph, the Last Sultan.", "你被安葬在「最後的蘇丹」雷舍夫(Resheph)的墓室中。" },  
            { "You were immolated by a dawnglider.", "你被黎明滑翔者燒成灰燼。" },  
            { "You were lased to death by a crypt sitter.", "你被墓穴潛藏者用雷射燒死。" },  
            { "You were not recalled as you're already in a resting place.", "你沒有被召回，因為你已在安息之地。" },  
            { "You were reconstituted in the material world with a stronger magnetic charge.", "你在物質世界中以更強的磁荷重塑成形。" },  
            { "You were reduced to dust.", "你化為塵埃。" },  
            { "You were resorbed into the Mass Mind.", "你被重新吸收進入聚合之心。" },  
            { "You were stepped on.", "你被踩到了。" },  
            { "You were vaporized.", "你被汽化了。" },  
            { "You're not under enough stress to scintillate.", "你的壓力還不夠大，無法閃爍。" },  
            { "You're ready to continue the journey on your own, but don't hesitate to play the tutorial again if you need a refresher.", "你已準備好獨自繼續旅程，但如果需要複習，別猶豫再玩一次教學。" },  
            { "You've been recalled to a resting place.", "你已被召回安息之地。" },  
            { "You've done nothing.", "你什麼都沒做。" },  
            { "Your =attribute.statCapDisplayName= isn't high enough to buy =powerEntry.name=!", "你的 =attribute.statCapDisplayName= 不夠高，無法購買 =powerEntry.name=！" },  
            { "Your Hobble cooldown is reduced by 5 rounds.", "你的「絆跛」冷卻時間減少 5 回合。" },  
            { "Your adrenaline level returns to normal!", "你的腎上腺素濃度恢復正常！" },  
            { "Your bilge sphincter is missing.", "你的艙底括約肌不見了。" },  
            { "Your blood frenzies last twice as long.", "你的血之狂暴持續時間變成兩倍。" },  
            { "Your body flushes with adrenaline!", "你的身體湧出腎上腺素！" },  
            { "Your chance to Bludgeon is doubled.", "你的「棍擊」機率加倍。" },  
            { "Your cloning capacity is refreshed.", "你的複製容量已刷新。" },  
            { "Your cybernetic implant was successfully uninstalled.", "你的植入物已成功卸除。" },  
            { "Your feverish feeling eases up a bit.", "你的發燒感稍稍緩解。" },  
            { "Your feverish feeling is getting worse.", "你的發燒感越來越嚴重。" },  
            { "Your genome enters an excited state!", "你的基因組進入激發態！" },
            { "Your healing is interrupted!", "你的治療被打斷了！" },  
            { "Your heart begins to beat faster and your pupils dilate.", "你的心跳開始加速，瞳孔放大。" },  
            { "Your heart rate returns to normal and your pupils shrink.", "你的心率恢復正常，瞳孔縮小。" },  
            { "Your heart rate slows again.", "你的心率再次放緩。" },  
            { "Your heart swells with a burning sensation.", "你的心臟因灼燒感而膨脹。" },  
            { "Your hearts begin to beat faster and your pupils dilate.", "你的心臟們開始加速跳動，瞳孔放大。" },  
            { "Your joints stretch much further than usual.", "你的關節伸展得比平時遠得多。" },  
            { "Your lunge is interrupted.", "你的突刺被打斷了。" },  
            { "Your mind clouds over once again.", "你的心智再度蒙上雲霧。" },  
            { "Your mind winked out of existence.", "你的心智曾閃滅消逝。" },  
            { "Your mind winks out of existence.", "你的心智閃滅消逝。" },  
            { "Your muscles bulge grotesquely.", "你的肌肉怪異地鼓脹。" },  
            { "Your muscles deflate to their usual size.", "你的肌肉縮回平常的大小。" },  
            { "Your mutant physiology reacts adversely to the tonic. Aaaaaaaaargh!", "你的變種生理對補劑產生不良反應。啊啊啊啊啊！" },  
            { "Your mutant physiology reacts adversely to the tonic. The soothing tingle fades.", "你的變種生理對補劑產生不良反應。舒適的刺痛感消失了。" },  
            { "Your mutant physiology reacts adversely to the tonic. The torrent of life sweeps away.", "你的變種生理對補劑產生不良反應。生命的洪流退去了。" },  
            { "Your mutant physiology reacts adversely to the tonic. You feel awfully frigid.", "你的變種生理對補劑產生不良反應。你覺得極為寒冷。" },  
            { "Your mutant physiology reacts adversely to the tonic. You flicker through spacetime uncontrollably.", "你的變種生理對補劑產生不良反應。你在時空中無法控制地閃爍。" },  
            { "Your mutant physiology reacts adversely to the tonic. Your field of vision erupts into a plane of blinding, white light!", "你的變種生理對補劑產生不良反應。你的視野迸裂成一片刺眼的白光平面！" },  
            { "Your mutant physiology reacts adversely to the tonic. Your skin starts to knot and misshape.", "你的變種生理對補劑產生不良反應。你的皮膚開始打結變形。" },  
            { "Your onboard cloning systems are offline.", "你的機載複製系統離線了。" },  
            { "Your opponent perished!", "你的對手隕落了！" },  
            { "Your psyche is too exhausted.", "你的心靈過度疲憊。" },  
            { "Your seat is unable to eject.", "你的座椅無法彈射。" },  
            { "Your shot goes wild!", "你的射擊飛偏了！" },  
            { "Your skin flattens out and stretches tautly around your body once again.", "你的皮膚再次攤平，繃緊包裹全身。" },  
            { "Your skin shrivels and dimples.", "你的皮膚皺縮塌陷。" },  
            { "Your tracking of your mark has been disrupted.", "你對獵物的追蹤被打斷了。" },  
            { "Your wish is my command!", "你的願望就是我的命令！" },  
            { "Your wounds heal very quickly.", "你的傷口癒合得非常快。" },
            { "% Quickness", "% 敏捷" },
            { "% action cost", "% 行動成本" },
            { "% chance of being randomly long-range teleported when taking ", "有 % 機率在受到 時被隨機長程傳送" },
            { "% chance that each activated ability"s cooldown is refreshed.", "每個已啟動能力的冷卻時間有 % 機率被刷新。" },
            { "% chance to behead ", "有 % 機率斬首 " },
            { "% chance to dismember on hit.", "命中時有 % 機率肢解。" },
            { "% chance to dismember on penetration", "穿透時有 % 機率肢解" },
            { "% chance to dismember opponents.", "有 % 機率肢解敵人。" },
            { "% chance to put opponents into cardiac arrest", "有 % 機率使敵人陷入心臟停搏" },
            { "% chance to refract light-based attacks.", "有 % 機率折射光系攻擊。" },
            { "% chance to slip away from natural melee attacks", "有 % 機率從自然近戰攻擊中脫身" },
            { "% chance to stop cardiac arrest.", "有 % 機率停止心臟停搏。" },
            { "% chance to stop time for two turns when ", "有 % 機率停止時間兩回合，當 " },
            { "% chance you blink away instead.", "你有 % 機率改為瞬移閃避。" },
            { "% damage back at your attackers, rounded up.", "將 % 的傷害反彈回攻擊者身上，無條件進位。" },
            { "% health", "% 生命值" },
            { "% of that damage instead", "改為承受該傷害的 %" },
            { "% of the time", "有 % 的時候" },
            { "% of the time, the Fates have their way.", "有 % 的時候，命運自有安排。" },
            { "% plus ", "加上 % " },
            { "% to ", "% 至 " },
            { "%Despises.LegendaryCreature.SacredThing", "鄙視傳說生物聖物的存在" },
            { "%Worships.LegendaryCreature.ProfaneThing", "崇拜傳說生物褻瀆物的存在" },
            { "being run over by %O.", "被 %O 輾過。" },
            { "from %O!", "死於 %O！" },
            { "from %t ", "死於 %t " },
            { "from %t adrenal stress!", "死於 %t 的腎上腺壓力！" },
            { "from %t attack.", "死於 %t 的攻擊。" },
            { "from %t charge!", "死於 %t 的衝撞！" },
            { "from %t cryokinesis!", "死於 %t 的寒控術！" },
            { "from %t damage reflection!", "死於 %t 的傷害反彈！" },
            { "from %t digestive enzymes!", "死於 %t 的消化酶！" },
            { "from %t explosion!", "死於 %t 的爆炸！" },
            { "from %t failed assault on the structure of spacetime.", "死於 %t 對時空結構攻擊的失敗。" },
            { "from %t flames!", "死於 %t 的烈焰！" },
            { "from %t flaming weapon!", "死於 %t 的燃燒武器！" },
            { "from %t freeze!", "死於 %t 的凍結！" },
            { "from %t freezing effect!", "死於 %t 的凍結效果！" },
            { "from %t freezing weapon!", "死於 %t 的凍結武器！" },
            { "from %t impalement.", "死於 %t 的穿刺。" },
            { "from %t life drain!", "死於 %t 的生命汲取！" },
            { "from %t passage!", "死於 %t 的穿越！" },
            { "from %t projectile.", "死於 %t 的投射物。" },
            { "from %t pummeling!", "死於 %t 的猛擊！" },
            { "from %t pyrokinesis!", "死於 %t 的控火術！" },
            { "from %t scalding steam!", "死於 %t 的灼熱蒸汽！" },
            { "from %t shield slam!", "死於 %t 的盾牌猛擊！" },
            { "from %t spores!", "死於 %t 的孢子！" },
            { "from %t stunning force!", "死於 %t 的暈眩衝擊！" },
            { "from %t thorns.", "死於 %t 的尖刺。" },
            { "from %t tiny spines!", "死於 %t 的細小棘刺！" },
            { "from being slammed into a wall by %t charge!", "死於被 %t 撞擊拋向牆壁！" },
            { "from the cumulative trauma of %t mental assault!", "死於 %t 心靈攻擊的累積創傷！" },
            { "Worn on Hands", "穿戴在手" },
            { "Worn in Hands", "穿戴在手" },
            { "Worn on Back", "穿戴在背" },
            { "Worn in Back", "穿戴在背" },
            { "Left Missile Weapon", "左側遠程武器欄" },
            { "Right Missile Weapon", "右側遠程武器欄" },
            { "Forefeet", "前足" },
            { "Hindfeet", "後足" },
            { "Missile Weapon", "遠程武器欄" },
            { "Floating Near", "漂浮物" },
            { "Light Torch", "點燃的火把" },              
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
    private const int PostfixCacheMax = 100000;

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
            // 執行期 faction 名（=faction.FormattedName= 槽位，GetFormattedName 若用英文名則靠此兜底）
            { "Mopango", "莫龐戈(Mopango)" }, 
            { "Gyre Wights", "渦流亡靈(Gyre Wights)" }, { "Kyakukya", "恰庫恰(Kyakukya)" },
            { "YdFreehold", "伊德自由領(YdFreehold)" }, { "Issachari", "伊薩查部落(Issachari)" },
            { "Chavvah", "夏瓦(Chavvah)" }, { "villagers of Joppa", "約帕村民" },
            { "villagers of Kyakukya", "恰庫恰村民" }, { "villagers of Ezra", "艾茲拉村民" },
            // 執行期固定專名（Factions 等），跑 translate_propernouns（本地 LLM）音譯
            { "Cherubim", "切魯比姆(Cherubim)" }, { "Girsh", "吉爾什(Girsh)" },
            { "Mechanimists", "梅卡尼主義者(Mechanimists)" },
            { "cragmensch", "克拉格曼(Cragmensch)" }, { "Baetyls", "貝提爾(Baetyls)" },
            { "dromad merchants", "德羅馬德商人(dromad merchants)" },
            // 固定地點（DLL/藍圖硬編碼，非生成）
            { "Agolgot", "阿戈爾戈特(Agolgot)" },
            { "Shug'ruith", "舒格魯斯(Shug'ruith)" },
            { "Rermadon", "雷爾馬登(Rermadon)" },
            { "Brightsheol", "布萊特希歐(Brightsheol)" },
            // 註：執行期程序生成村名不再在此攔截——新存檔由 Naming.zh-tw Qudish Site
            // Load=Replace 直接生成中文名（通用方案，不綁定存檔）。
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
        @"(?i)\b(hit|miss|toggle|dazed|stand|take|eat|toss|gather|sit|climb|jump|wade|swim|emerge|bump|bond|detach|slip|swap|entangle|engulf|drag|suck|impal|lying|sitting|enclosed|pilot|knock|stop|move|look|turn|fall|rise|dies|died|emit|sprint|block(?:ed)?|wait\w*|way)\w*|擊中|受到|落空|拿起了|拿走了|擋住|冰凍射線|停止了|移動中|在 你的 way|擋住了",
        RegexOptions.Compiled);

    // combat 殘留提示（無 {{、無 frame 動詞，但明確是 xN 傷害/死亡句 → 仍需整句處理）
    private static readonly Regex CombatLeakHint = new Regex(
        @"\(x\d+\)\s+for\s+\d+\s+damage|dies[.!]?$|died[.!]?$", RegexOptions.Compiled);

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

        // 保護 =...= token（避免 Words/Verbs/Artifacts 誤傷 token 內關鍵字）
        var tokenBox = new List<string>();
        string work = ProtectTokens(text, tokenBox);
        // 保護「中文(English)」括號（避免逐詞替換/ProperNoun 污染括號英文側）
        var parenBox = new List<string>();
        work = ProtectParens(work, parenBox);

        string result = LeadingArticle.Replace(work, "");
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
        // 所有格 's 後接中文 → 哈爾's 丈夫 改 哈爾的丈夫
        result = PossessiveZh.Replace(result, "$1的");
        // 程序化專名音譯（STEP 4 防漏：漏網英文專名 → 繁中音譯）
        result = CleanNames(result);
        // 還原 token
        result = RestoreTokens(result, tokenBox);
        // 還原 ProperNoun 括號英文
        result = RestoreParens(result, parenBox);
        if (result != text)
        {
            if (Cache.Count >= CacheMax) Cache.Clear();
            Cache[text] = result;
        }
        return result;
    }

    // 是否含中日韓文字（供 UI hook 判斷純英文短詞）
    public static bool HasCjk(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= '一' && c <= '鿿') return true;
        }
        return false;
    }

    // TMP/console 短文本白名單（屬性/技能/能力名）：詞級翻譯只替換此表內的詞，
    // 避免誤傷英文模板詞（如 "Use"、"Skill" 等不該被逐詞翻譯的 UI 詞）。
    private static readonly Dictionary<string, string> TmpWords =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 屬性
            { "Strength", "力量" }, { "Agility", "敏捷" }, { "Toughness", "韌性" },
            { "Willpower", "意志" }, { "Intelligence", "智力" }, { "Ego", "心智" },
            { "Speed", "速度" }, { "MoveSpeed", "移動速度" }, { "Level", "等級" }, { "Third", "第三" },
            // 技能/能力（高頻白名單）
            { "Swift Reflexes", "迅捷反射" }, { "Spry", "靈活" }, { "Jump", "跳躍" },
            { "Tumble", "翻滾" }, { "Axe Proficiency", "斧頭精通" }, { "Cleave", "劈砍" },
            { "Charging Strike", "衝鋒突刺" }, { "Dismember", "肢解" },
            { "Hook and Drag", "鉤拽" }, { "Decapitate", "斬首" }, { "Berserk!", "狂暴！" },
            { "Cudgel Proficiency", "棍術精通" }, { "Bludgeon", "猛擊" }, { "Conk", "敲昏" },
            { "Backswing", "回身揮擊" }, { "Slam", "猛擊" }, { "Demolish", "粉碎" },
            { "Flurry", "疾風連擊" }, { "Lunge", "突刺" }, { "Swipe", "揮斬" },
            { "Dueling Stance", "決鬥姿態" }, { "Block", "格擋" }, { "Shield Slam", "盾牌猛擊" },
            { "Deft Blocking", "靈巧格擋" }, { "Swift Blocking", "迅捷格擋" },
            { "Staggering Block", "踉蹌格擋" }, { "Shield Wall", "盾牆" },
            { "Jab", "刺擊" }, { "Hobble", "跛行" }, { "Shank", "捅刺" },
            { "Hurdle", "跨越" }, { "Deft Throwing", "精準投擲" }, { "Charge", "衝鋒" },
            { "Kickback", "反衝" }, { "Juke", "假動作" }, { "Akimbo", "雙持" },
            { "Disassemble", "拆解" }, { "Reverse Engineer", "逆向工程" },
            { "Scavenger", "拾荒者" }, { "Repair", "修理" }, { "Deploy Turret", "部署砲塔" },
            { "Tinker I", "修理匠 I" }, { "Tinker II", "修理匠 II" }, { "Tinker III", "修理匠 III" },
            { "Meditate", "冥想" }, { "Fasting Way", "禁食之道" }, { "Iron Mind", "鐵意志" },
            { "Lionheart", "獅心" }, { "Conatus", "內驅力" }, { "Mind over Body", "心勝於身" },
            { "Proselytize", "傳教" }, { "Intimidate", "恐嚇" }, { "Berate", "斥責" },
            { "Snake Oiler", "油嘴滑舌" }, { "Inspiring Presence", "鼓舞人心" },
            { "Menacing Stare", "威嚇凝視" }, { "Steady Hands", "穩手" },
            { "Make Camp", "紮營" }, { "Mind's Compass", "心靈羅盤" },
            // 2026-08-13 補：全技能/技能樹名（遊戲短文本 TMP 路徑漏詞）
            { "Draw a Bead", "繪製珠飾" },
            { "Suppressive Fire", "壓制射擊" },
            { "Flattening Fire", "壓平火焰" },
            { "Wounding Fire", "傷口灼燒" },
            { "Disorienting Fire", "令人迷失方向的火焰" },
            { "Sure Fire", "萬無一失" },
            { "Beacon Fire", "信標之火" },
            { "Ultra Fire", "超火" },
            { "Meal Preparation", "膳食準備" },
            { "Harvestry", "收穫術" },
            { "Butchery", "屠殺" },
            { "Spicer", "香料商" },
            { "Carbide Chef", "碳化物廚師(Carbide Chef)" },
            { "Tactful", "圓滑" },
            { "Trash Divining", "垃圾占卜" },
            { "Opportune Attacks", "時機恰當的攻擊" },
            { "Weapon Expertise", "武器專精" },
            { "Penetrating Strikes", "穿透打擊" },
            { "Weapon Mastery", "武器精通" },
            { "Multiweapon Proficiency", "多武器熟練度" },
            { "Multiweapon Expertise", "多武器專精" },
            { "Multiweapon Mastery", "多武器精通" },
            { "Shake It Off", "擺脫負面狀態" },
            { "Swimming", "游泳" },
            { "Poison Tolerance", "毒素耐性" },
            { "Weathered", "飽經風霜" },
            { "Juicer", "榨汁者" },
            { "Calloused", "繭化" },
            { "Longstrider", "長步者" },
            { "Staunch Wounds", "頑強傷口" },
            { "Nostrums", "藥劑" },
            { "Amputate Limb", "截肢" },
            { "Apothecary", "藥劑師" },
            { "Strapping Shoulders", "束帶肩甲" },
            { "Tank", "坦克" },
            { "Sweep", "橫掃" },
            { "Long Blade Proficiency", "長刃精通" },
            { "Improved Aggressive Stance", "改良型侵略姿態" },
            { "Improved Defensive Stance", "改良防禦姿態" },
            { "Improved Dueling Stance", "改良型決鬥架勢" },
            { "En Garde!", "小心！" },
            { "Steady Hand", "穩定的手" },
            { "Weak Spotter", "弱點偵測者" },
            { "Sling and Run", "投石與奔跑" },
            { "Disarming Shot", "卸力射擊" },
            { "Dead Shot", "神射手" },
            { "Empty the Clips", "清空彈匣" },
            { "Fastest Gun in the Rust", "鏽蝕之地最快槍手" },
            { "Short Blade Expertise", "短刃專精" },
            { "Bloodletter", "血刃" },
            { "Pointed Circle", "尖銳圓環" },
            { "Rejoinder", "反擊" },
            { "Gadget Inspector", "裝置檢查員" },
            { "Lay Mine / Set Bomb", "佈雷 / 設置炸彈" },
            { "Wilderness Lore: Flower Fields", "荒野知識：花田" },
            { "Wilderness Lore: Marshes", "荒野知識：沼澤" },
            { "Wilderness Lore: Hills and Mountains", "荒野知識：丘陵 與 山脈" },
            { "Wilderness Lore: Canyons", "荒野知識：峽谷" },
            { "Wilderness Lore: Salt Dunes", "荒野知識：鹽丘群" },
            { "Wilderness Lore: Jungles", "荒野知識：叢林" },
            { "Wilderness Lore: Rivers and Lakes", "荒野知識：河流與湖泊" },
            { "Wilderness Lore: Ruins", "荒野知識：遺跡" },
            { "Tomorrowful", "明日之光" },
        };

    // 短文本翻譯（TMP/console 屬性/技能需求行等）：先整詞，再詞級（只替換白名單詞）
    // 短文本翻譯（TMP/console 屬性/技能需求行等）：先整詞，再三詞/雙詞/單詞級（只替換白名單詞）
    public static string TranslateTmpText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string zh;
        string trimmed = text.Trim();
        if (TmpWords.TryGetValue(trimmed, out zh)) return zh;
        // 詞級拆譯安全範圍（2026-08-13 事故教訓）：只對「含空格或 markup 特徵」的
        // 短文本執行（技能需求串「Cleave [50sp]」/「Requires Cleave」/「{{Y|Cleave}}」等）；
        // 純字母數位單詞（無空格且無 markup，如液體名 Water/Oil）不進詞級——液體初始化期
        // 的詞級替換曾三度誘發 BaseLiquid.Initialize NRE（875c7c1/兩次 \b 修正）。
        // 技能名/屬性名（純單詞）走上方 TryGetValue 已命中；超長文本直接返回。
        bool hasMarkupish = trimmed.IndexOf('{') >= 0 || trimmed.IndexOf('<') >= 0 ||
                            trimmed.IndexOf('[') >= 0 || trimmed.IndexOf('(') >= 0 ||
                            trimmed.IndexOf('|') >= 0;
        if (!hasMarkupish && (trimmed.IndexOf(' ') < 0 || trimmed.Length > 120)) return text;
        string r = text;
        // 多詞技能名優先（整詞邊界，避免被單詞拆散）
        foreach (var kv in TmpWords)
        {
            if (kv.Key.IndexOf(' ') > 0)
            {
                r = Regex.Replace(r, @"\b" + Regex.Escape(kv.Key) + @"\b", kv.Value);
            }
        }
        // 單詞級：只替換白名單/常規字典中的英文單詞（整詞邊界），保留數字/markup/已譯中文
        r = Regex.Replace(r, @"\b[A-Za-z][A-Za-z]*\b", delegate(Match m)
        {
            string w = m.Value;
            string t;
            if (TmpWords.TryGetValue(w, out t)) return t;
            t = TranslateWord(w);
            return string.IsNullOrEmpty(t) || t == w ? m.Value : t;
        });
        return r;
    }

    // 整詞翻譯（TMP/Unity UI 短文本用）：純英文短詞查 Words/Verbs/ProperNounZh，
    // 供角色面板屬性名（Strength→力量）、技能名（Cleave→劈砍）等 TMP 文本兜底。
    public static string TranslateWord(string word)
    {
        if (string.IsNullOrEmpty(word)) return word;
        string zh;
        if (Words.TryGetValue(word, out zh)) return zh;
        if (Verbs.TryGetValue(word, out zh)) return zh;
        if (ProperNounZh.TryGetValue(word, out zh)) return zh;
        return word;
    }

    // ===== 防漏層：關鍵詞/短語（dram/data disks/of 等）在 Clean 被繞過時仍生效 =====
    private static string TranslateKeyLeaks(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // 只處理「中英混雜」：純英文（internal ID/path）不該被 Words 中文污染，且省 200 alternation
        bool hasEng, hasCjk;
        ScanLang(text, out hasEng, out hasCjk);
        if (!hasEng || !hasCjk) return text;
        // 括號規範保護：文本含「中文(English)」結構（如顯示名 皮革護甲(Leather Armor) /
        // 咬顎獸食腐者(Snapjaw Scavenger)）時，括號英文是規範格式不該被逐詞拆譯。
        // 此為防漏層（KeyLeaks），直接跳過整段處理，避免括號內英文被 Words 拆成
        // 「皮革 護甲 / Snapjaw 拾荒者」。
        for (int i = 1; i < text.Length - 1; i++)
        {
            if ((text[i] == '(' || text[i] == '（') && text[i - 1] >= '\u4e00' && text[i - 1] <= '\u9fff')
                return text;
        }
        var tokenBox = new List<string>();
        string work = ProtectTokens(text, tokenBox);
        work = PhraseRegex.Replace(work, new MatchEvaluator(PhraseMatch));
        work = WordsRegex.Replace(work, new MatchEvaluator(WordsMatch));
        return RestoreTokens(work, tokenBox);
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

    // 載入優化 profile（2026-08-13，每 32768 次輸出一次）
    private static long ProfileCalls;
    private static long ProfileChanged;
    private static int ProfileBase;

    private static void ToStringProfileTick(string cur)
    {
        ProfileCalls++;
        if ((ProfileCalls & 0x7FFF) == 0)
        {
            if (ProfileBase == 0) ProfileBase = Environment.TickCount;
            ZhTwReplacers.LogAlways("ToStringProfile calls=" + ProfileCalls +
                " ms=" + (Environment.TickCount - ProfileBase) + " last=" + (cur.Length > 60 ? cur.Substring(0, 60) : cur));
        }
    }

    public static void ToStringPostfix(ref string __result)
    {
        if (string.IsNullOrEmpty(__result)) return;
        try
        {
            ToStringProfileTick(__result);
            // 零分配快篩（載入優化）：長「結構 ID / path 類」純 ASCII 字串（如 conversation
            // 組裝 ID）完全沒有可譯特徵（無 {{、無 =token=、無 [N]、無結構標點、無空格），
            // 直接跳過整條管線——避免數萬次呼叫的熱點成本（2026-08-13 載入調查）。
            if (__result.Length > 48)
            {
                bool structural = false;
                for (int i = 0; i < __result.Length; i++)
                {
                    char c = __result[i];
                    if (c < ' ' || c > '~')
                    {
                        structural = true; // 非 ASCII（含中文/控制字元）→ 進管線
                        break;
                    }
                }
                if (!structural &&
                    __result.IndexOf(' ') < 0 &&
                    __result.IndexOfAny(new char[] { '\u007b', '=', '[', ':', '!', '.', '-', '_', '&', '#' }) < 0)
                    return; // 純 ASCII、無空格、無結構標點 → 結構 ID，跳過
            }
            __result = PostfixCached(__result, ToStringProcess);
        }
        catch (Exception e)
        {
            ZhTwReplacers.LogAlways("ToStringPostfix EX: " + e.GetType().Name + " " + e.Message);
        }
    }

    // 快速路徑：純中文 → 已翻譯，直接回傳（最大宗，零成本）
    //            純英文 → 只跑英文訊息 frame + 方向句，跳過混雜才會用到的 Clean/KeyLeaks
    internal static string ToStringProcess(string text)
    {
        bool hasEng, hasCjk;
        ScanLang(text, out hasEng, out hasCjk);
        if (!hasEng) return text;
        if (!hasCjk)
        {
            // SentenceDict 整句（離線回填模板）優先：O(1) 精確匹配，零正則
            string sFull;
            if (SentenceDict.TryGetValue(text, out sFull)) return sFull;
            string tTrim = text.Trim();
            if (tTrim != text && SentenceDict.TryGetValue(tTrim, out sFull)) return sFull;
            text = TryStage(text, TranslateStatusFragments, "StatusFragments");
            text = TryStage(text, TranslateDirection, "Direction");
            // console 純英文短文本（技能需求「10 Agility」、屬性名等）→ 詞級白名單兜底
            if (text.Length <= 40)
            {
                string t2 = TranslateTmpText(text);
                if (t2 != text) text = t2;
            }
            return text;
        }
        text = TryStage(text, TranslateStatusFragments, "StatusFragments");
        text = TryStage(text, TranslateDirection, "Direction");
        text = TryStage(text, Clean, "Clean");
        return text;
    }

    // 診斷：LogLeaks 已移除（2026-08-13）：執行期掃描影響載入效能；漏譯覆蓋改由離線 run_pipeline 管線負責。

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

    // ===== SentenceDict：整句精確匹配翻譯（2026-08-13，離線管線回填）=====
    // 由 run_pipeline（extract_msg_templates → 本地 LLM → backfill_sentences）生成，
    // 只含「無變量完整句」；查表 O(1)，在 Clean 之前；缺失時照舊走既有管線。
    private static readonly Dictionary<string, string> SentenceDict =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
        { "\" contains '|', attempting to use multiple types", "「」包含「|」，嘗試使用多種類型" },
        { "\". Not deleting old file in case some old mods still use the original locations.", "「未刪除舊檔案，以防某些舊模組仍在使用原始位置。」" },
        { "% chance to stop cardiac arrest.", "停止心臟驟停的機率百分比。" },
        { "% chance to stop time for two turns when", "當發生時，有 % 的機率停止時間兩回合" },
        { "%}}!}} Do you want to stop travelling?", "%}}!}} 你想要停止旅行嗎？" },
        { "&RYou must collect more junk! (minimum:", "&R你必須收集更多垃圾！（最低需求：" },
        { "&y engulfing you melts through the floor! You fall to the level below.", "&y 吞噬著你的東西融穿了地板！你掉落到了下一層。" },
        { "' does not exist, falling back", "「不存在，正在回退」" },
        { "' does not inherit from 'Water', falling back", "「」並未繼承自「Water」，正在回退" },
        { "', direction must be -1f or 1f", "', 方向必須為 -1f 或 1f" },
        { "(press =commandKey:CmdAbilities= to use activated abilities)", "（按 =commandKey:CmdAbilities= 使用已啟動的能力）" },
        { ") is considered unique, are you sure you want to create another?", ") 被視為獨一無二的，你確定要再建立一個嗎？" },
        { "*=subject.name:withTitles|strip= broken*", "*=subject.name:withTitles|strip= 破碎的*" },
        { "*broke free*", "掙脫束縛" },
        { "+5% XP gained", "獲得經驗值 +5%" },
        { ", once thought lost to the sands of time.", "，曾一度被認為已湮沒於時光的流沙之中。" },
        { ", using stopgap unmutate procedure", "，正在使用權宜變異程序 (stopgap unmutate procedure)" },
        { "-Use Ability", "使用能力" },
        { "25% chance to gain +1 Strength, Agility, Toughness, Intelligence, Willpower, and Ego permanently.", "有 25% 的機率永久獲得 +1 力量(Strength)、敏捷(Agility)、韌性(Toughness)、智力(Intelligence)、意志(Willpower) 與 自我(Ego)。" },
        { "35% chance to fall prone when moving.", "移動時有 35% 的機率倒地。" },
        { "<chargeninfo> no longer parsed, use <extrainfo>", "<chargeninfo> 不再進行解析，請使用 <extrainfo>" },
        { "<create new>", "〈建立新項目〉" },
        { "=adjunct|initCap.if#cap= of =useThat.if:that:those=", "=adjunct|initCap.if#cap= 的 =useThat.if:that:those=" },
        { "=chance=% chance of stopping ranged attacks. =part.statusSummary|when#notTinkering|addParens=", "=chance=% 的機率阻止遠程攻擊。=part.statusSummary|when#notTinkering|addParens=" },
        { "=multiple.if:Those items aren't:That item isn't= broken!", "=multiple.if:那些物品並非：該物品並非=損壞的！" },
        { "=number.cardinal.no= =subject.pluralName:useUnknown=", "=number.cardinal.no= 個 =subject.pluralName:useUnknown=" },
        { "=number= =subject.pluralName:useUnknown=", "=number= 個 =subject.pluralName:useUnknown=" },
        { "=spice.entity:name= lost control of =spice.entity:possessivePronoun= chariot and drove it off a cliff", "=spice.entity:name= 失去了對 =spice.entity:possessivePronoun= 戰車的控制，並將其開下了懸崖" },
        { "=subject.Does:begin= =effect.name|strip= from another wound!", "=subject.Does:begin= 從另一個傷口中 |strip=！" },
        { "=subject.Does:begin= acting like =subject.it.is= =effect.name|strip= from another wound.", "=subject.Does:begin= 正試圖從另一個傷口中 =effect.name|strip=。" },
        { "=subject.Does:begin= acting like =subject.it.is= =effect.name|strip=.", "=subject.Does:begin= 表現得像 {0} 一樣。" },
        { "=subject.Does:emit= a flaming ray from =subject.its= =part.ordinalName=!", "=subject.Does:emit= 一道火焰射線自 =subject.its= =part.ordinalName=!" },
        { "=subject.Does:emit= a flaming ray!", "=subject.Does:emit= 發射出一道火焰射線！" },
        { "=subject.Does:emit= a freezing ray from =subject.its= =part.ordinalName=!", "=subject.Does:emit= 一道冰凍射線自 =subject.its= =part.ordinalName=!" },
        { "=subject.Does:emit= a freezing ray!", "=subject.Does:emit= 發射了一道冰凍光線！" },
        { "=subject.Does:emit= a grinding noise.", "=subject.Does:emit= 發出磨碎聲。" },
        { "=subject.Does:emit= an electromagnetic pulse.", "=subject.Does:emit= 一道電磁脈衝。" },
        { "=subject.Does:fall= =object.tag:FallPreposition:down= =object.the.or.a.name#useDefinite=.", "=subject.Does:fall= 從 =object.tag:FallPreposition:down= =object.the.or.a.name#useDefinite=. 落下" },
        { "=subject.Does:fall= asleep!", "=subject.Does:fall= 睡著了！" },
        { "=subject.Does:fall= to the ground.", "=subject.Does:fall= 摔向地面。" },
        { "=subject.Does:fall= to the ground; you pick =subject.them= up.", "=subject.Does:fall= 到地面；你撿起了 =subject.them=。" },
        { "=subject.Does:fall= to the ground; you scoop =subject.them= up.", "=subject.Does:fall= 到地面；你將 =subject.them= 撿了起來。" },
        { "=subject.Does:gain= =xp|rules= XP!", "=subject.Does:gain= =xp|rules= 經驗值(XP)！" },
        { "=subject.Does:gain= a level!", "=subject.Does:gain= 等級了！" },
        { "=subject.Does:stop= releasing =gasdisplayname=.", "=subject.Does:stop= 停止釋放 =gasdisplayname=。" },
        { "=subject.It.is= missing =mainSummary.andList=, and so =subject.it.does:do= not have the use of =abstractSummary.orList=.", "=subject.It.is= 缺少 =mainSummary.andList=，因此 =subject.it.does:do= 無法使用 =abstractSummary.orList=。" },
        { "=subject.Itis= broken...", "=subject.Itis= 壞掉了⋯⋯" },
        { "=subject.T= =verb:fall= apart.", "=subject.T= 崩解了。" },
        { "=subject.The.name's:withTitles= brain begins to hemorrhage.", "=subject.The.name's:withTitles= 的大腦開始出血。" },
        { "=subject.The.name's:withTitles= core begins to leak.", "=subject.The.name's:withTitles= 的核心開始滲漏。" },
        { "=subject.The.name's:withTitles= mental mirror shatters!", "=subject.The.name's:withTitles= 的精神鏡像破碎了！" },
        { "=subject.The.name's:withTitles= nose begins to bleed.", "=subject.The.name's:withTitles= 的鼻子開始流血。" },
        { "=subject.does:emit= a powerful ray of flame.", "=subject.does:emit= 一道強大的火焰射線。" },
        { "=subject.does:emit= a powerful ray of frost.", "=subject.does:emit= 一道強大的寒霜射線。" },
        { "=subject.it.does:emit= a ray of flame per Flaming Ray at rank 5-6.", "在「火焰射線(Flaming Ray)」等級達到 5-6 時，=subject.it.does:emit= 每發射一條火焰射線。" },
        { "=subject.it.does:emit= a ray of frost per Freezing Ray at rank 5-6.", "在「冰凍射線(Freezing Ray)」等級達到 5-6 時，=subject.it.does:emit= 一道寒霜射線。" },
        { "=subject.it.does:gain= +6 AV for 50 turns.", "=subject.it.does:gain= +6 AV，持續 50 回合。" },
        { "=subject.it.does:gain= +8 Agility for 50 turns.", "=subject.it.does:gain= +8 敏捷，持續 50 回合。" },
        { "=subject.it.does:gain= +8 Strength for 50 turns.", "=subject.it.does:gain= 在 50 回合內獲得 +8 力量。" },
        { "=subject.it.does:gain= 125-175 Cold Resist for 6 hours.", "=subject.it.does:gain= 125-175 寒冷抗性，持續 6 小時。" },
        { "=subject.it.does:gain= 125-175 Electric Resist for 50 turns.", "=subject.it.does:gain= 125-175 電能抗性(Electric Resist)，持續 50 回合。" },
        { "=subject.it.does:gain= 125-175 Heat Resist for 50 turns.", "=subject.it.does:gain= 125-175 點耐熱值，持續 50 回合。" },
        { "=subject.it.does:gain= 40-50 Cold Resist for 6 hours.", "=subject.it.does:gain= 40-50 寒冷抗性(Cold Resist)，持續 6 小時。" },
        { "=subject.it.does:gain= 40-50 Electric Resist for 6 hours.", "=subject.it.does:gain= 40-50 電能抗性(Electric Resist)，持續 6 小時。" },
        { "=subject.it.does:gain= 40-50 Heat Resist for 6 hours.", "=subject.it.does:gain= 40-50 點耐熱(Heat Resist)，持續 6 小時。" },
        { "=subject.it.does:stop= bleeding.", "=subject.it.does:停止=出血。" },
        { "=subject.they= must be wielded =twoSlots.if:four:two=-handed by non-gigantic creatures", "若非巨型生物，=subject.they= 必須以雙手（=twoSlots.if:four:two=-handed）方式持握。" },
        { "=turns= is not a valid number of turns to wait.", "=turns= 不是有效的等待回合數。" },
        { "=user.The.name's.item:includeAdjunctNoun#subject= =subject.verb:emit= a shower of sparks!", "=user.The.name's.item:includeAdjunctNoun#subject= 噴發出一陣火花！" },
        { "=user.The.name's:includeAdjunctNoun#subject= =subject.verb:shower= sparks everywhere.", "=user.The.name's:includeAdjunctNoun#subject= =subject.verb:shower= 到處迸發火花。" },
        { "=usesCharge.if:When powered, ==pen.signed= penetration vs. walls.", "=usesCharge.if:當通電時，==pen.signed= 對牆壁的穿透力。" },
        { "=usesCharge.if:When powered, destroys:Destroys= walls after =hitCount.things:penetrating hit=.", "=usesCharge.if:當通電時，會摧毀：摧毀= 牆壁，穿透性打擊後可摧毀 =hitCount.things:次。=" },
        { "Activate Force Emitter", "啟動力場發射器(Force Emitter)" },
        { "Activate Stopsvaalinn", "啟動 Stopsvaalinn (Stopsvaalinn)" },
        { "Activate Stopsvalinn", "啟動 Stopsvalinn(Stopsvalinn)" },
        { "ActivatedAbilities - Gained new ability", "已啟動的能力 — 獲得新能力" },
        { "ActivatedAbilities - Gained new ability - Hasn't used ability menu much appendix", "已啟動的能力 — 獲得新能力 — 尚未頻繁使用能力選單附錄" },
        { "ActivatedAbilityEntry cant be used", "無法使用已啟用的能力 (ActivatedAbilityEntry)" },
        { "ActivatedAbilityEntry cant be used - cooldown", "無法使用 {0} — 冷卻中" },
        { "ActiveLightSource Condition Use", "使用主動光源條件 (ActiveLightSource Condition)" },
        { "ActivePartStatus (should not be used)", "ActivePartStatus（不應使用）" },
        { "Affected creatures act semi-randomly and receive a -{{rules|", "受影響的生物會表現得近乎隨機，並獲得 -{{rules|" },
        { "After a one-round warmup, you emit a pulse with radius", "經過一輪暖身後，你會發出半徑為 {0} 的脈衝波" },
        { "Airfoil: This item can be thrown at +4 throwing range.", "翼型(Airfoil)：此物品投擲距離增加 +4。" },
        { "All the Leaves are Grey", "萬葉皆灰" },
        { "Animate spec part subtype must be RenderString, Tile, ColorString, DetailColor, FirstFrame, or LastFrame, had '", "動畫規格部件的子類型必須為 RenderString、Tile、ColorString、DetailColor、FirstFrame 或 LastFrame，但卻是「'{0}'」" },
        { "ArkCore Broken Popup", "ArkCore 損壞彈出視窗" },
        { "Asleep Effect WakeSleeper v2", "睡眠效果 喚醒者 v2 (WakeSleeper v2)" },
        { "AutoAct Wait Loading Status Bar", "自動行動等待載入狀態列" },
        { "AutoAct Wait Loading Status Complete", "自動動作等待載入狀態完成" },
        { "AutoAct Wait Status Bar Done Resting", "自動行動等待狀態列 完成 休息中" },
        { "AutogetBlacklist StopAutogettingItem Announcement", "自動獲取黑名單 (AutogetBlacklist) 停止自動獲取物品公告 (StopAutogettingItem Announcement)" },
        { "BeginBeingUnequippedEvent - Append FailureMessage", "開始卸除裝備事件 — 附加失敗訊息" },
        { "Beginning world build for seed:", "種子值的世界生成開始：" },
        { "Beguiling failure lost contest", "魅惑失敗，輸掉了競賽" },
        { "Body Lost Use Of Part", "身體部位喪失功能" },
        { "Body Recover Use Of Part", "身體部位回收使用" },
        { "BrainBrineCurse Effect Gain Defect Fail Player Popup", "腦部鹽水詛咒（BrainBrineCurse）效果獲得缺陷失敗玩家彈出視窗" },
        { "BrainBrineCurse Effect Gain Defect Player Popup", "腦鹽詛咒（BrainBrineCurse）效果獲得缺陷玩家彈出視窗" },
        { "BrainBrineCurse Effect Gain Mutation Fail Player Popup", "大腦鹽水詛咒 (BrainBrineCurse) 效果獲得突變失敗玩家彈出視窗" },
        { "BrainBrineCurse Effect Gain Mutation Player Popup", "腦鹽詛咒 (BrainBrineCurse) 效果獲得突變玩家彈出視窗" },
        { "BrainBrineCurse Effect Gain Skill Player Popup", "腦部鹽水詛咒 (BrainBrineCurse) 效果獲得技能玩家彈出視窗" },
        { "Bricks Can Be Thrown, Too", "磚塊也可以投擲" },
        { "Broken Effect DisplayName", "破碎效果 (Broken Effect)" },
        { "Broken Effect DisplayName Tag", "損壞效果顯示名稱標籤" },
        { "Broken Effect Equip Fail Player Message", "裝備效果失效：玩家訊息" },
        { "Broken Effect Particle Text", "破碎效果粒子文字" },
        { "Brown sludge splashes into your mouth. You wince at the metallic taste.", "棕色的黏液濺入你的口中。你因那股金屬味而皺起眉頭。" },
        { "But I must leave you if you won't concede", "但如果你不肯讓步，我必須離開你。" },
        { "Can use =mutation= at level =tier=.", "可在等級 =tier= 使用 =mutation=。" },
        { "Can use =mutationName= at rank =addedTier=.", "可在等級 =addedTier= 使用 =mutationName=。" },
        { "Can use =mutationName= at rank =tier=.", "可在等級 =tier= 使用 =mutationName=。" },
        { "Can use =skillName=.", "可以使用 =skillName=。" },
        { "Chance of becoming lost adjusted by =min.range: to #max=%. =part.statusSummary|addParens=", "迷失機率受 =min.range: 調整至 #max=%。=part.statusSummary|addParens=" },
        { "Chance of becoming lost in =skill.terrain= adjusted by =min.range: to #max=%. =part.statusSummary|addParens=", "在 =skill.terrain= 中迷路的機率已調整，範圍為 =min.range: 至 #max=%。=part.statusSummary|addParens=" },
        { "Chance of becoming lost in =skill.terrain= increased by =min.range#max=%. =part.statusSummary|addParens=", "在 =skill.terrain= 中迷路的機率增加了 =min.range#max=%。=part.statusSummary|addParens=" },
        { "Chance of becoming lost in =skill.terrain= reduced by =min.range#max=%. =part.statusSummary|addParens=", "在 =skill.terrain= 中迷路的機率降低了 =min.range#max=%。 =part.statusSummary|addParens=" },
        { "Chance of becoming lost in certain terrain adjusted by =min.range: to #max=%. =part.statusSummary|addParens=", "在特定地形中迷路的機率已根據 =min.range: 調整至 #max=%。=part.statusSummary|addParens=" },
        { "Chance of becoming lost in certain terrain increased by =min.range#max=%. =part.statusSummary|addParens=", "在特定地形中迷路的機率增加 =min.range#max=%。 =part.statusSummary|addParens=" },
        { "Chance of becoming lost in certain terrain reduced by =min.range#max=%. =part.statusSummary|addParens=", "在特定地形中迷路的機率降低了 =min.range#max=%。 =part.statusSummary|addParens=" },
        { "Chance of becoming lost in other terrain adjusted by =min.range: to #max=%. =part.statusSummary|addParens=", "在其他地形中迷路的機率已根據 =min.range: 調整至 #max=%。=part.statusSummary|addParens=" },
        { "Chance of becoming lost in other terrain increased by =min.range#max=%. =part.statusSummary|addParens=", "在其他地形迷路的機率增加 =min.range#max=%。 =part.statusSummary|addParens=" },
        { "Chance of becoming lost in other terrain reduced by =min.range#max=%. =part.statusSummary|addParens=", "在其他地形迷路的機率降低了 =min.range#max=%。=part.statusSummary|addParens=" },
        { "Chance of becoming lost increased by =min.range#max=%. =part.statusSummary|addParens=", "迷失機率增加 =min.range#max=%。 =part.statusSummary|addParens=" },
        { "Chance of becoming lost reduced by =min.range#max=%. =part.statusSummary|addParens=", "迷失機率降低了 =min.range#max=%。 =part.statusSummary|addParens=" },
        { "Chargen no last character to use popup", "角色創建時未選取最後一個角色，不顯示彈出視窗" },
        { "Chills affected area over", "寒冷影響範圍已結束" },
        { "Choose artifacts to throw down the well.", "選擇要投入井中的神器。" },
        { "Core LostSightOf", "核心 LostSightOf" },
        { "Core Wait Command", "核心等待指令" },
        { "Core Wait Command Popup Title", "核心等待指令彈出視窗標題" },
        { "Core Wait Turns - Ask number popup", "核心等待回合數 — 詢問數量彈出視窗" },
        { "Core Wait Turns - Bottom Status Bar", "核心等待回合數 — 底部狀態欄" },
        { "Core Wait Turns - Invalid Number (negative)", "核心等待回合數 — 無效數字（負值）" },
        { "Could not create parent paths", "無法建立父層路徑" },
        { "Couldn't get user from IUserService, halting setup.", "無法從 IUserService 取得使用者，正在停止設定。" },
        { "Crushing Falling", "粉碎性墜落 (Crushing Falling)" },
        { "Cudgel (dazes on critical hit)", "槌棒（暴擊時造成暈眩）" },
        { "Deactivate Force Emitter", "停用力場發射器(Force Emitter)" },
        { "DecoyHologram Emitter Appear Message", "誘餌全息投影發射器 (DecoyHologram Emitter) 出現訊息" },
        { "DecoyHologram Emitter Disappear Message", "誘餌全息投影發射器 (Decoy Hologram Emitter) 消失訊息" },
        { "DecoyHologram Emitter Disappear Message Other", "誘餌全息投影發射器 (Decoy Hologram Emitter) 消失訊息 其他 (Other)" },
        { "DeployableInfrastructure Broken Fail", "可部署基礎設施 (Deployable Infrastructure) 損壞失敗" },
        { "DeployableInfrastructure No Useful Way", "可部署基礎設施 (DeployableInfrastructure) 無法有效使用" },
        { "Directory For Archive File is not created. Creating it..", "尚未建立封存檔案目錄。正在建立中⋯⋯" },
        { "Disassembly Message, nothing gained", "拆解訊息，未獲得任何物品" },
        { "Distance 1: creatures receive a {{rules|", "距離 1：生物會受到 {{rules|" },
        { "Distance 4: creatures receive a {{rules|", "距離 4：生物會受到 {{rules|" },
        { "Distance 7: creatures receive a {{rules|", "距離 7：生物會受到 {{rules|" },
        { "Do you want to continue despite being unable to use Psychometry?", "儘管無法使用靈媒感應(Psychometry)，你仍要繼續嗎？" },
        { "Do you want to stop travelling?", "你確定要停止旅行嗎？" },
        { "Domination broken", "支配(Domination)失效了" },
        { "Done waiting.", "等待結束。" },
        { "Duration between use and reversion: {{rules|", "使用與恢復之間的時間間隔：{{rules|" },
        { "EmbarkIntroTextID is deprecated because Text.txt is deprecated, use embarkIntroText", "EmbarkIntroTextID 因 Text.txt 已棄用，請改用 embarkIntroText" },
        { "Emits plumes of fire when the wearer moves while power skating.", "穿戴者在進行動力滑行（power skating）移動時，會噴發出火焰煙柱。" },
        { "FactionInterest (should never be used except mods, and river is untranslated)", "派系興趣 (FactionInterest)" },
        { "FactionInterest (should never be used except mods, and tagList is untranslated)", "派系興趣 (FactionInterest)" },
        { "Fall in love with a sign.", "愛上一個標誌。" },
        { "Fall in love with yourself.", "愛上你自己。" },
        { "FlamingRay Emit", "火焰射線 (FlamingRay) 發射" },
        { "FlamingRay emit", "火焰射線(FlamingRay)發射" },
        { "FlamingRay emit no part", "火焰射線 (FlamingRay) 未發射任何部分" },
        { "Force Emitter", "力場發射器 (Force Emitter)" },
        { "ForceEmitter AbilityName Activate Fallback", "力場發射器 {AbilityName} 啟動備援方案" },
        { "ForceEmitter AbilityName Deactivate Fallback", "力場發射器 {AbilityName} 停用備案" },
        { "Free Falling", "自由落體 (Free Falling)" },
        { "FreezingRay emit", "冰凍射線 (Freezing Ray) 發射" },
        { "FreezingRay emit no part", "冰凍射線 (Freezing Ray) 未發射任何部分" },
        { "GALAXY - Authenticating user.", "GALAXY — 正在驗證使用者。" },
        { "GALAXY - Authentication succeeded, requesting user stats.", "銀河系 (GALAXY) — 身分驗證成功，正在請求使用者統計數據。" },
        { "GALAXY - Connection lost.", "銀河系 (GALAXY) — 連線中斷。" },
        { "GALAXY - User stats retrieved.", "銀河系 — 已擷取使用者統計數據。" },
        { "Gesticulating: This item grants +=bonus= =strength.Name= but disallows the use of the Floating Nearby equipment slot.", "手勢動作(Gesticulating)：此物品賦予 +=bonus= 點 =strength.Name=，但禁止使用「漂浮近身(Floating Nearby)」裝備欄位。" },
        { "Get lost chance:", "迷失機率：" },
        { "HUD: When powered and started up, this item can be used with a smartgun to enable its bonuses.", "當此物品通電並啟動後，可與智能槍(smartgun)搭配使用以啟用其加成效果。" },
        { "HistoricEvent LoseItemAtTavern gospel", "歷史事件：在酒館遺失物品——福音 (gospel)" },
        { "HistoricEvent LoseItemAtTavern item", "歷史事件：在酒館遺失物品 {0}" },
        { "Hmm. This injector has been used.", "嗯。這個注射器已經使用過了。" },
        { "How many turns would you like to wait?", "你想要等待多少回合？" },
        { "How odd, the broken haft of a weapon, a sign of recent violence.", "真奇怪，武器的柄部斷裂了，這顯示不久前曾發生過暴力衝突。" },
        { "I couldn't choose a retreat point or going to flee from my worst enemy so I'm going to wait.", "我無法選擇撤退點，也無法從我最強大的敵人身邊逃離，所以我打算原地等待。" },
        { "I found the village stores of Bey Lah untouched; Eskhind must have traded something for her basics.", "我發現貝萊(Bey Lah)的村莊商店未經觸動；艾斯金德(Eskhind)一定是拿了某樣東西來交換她的生活必需品。" },
        { "I must pass on this offer, for now. Live and drink.", "我現在必須拒絕這個提議。活下去，盡情飲酒吧。" },
        { "I'm going to stop pursuing my target.", "我將停止追蹤目標。" },
        { "I'm going to throw!", "我要投擲了！" },
        { "I'm going to try to use abilities suitable for retreating.", "我將嘗試使用適合撤退的能力。" },
        { "I'm going to try to use my healing location.", "我打算嘗試使用我的治療地點。" },
        { "It looks like an awfully long fall. Are you sure you want to jump into the shaft?", "這看起來是個非常長的墜落。你確定要跳進這個豎井嗎？" },
        { "It's a chem cell. It can be used later to power other artifacts you find.", "這是一個化學電池。之後可以用來為你發現的其他神器提供動力。" },
        { "Item Stopsvalinn", "物品停止選擇" },
        { "Item Use Failure", "物品使用失敗" },
        { "Kith and Kin, Imporant NPC died popup", "親族 (Kith and Kin)，重要 NPC 已死亡。" },
        { "Light scatters off the surface of the wide and yawning pool and gets lost in its depths.", "光線在寬闊且深邃的池塘表面散射，隨即消失於其深處。" },
        { "Liquid Volume - seal but broken message", "液體容量 — 密封但已破損" },
        { "LiquidVolume - seal broken", "液體容量 (LiquidVolume) — 密封破損" },
        { "LoseItemAtTavern::Generate could not find an entity with name=", "LoseItemAtTavern::Generate 無法找到名稱為 =name= 的實體" },
        { "Lost parent effect reference, removing", "移除遺失父級效果引用" },
        { "Lost parent part reference, removing", "遺失父層部分引用，正在移除" },
        { "Mercurial: Teleports the user to safety upon taking damage", "變幻莫測(Mercurial)：受到傷害時將使用者傳送至安全地點" },
        { "Mercurial: Teleports the user to safety upon taking damage (=chance=% chance).", "變幻莫測(Mercurial)：受到傷害時會將使用者傳送到安全地點（=chance=% 機率）。" },
        { "Mercurial: Teleports the user to safety upon taking damage.", "變幻莫測(Mercurial)：受到傷害時，將使用者傳送至安全地點。" },
        { "Missile Combat Throw", "投擲遠程戰鬥" },
        { "Mod defining manual load order, please convert it to use the Dependencies field.", "請將定義手動載入順序的模組轉換為使用「依賴項目(Dependencies)」欄位。" },
        { "ModMagnetic Falls Down", "模組：磁力墜落 (ModMagnetic Falls Down)" },
        { "ModMagnetic Falls Down Picked Up", "模組磁力墜落已拾取" },
        { "Move Direction Until Stopped", "移動方向直到停止" },
        { "MultiNavigationBonus LostIncreased AllTerrain", "多重導航加成 (MultiNavigationBonus) 失去 全地形能力提升 (Increased AllTerrain)" },
        { "MultiNavigationBonus LostIncreased CertainTerrain", "多重導航加成 (MultiNavigationBonus) 損失 (Lost) 增加特定地形確信度 (Increased CertainTerrain)" },
        { "MultiNavigationBonus LostIncreased OtherTerrain", "多重導航加成 (MultiNavigationBonus) 損失 (Lost) 增加其他地形 (Increased OtherTerrain)" },
        { "MultiNavigationBonus LostIncreased SpecificTerrain", "多重導航加成 (MultiNavigationBonus) 損失 增加特定地形 (Increased SpecificTerrain)" },
        { "MultiNavigationBonus LostRandom AllTerrain", "多重導航加成 (MultiNavigationBonus) 遺失隨機全地形 (LostRandom AllTerrain)" },
        { "MultiNavigationBonus LostRandom CertainTerrain", "多重導航加成 (MultiNavigationBonus) 失去隨機特定地形 (LostRandom CertainTerrain)" },
        { "MultiNavigationBonus LostRandom OtherTerrain", "多重導航加成 (MultiNavigationBonus) 失去隨機其他地形 (LostRandom OtherTerrain)" },
        { "MultiNavigationBonus LostRandom SpecificTerrain", "多重導航加成 (MultiNavigationBonus) 失去隨機特定地形 (LostRandom SpecificTerrain)" },
        { "MultiNavigationBonus LostReduced AllTerrain", "多重導航加成 (MultiNavigationBonus) 失去 (Lost) 減少 (Reduced) 全地形 (AllTerrain)" },
        { "MultiNavigationBonus LostReduced CertainTerrain", "多重導航加成 (MultiNavigationBonus) 遺失，減少了特定地形 (CertainTerrain) 的效果" },
        { "MultiNavigationBonus LostReduced OtherTerrain", "多重導航加成 (MultiNavigationBonus) 失去 (Lost) 減少其他地形 (Reduced OtherTerrain)" },
        { "MultiNavigationBonus LostReduced SpecificTerrain", "多重導航加成 (MultiNavigationBonus) 失去 (Lost) 減少特定地形 (Reduced SpecificTerrain)" },
        { "Must be completely filled with", "必須完全填滿於" },
        { "Mutation Use Fail BodyPart Too Damaged", "變異使用失敗：身體部位損壞過於嚴重" },
        { "Mutation Use Fail BodyPart Too Damaged (null)", "變異使用失敗：身體部位損壞過於嚴重 ({0})" },
        { "No missile weapons I can use, I'll try a different weapon...", "沒有我能使用的遠程武器，我會嘗試換一種武器……" },
        { "Normally, one uses one's telekinesis to augment throwing. Toggle this ability to control this behavior.", "通常，玩家會使用念力來強化投擲。切換此能力可控制此行為。" },
        { "NorthSheva - Failure message, can't leave world map on this square - Ruined Stop", "北謝瓦(NorthSheva) — 失敗訊息，無法在此格離開世界地圖 — 毀滅之停(Ruined Stop)" },
        { "Now knowing I must go and you must stay", "既然我必須離去，而你必須留下" },
        { "Part CreateObjectOnHit Activation", "部分 CreateObjectOnHit 啟動" },
        { "Part EmitGasOnHit Activation Each", "部位 命中時釋放氣體 (EmitGasOnHit) 啟動 次數" },
        { "Part EmitGasOnHit Activation Main", "命中時釋放氣體部件 (EmitGasOnHit) 啟動 主 (Main)" },
        { "Penning Bonus: =bonus= Max: =maxbonus= Used: =used= Target: =target= (Penned =pens.things:time=)", "書寫加成：=bonus= 最大值：=maxbonus= 已使用：=used= 目標：=target=（已書寫 =pens.things:time=）" },
        { "Physical Skill Throwing Item", "物理技能 投擲物品" },
        { "PortableWall default DeployingWhat (used in title bar when picking tiles)", "正在部署 {0}" },
        { "Pounder: Receives +1 to its to-hit and penetration rolls for every", "重擊者(Pounder)：每增加一個，其命中與穿透判定獲得 +1 修正值" },
        { "PoweredFloating CheckFloating Fall Announce", "動力漂浮檢查漂浮落下宣告" },
        { "PoweredFloating CheckFloating FallScoop Announce", "動力漂浮檢查 (PoweredFloating Check) 漂浮落下 (Floating Fall) 舀取宣告 (Scoop Announce)" },
        { "Quantum reverb: When fired, this weapon creates a hologram of its wielder who continues to fire along the same path.", "量子回響 (Quantum Reverb)：開火時，此武器會產生持有者的全息投影，並沿著相同的路徑持續射擊。" },
        { "Quills DefenderHit Quills Broke", "刺針防禦者(Quills Defender) 擊中刺針，刺針斷裂" },
        { "RandomlyMutation gain mutation spentPointsReport fragment", "隨機突變獲得突變消耗點數報告片段" },
        { "Reflects incoming projectiles and thrown objects at a", "反射飛來的投射物與投擲物。" },
        { "Reflects incoming projectiles and thrown objects at a =chance=% chance.", "有 =chance=% 的機率反射飛來的投射物與投擲物。" },
        { "Reloading strings, please wait...", "正在重新載入字串，請稍候..." },
        { "Reputation attribute obsolete, should use <reputation> nodes instead.", "聲望屬性已過時，應改用 <reputation> 節點。" },
        { "Resheph Gospel (dies)", "雷舍夫福音 (Resheph Gospel)（死亡）" },
        { "Return from Golgotha with a repaired waydroid and gain admittance to Grit Gate.", "從哥德巴 (Golgotha) 返回，帶著修復好的路德羅伊德 (waydroid)，並獲得進入格里特之門 (Grit Gate) 的許可。" },
        { "RocketSkates EmitFlamePlume", "火箭滑板 (RocketSkates) 噴射火焰羽流 (EmitFlamePlume)" },
        { "Run Melee Hit Stop", "執行近戰擊退 (Melee Hit Stop)" },
        { "STEAM - Requesting user stats.", "STEAM - 正在請求使用者統計數據。" },
        { "STEAM - User stats retrieved.", "STEAM — 已取得使用者統計數據。" },
        { "SanityCheck::No falling objects on pulldowns", "SanityCheck::下拉時無掉落物" },
        { "Saving Throw Vs", "對抗 {0} 的救難檢定" },
        { "Saving Throw Vs (List)", "對抗 ({0}) 的存檔檢定 (Saving Throw)" },
        { "Saving Throw Vs (_default_)", "對抗 (_default_) 的拯救檢定" },
        { "Saving Throw Vs (liquid)", "對（液體）的豁免檢定" },
        { "Saving Throw Vs Axe", "對抗斧頭的豁免檢定" },
        { "Saving Throw Vs Beam", "對光束的救難檢定 (Saving Throw Vs Beam)" },
        { "Saving Throw Vs Blade", "對抗刃器的豁免檢定" },
        { "Saving Throw Vs Bleeding", "流血抗性檢定" },
        { "Saving Throw Vs Contact", "接觸判定 (Saving Throw Vs Contact)" },
        { "Saving Throw Vs Cudgel", "對抗棍棒(Cudgel)的救難檢定" },
        { "Saving Throw Vs Decarbonizer", "對抗脫碳劑(Decarbonizer)的豁免檢定" },
        { "Saving Throw Vs Disarm", "解械豁免檢定 (Saving Throw Vs Disarm)" },
        { "Saving Throw Vs Disease Onset", "對抗疾病發作的豁免檢定" },
        { "Saving Throw Vs Drag", "對拖拽的救難檢定" },
        { "Saving Throw Vs EMP", "對電磁脈衝 (EMP) 的豁免檢定" },
        { "Saving Throw Vs Escape", "逃脫檢定 (Saving Throw Vs Escape)" },
        { "Saving Throw Vs Fungal", "對抗真菌(Fungal)的豁免檢定" },
        { "Saving Throw Vs Gas", "對抗毒氣的豁免檢定" },
        { "Saving Throw Vs Gaze", "對注視的豁免檢定" },
        { "Saving Throw Vs Grab", "對抗抓取(Grab)的救難檢定" },
        { "Saving Throw Vs Hologram", "對抗全息投影(Hologram)的救難檢定" },
        { "Saving Throw Vs HookAndDrag", "對抗鉤引拖拽(HookAndDrag)的豁免檢定" },
        { "Saving Throw Vs Inhaled", "對吸入物的豁免檢定" },
        { "Saving Throw Vs Injected", "對抗注射物之豁免檢定" },
        { "Saving Throw Vs Knockdown", "對抗擊倒的豁免檢定" },
        { "Saving Throw Vs LatchOn", "對抗附著(LatchOn)的救難檢定" },
        { "Saving Throw Vs Lithofex", "對抗石化獸(Lithofex)的豁免檢定" },
        { "Saving Throw Vs LongBlades", "長劍(LongBlades)豁免檢定" },
        { "Saving Throw Vs Move", "移動對抗救骰" },
        { "Saving Throw Vs Onset", "對抗失衡(Onset)的救難檢定" },
        { "Saving Throw Vs Phase", "相位(Phase)豁免檢定" },
        { "Saving Throw Vs Pistol", "對抗手槍的救難檢定" },
        { "Saving Throw Vs Restraint", "對抗束縛的救難檢定" },
        { "Saving Throw Vs Rifle", "步槍(Rifle)豁免檢定" },
        { "Saving Throw Vs RobotStop", "對抗機器人停止(RobotStop)的救難檢定" },
        { "Saving Throw Vs ShieldSlam", "對盾擊(ShieldSlam)的豁免檢定" },
        { "Saving Throw Vs ShortBlades", "對抗短劍(Short Blades)的豁免檢定" },
        { "Saving Throw Vs Sleep", "睡眠豁免檢定 (Saving Throw Vs Sleep)" },
        { "Saving Throw Vs Slip", "對滑倒的豁免檢定" },
        { "Saving Throw Vs SlogGlands", "對抗黏液腺(SlogGlands)的豁免檢定" },
        { "Saving Throw Vs Stoning", "石化豁免檢定 (Saving Throw Vs Stoning)" },
        { "Saving Throw Vs Stuck", "對抗卡住的豁免檢定" },
        { "Saving Throw Vs Stun", "眩暈豁免檢定 (Saving Throw Vs Stun)" },
        { "Saving Throw Vs StunningForce", "對抗眩暈力(StunningForce)的豁免檢定" },
        { "Saving Throw Vs Swipe", "對抗揮擊的豁免檢定 (Saving Throw Vs Swipe)" },
        { "Saving Throw Vs Taunt", "對嘲諷(Taunt)的豁免檢定" },
        { "Saving Throw Vs Tinkering", "對抗修理(Tinkering)的救難檢定" },
        { "Saving Throw Vs Verbal", "口頭指令豁免檢定 (Saving Throw Vs Verbal)" },
        { "Saving Throw Vs Web", "對抗蛛網的豁免檢定" },
        { "Settlements Generate Farm Name fallback nameRoot", "定居點生成農場名稱備用名稱Root" },
        { "Skills attribute obsolete, should use <skill> nodes instead.", "技能屬性已過時，應改用 <skill> 節點。" },
        { "Snapjaw Hero Stopsvaalinn", "快咬者(Snapjaw)英雄 斯托普斯瓦林(Stopsvaalinn)" },
        { "StairsDown FallDown", "下樓梯 FallDown" },
        { "StairsDown FallDown v2", "樓梯向下 FallDown v2" },
        { "StairsDown You fall downward", "向下樓梯 (StairsDown) 你向下墜落" },
        { "Stomach died of thirst", "胃部因口渴而死亡" },
        { "Stopsvaalinn Command Label", "史托普斯瓦林(Stopsvaalinn)指令標籤" },
        { "Stopsvaalinn Not Enough Charge", "史托普斯瓦林(Stopsvaalinn)能量不足" },
        { "Stopsvaalinn Silent Fail", "史托普斯瓦林(Stopsvaalinn) 無聲失敗" },
        { "Stopsvaalinn Snapoff Message", "Stopsvaalinn (史托普斯瓦林) Snapoff 訊息" },
        { "Stopsvaalinn Snapoff Message Other", "Stopsvaalinn (Stopsvaalinn) 脫落訊息 其他" },
        { "Stopsvaalinn Success", "史托普斯瓦林(Stopsvaalinn)成功" },
        { "Stopsvaalinn Success Other", "史托普斯瓦林(Stopsvaalinn)成功 其他" },
        { "Stopsvaalinn Suspended Message", "史托普斯瓦林(Stopsvaalinn)暫停訊息" },
        { "Stopsvaalinn Techscanning NameForStatus", "史托普斯瓦林(Stopsvaalinn) 技術掃描名稱用於狀態 {0}" },
        { "Stream must be seekable", "串流必須可隨機搜尋" },
        { "StunningForce Use Message Other Away", "震懾力 (StunningForce) 使用訊息：將其他目標擊退" },
        { "StunningForce Use Message Other Nearby", "震懾力(StunningForce) 使用訊息 其他附近目標" },
        { "StunningForce Use Message Player Away", "震盪力(Stunning Force) 使用訊息：將玩家擊退" },
        { "StunningForce Use Message Player Nearby", "震懾力 (Stunning Force) 使用訊息：玩家在附近" },
        { "T must be an enumerated type", "T 必須是一個列舉類型" },
        { "Take =received.abs=% =received.threshold:0:more:less= damage", "受到 {=received.abs=% =received.threshold:0:more:less=} 點傷害" },
        { "Telekinetic Throwing", "念力投擲 (Telekinetic Throwing)" },
        { "TeleportGate Lost Popup", "傳送門(TeleportGate)遺失彈出視窗" },
        { "TemplarPhylactery CreateObject DisplayName", "聖騎士命匣(Templar Phylactery) 建立物件 顯示名稱" },
        { "Testing all replacers... please wait, this will take a second...", "正在測試所有替換項……請稍候，這需要一點時間……" },
        { "Testing lang replacers... please wait, this will take a second...", "正在測試語言替換器……請稍候，這需要一點時間……" },
        { "That stop is too ruined for the mover to land.", "該停靠點毀損過於嚴重，移動者無法降落。" },
        { "There is no one there to use", "那裡沒有可以使用的對象" },
        { "There is no one there you can use", "那裡沒有你可以使用的對象" },
        { "There is no useful way to", "沒有任何有效的方法可以" },
        { "There is no useful way to =verb= =object.the.name:single= there.", "那裡沒有任何有效的方法可以 =verb= =object.the.name:single=。" },
        { "There is no valid last character to use.", "沒有可使用的有效最後字元。" },
        { "This creature burns bright in the chord of", "此生物在……的和弦中閃耀著光芒" },
        { "This leather bracer has seen some use. Why is it out here?", "這件皮革護腕有些使用過的痕跡。為什麼它會出現在這裡？" },
        { "This target must not be invoked in a synchronous way.", "此目標不得以同步方式進行召喚。" },
        { "This zone isn't building properly. Do you want to force it to stop and build immediately?", "此區域無法正常建造。你是否要強制停止並立即開始建造？" },
        { "Thrown PseudoThrown Vorpal Cudgel", "投擲偽旋轉鋒利大棒 (PseudoThrown Vorpal Cudgel)" },
        { "Thrown Weapon", "投擲武器" },
        { "Thrown Weapons", "投擲武器" },
        { "Thrown Weapons,Grenades,Melee Weapons,Light Sources", "投擲武器、手榴彈、近戰武器、光源" },
        { "Timereaver: This weapon has =chance.a.number=% chance to stop time for two turns when =subject.it= =subject.verb:hit=.", "時光掠奪者(Timereaver)：當擊中 =subject.it= 時，此武器有 =chance.a.number=% 的機率使時間停止兩回合。" },
        { "Tinker Recharge Used", "已使用修理工充能 (Tinker Recharge)" },
        { "Too far to target for a throw, I'll try a different weapon...", "距離目標太遠，無法投擲，我試試看其他武器⋯⋯" },
        { "Too many mutation sync attempts", "突變同步嘗試次數過多" },
        { "Unable to progress until all hearts have stopped (current:", "必須等到所有心臟停止跳動才能繼續進行（目前：{0}）" },
        { "Unit Gain Experience", "單位獲得經驗值" },
        { "Unit Gain Level", "單位等級提升" },
        { "VillageBase CreateVillageFaction faction display name", "村莊基地 (VillageBase) 建立村莊派系 (CreateVillageFaction) {0} 派系顯示名稱 (faction display name)" },
        { "Wait 100 Turns", "等待 100 回合" },
        { "Wait 20 Turns", "等待 20 回合" },
        { "Wait N Turns", "等待 {0} 回合" },
        { "Wait Until Healed", "等待痊癒" },
        { "Wait Until Morning", "等待天亮" },
        { "Wait Until Party Healed", "等待隊伍治療完成" },
        { "Waiting for =num.things:turn=...", "等待 =num.things:turn=..." },
        { "Waiting for =remaining.things:round=...", "等待剩餘的 {0} 回合..." },
        { "Waiting for =turns.things:turn=...", "等待 =turns.things:turn=..." },
        { "Waiting for party leader", "等待隊長中" },
        { "What do we gain from =e2=", "我們從 =e2= 獲得了什麼？" },
        { "What name should be used for your", "請問你的名字是？" },
        { "What objective pronoun (him, her, them, etc.) should be used for this", "Since you haven't provided the specific English text yet, I cannot give you the exact translation. However, in **Caves of Qud** localization, we handle objective pronouns based on the context of the sentence: 1. **If it refers to a specific NPC/Entity:** We usually translate the action directly or use \"其\" (formal) or \"他/她\" (informal). 2. **If it is a generic tooltip (e.g., \"Attack him\"):** We often use \"攻擊目標\" (Attack target) or simply \"攻擊\" (Attack) to keep it natural in Chinese UI, rather than forcing a gendered pronoun. **Please provide the English string you want me to translate.**" },
        { "What possessive adjective (his, her, their, etc.) should be used for this", "In English grammar, the choice of a possessive adjective depends on the **gender** or **number** of the antecedent (the person/thing being referred to). Since you are translating a video game UI, here is how you should handle it based on the context: ### 1. If referring to a specific character (Gendered) If the tooltip refers to a single entity with a known gender: * **His:** 他的 (tā de) * **Her:** 她的 (tā de) * **Its (for creatures/objects):** 它的 (tā de) ### 2. If referring to \"The Player\" or an unknown entity (Gender-neutral) In modern English, \"their\" is often used as a singular gender-neutral pronoun. In Traditional Chinese, **「他的」** is often used as a default for \"his/her/their\" in technical writing, but if you want to be strictly neutral: * **Their (Singular/Neutral):** 其 (qí) — *This is very common in game UI/tooltips because it is short and formal.* * Example: \"Use their ability\" $\rightarrow$ 「使用其能力」 ### 3. If referring to a group (Plural) * **Their:** 他們的 (tā men de) --- ### Summary Table for your Translation Task | English Context | Recommended zh-TW | Usage Note | | :--- | :--- | :--- | | **His / Her / Its** | **其** | **Best for UI/Tooltips.** It is concise, formal, and avoids gender issues. | | **His / Her** | **他的 / 她的** | Use only if the character's gender is a vital part of the text. | | **Their (Plural)** | **他們的** | Use when referring to a group of enemies or NPCs. | **Professional Tip for *Caves of Qud*:** Because *Caves of Qud* has many strange creatures and mutated beings, I highly recommend using **「其」 (qí)** for most possessive tooltips. It sounds \"legendary/ancient\" and avoids the awkwardness of assigning a human gender to a sentient cloud of gas or a multi-limbed mutant. **Example:** * English: \"Increases his strength.\" * Standard: 「增加他的力量。」 (A bit too personal) * **UI Style: 「增加其力量。」 (Professional/Clean)**" },
        { "What reflexive pronoun (himself, herself, themself, themselves, etc.) should be used for this", "In English grammar, the choice of a reflexive pronoun depends on the **gender** and **number** of the antecedent (the person or thing being referred to). Here is a breakdown of which one to use: ### 1. Singular Pronouns (One Person) * **Himself:** Use when the subject is male. * *Example:* \"He taught **himself** how to play the guitar.\" * **Herself:** Use when the subject is female. * *Example:* \"She bought **herself** a new car.\" * **Itself:** Use when the subject is an object, animal, or abstract concept. * *Example:* \"The machine shut **itself** off.\" * **Themself:** This is increasingly used as a **singular gender-neutral** pronoun (when you don't know the person's gender or they prefer neutral pronouns). * *Note:* While common in spoken English and becoming more accepted in writing, some strict traditionalists still prefer \"themselves\" even for singular subjects. * *Example:* \"Someone left **themself** a note.\" ### 2. Plural Pronouns (Multiple People/Things) * **Themselves:** Use when the subject is plural (more than one person or thing). * *Example:* \"They organized the party **themselves**.\" * *Example:* \"The cats groomed **themselves**.\" --- ### Summary Table | Subject | Reflexive Pronoun | Context | | :--- | :--- | :--- | | **He** | **Himself** | Male | | **She** | **Herself** | Female | | **It** | **Itself** | Object/Animal | | **They (Singular)** | **Themself** / **Themselves** | Gender-neutral singular | | **They (Plural)** | **Themselves** | Multiple people/things | ### Quick Rule of Thumb: If you are writing for a game like *Caves of Qud* where characters can be any gender or even non-human, the safest and most modern approach is to use **themselves** (as a singular neutral) or rephrase the sentence to avoid the pronoun entirely if you want to be strictly formal." },
        { "What subjective pronoun (he, she, they, etc.) should be used for this", "In the context of translating a video game like *Caves of Qud*, which features many non-humanoid creatures, sentient plants, and genderless beings, you should follow these professional localization standards: ### 1. The \"Neutral\" Approach (Recommended) Since Traditional Chinese does not have grammatical gender in pronouns (unlike English), the translation itself is naturally gender-neutral. Instead of worrying about \"he/she/they,\" focus on the **contextual noun** or a **neutral pronoun**. * **If referring to a player/NPC:** Use **「他」** (He/Him) as the default universal pronoun in Chinese localization, OR use the character's name/title. * **If referring to an object/creature:** Use **「它」** (It). * **If referring to a group:** Use **「他們」** (They - inclusive/masculine) or **「它們」** (They - for objects/animals). ### 2. Specific Translation Strategies for *Caves of Qud* Because *Qud* is highly surreal, follow these rules based on the entity type: | Entity Type | English Pronoun | Recommended Chinese Strategy | Example | | :--- | :--- | :--- | :--- | | **Humanoid NPC** | He / She | Use **「他」** (Universal) or name. | \"He attacks\" $\rightarrow$ 「他發動了攻擊」 | | **Non-humanoid/Monster** | It | Use **「它」**. | \"It screeches\" $\rightarrow$ 「它發出尖叫」 | | **Group of Creatures** | They | Use **「它們」** (if non-human) or **「他們」** (if sentient). | \"They approach\" $\rightarrow$ 「它們正在靠近」 | | **Ambiguous/Eldritch** | They / It | Use the **Noun** directly to avoid awkwardness. | \"They consume\" $\rightarrow$ 「該生物正在吞噬」 | ### 3. Summary for your Workflow When you encounter English tooltips with pronouns: 1. **If it's a generic tooltip (e.g., \"He is wounded\"):** Translate as **「它受傷了」** (if referring to a creature) or simply omit the pronoun if possible to sound more natural in Chinese (e.g., 「受到傷害」). 2. **If it's a dialogue line:** Use **「他」** for people and **「它」** for monsters. 3. **Avoid \"Gendered\" confusion:** In Traditional Chinese localization, if the gender is unknown or irrelevant to the gameplay mechanic, **「他」** is the standard professional default for \"person/entity,\" and **「它」** is the standard for \"creature/object.\" **Rule of thumb for your task:** If the English text says \"He uses {0}\", translate it as **「{0} 使用了能力」** (omitting the pronoun entirely) to make it sound like professional, high-quality UI." },
        { "What substantive possessive (his, hers, theirs, etc.) should be used for this", "In the context of translating a video game UI like *Caves of Qud*, the choice of possessive pronoun depends entirely on whether you are translating a **label** (which usually omits pronouns) or a **descriptive tooltip/sentence**. Since you are asking about \"substantive possessives\" (his, hers, theirs), here is how to handle them in Traditional Chinese (zh-TW): ### 1. If it's a UI Label (Short & Natural) In Chinese UI design, we almost **never** use possessive pronouns like \"his\" or \"their.\" Instead, we use the noun itself or omit the pronoun entirely to keep it clean. * **English:** \"His Strength\" / \"Their Abilities\" * **Bad Translation:** 「他的力量」(Too wordy/clunky) * **Good Translation (Natural):** 「力量」(Strength) or 「能力」(Abilities) ### 2. If it's a Tooltip/Description (Contextual) If the text is a sentence describing an item or effect, you must choose based on the **subject** of the sentence. Chinese does not have gendered pronouns in the same way English does for objects, but it uses different characters for people. * **His/Her (Singular Person):** Use 「他」(He) or 「她」(She). * *Example:* \"Increases his speed\" $\rightarrow$ 「增加其速度」(Using 「其」 is more formal/elegant for games) or 「增加他的速度」. * **Their (Plural People/Gender Neutral):** Use 「他們」(They) or 「其」(Its/Their - formal). * *Example:* \"Their power grows\" $\rightarrow$ 「他們的威力增強了」. * **Its (Non-human/Object):** Use 「它的」 or the formal 「其」. * *Example:* \"Increases its damage\" $\rightarrow$ 「增加其傷害」. ### 3. The \"Professional Game Dev\" Secret: 「其」(Qí) For a professional, high-quality feel in Traditional Chinese localization (especially for RPGs like *Caves of Qud*), use the formal pronoun **「其」**. It acts as a universal possessive (\"its\", \"his\", \"her\", or \"their\") and sounds much more \"epic\" and polished than the colloquial 「他的」 or 「他們的」. **Comparison Table for your Translation Task:** | English Context | Colloquial (Avoid) | **Professional/Formal (Use this)** | | :--- | :--- | :--- | | **His/Her/Its [Noun]** | 他的/她的/它的 [名詞] | **其 [名詞]** | | **Their [Noun]** | 他們的 [名詞] | **其 [名詞]** | **Example Application:** * English: \"Increases his mutation rate.\" * Colloquial: 「增加他的突變率。」 (Sounds like a casual chat) * **Professional:** 「增加其突變率。」 (Sounds like a professional game UI)" },
        { "What term should be used for a mature person of this", "此種族之成年者" },
        { "What term should be used for an immature person of this", "未成年者" },
        { "What term should be used to address a person of this", "應如何稱呼此類人士？" },
        { "What term should be used to formally address a person of this", "應如何正式稱呼此類人士？" },
        { "Whatever =object.they= =object.is=, =object.it.is= broken...", "無論 =object.they= 是什麼，或是 =object.it.is= 壞掉了..." },
        { "Whenever you perform the water ritual with a new creature, you gain an extra", "每當你與新的生物進行水之儀式時，你將獲得額外的" },
        { "Wings Confirm long fall", "翅膀確認長距離墜落" },
        { "XP Gain Message", "經驗值獲得訊息" },
        { "You appeased a baetyl with =demand= and in return received =reward=.", "你以 =demand= 安撫了一位貝提爾(Baetyl)，並以此換取了 =reward=。" },
        { "You are drying out! Do you want to stop travelling?", "你正在脫水！要停止旅行嗎？" },
        { "You are dying of thirst! Do you want to stop travelling?", "你正渴死！你想停止旅行嗎？" },
        { "You are not in cardiac arrest. Do you want to use", "你並未處於心臟驟停狀態。你是否要使用" },
        { "You are stopped short by", "你被以下對象攔住了：" },
        { "You broke apart.", "你破碎了。" },
        { "You can't gain", "無法獲得" },
        { "You can't rename a recipe that someone else created.", "你無法重新命名他人所建立的配方。" },
        { "You can't use", "無法使用" },
        { "You cannot use the ingredient!", "你無法使用該材料！" },
        { "You died from =source|article=.", "你死於 =source|article=。" },
        { "You died of thirst.", "你死於口渴。" },
        { "You do not have a thrown weapon equipped.", "你沒有裝備投擲武器。" },
        { "You don't know how to use", "你不知道如何使用" },
        { "You emit a ray of flame from your", "你從你的{0}發射出一道火焰光束" },
        { "You emit a ray of frost from your", "你從你的 {0} 發射出一道寒霜光束" },
        { "You fall down a deep shaft!", "你掉進了一個深井！" },
        { "You fall downward!", "你向下墜落！" },
        { "You gain =amount|color:C= XP!", "你獲得了 {0} 點經驗值！" },
        { "You gain access to every schematic of", "你獲得了所有 {0} 的設計圖" },
        { "You gain access to the", "你獲得了使用 {0} 的權限" },
        { "You gain the skill", "你獲得了技能" },
        { "You gain {{C|", "你獲得 {{C|" },
        { "You gained the defect =mutation.name|color:R=!", "你獲得了缺陷 =mutation.name|color:R=!" },
        { "You gained the defect {{R|", "你獲得了缺陷 {{R|" },
        { "You gained the mutation =mutation.name|color:G=!", "你獲得了突變 {0}=mutation.name|color:G=!" },
        { "You gained the mutation {{G|", "你獲得了突變 {{G|" },
        { "You gained {{C|", "你獲得了 {{C|" },
        { "You have contracted glotrot! Your tongue begins to bleed as the muscle rots away.", "你感染了黏液腐爛症(glotrot)！隨著肌肉腐爛，你的舌頭開始出血。" },
        { "You have lost the use of your", "你失去了使用 {0} 的能力" },
        { "You have lost the use of your =part.ordinalName=.", "你失去了使用 =part.ordinalName= 的能力。" },
        { "You have received a new quest,", "你收到了一個新任務，" },
        { "You have recovered the use of your", "你已恢復使用你的" },
        { "You have recovered the use of your =part.ordinalName=.", "你已恢復使用你的 =part.ordinalName=。" },
        { "You invoke a concussive force in a nearby area, throwing enemies back and stunning them.", "你在附近區域引發一股衝擊力，將敵人擊退並使其陷入暈眩狀態。" },
        { "You muse over the =numShared.pluralize:secret= with =object.name|strip= and gain some insight.", "你沉思著關於 =object.name|strip= 的 =numShared.pluralize:secret=，並獲得了一些啟示。" },
        { "You must =verb= at least =range.cardinal.things:square=!", "你必須至少 =verb= =range.cardinal.things:square=!" },
        { "You must call Init() before applying this to an object", "在將此套用至物件之前，必須先呼叫 Init()" },
        { "You must charge at a target!", "你必須衝向目標！" },
        { "You must charge at least", "你必須至少充能" },
        { "You must have a cudgel equipped in order to use slam.", "你必須裝備棍棒(cudgel)才能使用重擊(slam)。" },
        { "You must have a cudgel equipped in your primary hand to conk.", "你必須在主手中裝備棍棒(cudgel)才能進行敲擊(conk)。" },
        { "You must have a cudgel equipped in your primary hand to demolish things.", "你必須在主手中裝備棍棒(cudgel)才能拆除物品。" },
        { "You must have a long blade equipped in your primary hand to lunge.", "你必須在主手中裝備長刃才能進行突刺。" },
        { "You must have a long blade equipped to effectively yell out 'En garde!'", "你必須裝備長刃，才能有效地大喊「小心(En garde!)」。" },
        { "You must have an axe equipped in your primary hand to dismember.", "你必須在主手中裝備斧頭才能進行肢解。" },
        { "You must have an axe equipped in your primary hand to use Hook and Drag.", "你必須在主手中裝備斧頭才能使用鉤引拖拽(Hook and Drag)。" },
        { "You must have an axe or a weapon capable of dismemberment equipped in order to perform a field amputation.", "你必須裝備斧頭或具備肢解能力的武器，才能進行野戰截肢。" },
        { "You must make a selection before advancing.", "你必須先進行選擇才能繼續。" },
        { "You must select a location within =range.things:tile=!", "你必須在 =range.things:tile=! 範圍內選擇一個位置" },
        { "You must wait", "你必須等待" },
        { "You must wait =turns.things:turn= before using =object.this= again.", "你必須等待 {0} 回合才能再次使用 =object.this=。" },
        { "You must wait {{C|", "你必須等待 {{C|" },
        { "You muster your will and shake off some of your confusion.", "你凝聚意志，擺脫了部分困惑。" },
        { "You muster your will and shake off your confusion.", "你凝聚意志，擺脫了困惑。" },
        { "You receive", "你獲得了" },
        { "You receive tinkering bits <=bittype.bits=>", "你獲得了 <=bittype.bits=> 零件。" },
        { "You receive tinkering bits <{{|", "你獲得了修補零件 <{{|" },
        { "You stop calling this location '", "你不再稱呼這個地點為「{0}」" },
        { "You stopped calling a location '", "你不再稱呼某個地點為「{0}」" },
        { "You strain to part the veil of time in order to use psychometry on", "你竭力撥開時間的面紗，以便對 {0} 使用靈媒感應(psychometry)" },
        { "You strain to part the veil of time in order to use psychometry on =object.the.name:single=, but you are too confused.", "你竭力撥開時間的面紗，試圖對 =object.the.name:single= 使用靈媒感應(psychometry)，但你的思緒太過混亂。" },
        { "You suddenly feel ready to use", "你突然感到準備就緒，可以使用了" },
        { "You think you broke", "你以為你壞掉了" },
        { "You think you broke =object.them=...", "你以為你弄壞了 =object.them=⋯" },
        { "You're lost! Regain your bearings by exploring your surroundings.", "你迷路了！透過探索周遭環境來重新找回方向。" },
        { "Your domination is broken!", "你的支配已崩潰！" },
        { "Your genome destabilized and you gained the", "你的基因組變得不穩定，你獲得了" },
        { "Your genome destabilizes and you gain", "你的基因組變得不穩定，你獲得了" },
        { "Your larva gestated and you gained the", "你的幼蟲孵化了，你獲得了" },
        { "Your mind begins to morph but the physiology of your brain restricts it.", "你的心智開始發生形變，但大腦的生理結構限制了這種變化。" },
        { "Your tongue begins to bleed as the muscle rots away.", "隨著肌肉腐爛，你的舌頭開始流血。" },
        { "Your torch burns out!", "你的火把熄滅了！" },
        { "Zone WindChange stops", "區域風向變化停止" },
        { "[ {{W|Choose where to use a dram of", "[ {{W|選擇使用一小杯的位址" },
        { "brain begins to hemorrhage.", "大腦開始出血。" },
        { "core begins to leak.", "核心開始滲漏。" },
        { "could not create contents for cryochamber:", "無法為冷凍艙(cryochamber)建立內容：" },
        { "died of asphyxiation", "死於窒息" },
        { "died of poison", "死於中毒" },
        { "from falling rocks! {{", "來自落石！{{" },
        { "gaining mutations", "獲得突變" },
        { "infinite loop in license upgrade wedge use", "升級許可證楔形區使用時發生無限迴圈" },
        { "lose interest", "失去興趣" },
        { "mental mirror shatters!", "精神鏡像破碎了！" },
        { "my heart scarce knows where to begin", "我的心不知該從何說起。" },
        { "nose begins to bleed.", "鼻子開始流血。" },
        { "one of your wounds is an illusion, and the pain from it suddenly stops", "其中一個傷口是幻覺，隨即停止了痛楚。" },
        { "received invalid item", "收到無效物品" },
        { "sfx_npc_level gain", "NPC 等級提升音效" },
        { "spec part type must be Stat, Save, Property, or Animate, had '", "規格部分類型必須為 Stat、Save、Property 或 Animate，卻得到「" },
        { "templatevar Name should begin with a lower case character now. \"", "變數名稱 {0} 現在應以小寫字母開頭。" },
        { "used in some indeterminate fashion", "以某種不明的方式使用" },
        { "your wound is an illusion, and the pain suddenly stops", "你的傷口只是幻覺，疼痛突然停止了。" },
        { "{lost masterwork pistol,", "{遺失的傑作手槍，" },
        { "|[begin water ritual; {{", "|[開始水之儀式；{{" },
        { "}} (must have the appropriate Tinker skill).", "}}（必須具備相應的修補(Tinker)技能）。" },
        { "}} to use that ability again.", "}} 以再次使用該能力。" },
        { "}}% chance that Sprint and skills with Agility prerequisites don't go on cooldown after use", "有 {{0}}% 的機率在使用「衝刺(Sprint)」與具備敏捷(Agility)前置需求的技能後，不會進入冷卻時間。" },
        { "}}. How many do you want to use?", "}}. 你想使用多少個？" },

        };

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
        // combat 整句輸出後，武器段殘留的「with your X」剝除所有格（用 你的 青銅匕首 → 用 青銅匕首）
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"用 (?:你的|the|a|an) ", "用 ");
        return text;
    }

    // 翻譯訊息整句/片段（message 路徑，含戰鬥/烹飪/XDidYToZ frame）
    private static string TranslateStatusFragments(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // 便宜預過濾：無 {{ 模板且無任何 frame 動詞 → 37 個正則不會命中，直接跳過；
        // 長文本（>300）非訊息句，跳過（載入優化 2026-08-13）
        if (text.Length > 300) return text;
        if (text.IndexOf("{{", StringComparison.Ordinal) < 0 && !FrameTrigger.IsMatch(text) && !CombatLeakHint.IsMatch(text))
            return text;
        text = TranslateDisplayNameFragments(text);
        // ===== 戰鬥/動作整句（在 Clean 逐詞破壞前翻譯，避免詞序壞掉）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+(?:critically\s+)?hit\s+\((.+?)\)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)!?\s*\[(.+?)\]$",
            "你用 $3 擊中($1)，造成 $2 傷害[$4]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+hit\s+(.+?)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)!?\s*\[(.+?)\]$",
            "你用 $3 擊中 $1，造成 $2 傷害[$4]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // ===== DoesZh 已轉換的玩家 hit（主詞「擊中」開頭，補回「你」；=object.the.name= 目標名）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*擊中\s+\((.+?)\)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)!?\s*\[(.+?)\]$",
            "你用 $3 擊中($1)，造成 $2 傷害[$4]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*擊中\s+\((.+?)\)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)[.!]?$",
            "你用 $3 擊中($1)，造成 $2 傷害", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*擊中\s+(.+?)\s+\((.+?)\)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)!?\s*\[(.+?)\]$",
            "你用 $4 擊中 $1($2)，造成 $3 傷害[$5]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*擊中\s+(.+?)\s+\((.+?)\)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)[.!]?$",
            "你用 $4 擊中 $1($2)，造成 $3 傷害", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*擊中\s+(.+?)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)!?\s*\[(.+?)\]$",
            "你用 $3 擊中 $1，造成 $2 傷害[$4]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*擊中\s+(.+?)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)[.!]?$",
            "你用 $3 擊中 $1，造成 $2 傷害", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+(?:critically\s+)?hit\s+\((.+?)\)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)[.!]?$",
            "你用 $3 擊中($1)，造成 $2 傷害", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?)\s+hit\s+(.+?)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)\.?\s*\[(.+?)\]$",
            "$1 用 $4 擊中 $2，造成 $3 傷害[$5]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+miss(?:ed)?\s+with\s+(.+?)[.!]?\s*\[(.+?)\]$",
            "你未擊中（用 $1）[$2]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+miss(?:ed)?\s+with\s+(.+?)[.!]?$",
            "你未擊中（用 $1）", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+miss(?:ed)?\s+(.+?)[.!]?\s*\[(.+?)\]$",
            "你未擊中 $1[$2]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // X misses Y with Z → X 未擊中 Y（用 Z）
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(?:The\s+)?(.+?)\s+misses\s+(.+?)\s+with\s+(.+?)[.!]?\s*(\[(.*?)\])?$",
            "$1 未擊中 $2（用 $3）$4", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+toggle\s+(.+?)\s+(on|off)[.!]?$",
            "你將 $1 切換為$2", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = text.Replace("切換為on", "切換為開啟").Replace("切換為off", "切換為關閉");
        // ===== 死亡整句（The X dies → X 死亡；容許 :: 等訊息前綴）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9{]*(?:The\s+)?(.+?)\s+dies[.!]?$",
            "$1 死亡。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9{]*(?:The\s+)?(.+?)\s+died[.!]?$",
            "$1 死亡。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?)\s+is\s+dazed[.!]?$",
            "$1 感到暈眩。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?)\s+stands?\s+up[.!]?$",
            "$1 站起來了。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?)\s+takes?\s+(\d+)\s+damage\s+from\s+(.+?)[.!]?$",
            "$1 因 $3 受到 $2 傷害。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // ===== 移動受阻（There/由於 X 是 Y 在 your way；Harmony 同款，系統訊息路徑兜底）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(?:The\s+)?way\s+is\s+blocked\s+by\s+(.+?)[.!]?$",
            "道路被 $1 阻擋", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*There\s+is\s+(.+?)\s+in\s+(?:your|the|my|its)\s+way[.!]?$",
            "$1 擋在你面前。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*由於 ?(?:那裡|The|the)? ?是 ?(.+?) 在 ?你的 ?(?:way|路)[,，]? ?你(?:停止了|停止|停) ?移動(?:中)?[。.!]?$",
            "$1 擋住了你的路，你停止了移動。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // ===== 發射射線/噴吐（The X emits a … from Y）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(?:The\s+)?(.+?)\s+emit(?:s|ted)?\s+(?:a|an)\s+(.+?)\s+from\s+(?:its|his|her|它的|他的|她的)\s+(.+?)[.!]?$",
            "$1 從 $3 發出一道 $2。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // ===== 停止衝刺 / 開始衝刺 =====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(?:The\s+)?(.+?)\s+stop(?:s|ped)?\s+sprint(?:ing|ing)?[.!]?$",
            "$1 停止了衝刺。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(?:You|你)\s+stop(?:ped)?\s+sprint(?:ing)?[.!]?$",
            "你停止了衝刺。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // ===== 冷卻等待回合（You must wait N rounds）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(?:You|你)\s+must\s+wait(?:ing)?\s+(\d+)\s+rounds?(?:\s+\([^)]*\))?[.!]?$",
            "你必須等待 $1 回合。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(?:You|你)\s+must\s+wait\s+(\d+)\s+rounds?\s+回合[。.!]?$",
            "你必須等待 $1 回合。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+take\s+(\d+)\s+damage\s+from\s+(.+?)[.!]?$",
            "你因 $2 受到 $1 傷害。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // ===== 中英混合 combat（=subject.Does:hit= 已轉「擊中」，subject 在句首）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?)\s+擊中\s+\((.+?)\)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)[.!]?\s*(\[(.*?)\])?$",
            "$1 用 $4 擊中($2)，造成 $3 傷害$5", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?)\s+擊中\s+(.+?)\s+\((.+?)\)\s+for\s+(\d+)\s+damage\s+with\s+(.+?)[.!]?\s*(\[(.*?)\])?$",
            "$1 用 $5 擊中 $2($3)，造成 $4 傷害$6", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?)\s+擊中\s+for\s+(\d+)\s+damage\s+with\s+(.+?)[.!]?$",
            "$1 用 $3 擊中，造成 $2 傷害", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // ===== 中英混合受到（=verb:take= 已轉「受到」）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?)\s+受到\s+(\d+)\s+damage\s+from\s+(.+?)[.!]?$",
            "$1 因 $3 受到 $2 傷害。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*(.+?)\s+受到\s+(\d+)\s+damage[.!]?$",
            "$1 受到 $2 傷害。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // ===== 拾取/奪取（玩家主詞 token 為空，=verb:take= 已轉「受到」）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*受到\s+(?:the |a |an )?(.+?)\s+from\s+(.+?)[.!]?$",
            "你從 $2 拿走了 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*受到\s+(?:the |a |an )?(.+?)[.!]?$",
            "你拿起了 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // ===== 烹飪/香料整句（spice 未載入時的兜底）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+eat\s+the\s+meal[.!]?$",
            "你吃下了這份餐點。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+toss\s+(.+?)\s+into\s+a\s+pot\s+and\s+stir[.!]?$",
            "你將 $1 丟進鍋子裡並攪拌。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+gather\s+(.+?)\s+for\s+your\s+meal[.:]?$",
            "你收集了 $1 來當作餐點。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+toss\s+them\s+in\s+a\s+pot\s+and\s+stir[.!]?$",
            "你將它們丟進鍋子裡攪拌。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // ===== XDidYToZ frame 整句（物件名可能尚未本地化，frame 必先翻）=====
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+sit\s+down\s+on\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你坐到 $1 上。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+sit\s+down\s+in\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你坐到 $1 裡。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+climb\s+onto\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你爬上 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+jump\s+onto\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你跳到 $1 上。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+wade\s+through\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你涉水穿過 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+swim\s+through\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你游泳穿過 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+emerge\s+from\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你從 $1 現身。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+bump\s+into\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你撞到 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+bond\s+with\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你與 $1 締結聯繫。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+detach\s+from\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你從 $1 脫離。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+slip\s+away\s+from\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你從 $1 溜走。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+swap\s+positions\s+with\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你與 $1 交換位置。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+get\s+entangled\s+in\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你被 $1 纏住。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // 被動 frame（You are X by Y）
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+are\s+engulfed\s+by\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你被 $1 吞噬。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+are\s+dragged\s+toward\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你被拖向 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+are\s+sucked\s+into\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
            "你被吸入 $1。", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^[^\u4e00-\u9fffA-Za-z0-9]*You\s+are\s+impaled\s+by\s+(?:the\s+|a\s+|an\s+)?(.+?)[.!]?$",
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