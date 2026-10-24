using System.Globalization;
using System.Text;
using SFHTrainer.Protocol;

namespace SFHTrainer;

/// <summary>界面展示用的名称映射与格式化（只影响显示，不改协议字段）。</summary>
public static class DisplayNames
{
    /// <summary>EItemType → 中文（见 docs/AGENT_PROTOCOL.md §3）。</summary>
    public static string ItemType(int type) => type switch
    {
        0 => "全部",
        1 => "突击步枪",
        2 => "机枪",
        3 => "马格南",
        4 => "近战",
        5 => "杂项",
        6 => "火箭筒",
        7 => "副武器",
        8 => "护盾",
        9 => "霰弹枪",
        10 => "狙击枪",
        -1 => "护甲/装备",
        _ => $"未知({type})",
    };

    /// <summary>equippedMask 位含义：1=主武器，2=副武器，4=装备/护甲。</summary>
    public static string Equipped(int mask)
    {
        if (mask == 0) return "—";
        var parts = new List<string>();
        if ((mask & 1) != 0) parts.Add("主武器");
        if ((mask & 2) != 0) parts.Add("副武器");
        if ((mask & 4) != 0) parts.Add("装备");
        if (parts.Count == 0) parts.Add($"0x{mask:X}");
        return string.Join("/", parts);
    }

    /// <summary>稀有度名称（优先用 STATE 里的 RARITY 行）。</summary>
    public static string RarityName(GameState? state, int rarity)
    {
        var def = state?.FindRarity(rarity);
        if (def is not null && !string.IsNullOrEmpty(def.Name)) return def.Name;
        return rarity switch
        {
            0 => "普通", 1 => "稀有", 2 => "史诗", 3 => "传说",
            4 => "原型", 5 => "大便", 6 => "创意工坊", _ => $"#{rarity}",
        };
    }

    /// <summary>稀有度倍率文本（优先用 STATE 里的 RARITY 行，缺省按协议 0/1/2/3 → ×1/×1.5/×2/×3）。</summary>
    public static string RarityMultiplier(GameState? state, int rarity)
    {
        var def = state?.FindRarity(rarity);
        if (def is not null && !string.IsNullOrEmpty(def.MultiplierText)) return def.MultiplierText;
        return rarity switch { 0 => "×1", 1 => "×1.5", 2 => "×2", 3 => "×3", _ => "?" };
    }

    public static string RarityLabel(GameState? state, int rarity)
        => $"{RarityName(state, rarity)} {RarityMultiplier(state, rarity)}";

    // 20 条词条的中文短名（协议 §3 固定名单），用于列表摘要与兜底。
    static readonly Dictionary<string, string> ModChineseHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Damage"] = "伤害",
        ["Rate"] = "射速",
        ["Reload"] = "装填速度",
        ["Range"] = "射程",
        ["Aim"] = "精准度",
        ["HeadDamage"] = "爆头伤害",
        ["CritDamage"] = "暴击伤害",
        ["CritChance"] = "暴击率",
        ["AmmoClip"] = "弹匣容量",
        ["AmmoSpare"] = "备弹",
        ["Multi"] = "多重射击",
        ["Jam"] = "卡壳几率",
        ["ExplosiveRadius"] = "爆炸半径",
        ["Dash"] = "冲刺距离",
        ["Vision"] = "视野",
        ["Streak"] = "充能速度",
        ["Exp"] = "经验获取",
        ["DamageAir"] = "空中伤害",
        ["DamageStreak"] = "连杀伤害",
        ["Block"] = "格挡减伤",
    };

    public static string? ModChineseHint(string name)
        => ModChineseHints.TryGetValue(name, out var zh) ? zh : null;

    /// <summary>
    /// 词条下拉的推荐顺序：最常用的六个排在最前面
    /// （伤害 / 射速 / 精度 / 射程 / 弹匣容量 / 备弹），其余按协议里的原始顺序。
    /// 只影响下拉显示顺序，回传给代理的仍然是协议里的 Index。
    /// </summary>
    static readonly string[] PreferredModOrder =
    {
        "Damage", "Rate", "Aim", "Range", "AmmoClip", "AmmoSpare",
    };

    static int PreferredModRank(string? name)
    {
        if (string.IsNullOrEmpty(name)) return int.MaxValue / 2;
        for (var i = 0; i < PreferredModOrder.Length; i++)
            if (string.Equals(PreferredModOrder[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return PreferredModOrder.Length;
    }

    /// <summary>按「常用优先」重排词条列表。</summary>
    public static List<ModDef> SortMods(IEnumerable<ModDef> mods)
    {
        var list = mods.ToList();
        list.Sort((a, b) =>
        {
            var ra = PreferredModRank(a.Name);
            var rb = PreferredModRank(b.Name);
            if (ra != rb) return ra.CompareTo(rb);
            return a.Index.CompareTo(b.Index);
        });
        return list;
    }

    /// <summary>
    /// 皮肤下拉的顺序：稀有度从高到低（传说在最上、普通在最下），
    /// 同一稀有度内保持协议里的原始序号。
    /// </summary>
    public static List<CamoDef> SortCamos(IEnumerable<CamoDef> camos)
    {
        var list = camos.ToList();
        list.Sort((a, b) =>
        {
            if (a.Rarity != b.Rarity) return b.Rarity.CompareTo(a.Rarity);
            return a.Index.CompareTo(b.Index);
        });
        return list;
    }

    static bool HasCjk(string text)
    {
        foreach (var ch in text)
            if (ch >= 0x4E00 && ch <= 0x9FFF) return true;
        return false;
    }

    /// <summary>把基础值（如 0.04）格式化成百分比文本（4% / 2.5%）。</summary>
    static string PercentText(string baseValue)
    {
        if (!double.TryParse(baseValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return string.Empty;
        var pct = v * 100.0;
        var text = pct.ToString("0.#", CultureInfo.InvariantCulture);
        return text + "%";
    }

    /// <summary>词条短名（中文），用于摘要与下拉兜底。</summary>
    public static string ModShortName(ModDef mod)
    {
        var zh = ModChineseHint(mod.Name);
        if (zh is not null) return zh;
        if (!string.IsNullOrWhiteSpace(mod.Desc) && HasCjk(mod.Desc))
        {
            var cut = mod.Desc.Split("@%")[0].Trim().TrimEnd('，', '。', '：', ' ');
            if (cut.Length > 0) return cut;
        }
        return mod.Name;
    }

    /// <summary>
    /// 词条下拉的显示文本：只显示中文描述 + 基础百分比。
    /// 代理已经把游戏语言表里的中文描述（形如「伤害@%」）带回来了；
    /// 若代理没拿到中文（回退英文），就用中文短名兜底。
    /// </summary>
    public static string ModLabel(ModDef mod)
    {
        var pct = PercentText(mod.BaseValue);
        var desc = mod.Desc ?? string.Empty;
        if (HasCjk(desc))
        {
            var text = desc.Replace("@%", pct).Trim();
            return string.IsNullOrWhiteSpace(text) ? ModShortName(mod) : text;
        }

        var name = ModShortName(mod);
        return string.IsNullOrEmpty(pct) ? name : $"{name} {pct}";
    }

    /// <summary>武器列表里的词条摘要（中文短名 + 倍率）。</summary>
    public static string SlotSummary(ItemState item, GameState state)
    {
        if (!item.IsWeapon) return "（无武器数据，不可编辑）";
        if (item.Slots.Count == 0) return "无词条";

        var parts = new List<string>(item.Slots.Count);
        foreach (var slot in item.Slots)
        {
            var mod = state.FindMod(slot.ModIndex);
            var name = mod is null ? $"#{slot.ModIndex}" : ModShortName(mod);
            parts.Add($"{slot.Slot + 1}.{name}{RarityMultiplier(state, slot.Rarity)}");
        }
        return string.Join("  ", parts);
    }

    public static string Money(int money) => money.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>物品显示名：游戏内克隆对象的 name 可能为空，回退为「未命名武器 #id」。</summary>
    public static string ItemName(ItemState item)
        => string.IsNullOrWhiteSpace(item.Name) ? $"未命名武器 #{item.Id}" : item.Name;

    public static string Value(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}

