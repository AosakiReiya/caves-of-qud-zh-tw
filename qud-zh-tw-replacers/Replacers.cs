// Replacers.cs — Qud 繁中動態文字 replacer（測試 v3）
// 機制（已由 DLL 反編譯確認）：
//   1. 類別需帶 [HasVariableReplacer] 才會被 VariableReplacers.LoadReplacers 掃描
//   2. 類別需帶 [HasModSensitiveStaticCache]，遊戲在 mod 載入時會重置其靜態快取，
//      且 [ModSensitiveCacheInit] 方法會被呼叫
//   3. 方法上的 [VariableReplacer(keys)] keys 需與遊戲實際使用的 key 一致
//      代名詞：possessive / their / its / possessiveAdjective / subjective / they / objective / reflexive / itself
//      動詞：verb（遊戲的 Verb 方法用無參建構子，key 取自方法名）
//      冠詞/名稱：a.name / an / the.name / the / a
// 診斷：所有動作寫入 replacer_log.txt

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using XRL;
using XRL.World;
using XRL.World.Anatomy;
using XRL.World.Text;
using XRL.World.Text.Attributes;
using XRL.World.Text.Delegates;

[HasVariableReplacer]
[HasModSensitiveStaticCache]
public static class ZhTwReplacers
{
    // ============ 診斷記錄（預設關閉，設環境變數 ZH_TW_REPLACER_LOG=1 才開啟）============

    private static readonly object LogLock = new object();
    private static int LogCount;
    private const int LogMax = 600;
    private const int LogFlushEvery = 50;
    private static readonly System.Text.StringBuilder LogBuffer = new System.Text.StringBuilder(4096);

    // 預設關閉：生產環境零開銷（不建字串、不寫檔、不呼叫 Debug.Log）
    private static readonly bool LoggingEnabled = !string.IsNullOrEmpty(
        Environment.GetEnvironmentVariable("ZH_TW_REPLACER_LOG"));

    private static string LogPath
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "LocalLow", "Freehold Games", "CavesOfQud", "replacer_log.txt");
        }
    }

    private static void FlushLog()
    {
        if (LogBuffer.Length == 0) return;
        try
        {
            File.AppendAllText(LogPath, LogBuffer.ToString());
            LogBuffer.Clear();
        }
        catch
        {
        }
    }

    public static void Log(string msg)
    {
        if (!LoggingEnabled) return;   // 預設關閉：直接早退，零開銷
        lock (LogLock)
        {
            if (LogCount >= LogMax) return;   // 超過上限：不再處理
            LogCount++;
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine;
            LogBuffer.Append(line);
            if (LogCount % LogFlushEvery == 0 || LogCount >= LogMax) FlushLog();
            try
            {
                UnityEngine.Debug.Log("[ZhTw] " + line.TrimEnd());
            }
            catch
            {
            }
        }
    }

    // 供外部（如遊戲回呼）在結束時呼叫，確保緩衝區寫出
    public static void FlushDiagnostics()
    {
        if (!LoggingEnabled) return;
        lock (LogLock)
        {
            FlushLog();
        }
    }

    // ============ 無條件診斷（生命週期/例外用，不依賴 ZH_TW_REPLACER_LOG）============
    // 用於 Init、Harmony patch 結果、例外堆疊——這些必須隨時可見，
    // 否則 catch{} 吞例外會讓「修A壞B」完全看不到原因。
    private static readonly object AlwaysLock = new object();
    private static int AlwaysCount;
    private const int AlwaysMax = 400;

    public static void LogAlways(string msg)
    {
        lock (AlwaysLock)
        {
            if (AlwaysCount >= AlwaysMax) return;
            AlwaysCount++;
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [ZH] " + msg;
            try
            {
                UnityEngine.Debug.Log("[ZhTw] " + line);
            }
            catch { }
            try
            {
                File.AppendAllText(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LocalLow", "Freehold Games", "CavesOfQud", "replacer_log.txt"),
                    line + Environment.NewLine);
            }
            catch { }
        }
    }

    static ZhTwReplacers()
    {
        try
        {
            LogAlways("=== ZhTwReplacers static ctor ===");
            if (LoggingEnabled)
            {
                try { File.Delete(LogPath); } catch { }
                Log("=== ZhTwReplacers static ctor OK ===");
            }
        }
        catch
        {
        }
    }

    // ============ mod 初始化鉤子（mod 載入時呼叫） ============

    [ModSensitiveCacheInit]
    public static void Init()
    {
        LogAlways("=== Init called ===");
        try
        {
            // 1. 列出所有 AddReplacer 多載（下一輪手動註冊用）
            foreach (MethodInfo mi in typeof(VariableReplacers).GetMethods())
            {
                if (mi.Name == "AddReplacer")
                {
                    string ps = string.Join(", ", Array.ConvertAll(mi.GetParameters(), p => p.ParameterType.FullName));
                    Log("AddReplacer(" + ps + ")");
                }
            }
            // 2. 列出 VariableReplacers 的靜態欄位（Map/PostMap 是否可存取）
            foreach (FieldInfo fi in typeof(VariableReplacers).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Log("VariableReplacers field " + fi.Name + " : " + fi.FieldType.FullName + " public=" + fi.IsPublic);
            }
        }
        catch (Exception e)
        {
            LogAlways("introspection error: " + e.GetType().Name + " " + e.Message);
        }
        try
        {
            // 3. 強制重掃 replacer（模組載入後）
            VariableReplacers.Reset();
            LogAlways("VariableReplacers.Reset() OK");
        }
        catch (Exception e)
        {
            LogAlways("Reset error: " + e.GetType().Name + " " + e.Message);
        }
        try
        {
            // 4. Harmony 補丁（硬編碼戰鬥訊息）
            ZhTwHarmonyPatches.Init();
            LogAlways("ZhTwHarmonyPatches.Init() done");
        }
        catch (Exception e)
        {
            LogAlways("Harmony patch error: " + e.GetType().Name + " " + e.Message + "\n" + e.StackTrace);
        }
    }

    // ============ 代名詞 ============

    private static string ZhPronounMap(string en, string kind)
    {
        string e = (en ?? "").ToLowerInvariant();
        switch (kind)
        {
            case "subjective":
                if (e == "he") return "他";
                if (e == "she") return "她";
                if (e == "they") return "他們";
                if (e == "you") return "你";
                return "它";
            case "possessive":
                if (e == "his") return "他的";
                if (e == "her") return "她的";
                if (e == "their") return "他們的";
                if (e == "your") return "你的";
                return "它的";
            case "objective":
                if (e == "him") return "他";
                if (e == "her") return "她";
                if (e == "them") return "他們";
                if (e == "you") return "你";
                return "它";
            case "substantive":
                if (e == "his" || e == "hers") return "他的";
                if (e == "theirs") return "他們的";
                if (e == "yours") return "你的";
                return "它的";
            case "reflexive":
                if (e == "himself") return "他自己";
                if (e == "herself") return "她自己";
                if (e == "themselves") return "他們自己";
                if (e == "yourself") return "你自己";
                return "它自己";
            default:
                return "";
        }
    }

    private static string ZhPronoun(GenderedNoun noun, string kind)
    {
        try
        {
            if (noun.Pronouns == null) return "";
            string en;
            switch (kind)
            {
                case "subjective": en = noun.Pronouns.Subjective; break;
                case "possessive": en = noun.Pronouns.PossessiveAdjective; break;
                case "objective": en = noun.Pronouns.Objective; break;
                case "substantive": en = noun.Pronouns.SubstantivePossessive; break;
                case "reflexive": en = noun.Pronouns.Reflexive; break;
                default: return "";
            }
            return ZhPronounMap(en, kind);
        }
        catch
        {
            return "";
        }
    }

    private static string ZhPronounObj(GameObject obj, string kind)
    {
        try
        {
            if (obj == null) return "";
            var p = obj.GetPronounProvider();
            if (p == null) return "";
            string en;
            switch (kind)
            {
                case "subjective": en = p.Subjective; break;
                case "possessive": en = p.PossessiveAdjective; break;
                case "objective": en = p.Objective; break;
                case "substantive": en = p.SubstantivePossessive; break;
                case "reflexive": en = p.Reflexive; break;
                default: return "";
            }
            return ZhPronounMap(en, kind);
        }
        catch
        {
            return "";
        }
    }

    [VariableReplacer(new string[] { "possessive", "their", "its", "possessiveAdjective" }, Default = "它的", Capitalization = true, Override = true)]
    public static string Possessive(VariableContext context, GenderedNoun noun)
    {
        string r = ZhPronoun(noun, "possessive");
        Log("CALL possessive -> '" + r + "'");
        return r;
    }

    [VariableReplacer(new string[] { "subjective", "they" }, Default = "它", Capitalization = true, Override = true)]
    public static string Subjective(VariableContext context, GenderedNoun noun)
    {
        string r = ZhPronoun(noun, "subjective");
        Log("CALL subjective -> '" + r + "'");
        return r;
    }

    [VariableReplacer(new string[] { "objective" }, Default = "它", Capitalization = true, Override = true)]
    public static string Objective(VariableContext context, GenderedNoun noun)
    {
        string r = ZhPronoun(noun, "objective");
        Log("CALL objective -> '" + r + "'");
        return r;
    }

    [VariableReplacer(new string[] { "substantivePossessive", "theirs" }, Default = "它的", Capitalization = true, Override = true)]
    public static string SubstantivePossessive(VariableContext context, GenderedNoun noun)
    {
        string r = ZhPronoun(noun, "substantive");
        Log("CALL substantivePossessive -> '" + r + "'");
        return r;
    }

    [VariableReplacer(new string[] { "reflexive", "itself" }, Default = "它自己", Capitalization = true, Override = true)]
    public static string Reflexive(VariableContext context, GenderedNoun noun)
    {
        string r = ZhPronoun(noun, "reflexive");
        Log("CALL reflexive -> '" + r + "'");
        return r;
    }

    // ============ 代名詞（GameObject 上下文，處理 his/its/their 等） ============

    [VariableReplacer(new string[] { "possessive", "their", "its", "possessiveAdjective" }, Default = "它的", Capitalization = true, Override = true)]
    public static string PossessiveObject(VariableContext context, GameObject obj)
    {
        string r = ZhPronounObj(obj, "possessive");
        Log("CALL possessive(obj) -> '" + r + "'");
        return r;
    }

    [VariableReplacer(new string[] { "subjective", "they" }, Default = "它", Capitalization = true, Override = true)]
    public static string SubjectiveObject(VariableContext context, GameObject obj)
    {
        string r = ZhPronounObj(obj, "subjective");
        Log("CALL subjective(obj) -> '" + r + "'");
        return r;
    }

    [VariableReplacer(new string[] { "objective", "them" }, Default = "它", Capitalization = true, Override = true)]
    public static string ObjectiveObject(VariableContext context, GameObject obj)
    {
        string r = ZhPronounObj(obj, "objective");
        Log("CALL objective(obj) -> '" + r + "'");
        return r;
    }

    [VariableReplacer(new string[] { "substantivePossessive", "theirs" }, Default = "它的", Capitalization = true, Override = true)]
    public static string SubstantivePossessiveObject(VariableContext context, GameObject obj)
    {
        string r = ZhPronounObj(obj, "substantive");
        Log("CALL substantivePossessive(obj) -> '" + r + "'");
        return r;
    }

    [VariableReplacer(new string[] { "reflexive", "itself" }, Default = "它自己", Capitalization = true, Override = true)]
    public static string ReflexiveObject(VariableContext context, GameObject obj)
    {
        string r = ZhPronounObj(obj, "reflexive");
        Log("CALL reflexive(obj) -> '" + r + "'");
        return r;
    }

    [VariableReplacer(new string[] { "personTerm", "immaturePersonTerm", "subject.personTerm", "subject.immaturePersonTerm" }, Default = "個體", Capitalization = true, Override = true)]
    public static string PersonTerm(VariableContext context, GenderedNoun noun)
    {
        Log("CALL personTerm -> '個體'");
        return "個體";
    }

    [VariableReplacer(new string[] { "personTerm", "immaturePersonTerm", "subject.personTerm", "subject.immaturePersonTerm" }, Default = "個體", Capitalization = true, Override = true)]
    public static string PersonTermObject(VariableContext context, GameObject obj)
    {
        Log("CALL personTerm(obj) -> '個體'");
        return "個體";
    }

    [VariableReplacer(new string[] { "its.item", "possessive.item", "subject.its.item", "object.its.item", "its_", "possessivePronounItem" }, Default = "它的", Capitalization = true, Override = true)]
    public static string ItsItem(VariableContext context, GameObject subject, GameObject item)
    {
        string pos = ZhPronounObj(subject, "possessive");
        string name = ZhName(item);
        string r = pos + name;
        Log("CALL its.item -> '" + r + "'");
        return r;
    }

    // ============ 不定冠詞（indefiniteForOthers：修「the 青銅匕首」） ============

    [VariableReplacer(new string[] { "indefiniteForOthers", "aForNPCSubject.name", "subject.aForNPCSubject.name", "object.aForNPCSubject.name", "indefiniteForOthers.name" }, Default = "", Capitalization = true, Override = true)]
    public static string IndefiniteForOthers(VariableContext context, GameObject obj, GameObject subject)
    {
        string r = ZhName(obj);
        Log("CALL indefiniteForOthers subject='" + (subject != null ? subject.DisplayName : "?") + "' -> '" + r + "'");
        return r;
    }

    // ============ 身體部位（part.ordinalName → 手/頭/腳...） ============

    private static readonly Dictionary<string, string> BodyPartZh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Hand"] = "手", ["Hands"] = "手", ["Arm"] = "手臂", ["Arms"] = "手臂",
        ["Head"] = "頭", ["Face"] = "臉", ["Eye"] = "眼", ["Eyes"] = "眼",
        ["Ear"] = "耳", ["Ears"] = "耳", ["Nose"] = "鼻", ["Mouth"] = "嘴",
        ["Tongue"] = "舌", ["Tooth"] = "牙", ["Teeth"] = "牙", ["Jaw"] = "顎",
        ["Horn"] = "角", ["Horns"] = "角", ["Antenna"] = "觸角", ["Antennae"] = "觸角",
        ["Leg"] = "腿", ["Legs"] = "腿", ["Foot"] = "腳", ["Feet"] = "腳",
        ["Knee"] = "膝", ["Hip"] = "髖", ["Tail"] = "尾", ["Tails"] = "尾",
        ["Back"] = "背", ["Shoulder"] = "肩", ["Shoulders"] = "肩", ["Neck"] = "頸",
        ["Chest"] = "胸", ["Torso"] = "軀幹", ["Belly"] = "腹", ["Stomach"] = "腹",
        ["Wing"] = "翼", ["Wings"] = "翼", ["Claw"] = "爪", ["Claws"] = "爪",
        ["Stinger"] = "螫針", ["Tentacle"] = "觸手", ["Tentacles"] = "觸手",
        ["Finger"] = "指", ["Fingers"] = "指", ["Thumb"] = "拇指",
        ["Paw"] = "掌", ["Paws"] = "掌", ["Hoof"] = "蹄", ["Hooves"] = "蹄",
        ["Fang"] = "尖牙", ["Fangs"] = "尖牙", ["Beak"] = "喙", ["Gill"] = "鰓",
        ["Fin"] = "鰭", ["Fins"] = "鰭", ["Scale"] = "鱗", ["Scales"] = "鱗",
        ["Shell"] = "殼", ["Carapace"] = "甲殼", ["Web"] = "蹼", ["Mane"] = "鬃毛",
        ["Feather"] = "羽毛", ["Feathers"] = "羽毛", ["Fur"] = "毛皮", ["Pelt"] = "皮毛",
        ["Skin"] = "皮膚", ["Brain"] = "腦", ["Skull"] = "顱骨", ["Heart"] = "心臟",
    };

    private static string ZhBodyPart(BodyPart part)
    {
        try
        {
            if (part == null) return "";
            string type = part.Type;
            if (string.IsNullOrEmpty(type)) type = part.Name;
            string zh;
            if (BodyPartZh.TryGetValue(type, out zh)) return zh;
            return type;
        }
        catch
        {
            return "";
        }
    }

    [VariableReplacer(new string[] { "ordinalName", "part.ordinalName", "part.name", "subject.ordinalName", "object.ordinalName", "subject.part.ordinalName", "object.part.ordinalName", "part.ordinalDescription" }, Default = "", Capitalization = true, Override = true)]
    public static string BodyPartName(VariableContext context, BodyPart part)
    {
        string r = ZhBodyPart(part);
        Log("CALL part.ordinalName type='" + (part != null ? part.Type : "?") + "' -> '" + r + "'");
        return r;
    }

    // ============ is / it is / it has / this / that ============

    [VariableReplacer(new string[] { "is", "subject.is", "object.is", "it.is", "itis", "it's", "they're" }, Default = "是", Capitalization = true, Override = true)]
    public static string IsReplacer(VariableContext context, GameObject obj)
    {
        Log("CALL is -> '是'");
        return "是";
    }

    [VariableReplacer(new string[] { "is", "subject.is", "object.is", "it.is", "itis", "it's", "they're" }, Default = "是", Capitalization = true, Override = true)]
    public static string IsReplacerNoun(VariableContext context, GenderedNoun noun)
    {
        Log("CALL is(GenderedNoun) -> '是'");
        return "是";
    }

    [VariableReplacer(new string[] { "it.has", "ithas", "they've" }, Default = "有", Capitalization = true, Override = true)]
    public static string ItHas(VariableContext context, GameObject obj)
    {
        Log("CALL it.has -> '有'");
        return "有";
    }

    [VariableReplacer(new string[] { "it.does", "subject.it.does", "object.it.does" }, Default = "", Capitalization = true, Override = true)]
    public static string ItDoes(VariableContext context, GameObject obj)
    {
        string verb = ReadVerb(context);
        if (IsBeVerb(verb)) return ""; // be 動詞：中文語境多餘（=it.does:are= immune → 免疫）
        string zh = LookupVerbZh(verb);
        if (string.IsNullOrEmpty(zh)) zh = verb;
        if (zh == null) zh = "";
        Log("CALL it.does verb='" + verb + "' -> '" + zh + "'");
        return zh;
    }

    [VariableReplacer(new string[] { "this", "subject.this", "object.this", "indicativeProximal" }, Default = "這", Capitalization = true, Override = true)]
    public static string ThisReplacer(VariableContext context, GameObject obj)
    {
        Log("CALL this -> '這'");
        return "這";
    }

    [VariableReplacer(new string[] { "that", "subject.that", "object.that", "indicativeDistal" }, Default = "那", Capitalization = true, Override = true)]
    public static string ThatReplacer(VariableContext context, GameObject obj)
    {
        Log("CALL that -> '那'");
        return "那";
    }

    // ============ creature / thing / poss / 親屬詞 ============

    [VariableReplacer(new string[] { "creature", "subject.creature", "object.creature" }, Default = "生物", Capitalization = true, Override = true)]
    public static string Creature(VariableContext context, GameObject obj)
    {
        Log("CALL creature -> '生物'");
        return "生物";
    }

    [VariableReplacer(new string[] { "thing", "subject.thing", "object.thing" }, Default = "東西", Capitalization = true, Override = true)]
    public static string Thing(VariableContext context, GameObject obj)
    {
        Log("CALL thing -> '東西'");
        return "東西";
    }

    [VariableReplacer(new string[] { "poss", "subject.poss", "object.poss" }, Default = "的", Capitalization = true, Override = true)]
    public static string Poss(VariableContext context, GameObject obj)
    {
        Log("CALL poss -> '的'");
        return "的";
    }

    [VariableReplacer(new string[] { "formalAddressTerm", "subject.formalAddressTerm", "formaladdressterm" }, Default = "您", Capitalization = true, Override = true)]
    public static string FormalAddress(VariableContext context, GameObject obj)
    {
        Log("CALL formalAddressTerm -> '您'");
        return "您";
    }

    [VariableReplacer(new string[] { "formalAddressTerm", "subject.formalAddressTerm", "formaladdressterm" }, Default = "您", Capitalization = true, Override = true)]
    public static string FormalAddressNoun(VariableContext context, GenderedNoun noun)
    {
        Log("CALL formalAddressTerm(GenderedNoun) -> '您'");
        return "您";
    }

    [VariableReplacer(new string[] { "offspringTerm", "subject.offspringTerm", "subject.immaturePersonTerm" }, Default = "後代", Capitalization = true, Override = true)]
    public static string Offspring(VariableContext context, GameObject obj)
    {
        Log("CALL offspringTerm -> '後代'");
        return "後代";
    }

    [VariableReplacer(new string[] { "offspringTerm", "subject.offspringTerm", "subject.immaturePersonTerm" }, Default = "後代", Capitalization = true, Override = true)]
    public static string OffspringNoun(VariableContext context, GenderedNoun noun)
    {
        Log("CALL offspringTerm(GenderedNoun) -> '後代'");
        return "後代";
    }

    [VariableReplacer(new string[] { "siblingTerm", "subject.siblingTerm", "siblingterm" }, Default = "手足", Capitalization = true, Override = true)]
    public static string Sibling(VariableContext context, GameObject obj)
    {
        Log("CALL siblingTerm -> '手足'");
        return "手足";
    }

    [VariableReplacer(new string[] { "siblingTerm", "subject.siblingTerm", "siblingterm" }, Default = "手足", Capitalization = true, Override = true)]
    public static string SiblingNoun(VariableContext context, GenderedNoun noun)
    {
        Log("CALL siblingTerm(GenderedNoun) -> '手足'");
        return "手足";
    }

    [VariableReplacer(new string[] { "parentTerm", "subject.parentTerm", "parentterm" }, Default = "親長", Capitalization = true, Override = true)]
    public static string Parent(VariableContext context, GameObject obj)
    {
        Log("CALL parentTerm -> '親長'");
        return "親長";
    }

    [VariableReplacer(new string[] { "parentTerm", "subject.parentTerm", "parentterm" }, Default = "親長", Capitalization = true, Override = true)]
    public static string ParentNoun(VariableContext context, GenderedNoun noun)
    {
        Log("CALL parentTerm(GenderedNoun) -> '親長'");
        return "親長";
    }

    // ============ 冠詞組合（a.name's / a.or.the 等 → 補中文名） ============

    [VariableReplacer(new string[] { "a.name's", "the.name's", "name's", "a.name's.item", "the.name's.item", "subject.a.name's", "object.the.name's" }, Default = "", Capitalization = true, Override = true)]
    public static string PossessiveName(VariableContext context, GameObject obj)
    {
        string r = ZhName(obj) + "的";
        Log("CALL possessiveName -> '" + r + "'");
        return r;
    }

    [VariableReplacer(new string[] { "a.or.the", "the.or.a.name", "a.or.stack.name", "a.or.x.things" }, Default = "", Capitalization = true, Override = true)]
    public static string ArticleChoice(VariableContext context, GameObject obj)
    {
        string r = ZhName(obj);
        Log("CALL articleChoice -> '" + r + "'");
        return r;
    }

    // ============ 動詞 ============

    private static readonly Dictionary<string, string> VerbZh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["stand"] = "站著", ["stands"] = "站著", ["stand up"] = "站起",
        ["sit"] = "坐著", ["sits"] = "坐著", ["lie"] = "躺著", ["lies"] = "躺著", ["lay"] = "躺著",
        ["is"] = "是", ["are"] = "是", ["was"] = "曾是", ["were"] = "曾是",
        ["has"] = "有", ["have"] = "有", ["had"] = "曾有",
        ["does"] = "做", ["do"] = "做", ["did"] = "做了",
        ["seems"] = "似乎", ["appears"] = "出現", ["become"] = "變成", ["becomes"] = "變成",
        ["remains"] = "保持", ["stay"] = "停留", ["stays"] = "停留",
        ["screech"] = "發出尖叫", ["screeches"] = "發出尖叫",
        ["growl"] = "發出咆哮", ["growls"] = "發出咆哮",
        ["snarl"] = "咆哮", ["snarls"] = "咆哮",
        ["roar"] = "吼叫", ["roars"] = "吼叫", ["howl"] = "嚎叫", ["howls"] = "嚎叫",
        ["bark"] = "吠叫", ["barks"] = "吠叫",
        ["hiss"] = "嘶鳴", ["hisses"] = "嘶鳴",
        ["gurgle"] = "發出咕嚕聲", ["gurgles"] = "發出咕嚕聲",
        ["squeak"] = "吱吱叫", ["squeaks"] = "吱吱叫",
        ["wail"] = "哀號", ["wails"] = "哀號",
        ["moan"] = "呻吟", ["moans"] = "呻吟", ["groan"] = "呻吟", ["groans"] = "呻吟",
        ["whimper"] = "嗚咽", ["whimpers"] = "嗚咽",
        ["chitter"] = "吱吱作響", ["chitters"] = "吱吱作響",
        ["squeal"] = "尖叫", ["squeals"] = "尖叫",
        ["speak"] = "說話", ["speaks"] = "說話", ["speaking"] = "說話",
        ["talk"] = "交談", ["talks"] = "交談",
        ["say"] = "說", ["says"] = "說", ["said"] = "說",
        ["mutter"] = "喃喃自語", ["mutters"] = "喃喃自語",
        ["whisper"] = "低語", ["whispers"] = "低語",
        ["shout"] = "大喊", ["shouts"] = "大喊", ["yell"] = "大喊", ["yells"] = "大喊",
        ["scream"] = "尖叫", ["screams"] = "尖叫",
        ["sing"] = "歌唱", ["sings"] = "歌唱", ["chant"] = "吟唱", ["chants"] = "吟唱",
        ["twitch"] = "抽搐", ["twitches"] = "抽搐",
        ["shudder"] = "顫抖", ["shudders"] = "顫抖",
        ["quiver"] = "顫動", ["quivers"] = "顫動", ["tremble"] = "顫抖", ["trembles"] = "顫抖",
        ["shiver"] = "顫抖", ["shivers"] = "顫抖",
        ["vibrate"] = "震動", ["vibrates"] = "震動",
        ["jitter"] = "抖動", ["jitters"] = "抖動",
        ["sway"] = "搖晃", ["sways"] = "搖晃",
        ["wobble"] = "搖晃", ["wobbles"] = "搖晃",
        ["bob"] = "上下擺動", ["bobs"] = "上下擺動",
        ["rock"] = "搖擺", ["rocks"] = "搖擺",
        ["nod"] = "點頭", ["nods"] = "點頭",
        ["shake"] = "搖晃", ["shakes"] = "搖晃",
        ["stir"] = "攪動", ["stirs"] = "攪動",
        ["move"] = "移動", ["moves"] = "移動", ["moved"] = "移動",
        ["walk"] = "行走", ["walks"] = "行走",
        ["stroll"] = "漫步", ["strolls"] = "漫步",
        ["wander"] = "徘徊", ["wanders"] = "徘徊",
        ["pace"] = "踱步", ["paces"] = "踱步",
        ["step"] = "邁步", ["steps"] = "邁步", ["stepping"] = "邁步",
        ["run"] = "奔跑", ["runs"] = "奔跑",
        ["sprint"] = "衝刺", ["sprints"] = "衝刺",
        ["crawl"] = "爬行", ["crawls"] = "爬行",
        ["clamber"] = "攀爬", ["clambers"] = "攀爬",
        ["lumber"] = "蹣跚而行", ["lumbers"] = "蹣跚而行",
        ["amble"] = "緩步", ["ambles"] = "緩步",
        ["trudge"] = "艱難前行", ["trudges"] = "艱難前行",
        ["tread"] = "踩踏", ["treads"] = "踩踏",
        ["march"] = "行進", ["marches"] = "行進",
        ["scamper"] = "蹦跳", ["scampers"] = "蹦跳",
        ["skitter"] = "疾走", ["skitters"] = "疾走",
        ["scurry"] = "匆忙", ["scurries"] = "匆忙",
        ["hurry"] = "急忙", ["hurries"] = "急忙",
        ["dash"] = "衝", ["dashes"] = "衝",
        ["charge"] = "衝鋒", ["charges"] = "衝鋒",
        ["approach"] = "靠近", ["approaches"] = "靠近",
        ["flee"] = "逃離", ["flees"] = "逃離",
        ["retreat"] = "撤退", ["retreats"] = "撤退",
        ["frolic"] = "嬉戲", ["play"] = "玩耍", ["dance"] = "跳舞", ["leap"] = "跳躍",
        ["jump"] = "跳", ["bound"] = "蹦跳", ["pounce"] = "撲向",
        ["fly"] = "飛行", ["soar"] = "翱翔", ["hover"] = "盤旋",
        ["glow"] = "發光", ["flicker"] = "閃爍", ["gleam"] = "閃耀", ["glint"] = "閃爍",
        ["shine"] = "照耀", ["flash"] = "閃光", ["crackle"] = "劈啪作響",
        ["fizzle"] = "嘶嘶作響", ["spurt"] = "噴出", ["gush"] = "湧出",
        ["flow"] = "流動", ["drip"] = "滴落", ["trickle"] = "涓流", ["leak"] = "滲漏",
        ["drum"] = "敲擊", ["tap"] = "輕敲", ["flick"] = "輕彈", ["rub"] = "摩擦",
        ["scrub"] = "擦洗", ["wring"] = "擰", ["knead"] = "揉捏", ["shape"] = "塑形",
        ["paint"] = "塗抹", ["craft"] = "製作", ["sort"] = "整理", ["gather"] = "收集",
        ["collect"] = "蒐集", ["search"] = "搜尋", ["find"] = "找到", ["seek"] = "尋找",
        ["enter"] = "進入", ["leave"] = "離開", ["return"] = "返回", ["arrive"] = "抵達",
        ["cross"] = "穿越", ["climb"] = "攀爬", ["inch"] = "緩慢移動", ["maneuver"] = "移動",
        ["collapse"] = "崩塌", ["crumple"] = "皺縮", ["flatten"] = "壓平", ["shrink"] = "縮小",
        ["tauten"] = "繃緊", ["flex"] = "屈伸", ["extrude"] = "擠出", ["compact"] = "壓實",
        ["camber"] = "拱起", ["undulate"] = "波動", ["bend"] = "彎曲", ["curl"] = "蜷曲",
        ["evaporate"] = "蒸發", ["melt"] = "融化", ["freeze"] = "凍結",
        ["struggle"] = "掙扎", ["resist"] = "抵抗", ["fight"] = "戰鬥",
        ["sense"] = "感知", ["detect"] = "偵測", ["feel"] = "感覺", ["seem"] = "似乎",
        ["exhibit"] = "展現", ["show"] = "顯示", ["reveal"] = "顯現", ["claim"] = "宣稱",
        ["peddle"] = "叫賣",
        ["perch"] = "棲息", ["squat"] = "蹲伏", ["crouch"] = "蹲下",
        ["saunter"] = "閒逛", ["shuffle"] = "拖步", ["slip"] = "滑動", ["slide"] = "滑行",
        ["split"] = "分裂", ["cut"] = "切割", ["draw"] = "拔出", ["cast"] = "施放",
        ["sport"] = "炫耀", ["ring"] = "鳴響", ["swing"] = "揮動",
        ["pick"] = "撿起", ["place"] = "放置", ["drop"] = "掉落", ["throw"] = "丟擲",
        ["catch"] = "接住", ["cull"] = "採集", ["harvest"] = "收割",
        ["bat"] = "拍打", ["beam"] = "發出光束", ["bear"] = "背負", ["beep"] = "發出嗶聲",
        ["bite"] = "咬", ["bloom"] = "綻放", ["blow"] = "吹", ["boop"] = "輕戳",
        ["brush"] = "刷", ["buzz"] = "嗡嗡作響", ["carry"] = "攜帶", ["chew"] = "咀嚼",
        ["chirp"] = "啾啾叫", ["chirr"] = "唧唧叫", ["clasp"] = "緊握", ["clutch"] = "抓住",
        ["creep"] = "潛行", ["croak"] = "呱呱叫", ["croon"] = "低吟", ["crumble"] = "碎裂",
        ["crust"] = "結殼", ["dehydrate"] = "脫水", ["dissipate"] = "消散", ["don"] = "穿上",
        ["dress"] = "穿衣", ["drink"] = "喝", ["drone"] = "嗡嗡作響", ["employ"] = "使用",
        ["explode"] = "爆炸", ["face"] = "面向", ["gibber"] = "嘰哩咕嚕", ["glitch"] = "故障",
        ["grab"] = "抓起", ["gulp"] = "吞嚥", ["harmonize"] = "和諧", ["hold"] = "握著",
        ["let"] = "讓", ["long"] = "渴望", ["look"] = "看", ["manage"] = "設法",
        ["meow"] = "喵喵叫", ["occupy"] = "佔據", ["peer"] = "凝視", ["poke"] = "戳",
        ["reach"] = "伸手", ["reel"] = "踉蹌", ["roam"] = "漫遊", ["roll"] = "滾動",
        ["root"] = "挖掘", ["serve"] = "服務", ["shloop"] = "發出嗖聲", ["shoot"] = "射擊",
        ["slink"] = "潛行", ["slither"] = "滑行", ["smell"] = "聞", ["sniff"] = "嗅",
        ["spin"] = "旋轉", ["squint"] = "瞇眼", ["squish"] = "擠壓", ["stamp"] = "跺腳",
        ["stare"] = "凝視", ["stumble"] = "絆倒", ["swallow"] = "吞嚥", ["take"] = "拿走",
        ["teeter"] = "搖搖欲墜", ["tug"] = "拉扯", ["turn"] = "轉身", ["twirl"] = "旋轉",
        ["use"] = "使用", ["waggle"] = "搖擺", ["warble"] = "顫音鳴叫", ["wear"] = "穿著",
        ["welcome"] = "歡迎", ["wield"] = "揮舞",
        ["receive"] = "受到", ["refuse"] = "拒絕", ["equip"] = "裝備",
        ["miss"] = "落空", ["misses"] = "落空", ["hit"] = "擊中", ["hits"] = "擊中",
        ["penetrate"] = "穿透", ["penetrates"] = "穿透", ["penetrating"] = "穿透",
        ["begin"] = "開始", ["begins"] = "開始", ["began"] = "開始",
        ["stop"] = "停止", ["stops"] = "停止",
        ["emit"] = "發出", ["emits"] = "發出", ["emitted"] = "發出",
        ["die"] = "死亡", ["dies"] = "死亡", ["died"] = "死亡",
        ["explode"] = "爆炸", ["explodes"] = "爆炸",
        ["resist"] = "抵抗", ["resists"] = "抵抗", ["resisted"] = "抵抗",
        ["fall"] = "倒下", ["falls"] = "倒下", ["fell"] = "倒下",
        ["try"] = "嘗試", ["tries"] = "嘗試", ["tried"] = "嘗試",
        ["need"] = "需要", ["needs"] = "需要", ["needed"] = "需要",
        ["seem"] = "似乎", ["seemed"] = "似乎",
        ["transfer"] = "轉移", ["transfers"] = "轉移", ["transferred"] = "轉移",
        ["start"] = "開始", ["starts"] = "開始", ["started"] = "開始",
        ["slip"] = "滑落", ["slips"] = "滑落", ["slipped"] = "滑落",
        ["pour"] = "傾倒", ["pours"] = "傾倒", ["poured"] = "傾倒",
        ["reflect"] = "反射", ["reflects"] = "反射", ["reflected"] = "反射",
        ["intercept"] = "攔截", ["intercepts"] = "攔截",
        ["clean"] = "清潔", ["cleans"] = "清潔", ["cleaned"] = "清潔",
        ["activate"] = "啟動", ["activates"] = "啟動", ["activated"] = "啟動",
        ["wander"] = "徘徊", ["wanders"] = "徘徊", ["wandered"] = "徘徊",
        ["wake"] = "醒來", ["wakes"] = "醒來", ["woke"] = "醒來",
        ["vibrate"] = "震動", ["vibrates"] = "震動",
        ["touch"] = "觸碰", ["touches"] = "觸碰", ["touched"] = "觸碰",
        ["merely"] = "只是", ["don't"] = "不", ["won't"] = "不會", ["dont"] = "不",
        ["deal"] = "造成", ["deals"] = "造成", ["dealt"] = "造成",
        ["suffer"] = "承受", ["suffers"] = "承受", ["suffered"] = "承受",
        ["attack"] = "攻擊", ["attacks"] = "攻擊", ["attacked"] = "攻擊",
        ["strike"] = "攻擊", ["strikes"] = "攻擊", ["struck"] = "攻擊",
        ["kill"] = "殺死", ["kills"] = "殺死", ["killed"] = "殺死",
        ["survive"] = "存活", ["survives"] = "存活", ["survived"] = "存活",
        ["avoid"] = "迴避", ["avoids"] = "迴避", ["avoided"] = "迴避",
        ["dodge"] = "閃避", ["dodges"] = "閃避", ["dodged"] = "閃避",
        ["block"] = "格擋", ["blocks"] = "格擋", ["blocked"] = "格擋",
        ["counter"] = "反擊", ["counters"] = "反擊", ["countered"] = "反擊",
        ["retaliate"] = "報復", ["retaliates"] = "報復",
        ["shove"] = "推擠", ["shoves"] = "推擠", ["shoved"] = "推擠",
        ["push"] = "推", ["pushes"] = "推", ["pushed"] = "推",
        ["pull"] = "拉", ["pulls"] = "拉", ["pulled"] = "拉",
        ["grapple"] = "擒抱", ["grapples"] = "擒抱",
        ["wound"] = "重創", ["wounds"] = "重創", ["wounded"] = "重創",
        ["crit"] = "暴擊", ["crits"] = "暴擊",
        ["glance"] = "擦過", ["glances"] = "擦過", ["glanced"] = "擦過",
        ["deflect"] = "偏轉", ["deflects"] = "偏轉", ["deflected"] = "偏轉",
        ["absorb"] = "吸收", ["absorbs"] = "吸收", ["absorbed"] = "吸收",
        ["heal"] = "治療", ["heals"] = "治療", ["healed"] = "治療",
        ["recover"] = "恢復", ["recovers"] = "恢復", ["recovered"] = "恢復",
        ["regen"] = "再生", ["regens"] = "再生",
        ["freeze"] = "凍結", ["freezes"] = "凍結", ["froze"] = "凍結",
        ["burn"] = "燃燒", ["burns"] = "燃燒", ["burned"] = "燃燒",
        ["boil"] = "沸騰", ["boils"] = "沸騰",
        ["melt"] = "融化", ["melts"] = "融化",
        ["shock"] = "電擊", ["shocks"] = "電擊", ["shocked"] = "電擊",
        ["electrify"] = "通電", ["electrifies"] = "通電",
        ["poison"] = "中毒", ["poisons"] = "中毒", ["poisoned"] = "中毒",
        ["disease"] = "染病", ["diseases"] = "染病",
        ["stun"] = "暈眩", ["stuns"] = "暈眩", ["stunned"] = "暈眩",
        ["daze"] = "暈眩", ["dazes"] = "暈眩", ["dazed"] = "暈眩",
        ["confuse"] = "困惑", ["confuses"] = "困惑", ["confused"] = "困惑",
        ["fear"] = "恐懼", ["fears"] = "恐懼", ["feared"] = "恐懼",
        ["terrify"] = "驚嚇", ["terrifies"] = "驚嚇", ["terrified"] = "驚嚇",
        ["flee"] = "逃離", ["flees"] = "逃離", ["fled"] = "逃離",
        ["surrender"] = "投降", ["surrenders"] = "投降",
        ["enrage"] = "激怒", ["enrages"] = "激怒", ["enraged"] = "激怒",
        ["transform"] = "轉變", ["transforms"] = "轉變",
        ["levitate"] = "飄浮", ["levitates"] = "飄浮",
        ["teleport"] = "傳送", ["teleports"] = "傳送", ["teleported"] = "傳送",
        ["submerge"] = "潛入", ["submerges"] = "潛入",
        ["surface"] = "浮出", ["surfaces"] = "浮出",
        ["dig"] = "挖掘", ["digs"] = "挖掘", ["dug"] = "挖掘",
        ["burrow"] = "鑽洞", ["burrows"] = "鑽洞",
        ["swim"] = "游泳", ["swims"] = "游泳", ["swam"] = "游泳",
        ["swallow"] = "吞嚥", ["swallows"] = "吞嚥", ["swallowed"] = "吞嚥",
        ["regurgitate"] = "反芻", ["regurgitates"] = "反芻",
        ["bite"] = "咬", ["bites"] = "咬", ["bit"] = "咬",
        ["scratch"] = "抓撓", ["scratches"] = "抓撓", ["scratched"] = "抓撓",
        ["claw"] = "抓擊", ["claws"] = "抓擊", ["clawed"] = "抓擊",
        ["chomp"] = "咀嚼", ["chomps"] = "咀嚼",
        ["gnaw"] = "啃咬", ["gnaws"] = "啃咬",
        ["trample"] = "踐踏", ["tramples"] = "踐踏",
        ["stomp"] = "踩踏", ["stomps"] = "踩踏",
        ["kick"] = "踢", ["kicks"] = "踢", ["kicked"] = "踢",
        ["punch"] = "拳擊", ["punches"] = "拳擊", ["punched"] = "拳擊",
        ["gore"] = "頂刺", ["gores"] = "頂刺",
        ["stab"] = "刺", ["stabs"] = "刺", ["stabbed"] = "刺",
        ["slash"] = "揮砍", ["slashes"] = "揮砍", ["slashed"] = "揮砍",
        ["smash"] = "粉碎", ["smashes"] = "粉碎", ["smashed"] = "粉碎",
        ["crush"] = "碾壓", ["crushes"] = "碾壓", ["crushed"] = "碾壓",
        ["batter"] = "猛擊", ["batters"] = "猛擊",
        ["pummel"] = "痛毆", ["pummels"] = "痛毆",
        ["graze"] = "擦傷", ["grazes"] = "擦傷",
        ["scrape"] = "刮傷", ["scrapes"] = "刮傷",
        ["tear"] = "撕裂", ["tears"] = "撕裂", ["tore"] = "撕裂",
        ["rend"] = "撕裂", ["rends"] = "撕裂",
        ["sever"] = "斬斷", ["severs"] = "斬斷", ["severed"] = "斬斷",
        ["gouge"] = "挖", ["gouges"] = "挖",
        ["shatter"] = "粉碎", ["shatters"] = "粉碎",
        ["sunder"] = "震碎", ["sunders"] = "震碎",
        ["blast"] = "爆破", ["blasts"] = "爆破", ["blasted"] = "爆破",
        ["burst"] = "爆裂", ["bursts"] = "爆裂",
        ["splatter"] = "飛濺", ["splatters"] = "飛濺",
        ["splash"] = "濺灑", ["splashes"] = "濺灑",
        ["douse"] = "澆濕", ["douses"] = "澆濕",
        ["soak"] = "浸透", ["soaks"] = "浸透",
        ["ignite"] = "點燃", ["ignites"] = "點燃", ["ignited"] = "點燃",
        ["scorch"] = "燒焦", ["scorches"] = "燒焦",
        ["char"] = "燒焦", ["chars"] = "燒焦",
        ["singe"] = "燎焦", ["singes"] = "燎焦",
        ["wither"] = "枯萎", ["withers"] = "枯萎",
        ["decay"] = "腐爛", ["decays"] = "腐爛",
        ["rot"] = "腐敗", ["rots"] = "腐敗",
        ["fester"] = "潰爛", ["festers"] = "潰爛",
        ["seep"] = "滲出", ["seeps"] = "滲出",
        ["ooze"] = "滲流", ["oozes"] = "滲流",
        ["drip"] = "滴落", ["drips"] = "滴落",
        ["spray"] = "噴灑", ["sprays"] = "噴灑",
        ["shoot"] = "射擊", ["shoots"] = "射擊", ["shot"] = "射擊",
        ["fire"] = "開火", ["fires"] = "開火", ["fired"] = "開火",
        ["launch"] = "發射", ["launches"] = "發射", ["launched"] = "發射",
        ["hurl"] = "猛擲", ["hurls"] = "猛擲",
        ["fling"] = "擲出", ["flings"] = "擲出", ["flung"] = "擲出",
        ["toss"] = "拋擲", ["tosses"] = "拋擲",
        ["drop"] = "掉落", ["drops"] = "掉落", ["dropped"] = "掉落",
        ["summon"] = "召喚", ["summons"] = "召喚", ["summoned"] = "召喚",
        ["conjure"] = "召喚", ["conjures"] = "召喚",
        ["invoke"] = "施放", ["invokes"] = "施放",
        ["charge"] = "充能", ["charges"] = "充能", ["charged"] = "充能",
        ["discharge"] = "釋放", ["discharges"] = "釋放",
        ["siphon"] = "吸取", ["siphons"] = "吸取",
        ["drain"] = "吸取", ["drains"] = "吸取", ["drained"] = "吸取",
        ["leech"] = "吸血", ["leeches"] = "吸血",
        ["devour"] = "吞噬", ["devours"] = "吞噬", ["devoured"] = "吞噬",
        ["consume"] = "消耗", ["consumes"] = "消耗",
        ["gaze"] = "凝視", ["gazes"] = "凝視",
        ["stare"] = "凝視", ["stares"] = "凝視",
        ["glare"] = "怒視", ["glares"] = "怒視",
        ["scowl"] = "皺眉", ["scowls"] = "皺眉",
        ["grin"] = "咧嘴笑", ["grins"] = "咧嘴笑",
        ["sneer"] = "冷笑", ["sneers"] = "冷笑",
        ["snarl"] = "咆哮", ["snarls"] = "咆哮",
        ["growl"] = "低吼", ["growls"] = "低吼",
        ["screech"] = "尖叫", ["screeches"] = "尖叫",
        ["screech"] = "發出尖叫",
        ["cackle"] = "咯咯笑", ["cackles"] = "咯咯笑",
        ["chuckle"] = "輕笑", ["chuckles"] = "輕笑",
        ["giggle"] = "咯咯笑", ["giggles"] = "咯咯笑",
        ["laugh"] = "大笑", ["laughs"] = "大笑", ["laughed"] = "大笑",
        ["weep"] = "哭泣", ["weeps"] = "哭泣", ["wept"] = "哭泣",
        ["wail"] = "哀號", ["wails"] = "哀號",
        ["sob"] = "啜泣", ["sobs"] = "啜泣",
        ["hiss"] = "嘶鳴", ["hisses"] = "嘶鳴",
        ["roar"] = "吼叫", ["roars"] = "吼叫",
        ["howl"] = "嚎叫", ["howls"] = "嚎叫",
        ["bellow"] = "怒吼", ["bellows"] = "怒吼",
        ["shout"] = "大喊", ["shouts"] = "大喊",
        ["yell"] = "大喊", ["yells"] = "大喊",
        ["scream"] = "尖叫", ["screams"] = "尖叫",
        ["whisper"] = "低語", ["whispers"] = "低語",
        ["mumble"] = "咕噥", ["mumbles"] = "咕噥",
        ["babble"] = "胡言亂語", ["babbles"] = "胡言亂語",
        ["chant"] = "吟唱", ["chants"] = "吟唱",
        ["pray"] = "祈禱", ["prays"] = "祈禱",
        ["bless"] = "祝福", ["blesses"] = "祝福", ["blessed"] = "祝福",
        ["curse"] = "詛咒", ["curses"] = "詛咒", ["cursed"] = "詛咒",
        ["hex"] = "施咒", ["hexes"] = "施咒",
        ["enchant"] = "附魔", ["enchants"] = "附魔",
        ["merge"] = "合併", ["merges"] = "合併", ["merged"] = "合併",
        ["split"] = "分裂", ["splits"] = "分裂",
        ["reproduce"] = "繁殖", ["reproduces"] = "繁殖",
        ["hatch"] = "孵化", ["hatches"] = "孵化",
        ["gestate"] = "孕育", ["gestates"] = "孕育",
        ["pupate"] = "化蛹", ["pupates"] = "化蛹",
        ["spawn"] = "生成", ["spawns"] = "生成",
        ["bloom"] = "綻放", ["blooms"] = "綻放",
        ["bud"] = "發芽", ["buds"] = "發芽",
        ["sprout"] = "萌芽", ["sprouts"] = "萌芽",
        ["wither"] = "枯萎",
        ["shrink"] = "縮小", ["shrinks"] = "縮小",
        ["swell"] = "膨脹", ["swells"] = "膨脹", ["swelled"] = "膨脹",
        ["expand"] = "擴張", ["expands"] = "擴張",
        ["inflate"] = "充氣", ["inflates"] = "充氣",
        ["contract"] = "收縮", ["contracts"] = "收縮",
        ["pulse"] = "脈動", ["pulses"] = "脈動",
        ["throb"] = "悸動", ["throbs"] = "悸動",
        ["pump"] = "泵動", ["pumps"] = "泵動",
        ["beat"] = "跳動", ["beats"] = "跳動",
        ["flutter"] = "顫動", ["flutters"] = "顫動",
        ["quiver"] = "顫動", ["quivers"] = "顫動",
        ["shiver"] = "顫抖", ["shivers"] = "顫抖",
        ["tremble"] = "顫抖", ["trembles"] = "顫抖",
        ["judder"] = "震顫", ["judders"] = "震顫",
        ["ratchet"] = "咔嗒", ["ratchets"] = "咔嗒",
        ["rattle"] = "嘎嘎作響", ["rattles"] = "嘎嘎作響",
        ["clatter"] = "鏗鏘作響", ["clatters"] = "鏗鏘作響",
        ["clang"] = "噹噹作響", ["clangs"] = "噹噹作響",
        ["boom"] = "轟鳴", ["booms"] = "轟鳴",
        ["thunder"] = "雷鳴", ["thunders"] = "雷鳴",
        ["rumble"] = "隆隆作響", ["rumbles"] = "隆隆作響",
        ["crunch"] = "嘎吱作響", ["crunches"] = "嘎吱作響",
        ["crack"] = "爆裂", ["cracks"] = "爆裂",
        ["snap"] = "啪嗒", ["snaps"] = "啪嗒",
        ["pop"] = "砰", ["pops"] = "砰",
        ["fizz"] = "嘶嘶", ["fizzes"] = "嘶嘶",
        ["sizzle"] = "滋滋作響", ["sizzles"] = "滋滋作響",
        ["simmer"] = "煨煮", ["simmers"] = "煨煮",
        ["bubble"] = "冒泡", ["bubbles"] = "冒泡",
        ["churn"] = "翻騰", ["churns"] = "翻騰",
        ["swirl"] = "旋渦", ["swirls"] = "旋渦",
        ["eddies"] = "渦旋",
        ["circulate"] = "循環", ["circulates"] = "循環",
        ["revolve"] = "旋轉", ["revolves"] = "旋轉",
        ["rotate"] = "旋轉", ["rotates"] = "旋轉",
        ["gyrate"] = "迴旋", ["gyrates"] = "迴旋",
        ["whirl"] = "旋轉", ["whirls"] = "旋轉",
        ["tumble"] = "翻滾", ["tumbles"] = "翻滾",
        ["topple"] = "傾倒", ["topples"] = "傾倒",
        ["stagger"] = "踉蹌", ["staggers"] = "踉蹌",
        ["stumble"] = "絆倒", ["stumbles"] = "絆倒",
        ["lurch"] = "顛簸", ["lurches"] = "顛簸",
        ["wobble"] = "搖晃", ["wobbles"] = "搖晃",
        ["totter"] = "搖搖欲墜", ["totters"] = "搖搖欲墜",
        ["hover"] = "盤旋", ["hovers"] = "盤旋",
        ["glide"] = "滑翔", ["glides"] = "滑翔",
        ["swoop"] = "俯衝", ["swoops"] = "俯衝",
        ["plummet"] = "急墜", ["plummets"] = "急墜",
        ["dive"] = "俯衝", ["dives"] = "俯衝",
        ["leap"] = "跳躍", ["leaps"] = "跳躍",
        ["vault"] = "躍過", ["vaults"] = "躍過",
        ["scramble"] = "攀爬", ["scrambles"] = "攀爬",
        ["clamber"] = "攀爬", ["clambers"] = "攀爬",
        ["scuttle"] = "疾走", ["scuttles"] = "疾走",
        ["scurry"] = "匆忙", ["scurries"] = "匆忙",
        ["dart"] = "疾衝", ["darts"] = "疾衝",
        ["bolt"] = "疾奔", ["bolts"] = "疾奔",
        ["hasten"] = "加快", ["hastens"] = "加快",
        ["quicken"] = "加速", ["quickens"] = "加速",
        ["linger"] = "逗留", ["lingers"] = "逗留",
        ["loiter"] = "閒逛", ["loiters"] = "閒逛",
        ["wait"] = "等待", ["waits"] = "等待", ["waited"] = "等待",
        ["rest"] = "休息", ["rests"] = "休息", ["rested"] = "休息",
        ["sleep"] = "睡覺", ["sleeps"] = "睡覺", ["slept"] = "睡覺",
        ["nap"] = "小憩", ["naps"] = "小憩",
        ["slumber"] = "沉睡", ["slumbers"] = "沉睡",
        ["doze"] = "打盹", ["dozes"] = "打盹",
        ["dream"] = "作夢", ["dreams"] = "作夢", ["dreamed"] = "作夢",
        ["meditate"] = "冥想", ["meditates"] = "冥想",
        ["ponder"] = "沉思", ["ponders"] = "沉思",
        ["contemplate"] = "沉思", ["contemplates"] = "沉思",
        ["reflect"] = "反思",
        ["mourn"] = "哀悼", ["mourns"] = "哀悼",
        ["grieve"] = "悲傷", ["grieves"] = "悲傷",
        ["celebrate"] = "慶祝", ["celebrates"] = "慶祝",
        ["rejoice"] = "歡欣", ["rejoices"] = "歡欣",
        ["dance"] = "跳舞", ["dances"] = "跳舞",
        ["waltz"] = "旋舞", ["waltzes"] = "旋舞",
        ["jig"] = "跳吉格舞", ["jigs"] = "跳吉格舞",
        ["prance"] = "雀躍", ["prances"] = "雀躍",
        ["caper"] = "蹦跳", ["capers"] = "蹦跳",
        ["gambol"] = "嬉跳", ["gambols"] = "嬉跳",
        ["skip"] = "蹦跳", ["skips"] = "蹦跳",
        ["hop"] = "跳躍", ["hops"] = "跳躍",
        ["gallop"] = "奔馳", ["gallops"] = "奔馳",
        ["trot"] = "小跑", ["trots"] = "小跑",
        ["jog"] = "慢跑", ["jogs"] = "慢跑",
        ["sprint"] = "衝刺", ["sprints"] = "衝刺",
        ["hike"] = "遠足", ["hikes"] = "遠足",
        ["trek"] = "跋涉", ["treks"] = "跋涉",
        ["traverse"] = "穿越", ["traverses"] = "穿越",
        ["ford"] = "涉渡", ["fords"] = "涉渡",
        ["cross"] = "穿越", ["crosses"] = "穿越",
        ["board"] = "登乘", ["boards"] = "登乘",
        ["dismount"] = "下乘", ["dismounts"] = "下乘",
        ["mount"] = "騎乘", ["mounts"] = "騎乘",
        ["ride"] = "騎乘", ["rides"] = "騎乘", ["rode"] = "騎乘",
        ["pilot"] = "駕駛", ["pilots"] = "駕駛",
        ["drive"] = "駕駛", ["drives"] = "駕駛", ["drove"] = "駕駛",
        ["steer"] = "操控", ["steers"] = "操控",
        ["navigate"] = "導航", ["navigates"] = "導航",
        ["compute"] = "計算", ["computes"] = "計算",
        ["calculate"] = "計算", ["calculates"] = "計算",
        ["process"] = "處理", ["processes"] = "處理",
        ["analyze"] = "分析", ["analyzes"] = "分析",
        ["scan"] = "掃描", ["scans"] = "掃描",
        ["read"] = "閱讀", ["reads"] = "閱讀", ["read"] = "閱讀",
        ["write"] = "書寫", ["writes"] = "書寫", ["wrote"] = "書寫",
        ["record"] = "記錄", ["records"] = "記錄",
        ["memorize"] = "記憶", ["memorizes"] = "記憶",
        ["forget"] = "遺忘", ["forgets"] = "遺忘", ["forgot"] = "遺忘",
        ["learn"] = "學習", ["learns"] = "學習", ["learned"] = "學習",
        ["teach"] = "教導", ["teaches"] = "教導",
        ["train"] = "訓練", ["trains"] = "訓練",
        ["practice"] = "練習", ["practices"] = "練習",
        ["perform"] = "施展", ["performs"] = "施展",
        ["execute"] = "執行", ["executes"] = "執行",
        ["complete"] = "完成", ["completes"] = "完成",
        ["finish"] = "完成", ["finishes"] = "完成",
        ["continue"] = "繼續", ["continues"] = "繼續",
        ["resume"] = "恢復", ["resumes"] = "恢復",
        ["pause"] = "暫停", ["pauses"] = "暫停",
        ["abandon"] = "放棄", ["abandons"] = "放棄",
        ["withdraw"] = "撤退", ["withdraws"] = "撤退",
        ["retreat"] = "撤退", ["retreats"] = "撤退",
        ["advance"] = "前進", ["advances"] = "前進",
        ["approach"] = "靠近", ["approaches"] = "靠近",
        ["confront"] = "對峙", ["confronts"] = "對峙",
        ["challenge"] = "挑戰", ["challenges"] = "挑戰",
        ["provoke"] = "挑釁", ["provokes"] = "挑釁",
        ["threaten"] = "威脅", ["threatens"] = "威脅",
        ["warn"] = "警告", ["warns"] = "警告",
        ["beg"] = "乞求", ["begs"] = "乞求",
        ["plead"] = "懇求", ["pleads"] = "懇求",
        ["implore"] = "懇求", ["implores"] = "懇求",
        ["demand"] = "要求", ["demands"] = "要求",
        ["request"] = "請求", ["requests"] = "請求",
        ["ask"] = "詢問", ["asks"] = "詢問", ["asked"] = "詢問",
        ["answer"] = "回答", ["answers"] = "回答",
        ["reply"] = "回覆", ["replies"] = "回覆",
        ["respond"] = "回應", ["responds"] = "回應",
        ["agree"] = "同意", ["agrees"] = "同意",
        ["disagree"] = "不同意", ["disagrees"] = "不同意",
        ["nod"] = "點頭", ["nods"] = "點頭",
        ["bow"] = "鞠躬", ["bows"] = "鞠躬",
        ["kneel"] = "跪下", ["kneels"] = "跪下",
        ["prostrate"] = "俯伏", ["prostrates"] = "俯伏",
        ["salute"] = "敬禮", ["salutes"] = "敬禮",
        ["embrace"] = "擁抱", ["embraces"] = "擁抱",
        ["hug"] = "擁抱", ["hugs"] = "擁抱",
        ["kiss"] = "親吻", ["kisses"] = "親吻",
        ["wave"] = "揮手", ["waves"] = "揮手",
        ["gesture"] = "做手勢", ["gestures"] = "做手勢",
        ["point"] = "指向", ["points"] = "指向",
        ["beckon"] = "招手", ["beckons"] = "招手",
        ["signal"] = "示意", ["signals"] = "示意",
        ["motion"] = "示意", ["motions"] = "示意",
        ["call"] = "呼叫", ["calls"] = "呼叫", ["called"] = "呼叫",
        ["summon"] = "召喚",
        ["dismiss"] = "遣散", ["dismisses"] = "遣散",
        ["release"] = "釋放", ["releases"] = "釋放",
        ["capture"] = "捕獲", ["captures"] = "捕獲",
        ["imprison"] = "囚禁", ["imprisons"] = "囚禁",
        ["enslave"] = "奴役", ["enslaves"] = "奴役",
        ["free"] = "解放", ["frees"] = "解放",
        ["rescue"] = "救援", ["rescues"] = "救援",
        ["save"] = "拯救", ["saves"] = "拯救",
        ["protect"] = "保護", ["protects"] = "保護",
        ["guard"] = "守衛", ["guards"] = "守衛",
        ["defend"] = "防禦", ["defends"] = "防禦",
        ["shield"] = "護衛", ["shields"] = "護衛",
        ["shelter"] = "庇護", ["shelters"] = "庇護",
        ["hide"] = "隱藏", ["hides"] = "隱藏", ["hid"] = "隱藏",
        ["conceal"] = "藏匿", ["conceals"] = "藏匿",
        ["disguise"] = "偽裝", ["disguises"] = "偽裝",
        ["mask"] = "掩飾", ["masks"] = "掩飾",
        ["camouflage"] = "迷彩", ["camouflages"] = "迷彩",
        ["stalk"] = "潛行追蹤", ["stalks"] = "潛行追蹤",
        ["hunt"] = "狩獵", ["hunts"] = "狩獵",
        ["chase"] = "追逐", ["chases"] = "追逐",
        ["pursue"] = "追趕", ["pursues"] = "追趕",
        ["track"] = "追蹤", ["tracks"] = "追蹤",
        ["ambush"] = "伏擊", ["ambushes"] = "伏擊",
        ["pounce"] = "撲向", ["pounces"] = "撲向",
        ["lunge"] = "突刺", ["lunges"] = "突刺",
        ["dash"] = "衝", ["dashes"] = "衝",
        ["surge"] = "湧起", ["surges"] = "湧起",
        ["swell"] = "膨脹",
        ["overflow"] = "溢出", ["overflows"] = "溢出",
        ["spill"] = "灑出", ["spills"] = "灑出",
        ["gush"] = "湧出", ["gushes"] = "湧出",
        ["squirt"] = "噴射", ["squirts"] = "噴射",
        ["ejaculate"] = "噴射", ["ejaculates"] = "噴射",
        ["congeal"] = "凝結", ["congeals"] = "凝結",
        ["coagulate"] = "凝結", ["coagulates"] = "凝結",
        ["clot"] = "凝塊", ["clots"] = "凝塊",
        ["crystallize"] = "結晶", ["crystallizes"] = "結晶",
        ["condense"] = "凝縮", ["condenses"] = "凝縮",
        ["solidify"] = "凝固", ["solidifies"] = "凝固",
        ["liquefy"] = "液化", ["liquefies"] = "液化",
        ["vaporize"] = "蒸發", ["vaporizes"] = "蒸發",
        ["dissolve"] = "溶解", ["dissolves"] = "溶解",
        ["disperse"] = "散開", ["disperses"] = "散開",
        ["scatter"] = "散落", ["scatters"] = "散落",
        ["drift"] = "飄移", ["drifts"] = "飄移",
        ["float"] = "漂浮", ["floats"] = "漂浮",
        ["sink"] = "下沉", ["sinks"] = "下沉", ["sank"] = "下沉",
        ["drown"] = "淹沒", ["drowns"] = "淹沒",
        ["choke"] = "窒息", ["chokes"] = "窒息",
        ["suffocate"] = "窒息", ["suffocates"] = "窒息",
        ["gag"] = "作嘔", ["gags"] = "作嘔",
        ["vomit"] = "嘔吐", ["vomits"] = "嘔吐",
        ["retch"] = "乾嘔", ["retches"] = "乾嘔",
        ["cough"] = "咳嗽", ["coughs"] = "咳嗽",
        ["sneeze"] = "打噴嚏", ["sneezes"] = "打噴嚏",
        ["yawn"] = "打呵欠", ["yawns"] = "打呵欠",
        ["sigh"] = "嘆息", ["sighs"] = "嘆息",
        ["pant"] = "喘息", ["pants"] = "喘息",
        ["gasp"] = "倒吸氣", ["gasps"] = "倒吸氣",
        ["wheeze"] = "喘鳴", ["wheezes"] = "喘鳴",
        ["hiccup"] = "打嗝", ["hiccups"] = "打嗝",
        ["burp"] = "打嗝", ["burps"] = "打嗝",
        ["fart"] = "放屁", ["farts"] = "放屁",
        ["sweat"] = "流汗", ["sweats"] = "流汗",
        ["perspire"] = "流汗", ["perspires"] = "流汗",
        ["bleed"] = "流血", ["bleeds"] = "流血", ["bled"] = "流血",
        ["hemorrhage"] = "大量出血", ["hemorrhages"] = "大量出血",
        ["throb"] = "悸動",
        ["ache"] = "疼痛", ["aches"] = "疼痛",
        ["hurt"] = "疼痛", ["hurts"] = "疼痛",
        ["sting"] = "刺痛", ["stings"] = "刺痛",
        ["burn"] = "灼燒",
        ["itch"] = "發癢", ["itches"] = "發癢",
        ["tingle"] = "刺痛", ["tingles"] = "刺痛",
        ["numb"] = "麻木", ["numbs"] = "麻木",
        ["paralyze"] = "麻痺", ["paralyzes"] = "麻痺",
        ["petrify"] = "石化", ["petrifies"] = "石化",
        ["freeze"] = "凍結",
        ["slow"] = "減速", ["slows"] = "減速",
        ["hasten"] = "加速",
        ["exhaust"] = "耗盡", ["exhausts"] = "耗盡",
        ["tire"] = "疲勞", ["tires"] = "疲勞",
        ["weary"] = "疲憊", ["wearies"] = "疲憊",
        ["fatigue"] = "疲勞", ["fatigues"] = "疲勞",
        ["starve"] = "飢餓", ["starves"] = "飢餓",
        ["thirst"] = "口渴", ["thirsts"] = "口渴",
        ["dehydrate"] = "脫水", ["dehydrates"] = "脫水",
        ["nourish"] = "滋養", ["nourishes"] = "滋養",
        ["feed"] = "進食", ["feeds"] = "進食", ["fed"] = "進食",
        ["graze"] = "吃草",
        ["forage"] = "覓食", ["forages"] = "覓食",
        ["scavenge"] = "拾荒", ["scavenges"] = "拾荒",
        ["rummage"] = "翻找", ["rummages"] = "翻找",
        ["loot"] = "掠奪", ["loots"] = "掠奪",
        ["plunder"] = "掠奪", ["plunders"] = "掠奪",
        ["steal"] = "偷竊", ["steals"] = "偷竊", ["stole"] = "偷竊",
        ["pickpocket"] = "扒竊", ["pickpockets"] = "扒竊",
        ["rob"] = "搶劫", ["robs"] = "搶劫",
        ["buy"] = "購買", ["buys"] = "購買", ["bought"] = "購買",
        ["sell"] = "販賣", ["sells"] = "販賣", ["sold"] = "販賣",
        ["trade"] = "交易", ["trades"] = "交易",
        ["barter"] = "以物易物", ["barters"] = "以物易物",
        ["negotiate"] = "談判", ["negotiates"] = "談判",
        ["bargain"] = "討價還價", ["bargains"] = "討價還價",
        ["pay"] = "支付", ["pays"] = "支付", ["paid"] = "支付",
        ["earn"] = "賺取", ["earns"] = "賺取",
        ["spend"] = "花費", ["spends"] = "花費", ["spent"] = "花費",
        ["save"] = "儲存",
        ["hoard"] = "囤積", ["hoards"] = "囤積",
        ["store"] = "儲存", ["stores"] = "儲存",
        ["stockpile"] = "囤積", ["stockpiles"] = "囤積",
        ["repair"] = "修理", ["repairs"] = "修理",
        ["fix"] = "修理", ["fixes"] = "修理",
        ["maintain"] = "維護", ["maintains"] = "維護",
        ["build"] = "建造", ["builds"] = "建造", ["built"] = "建造",
        ["construct"] = "建造", ["constructs"] = "建造",
        ["fabricate"] = "製造", ["fabricates"] = "製造",
        ["manufacture"] = "製造", ["manufactures"] = "製造",
        ["produce"] = "生產", ["produces"] = "生產",
        ["create"] = "創造", ["creates"] = "創造",
        ["make"] = "製作", ["makes"] = "製作", ["made"] = "製作",
        ["mold"] = "塑形", ["molds"] = "塑形",
        ["sculpt"] = "雕刻", ["sculpts"] = "雕刻",
        ["carve"] = "雕刻", ["carves"] = "雕刻",
        ["etch"] = "蝕刻", ["etches"] = "蝕刻",
        ["engrave"] = "雕刻", ["engraves"] = "雕刻",
        ["inscribe"] = "銘刻", ["inscribes"] = "銘刻",
        ["paint"] = "塗抹",
        ["dye"] = "染色", ["dyes"] = "染色",
        ["varnish"] = "上漆", ["varnishes"] = "上漆",
        ["polish"] = "拋光", ["polishes"] = "拋光",
        ["grind"] = "研磨", ["grinds"] = "研磨",
        ["sharpen"] = "磨利", ["sharpens"] = "磨利",
        ["hone"] = "磨礪", ["hones"] = "磨礪",
        ["temper"] = "淬煉", ["tempers"] = "淬煉",
        ["forge"] = "鍛造", ["forges"] = "鍛造",
        ["smelt"] = "冶煉", ["smelts"] = "冶煉",
        ["melt"] = "熔化",
        ["cast"] = "澆鑄",
        ["weld"] = "焊接", ["welds"] = "焊接",
        ["solder"] = "焊接", ["solders"] = "焊接",
        ["rivet"] = "鉚接", ["rivets"] = "鉚接",
        ["assemble"] = "組裝", ["assembles"] = "組裝",
        ["disassemble"] = "拆解", ["disassembles"] = "拆解",
        ["dismantle"] = "拆除", ["dismantles"] = "拆除",
        ["demolish"] = "摧毀", ["demolishes"] = "摧毀",
        ["destroy"] = "摧毀", ["destroys"] = "摧毀",
        ["ruin"] = "毀壞", ["ruins"] = "毀壞",
        ["wreck"] = "破壞", ["wrecks"] = "破壞",
        ["break"] = "弄斷", ["breaks"] = "弄斷", ["broke"] = "弄斷",
        ["fracture"] = "斷裂", ["fractures"] = "斷裂",
        ["crack"] = "開裂",
        ["bend"] = "彎曲",
        ["twist"] = "扭曲", ["twists"] = "扭曲",
        ["warp"] = "翹曲", ["warps"] = "翹曲",
        ["distort"] = "扭曲", ["distorts"] = "扭曲",
        ["fold"] = "摺疊", ["folds"] = "摺疊",
        ["unfold"] = "展開", ["unfolds"] = "展開",
        ["roll"] = "捲動",
        ["wrap"] = "包裹", ["wraps"] = "包裹",
        ["unwind"] = "解開", ["unwinds"] = "解開",
        ["coil"] = "盤繞", ["coils"] = "盤繞",
        ["tangle"] = "纏繞", ["tangles"] = "纏繞",
        ["knot"] = "打結", ["knots"] = "打結",
        ["bind"] = "捆綁", ["binds"] = "捆綁",
        ["tie"] = "綁", ["ties"] = "綁",
        ["fasten"] = "繫緊", ["fastens"] = "繫緊",
        ["loosen"] = "鬆開", ["loosens"] = "鬆開",
        ["tighten"] = "收緊", ["tightens"] = "收緊",
        ["unlock"] = "解鎖", ["unlocks"] = "解鎖",
        ["lock"] = "鎖定", ["locks"] = "鎖定",
        ["open"] = "開啟", ["opens"] = "開啟", ["opened"] = "開啟",
        ["close"] = "關閉", ["closes"] = "關閉",
        ["shut"] = "關上", ["shuts"] = "關上",
        ["slam"] = "猛關", ["slams"] = "猛關",
        ["slide"] = "滑動",
        ["glide"] = "滑行",
        ["skid"] = "打滑", ["skids"] = "打滑",
        ["tilt"] = "傾斜", ["tilts"] = "傾斜",
        ["lean"] = "傾靠", ["leans"] = "傾靠",
        ["recline"] = "倚靠", ["reclines"] = "倚靠",
        ["hunch"] = "駝背", ["hunches"] = "駝背",
        ["stoop"] = "彎腰", ["stoops"] = "彎腰",
        ["crouch"] = "蹲下", ["crouches"] = "蹲下",
        ["kneel"] = "跪下",
        ["squat"] = "蹲伏", ["squats"] = "蹲伏",
        ["sit"] = "坐著", ["sits"] = "坐著", ["sat"] = "坐著",
        ["stand"] = "站著",
        ["rise"] = "站起", ["rises"] = "站起", ["rose"] = "站起",
        ["arise"] = "升起", ["arises"] = "升起",
        ["lay"] = "躺著",
        ["lie"] = "躺著",
        ["repose"] = "安息", ["reposes"] = "安息",
        ["recline"] = "倚靠",
        ["stretch"] = "伸展", ["stretches"] = "伸展",
        ["reach"] = "伸手",
        ["extend"] = "伸出", ["extends"] = "伸出",
        ["flex"] = "屈伸",
        ["grip"] = "緊握", ["grips"] = "緊握",
        ["grasp"] = "抓住", ["grasps"] = "抓住",
        ["hold"] = "握著",
        ["carry"] = "攜帶",
        ["lift"] = "舉起", ["lifts"] = "舉起",
        ["raise"] = "舉起", ["raises"] = "舉起",
        ["lower"] = "放下", ["lowers"] = "放下",
        ["hoist"] = "吊起", ["hoists"] = "吊起",
        ["drag"] = "拖動", ["drags"] = "拖動",
        ["haul"] = "拖運", ["hauls"] = "拖運",
        ["tow"] = "拖曳", ["tows"] = "拖曳",
        ["shove"] = "推",
        ["shove"] = "推擠",
        ["shove"] = "推擠",
        ["prod"] = "戳", ["prods"] = "戳",
        ["jab"] = "戳刺", ["jabs"] = "戳刺",
        ["thrust"] = "突刺", ["thrusts"] = "突刺",
        ["impale"] = "刺穿", ["impales"] = "刺穿",
        ["pierce"] = "刺穿", ["pierces"] = "刺穿",
        ["puncture"] = "刺破", ["punctures"] = "刺破",
        ["stab"] = "刺",
        ["lacerate"] = "撕裂", ["lacerates"] = "撕裂",
        ["mangle"] = "絞碎", ["mangles"] = "絞碎",
        ["maim"] = "殘害", ["maims"] = "殘害",
        ["mutilate"] = "殘害", ["mutilates"] = "殘害",
        ["dismember"] = "肢解", ["dismembers"] = "肢解",
        ["decapitate"] = "斬首", ["decapitates"] = "斬首",
        ["behead"] = "斬首", ["beheads"] = "斬首",
        ["strangle"] = "勒死", ["strangles"] = "勒死",
        ["choke"] = "窒息",
        ["suffocate"] = "窒息",
        ["smother"] = "悶死", ["smothers"] = "悶死",
        ["drown"] = "溺斃",
        ["burn"] = "燃燒",
        ["ignite"] = "點燃",
        ["torch"] = "火燒", ["torches"] = "火燒",
        ["roast"] = "烤", ["roasts"] = "烤",
        ["toast"] = "烘烤", ["toasts"] = "烘烤",
        ["grill"] = "燒烤", ["grills"] = "燒烤",
        ["cook"] = "烹飪", ["cooks"] = "烹飪",
        ["bake"] = "烘烤", ["bakes"] = "烘烤",
        ["boil"] = "煮沸",
        ["steam"] = "蒸", ["steams"] = "蒸",
        ["fry"] = "油炸", ["fries"] = "油炸",
        ["sear"] = "炙烤", ["sears"] = "炙烤",
        ["char"] = "燒焦",
        ["smoke"] = "煙燻", ["smokes"] = "煙燻",
        ["cure"] = "醃製", ["cures"] = "醃製",
        ["preserve"] = "保存", ["preserves"] = "保存",
        ["ferment"] = "發酵", ["ferments"] = "發酵",
        ["brew"] = "釀造", ["brews"] = "釀造",
        ["distill"] = "蒸餾", ["distills"] = "蒸餾",
        ["mix"] = "混合", ["mixes"] = "混合",
        ["stir"] = "攪拌",
        ["whisk"] = "攪打", ["whisks"] = "攪打",
        ["blend"] = "攪拌", ["blends"] = "攪拌",
        ["combine"] = "結合", ["combines"] = "結合",
        ["pour"] = "傾注",
        ["drizzle"] = "淋灑", ["drizzles"] = "淋灑",
        ["sprinkle"] = "撒", ["sprinkles"] = "撒",
        ["sprout"] = "萌芽",
        ["grow"] = "生長", ["grows"] = "生長", ["grew"] = "生長",
        ["cultivate"] = "培育", ["cultivates"] = "培育",
        ["plant"] = "種植", ["plants"] = "種植",
        ["sow"] = "播種", ["sows"] = "播種",
        ["harvest"] = "收穫",
        ["gather"] = "收集",
        ["pick"] = "採摘",
        ["pluck"] = "拔", ["plucks"] = "拔",
        ["uproot"] = "連根拔起", ["uproots"] = "連根拔起",
        ["trim"] = "修剪", ["trims"] = "修剪",
        ["prune"] = "修剪", ["prunes"] = "修剪",
        ["chop"] = "砍", ["chops"] = "砍",
        ["axe"] = "斧劈", ["axes"] = "斧劈",
        ["saw"] = "鋸", ["saws"] = "鋸",
        ["cut"] = "切割",
        ["slice"] = "切片", ["slices"] = "切片",
        ["dice"] = "切丁", ["dices"] = "切丁",
        ["mince"] = "剁碎", ["minces"] = "剁碎",
        ["grate"] = "刨碎", ["grates"] = "刨碎",
        ["shred"] = "撕碎", ["shreds"] = "撕碎",
        ["grate"] = "研磨",
        ["sand"] = "打磨", ["sands"] = "打磨",
        ["buff"] = "拋光", ["buffs"] = "拋光",
        ["scour"] = "擦亮", ["scours"] = "擦亮",
        ["rinse"] = "沖洗", ["rinses"] = "沖洗",
        ["wash"] = "清洗", ["washes"] = "清洗",
        ["wipe"] = "擦拭", ["wipes"] = "擦拭",
        ["dust"] = "撢塵", ["dusts"] = "撢塵",
        ["sweep"] = "清掃", ["sweeps"] = "清掃",
        ["mop"] = "拖地", ["mops"] = "拖地",
        ["polish"] = "擦亮",
        ["disinfect"] = "消毒", ["disinfects"] = "消毒",
        ["sterilize"] = "滅菌", ["sterilizes"] = "滅菌",
        ["purify"] = "淨化", ["purifies"] = "淨化",
        ["filter"] = "過濾", ["filters"] = "過濾",
        ["strain"] = "過濾", ["strains"] = "過濾",
        ["distill"] = "蒸餾",
        ["evaporate"] = "蒸發",
        ["crystallize"] = "結晶",
        ["precipitate"] = "沉澱", ["precipitates"] = "沉澱",
        ["sediment"] = "沉積", ["sediments"] = "沉積",
        ["settle"] = "沉澱", ["settles"] = "沉澱",
        ["clarify"] = "澄清", ["clarifies"] = "澄清",
        ["charge"] = "衝鋒", ["cast"] = "施放", ["reflect"] = "反射",
        ["graze"] = "擦傷", ["save"] = "拯救", ["pick"] = "撿起",
        ["roll"] = "滾動", ["glide"] = "滑翔", ["boil"] = "沸騰",
        ["melt"] = "融化", ["crack"] = "爆裂", ["stir"] = "攪動",
        ["pour"] = "傾倒", ["shove"] = "推擠", ["harvest"] = "收割",
        ["slip"] = "滑動", ["polish"] = "拋光", ["drown"] = "淹沒",
        ["take"] = "受到",
        // ==== 第二批補齊（base 遊戲動詞）====
        ["'re"] = "是", ["'ve"] = "已", ["aren't"] = "不是", ["cannot"] = "不能", ["can't"] = "不能",
        ["accidentally"] = "意外地", ["already"] = "已", ["gently"] = "輕輕地", ["suddenly"] = "突然",
        ["add"] = "添加", ["amputate"] = "截肢", ["appear"] = "出現", ["assume"] = "認為",
        ["attempt"] = "試圖", ["attune"] = "調諧", ["bandage"] = "包紮", ["bask"] = "享受著",
        ["blink"] = "眨眼", ["blur"] = "模糊", ["bump"] = "撞上", ["cause"] = "導致",
        ["cease"] = "停止", ["cleave"] = "劈開", ["click"] = "發出咔嗒聲", ["collide"] = "碰撞",
        ["contain"] = "包含", ["convert"] = "轉換", ["cool"] = "冷卻", ["decide"] = "決定",
        ["desecrate"] = "褻瀆", ["destabilize"] = "顛覆", ["detach"] = "分離", ["detonate"] = "引爆",
        ["disappear"] = "消失", ["disarm"] = "解除武裝", ["discorporate"] = "解體", ["eat"] = "吃",
        ["emerge"] = "浮現", ["erupt"] = "爆發", ["exit"] = "離開", ["expel"] = "驅逐",
        ["extinguish"] = "熄滅", ["fail"] = "失敗", ["flinch"] = "退縮", ["flip"] = "翻轉",
        ["flush"] = "沖洗", ["focus"] = "專注", ["gain"] = "獲得", ["get"] = "獲得",
        ["give"] = "給予", ["go"] = "前往", ["hook"] = "鉤住", ["identify"] = "辨識",
        ["ignore"] = "忽略", ["impel"] = "驅使", ["implode"] = "內爆", ["incense"] = "激怒",
        ["intimidate"] = "威嚇", ["jut"] = "突出", ["latch"] = "鎖上", ["light"] = "點亮",
        ["lop"] = "砍除", ["lose"] = "失去", ["match"] = "匹配", ["multiply"] = "倍增",
        ["ogle"] = "凝視", ["pass"] = "通過", ["phase"] = "相位轉移", ["plop"] = "撲通落地",
        ["pony"] = "小跑", ["press"] = "按壓", ["react"] = "反應", ["rebuke"] = "斥責",
        ["recharge"] = "充能", ["regenerate"] = "再生", ["remove"] = "移除", ["revert"] = "回復",
        ["seal"] = "密封", ["see"] = "看見", ["shift"] = "轉變", ["shimmer"] = "閃爍",
        ["shower"] = "撒下", ["slot"] = "插入", ["snore"] = "打鼾", ["spark"] = "迸發",
        ["spit"] = "吐出", ["staunch"] = "止血", ["stiffen"] = "僵硬", ["swap"] = "交換",
        ["tinker"] = "修理", ["unequip"] = "卸下", ["unseal"] = "解封", ["vanish"] = "消失",
        ["voice"] = "出聲", ["wade"] = "涉水", ["waver"] = "動搖", ["wince"] = "畏縮",
        ["lapse"] = "消退",
    };

    private static string ReadVerb(VariableContext context)
    {
        try
        {
            string p = null;
            try { p = context.GetParameter(0, null); } catch { }
            if (string.IsNullOrEmpty(p)) try { p = context.GetParameter(1, null); } catch { }
            if (string.IsNullOrEmpty(p)) try { p = context.FetchParameter(0, null); } catch { }
            if (string.IsNullOrEmpty(p)) try { p = context.GetParameter(0, "verb"); } catch { }
            if (string.IsNullOrEmpty(p)) try { p = context.FetchParameter(0, "verb"); } catch { }
            if (string.IsNullOrEmpty(p)) return null;
            p = p.Trim();
            int colon = p.IndexOf(':');
            if (colon > 0) p = p.Substring(0, colon);
            return p;
        }
        catch
        {
            return null;
        }
    }

    // 動詞查表公用：先直接查，查不到剝英文屈折尾（第三人稱 s/es/ies）再查。
    // 避免執行期 =verb:harvests= ->「收割s」這類殘留（VerbZh 只有 harvest）。
    private static readonly Dictionary<string, bool> BeVerbs =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            { "are", true }, { "is", true }, { "am", true },
            { "was", true }, { "were", true }, { "'re", true }, { "'s", true },
        };

    private static bool IsBeVerb(string verb)
    {
        if (string.IsNullOrEmpty(verb)) return false;
        bool v;
        return BeVerbs.TryGetValue(verb, out v) && v;
    }

    private static string LookupVerbZh(string verb)
    {
        if (string.IsNullOrEmpty(verb)) return null;
        string zh;
        if (VerbZh.TryGetValue(verb, out zh)) return zh;
        // 剝 -ies -> -y、-es -> -、-s -> -（僅在 base 存在時）
        string cand = null;
        if (verb.EndsWith("ies") && verb.Length > 4)
            cand = verb.Substring(0, verb.Length - 3) + "y";
        else if (verb.EndsWith("es") && verb.Length > 3)
            cand = verb.Substring(0, verb.Length - 2);
        else if (verb.EndsWith("s") && verb.Length > 3)
            cand = verb.Substring(0, verb.Length - 1);
        if (cand != null && VerbZh.TryGetValue(cand, out zh)) return zh;
        return null;
    }

    [VariableReplacer(new string[] { "verb", "Verb", "subject.verb", "object.verb", "player.verb" }, Default = "do", Override = true)]
    public static string Verb(VariableContext context, GameObject subject)
    {
        string verb = ReadVerb(context);
        string zh = LookupVerbZh(verb);
        if (string.IsNullOrEmpty(zh)) zh = verb;
        if (zh == null) zh = "do";
        Log("CALL verb verb='" + verb + "' -> '" + zh + "'");
        return zh;
    }

    [VariableReplacer(new string[] { "verb", "Verb", "subject.verb", "object.verb", "player.verb" }, Default = "do", Override = true)]
    public static string VerbNoun(VariableContext context, GenderedNoun noun)
    {
        string verb = ReadVerb(context);
        string zh = LookupVerbZh(verb);
        if (string.IsNullOrEmpty(zh)) zh = verb;
        if (zh == null) zh = "do";
        Log("CALL verb(GenderedNoun) verb='" + verb + "' -> '" + zh + "'");
        return zh;
    }

    // ============ Does（=subject.Does:X= 產生變位動詞，用於戰鬥/動作訊息） ============
    // 重要：遊戲原生 does: 輸出「主詞顯示名 + 動詞」（如 "The boulder is"）。
    // 此覆寫必須保留主詞名，否則 "=blocker.does:are= in the way" 會只剩「是 擋在路中間」
    // （主詞消失）。玩家主詞由模板其他部分處理，此處不重複加名。

    private static bool IsPlayerObject(GameObject obj)
    {
        try
        {
            if (obj == null) return false;
            return obj.IsPlayer();
        }
        catch
        {
            return false;
        }
    }

    private static string DoesZh(string verb, GameObject subject)
    {
        // be 動詞：中文語境「=X.Does:are= 已封印」的「是」多餘 → 只回主詞名（或不回）
        if (!string.IsNullOrEmpty(verb) && IsBeVerb(verb))
        {
            string name = (!IsPlayerObject(subject)) ? ZhName(subject) : "";
            return name; // 主詞名 + 空動詞（「石頭 已封印」），玩家主詞為空
        }
        string zh = LookupVerbZh(verb);
        if (string.IsNullOrEmpty(zh)) zh = verb;
        if (zh == null) zh = "do";
        string name2 = (!IsPlayerObject(subject)) ? ZhName(subject) : "";
        if (!string.IsNullOrEmpty(name2)) return name2 + " " + zh;
        return zh;
    }

    [VariableReplacer(new string[] { "does", "did", "Does", "subject.does", "object.does", "player.does", "blocker.does", "subject.Does", "object.Does", "blocker.Does" }, Default = "do", Override = true)]
    public static string Does(VariableContext context, GameObject subject)
    {
        string verb = ReadVerb(context);
        string r = DoesZh(verb, subject);
        Log("CALL does verb='" + verb + "' -> '" + r + "'");
        return r;
    }

    [VariableReplacer(new string[] { "does", "did", "Does", "subject.does", "object.does", "player.does", "blocker.does", "subject.Does", "object.Does", "blocker.Does" }, Default = "do", Override = true)]
    public static string DoesNoun(VariableContext context, GenderedNoun noun)
    {
        string verb = ReadVerb(context);
        if (IsBeVerb(verb)) return ""; // be 動詞：中文語境多餘
        string zh = LookupVerbZh(verb);
        if (string.IsNullOrEmpty(zh)) zh = verb;
        if (zh == null) zh = "do";
        Log("CALL does(GenderedNoun) verb='" + verb + "' -> '" + zh + "'");
        return zh;
    }

    // ============ thisCreature（此生物） ============

    [VariableReplacer(new string[] { "thisCreature", "ThisCreature", "subject.thisCreature" }, Default = "此生物", Capitalization = true, Override = true)]
    public static string ThisCreature(VariableContext context, GameObject obj)
    {
        Log("CALL thisCreature -> '此生物'");
        return "此生物";
    }

    // ============ 無冠詞（中文沒有 a/the） ============

    private static string ZhName(GameObject obj)
    {
        try
        {
            if (obj == null) return "";
            string n = obj.DisplayName;
            if (string.IsNullOrEmpty(n)) n = obj.ShortDisplayName;
            if (string.IsNullOrEmpty(n)) n = obj.BaseDisplayName;
            return n;
        }
        catch
        {
            return "";
        }
    }

    [VariableReplacer(new string[] { "a.name", "an" }, Default = "", Capitalization = true, Override = true)]
    public static string WithIndefiniteArticle(VariableContext context, GameObject obj)
    {
        string r = ZhName(obj);
        Log("CALL a.name -> '" + r + "'");
        return r;
    }

    [VariableReplacer(new string[] { "the.name" }, Default = "", Capitalization = true, Override = true)]
    public static string WithDefiniteArticle(VariableContext context, GameObject obj)
    {
        string r = ZhName(obj);
        Log("CALL the.name -> '" + r + "'");
        return r;
    }

    [VariableReplacer(Default = "the", Capitalization = true, Override = true)]
    public static string The(VariableContext context, GameObject obj)
    {
        Log("CALL the -> ''");
        return "";
    }

    [VariableReplacer(Default = "a", Capitalization = true, Override = true)]
    public static string A(VariableContext context, GameObject obj)
    {
        Log("CALL a -> ''");
        return "";
    }
}
