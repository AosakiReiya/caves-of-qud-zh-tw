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