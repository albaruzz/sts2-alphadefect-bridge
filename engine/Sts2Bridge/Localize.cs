using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using Sts2Headless;

namespace Sts2Bridge;

/// <summary>
/// Formats all combat &amp; out-of-combat candidate actions into clean, human-readable Simplified Chinese (zhs).
/// Works seamlessly regardless of whether the player's game client language is set to Chinese or English:
/// - When the game client is in Chinese, uses STS2's native SmartFormat output directly;
/// - When the game client is in English (or another non-CJK language), automatically loads STS2's built-in
///   `res://localization/zhs/&lt;table&gt;.json` dictionary from the game pack and substitutes DynamicVars!
/// </summary>
public static class BridgeLocalize
{
    static readonly Regex ImgTagRegex = new(@"\[img[^\]]*\](.*?)\[/img\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex BbTagRegex = new(@"\[/?[a-zA-Z0-9_=#.%+-]+(?:\s+[^\]]*)?\]", RegexOptions.Compiled);
    static readonly Regex PlaceholderRegex = new(@"\{([a-zA-Z0-9_]+)(?::[^{}]*)?\}", RegexOptions.Compiled);
    static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    static readonly FieldInfo? EventBtnLabelField = AccessTools.Field(typeof(NEventOptionButton), "_label");
    static readonly ConcurrentDictionary<string, Dictionary<string, string>> ZhsTables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set to true by Entry.Init() when loaded inside the real Godot game engine (where Godot native ptrcalls are bound).</summary>
    public static bool GodotRuntimeActive { get; set; }

    static Dictionary<string, string> GetZhsTable(string? table)
    {
        if (string.IsNullOrWhiteSpace(table)) return EmptyDict;
        return ZhsTables.GetOrAdd(table.Trim(), static tbl =>
        {
            // 1. Try Godot virtual pack path (res://localization/zhs/<table>.json) when running inside Godot engine
            if (GodotRuntimeActive)
            {
                try
                {
                    string resPath = $"res://localization/zhs/{tbl}.json";
                    if (Godot.FileAccess.FileExists(resPath))
                    {
                        string json = Godot.FileAccess.GetFileAsString(resPath);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                            if (parsed != null) return parsed;
                        }
                    }
                }
                catch { }
            }

            // 2. Fallback to extracted filesystem res/ directory (used in headless tests)
            try
            {
                string fsPath = Path.Combine(VirtualFs.ResDir, "localization", "zhs", $"{tbl}.json");
                if (File.Exists(fsPath))
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(fsPath));
                    if (parsed != null) return parsed;
                }
            }
            catch { }

            return EmptyDict;
        });
    }

    static readonly Dictionary<string, string> EmptyDict = new();

    static bool ContainsCjk(string s)
    {
        foreach (char ch in s)
        {
            if ((ch >= '\u4E00' && ch <= '\u9FFF') || (ch >= '\u3400' && ch <= '\u4DBF'))
                return true;
        }
        return false;
    }

    static string? LookupZhsRaw(string? table, string? key)
    {
        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(key)) return null;
        var dict = GetZhsTable(table);
        return dict.TryGetValue(key, out string? val) ? val : null;
    }

    static string SubstituteVars(string template, IReadOnlyDictionary<string, object>? vars)
    {
        if (vars == null || vars.Count == 0 || !template.Contains('{'))
            return template;

        return PlaceholderRegex.Replace(template, m =>
        {
            string key = m.Groups[1].Value;
            if (!vars.TryGetValue(key, out object? v) || v == null)
                return m.Value;

            return v switch
            {
                DynamicVar dv => FormatDecimal(dv.BaseValue),
                LocString subLoc => SafeLoc(subLoc),
                CardModel cm => CardName(cm),
                RelicModel rm => RelicName(rm),
                PotionModel pm => PotionName(pm),
                decimal dec => FormatDecimal(dec),
                int iv => iv.ToString(CultureInfo.InvariantCulture),
                long lv => lv.ToString(CultureInfo.InvariantCulture),
                float fv => fv.ToString("0.##", CultureInfo.InvariantCulture),
                double dv2 => dv2.ToString("0.##", CultureInfo.InvariantCulture),
                bool bv => bv ? "1" : "0",
                _ => CleanText(v.ToString()),
            };
        });
    }

    static string FormatDecimal(decimal d) =>
        d == decimal.Truncate(d)
            ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : d.ToString("0.##", CultureInfo.InvariantCulture);

    public static string CleanText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        string s = ImgTagRegex.Replace(raw, m =>
        {
            string path = m.Groups[1].Value.ToLowerInvariant();
            if (path.Contains("energy")) return "[能量]";
            if (path.Contains("star")) return "[星]";
            if (path.Contains("gold")) return "金币";
            if (path.Contains("card")) return "卡牌";
            if (path.Contains("potion")) return "药水";
            if (path.Contains("relic")) return "遗物";
            return "";
        });

        s = s.Replace("\r\n", " · ").Replace('\n', ' ').Replace('\r', ' ');
        s = BbTagRegex.Replace(s, "");
        s = MultiSpaceRegex.Replace(s, " ").Trim();
        return s;
    }

    /// <summary>
    /// Resolves a LocString into Simplified Chinese:
    /// 1) Tries native GetFormattedText(); if it already contains CJK, returns it directly (exact SmartFormat).
    /// 2) Otherwise looks up the exact (LocTable, LocEntryKey) in `res://localization/zhs/&lt;table&gt;.json`
    ///    and substitutes `loc.Variables` so English game clients still get 100% Chinese candidate labels!
    /// </summary>
    public static string SafeLoc(LocString? loc)
    {
        if (loc == null || loc.IsEmpty) return "";

        string nativeTxt = "";
        try { nativeTxt = CleanText(loc.GetFormattedText()); }
        catch
        {
            try { nativeTxt = CleanText(loc.GetRawText()); } catch { }
        }

        if (ContainsCjk(nativeTxt))
            return nativeTxt;

        try
        {
            if (LookupZhsRaw(loc.LocTable, loc.LocEntryKey) is { Length: > 0 } zhsRaw)
            {
                string subst = CleanText(SubstituteVars(zhsRaw, loc.Variables));
                if (!string.IsNullOrEmpty(subst))
                    return subst;
            }
        }
        catch { }

        return nativeTxt;
    }

    public static string CardName(CardModel? c)
    {
        if (c == null) return "卡牌";

        string name = "";
        try
        {
            string nativeTitle = CleanText(c.Title);
            if (ContainsCjk(nativeTitle))
                name = nativeTitle;
        }
        catch { }

        if (string.IsNullOrEmpty(name))
        {
            if (LookupZhsRaw("cards", $"{c.Id.Entry}.title") is { Length: > 0 } zhsCard)
                name = CleanText(zhsCard);
            else
                name = SafeLoc(c.TitleLocString);
        }

        if (string.IsNullOrEmpty(name))
            name = c.Id.Entry;

        if (c.IsUpgraded && !name.EndsWith('+'))
            name += c.CurrentUpgradeLevel > 1 ? $"+{c.CurrentUpgradeLevel}" : "+";

        if (c.Enchantment is { } enc)
        {
            string encName = SafeLoc(enc.Title);
            if (string.IsNullOrEmpty(encName) && LookupZhsRaw("enchantments", $"{enc.Id.Entry}.title") is { Length: > 0 } ze)
                encName = CleanText(ze);
            if (!string.IsNullOrEmpty(encName))
                name += $"·{encName}";
        }
        return name;
    }

    public static string RelicName(RelicModel? r)
    {
        if (r == null) return "遗物";
        string name = SafeLoc(r.Title);
        if (!ContainsCjk(name) && LookupZhsRaw("relics", $"{r.Id.Entry}.title") is { Length: > 0 } zr)
            name = CleanText(zr);
        return string.IsNullOrEmpty(name) ? r.Id.Entry : name;
    }

    public static string PotionName(PotionModel? p)
    {
        if (p == null) return "药水";
        string name = SafeLoc(p.Title);
        if (!ContainsCjk(name) && LookupZhsRaw("potions", $"{p.Id.Entry}.title") is { Length: > 0 } zp)
            name = CleanText(zp);
        return string.IsNullOrEmpty(name) ? p.Id.Entry : name;
    }

    public static string MonsterName(Creature t)
    {
        string name = "";
        try
        {
            string nativeName = CleanText(t.Name);
            if (ContainsCjk(nativeName))
                name = nativeName;
        }
        catch { }

        if (string.IsNullOrEmpty(name) && t.Monster != null)
        {
            if (LookupZhsRaw("monsters", $"{t.Monster.Id.Entry}.name") is { Length: > 0 } zm)
                name = CleanText(zm);
            else
                name = SafeLoc(t.Monster.Title);
        }

        if (string.IsNullOrEmpty(name))
        {
            try { name = CleanText(t.Name); } catch { }
        }
        return string.IsNullOrEmpty(name) ? (t.Monster?.Id.Entry ?? "敌人") : name;
    }

    public static string TargetSuffix(Creature? t, ICombatState? cs)
    {
        if (t == null) return "";
        if (t.IsPlayer) return " → 自己";

        string name = MonsterName(t);

        if (cs != null && t.Monster != null)
        {
            var aliveFoes = cs.Creatures.Where(c => !c.IsPlayer && c.IsAlive).ToList();
            int sameCount = aliveFoes.Count(c => c.Monster?.Id == t.Monster.Id);
            if (sameCount > 1)
            {
                int pos = aliveFoes.IndexOf(t);
                string posTag = pos >= 0 ? $"左{pos + 1}·" : "";
                return $" → {posTag}{name} ({t.CurrentHp}血)";
            }
        }
        return $" → {name} ({t.CurrentHp}血)";
    }

    public static string PlayCard(CardModel c, Creature? t, ICombatState? cs) =>
        $"打出「{CardName(c)}」{TargetSuffix(t, cs)}";

    public static string UsePotion(PotionModel p, Creature? t, ICombatState? cs) =>
        $"使用药水「{PotionName(p)}」{TargetSuffix(t, cs)}";

    public static string DiscardPotion(PotionModel p) =>
        $"丢弃药水「{PotionName(p)}」";

    public static string SelectCard(CardModel c, SelectInfo? info, bool upgradeAny)
    {
        string verb = "选择";
        if (upgradeAny || info?.PromptKey == "TO_UPGRADE")
            verb = "升级";
        else if (info?.PromptKey == "TO_REMOVE" || (info?.Method?.Contains("Remove", StringComparison.OrdinalIgnoreCase) == true))
            verb = "移除";
        else if (info?.PromptKey == "TO_TRANSFORM" || (info?.Method?.Contains("Transform", StringComparison.OrdinalIgnoreCase) == true))
            verb = "变化";
        else if (info?.PromptKey == "TO_EXHAUST" || (info?.Method?.Contains("Exhaust", StringComparison.OrdinalIgnoreCase) == true))
            verb = "消耗";
        else if (info?.PromptKey == "TO_DISCARD" || (info?.Method?.Contains("Discard", StringComparison.OrdinalIgnoreCase) == true))
            verb = "丢弃";
        else if (info?.PromptKey == "TO_ENCHANT" || info?.Enchantment != null)
            verb = "附魔";
        return $"{verb}「{CardName(c)}」";
    }

    public static string ConfirmSelection(int selectedCount, int minSelect)
    {
        if (selectedCount == 0 && minSelect == 0)
            return "跳过（不选卡牌）";
        return selectedCount > 0 ? $"确认选择（已选 {selectedCount} 张）" : "确认选择";
    }

    public static string CardRewardAlt(CardRewardAlternative alt)
    {
        string txt = SafeLoc(alt.Title);
        if (ContainsCjk(txt)) return txt;
        string id = alt.OptionId.ToUpperInvariant();
        string? za = LookupZhsRaw("card_reward_ui", $"OPTION_{id}.title") ?? LookupZhsRaw("card_reward_ui", $"OPTION_{id}.name");
        if (!string.IsNullOrEmpty(za))
            return CleanText(za);
        return id switch
        {
            "SKIP" => "跳过选牌",
            _ => string.IsNullOrEmpty(txt) ? alt.OptionId : txt,
        };
    }

    public static string Bundle(int index, IReadOnlyList<CardModel> cards) =>
        $"选择卡包 {index + 1}：{string.Join(" + ", cards.Select(c => $"「{CardName(c)}」"))}";

    public static string Reward(Reward r) => r switch
    {
        GoldReward g => $"领取 {g.Amount} 金币",
        PotionReward p => $"领取药水「{PotionName(p.Potion)}」",
        RelicReward rr => $"领取遗物「{RelicName(rr.Relic)}」",
        CardReward => "查看卡牌奖励",
        _ when r.GetType().Name == "SpecialCardReward" => "领取特殊卡牌奖励",
        _ => "领取战利品",
    };

    public static string EventOption(EventOption opt, Player me)
    {
        string title = SafeLoc(opt.Title);
        string desc = SafeLoc(opt.Description);

        // Fallback to TextKey lookup in zhs events.json / ancients.json if LocString wasn't populated
        if (!ContainsCjk(title) && !string.IsNullOrEmpty(opt.TextKey))
        {
            string? zt = LookupZhsRaw("events", $"{opt.TextKey}.title") ?? LookupZhsRaw("ancients", $"{opt.TextKey}.title");
            if (!string.IsNullOrEmpty(zt))
                title = CleanText(SubstituteVars(zt, opt.Title?.Variables ?? opt.Description?.Variables));
        }
        if (!ContainsCjk(desc) && !string.IsNullOrEmpty(opt.TextKey))
        {
            string? zd = LookupZhsRaw("events", $"{opt.TextKey}.description") ?? LookupZhsRaw("ancients", $"{opt.TextKey}.description");
            if (!string.IsNullOrEmpty(zd))
                desc = CleanText(SubstituteVars(zd, opt.Description?.Variables));
        }

        // Neow / Ancient RelicOption fallback (e.g. NEOW.pages.INITIAL.options.<RELIC>)
        if (opt.Relic != null)
        {
            if (string.IsNullOrEmpty(title) || !ContainsCjk(title))
                title = RelicName(opt.Relic);
            if (string.IsNullOrEmpty(desc) || !ContainsCjk(desc))
            {
                string ed = SafeLoc(opt.Relic.DynamicEventDescription);
                if (!string.IsNullOrEmpty(ed))
                    desc = ed;
                else
                    desc = SafeLoc(opt.Relic.DynamicDescription);
            }
        }

        // Fallback to NEventOptionButton rendered text if custom dynamic text was set directly on UI button
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(desc) && NEventRoom.Instance?.Layout is { } layout)
        {
            try
            {
                var btn = layout.OptionButtons.FirstOrDefault(b => ReferenceEquals(b.Option, opt));
                if (btn != null && EventBtnLabelField?.GetValue(btn) is { } lblObj)
                {
                    if (AccessTools.Method(lblObj.GetType(), "GetParsedText")?.Invoke(lblObj, null) is string parsed && !string.IsNullOrWhiteSpace(parsed))
                        desc = CleanText(parsed);
                    else if (AccessTools.Property(lblObj.GetType(), "Text")?.GetValue(lblObj) is string rawTxt && !string.IsNullOrWhiteSpace(rawTxt))
                        desc = CleanText(rawTxt);
                }
            }
            catch { }
        }

        string result;
        if (opt.IsProceed && string.IsNullOrEmpty(desc))
        {
            if (string.IsNullOrEmpty(title) || !ContainsCjk(title))
                result = "继续前进";
            else
                result = $"{title}（离开）";
        }
        else if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(desc))
        {
            result = string.Equals(title, desc, StringComparison.Ordinal) ? $"【{title}】" : $"【{title}】{desc}";
        }
        else if (!string.IsNullOrEmpty(title))
        {
            result = $"【{title}】";
        }
        else if (!string.IsNullOrEmpty(desc))
        {
            result = desc;
        }
        else
        {
            result = (opt.IsProceed || opt.TextKey == "PROCEED") ? "继续前进" : opt.TextKey;
        }

        try
        {
            if (opt.WillKillPlayer?.Invoke(me) == true)
                result += " [⚠致死]";
        }
        catch { }

        return result;
    }

    public static string RestOption(RestSiteOption opt, Player me)
    {
        string title = SafeLoc(opt.Title);
        if (!ContainsCjk(title))
        {
            string oid = opt.OptionId.ToUpperInvariant();
            string? zr = LookupZhsRaw("rest_site_ui", $"OPTION_{oid}.title") ?? LookupZhsRaw("rest_site_ui", $"OPTION_{oid}.name");
            if (!string.IsNullOrEmpty(zr))
                title = CleanText(zr);
            else
            {
                title = oid switch
                {
                    "HEAL" => "休息",
                    "SMITH" => "锻造升级",
                    "DIG" => "挖掘遗物",
                    "LIFT" => "举重增力",
                    "RECALL" => "回忆",
                    "MEND" or "RESTORE" => "恢复",
                    "EVOLVE" => "进化",
                    _ => string.IsNullOrEmpty(title) ? opt.OptionId : title,
                };
            }
        }

        if (opt is HealRestSiteOption)
        {
            try
            {
                var (actual, _, _) = RunSession.RestHealInfo(me);
                return $"营火：{title}（回复 {actual} 生命）";
            }
            catch { }
        }

        string desc = SafeLoc(opt.Description);
        if (!string.IsNullOrEmpty(desc) && ContainsCjk(desc) && opt is not SmithRestSiteOption)
            return $"营火：【{title}】{desc}";
        return $"营火：{title}";
    }

    public static string ShopBuy(MerchantEntry e) => e switch
    {
        MerchantCardRemovalEntry => $"购买：删牌服务（{e.Cost} 金币）",
        MerchantCardEntry c => $"购买卡牌「{CardName(c.CreationResult?.Card)}」（{e.Cost} 金币）",
        MerchantRelicEntry r => $"购买遗物「{RelicName(r.Model)}」（{e.Cost} 金币）",
        MerchantPotionEntry p => $"购买药水「{PotionName(p.Model)}」（{e.Cost} 金币）",
        _ => $"购买商品（{e.Cost} 金币）",
    };

    public static string MapPointTypeZh(MapPointType pt) => pt switch
    {
        MapPointType.Monster => "普通战斗",
        MapPointType.Elite => "精英战斗",
        MapPointType.Unknown => "未知事件(?)",
        MapPointType.Shop => "商人商店",
        MapPointType.RestSite => "休息处(火堆)",
        MapPointType.Treasure => "宝箱房",
        MapPointType.Boss => "首领(Boss)",
        MapPointType.Ancient => "先古之民",
        _ => pt.ToString(),
    };

    public static string MapMove(MapPoint p, int index, int totalCount)
    {
        string typeZh = MapPointTypeZh(p.PointType);
        string coordTag = $"第{p.coord.row + 1}层·列{p.coord.col + 1}";

        if (p.PointType is MapPointType.Boss or MapPointType.Ancient && totalCount == 1)
            return $"前往【{typeZh}】";

        if (totalCount <= 1)
            return $"前往【{typeZh}】（{coordTag}）";

        string lane = totalCount switch
        {
            2 => index == 0 ? "左路" : "右路",
            3 => index == 0 ? "左路" : index == 1 ? "中路" : "右路",
            _ => $"左起第{index + 1}/{totalCount}路",
        };
        return $"前往{lane}【{typeZh}】（{coordTag}）";
    }

    public static string MinigameCell(int x, int y, bool big) =>
        $"占卜 第{y + 1}行·第{x + 1}列（{(big ? "大范围3×3" : "单格精准")}）";
}
