using System.Globalization;

namespace SFHTrainer.Protocol;

/// <summary>词条目录中的一条 MOD（对应 <c>MOD</c> 行）。</summary>
public sealed class ModDef
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Desc { get; init; } = string.Empty;
    public string BaseValue { get; init; } = string.Empty;
    public string WeaponMask { get; init; } = string.Empty;
    public string RarityMask { get; init; } = string.Empty;
}

/// <summary>皮肤目录中的一条（对应 <c>CAMO</c> 行）。</summary>
public sealed class CamoDef
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Desc { get; init; } = string.Empty;
    public int Rarity { get; init; }
}

/// <summary>稀有度目录（对应 <c>RARITY</c> 行，共 4 行）。</summary>
public sealed class RarityDef
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public string MultiplierText { get; init; } = string.Empty;
}

/// <summary>某件物品某个槽位上的词条。Slot 由 ITEM 行中成对字段的位置决定。</summary>
public sealed record ModSlot(int Slot, int ModIndex, int Rarity);

/// <summary>一件物品（对应 <c>ITEM</c> 行）。</summary>
public sealed class ItemState
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Type { get; init; } = -1;
    public int Level { get; init; }
    public int Rarity { get; init; }
    public double Value { get; init; }
    public int EquippedMask { get; init; }
    public int CamoIndex { get; init; } = -1;
    public List<ModSlot> Slots { get; init; } = new();

    /// <summary>type &gt;= 0 才是武器（有武器数据）；-1 表示护甲/装备类，不可改词条。</summary>
    public bool IsWeapon => Type >= 0;
}

/// <summary>一次 STATE 响应的完整内容。</summary>
public sealed class GameState
{
    public int Money { get; init; }

    /// <summary>协议 v1.1：<c>READY 1</c> 表示存档已完全加载，可以安全修改；0 表示还在加载。</summary>
    public bool Ready { get; init; } = true;

    public string CurrentClass { get; init; } = string.Empty;
    public List<RarityDef> Rarities { get; init; } = new();
    public List<ModDef> Mods { get; init; } = new();
    public List<CamoDef> Camos { get; init; } = new();
    public List<ItemState> Items { get; init; } = new();

    public ModDef? FindMod(int index) => Mods.FirstOrDefault(m => m.Index == index);

    public CamoDef? FindCamo(int index) => Camos.FirstOrDefault(c => c.Index == index);

    public RarityDef? FindRarity(int index) => Rarities.FirstOrDefault(r => r.Index == index);
}

/// <summary>严格按 docs/AGENT_PROTOCOL.md §3 解析 <c>STATE</c> 响应块。</summary>
public static class StateParser
{
    public static GameState Parse(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || lines[0].Trim() != "STATE")
            throw new FormatException("STATE 响应第一行不是 STATE。");
        if (lines[^1].Trim() != "END")
            throw new FormatException("STATE 响应没有以 END 结束（数据被截断）。");

        var money = 0;
        var ready = 1;
        var curClass = string.Empty;
        var rarities = new List<RarityDef>();
        var mods = new List<ModDef>();
        var camos = new List<CamoDef>();
        var items = new List<ItemState>();
        int modCount = -1, itemCount = -1;

        for (var i = 1; i < lines.Count - 1; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            var f = line.Split('\t');
            switch (f[0])
            {
                case "MONEY":
                    Require(f, 2, line);
                    money = ParseInt(f[1], line);
                    break;

                case "READY":
                    // 协议 v1.1：MONEY 之后的 READY 行。老版本代理不发送时按「就绪」处理。
                    Require(f, 2, line);
                    ready = ParseInt(f[1], line);
                    break;

                case "CURCLASS":
                    Require(f, 2, line);
                    curClass = f[1];
                    break;

                case "RARITY":
                    Require(f, 4, line);
                    rarities.Add(new RarityDef
                    {
                        Index = ParseInt(f[1], line),
                        Name = f[2],
                        MultiplierText = f[3],
                    });
                    break;

                case "MODCOUNT":
                    Require(f, 2, line);
                    modCount = ParseInt(f[1], line);
                    break;

                case "CAMO":
                    Require(f, 5, line);
                    camos.Add(new CamoDef
                    {
                        Index = ParseInt(f[1], line),
                        Name = f[2],
                        Desc = f[3],
                        Rarity = ParseInt(f[4], line),
                    });
                    break;

                case "MOD":
                    // MOD <index> <name> <desc> <baseValue> <weaponMask> <rarityMask>
                    Require(f, 7, line);
                    mods.Add(new ModDef
                    {
                        Index = ParseInt(f[1], line),
                        Name = f[2],
                        Desc = f[3],
                        BaseValue = f[4],
                        WeaponMask = f[5],
                        RarityMask = f[6],
                    });
                    break;

                case "ITEMCOUNT":
                    Require(f, 2, line);
                    itemCount = ParseInt(f[1], line);
                    break;

                case "ITEM":
                    items.Add(ParseItem(f, line));
                    break;

                default:
                    // 响应里出现未知行：忽略，保持前向兼容。
                    break;
            }
        }

        // 数量字段用于自检：不一致说明响应被截断或代理拼装出错。
        if (modCount >= 0 && modCount != mods.Count)
            throw new FormatException($"MODCOUNT 声明 {modCount} 条词条，实际收到 {mods.Count} 条。");
        if (itemCount >= 0 && itemCount != items.Count)
            throw new FormatException($"ITEMCOUNT 声明 {itemCount} 件物品，实际收到 {items.Count} 件。");

        return new GameState
        {
            Money = money,
            Ready = ready != 0,
            CurrentClass = curClass,
            Rarities = rarities,
            Mods = mods,
            Camos = camos,
            Items = items,
        };
    }

    static ItemState ParseItem(string[] f, string line)
    {
        // ITEM <id> <name> <type> <level> <rarity> <value> <equippedMask> <camoIndex> <slotCount> [<modIndex> <modRarity>]*
        Require(f, 10, line);
        var camoIndex = ParseInt(f[8], line);
        var slotCount = ParseInt(f[9], line);
        var rest = f.Length - 10;
        if (rest < 0 || rest % 2 != 0)
            throw new FormatException($"ITEM 行的词条字段数量不是成对的：{line}");
        var pairs = rest / 2;
        if (slotCount >= 0 && pairs != slotCount)
            throw new FormatException($"ITEM 声明 slotCount={slotCount}，实际给出 {pairs} 组词条：{line}");

        var slots = new List<ModSlot>(pairs);
        for (var i = 0; i < pairs; i++)
        {
            slots.Add(new ModSlot(
                i,
                ParseInt(f[10 + i * 2], line),
                ParseInt(f[11 + i * 2], line)));
        }

        return new ItemState
        {
            Id = ParseInt(f[1], line),
            Name = f[2],
            Type = ParseInt(f[3], line),
            Level = ParseInt(f[4], line),
            Rarity = ParseInt(f[5], line),
            Value = ParseDouble(f[6], line),
            EquippedMask = ParseInt(f[7], line),
            CamoIndex = camoIndex,
            Slots = slots,
        };
    }

    static void Require(string[] fields, int min, string line)
    {
        if (fields.Length < min)
            throw new FormatException($"字段不足（需要 {min} 个，实际 {fields.Length} 个）：{line}");
    }

    static int ParseInt(string s, string line)
    {
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"无法解析整数「{s}」：{line}");
        return v;
    }

    static double ParseDouble(string s, string line)
    {
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"无法解析数值「{s}」：{line}");
        return v;
    }
}

