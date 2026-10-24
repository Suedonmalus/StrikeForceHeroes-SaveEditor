# SFH 修改器 —— GUI ↔ 注入代理 通信协议（v1）

本文件是 `sfh_agent.dll`（C++，注入游戏进程）与 `SFHTrainer.exe`（C# WinForms）之间的唯一契约。
GUI 作者按此实现即可，不需要了解 IL2CPP 内部结构。

## 1. 进程与注入

- 目标进程：`Strike Force Heroes.exe`（Steam appid 1981820）。
- 代理 DLL：`sfh_agent.dll`，与 GUI 同目录。
- 注入方式：`OpenProcess(PROCESS_ALL_ACCESS)` → `VirtualAllocEx` 写入 DLL 绝对路径（UTF-16）→
  `CreateRemoteThread(LoadLibraryW)`。注入成功后 DLL 自带线程会创建命名管道服务端。
- 管道名：`\\.\pipe\SFHTrainer`（本机、字节模式）。
- 编码：UTF-8；每条消息一行，以 `\n` 结尾；字段用 `\t` 分隔。
- 连接顺序：GUI 先注入，再 `NamedPipeClientStream.Connect(5000)`；若失败重试最多 10 次。
- 代理只在游戏处于可读取状态时返回数据；游戏未加载存档时 `STATE` 返回 `ERR 1 not-ready`。

## 2. 命令（GUI → 代理）

| 命令 | 说明 |
| --- | --- |
| `PING` | 连通性测试 |
| `STATE` | 读取完整状态（金钱、词条目录、物品列表） |
| `SETMONEY <int>` | 设置金钱，代理自动 clamp 到 0..999999 |
| `SETMOD <itemId> <slot> <modIndex> <rarity>` | 设置某物品某槽位的词条与词条稀有度 |
| `SETRARITY <itemId> <rarity>` | 设置武器自身稀有度（0..6） |
| `SETCAMO <itemId> <camoIndex>` | 设置武器皮肤；`-1` 表示无皮肤 |
| `SAVE` | 调用游戏 `SD.Save()` 落盘 |
| `REFRESH` | 尝试让界面刷新（当前实现：空操作，返回 OK） |
| `QUIT` | 停止代理线程（DLL 保持加载，不卸载） |

- `slot`：0..3。
- `rarity`：0=Normal(×1)，1=Rare(×1.5)，2=Epic(×2)，3=Legendary(×3)。词条强度就是稀有度倍率。
- 物品原本 0 词条时，`SETMOD` 会自动创建长度 4 的词条数组。
- 代理侧也有防线：`READY=0` 时 `SETMONEY` / `SETMOD` / `SAVE` 一律返回 `ERR 1 not-ready`，不会盲写内存。

## 3. 响应（代理 → GUI）

简单响应：`OK`、`OK <value>`、`PONG`、或 `ERR <code> <message>`。

错误码：

| code | 含义 |
| --- | --- |
| 1 | not-ready（游戏未加载存档 / 找不到 SD 实例） |
| 2 | bad-args |
| 3 | item-not-found |
| 4 | item-has-no-weapon-data（护甲/装备类物品，不能改武器词条） |
| 5 | index-invalid（mod 或 camo 序号越界） |
| 6 | write-failed |

### `STATE` 响应格式（严格按行）

```
STATE
MONEY	<int>
READY	<0|1>          ← 1=存档已完全加载，可安全修改；0=仍在加载
CURCLASS	<string>
RARITY	<int>	<string>	<string>        ← 共 4 行，稀有度名称 + 倍率文本
CAMOCOUNT	<n>
CAMO	<index>	<name>	<desc>	<rarity>
MODCOUNT	<n>
MOD	<index>	<name>	<desc>	<baseValue>	<weaponMask>	<rarityMask>
ITEMCOUNT	<m>
ITEM	<id>	<name>	<type>	<level>	<rarity>	<value>	<equippedMask>	<camoIndex>	<slotCount>	[<modIndex>	<modRarity>]*
END
```

- `MOD` 行按 index 升序；GUI 直接使用 index 回传，不要自己重排。
- `MOD.desc` 是**游戏自带中文语言表**里的描述（当前游戏语言为中文时），形如 `伤害@%`、`射速@%`；`@%` 是数值占位符，GUI 用 `baseValue×100` 替换后显示即可（不要显示英文名）。
- `ITEM.name` 是游戏中文名（如 `武士刀`、`G36`、`战术脚架`），来自 `WeaponData.GetLocalizedName()` / 语言表 `WeapName.*` / `EquipName.*`；游戏语言不是中文时回退为资产英文名。
- `weaponMask` / `rarityMask` 是词条适用范围的位掩码（十六进制字符串，如 `0x7FE`）。
- `ITEM.type` 为 `EItemType` 枚举值（见下）；`-1` 表示该物品没有武器数据。
- `ITEM.camoIndex` 是当前皮肤在 `CAMO` 目录里的序号；`-1` 表示无皮肤。
- `CAMO` 包含游戏内全部迷彩（含角色 / 工坊皮肤）：游戏本身允许把角色迷彩套在武器上（例如十字弩的「劫掠者」就是 Niz Medic 4）。
- `SETCAMO` / `SETMOD` 写入后，代理会调用游戏的 `ApplyModsLevelCamo` 把皮肤和词条真正套到武器数据上。
- `equippedMask` 位含义：1=当前职业主武器，2=副武器，4=装备/护甲。
- 数值字段全部为十进制整数/浮点文本；`value` 为物品价值（float，保留 2 位）。

### EItemType 枚举（GUI 显示用）

```
0 All, 1 Assault, 2 Machine, 3 Magnum, 4 Melee, 5 Misc,
6 Rocket, 7 Secondary, 8 Shield, 9 Shotgun, 10 Sniper
```

### 词条目录（游戏内共 20 条，名字固定）

Damage, Rate, Reload, Range, Aim, HeadDamage, CritDamage, CritChance, Ammo, AmmoSpare,
Multi, Jam, Explosive, Dash, Vision, Streak, Exp, DamageAir, DamageStreak, Block

## 4. 已知语义（GUI 文案可据此写）

- 金钱上限 999999（游戏显示上限）。
- 词条强度 = 稀有度：0/1/2/3 分别为基础值的 ×1 / ×1.5 / ×2 / ×3。
- 一条词条的数值 = `baseValue × 倍率`，例如 Damage 基础 4%，Legendary 为 12%。
- 修改后必须 `SAVE` 才能持久化；游戏下一次打开仓库/重载场景时界面会按新数据重建。
- 护甲/装备类物品（`type=-1`）不允许改词条，GUI 需把词条面板禁用。

## 5. 稳定性约定

- 代理内所有游戏内存读写都在自己的线程上完成；GUI 不要高频轮询（`STATE` 建议只在需要时调用，间隔 ≥ 500ms）。
- 代理对非法 index/itemId/rarity 一律返回 ERR，不写内存。
- `SETMOD` 成功后 GUI 应重新拉一次 `STATE` 以刷新显示。





