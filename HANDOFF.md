# 交接文档 · 战火英雄重置版存档修改器

> 整理时间：2026-10-24
> 项目状态：**可用，已发布**（v1.0.0），但 Release 附件需替换为最新版
> 制作依据：基于 **Deepseek-v4.1-flash1017** 制作

---

## 1. 这是什么

《Strike Force Heroes》（战火英雄，Steam appid `1981820`）的单机存档修改器。
绿色单文件 `SFHTrainer.exe`，注入游戏进程后改内存，再调用游戏自己的 `SD.Save()` 落盘。

| 项 | 值 |
| --- | --- |
| GitHub | <https://github.com/Suedonmalus/StrikeForceHeroes-SaveEditor> |
| 本地目录 | `F:\文档\DSH_work files\项目\战火英雄修改器` |
| 最新提交 | `ed9576f`（说明：词条只支持修改，不支持增减数量） |
| 发布产物 | `dist/SFHTrainer.exe`，419,942 字节 |
| SHA256 | `A8EE5A400913D40B55D8DF8DA3C328E277C90BC286895877ED015F4959767ECF` |
| 运行依赖 | Windows 10/11 x64 + .NET 8 Desktop Runtime（框架依赖发布） |

---

## 2. 当前能做什么

| 功能 | 状态 | 说明 |
| --- | --- | --- |
| 改金钱 | ✅ | 0–999999，游戏显示上限 |
| 改词条（4 个槽） | ✅ | 改类型 + 改词条稀有度（×1 / ×1.5 / ×2 / ×3） |
| 改武器稀有度 | ✅ | 普通/稀有/史诗/传说/原型/大便/创意工坊 |
| 改皮肤 | ✅ | 全部 81 个迷彩（含角色 / 工坊皮肤）；**保存后要退出游戏重进才看得到** |
| 保存到存档 | ✅ | 调用游戏 `SD.Save()`，仅在 `READY=1` 时允许 |

### 明确做不到的（不要当成 bug）

| 限制 | 原因 |
| --- | --- |
| **给「无词条」武器加词条** | 手工 `il2cpp_array_new` 造槽位能写进内存，但游戏一 `Save` 就崩（已实测复现） |
| **把词条数量删减到 0** | 同上，破坏存档结构 |
| 改武器名字 | 名字由 `WeaponData.GetLocalizedName()` 算，写模板字段会串型号 |

---

## 3. 架构

```
SFHTrainer.exe (C# WinForms, .NET 8)
  └─ 释放内置 sfh_agent.dll 到 %LOCALAPPDATA%\SFHTrainer\agent\
  └─ 远程线程 LoadLibraryW 注入游戏
  └─ 命名管道 \\.\pipe\SFHTrainer（UTF-8 文本行协议）

sfh_agent.dll (C, MinGW-w64)
  └─ GetProcAddress 从 GameAssembly.dll 取 il2cpp_* 导出
  └─ 读写托管对象内存 / il2cpp_runtime_invoke 调用游戏方法
```

### 目录

```
战火英雄修改器/
├── dist/SFHTrainer.exe      # 唯一分发文件（内置代理）
├── agent/
│   ├── sfh_agent.c          # 注入代理源码
│   ├── sfh_agent.dll        # 编译产物（构建 GUI 时嵌入）
│   └── README.md            # 重编译说明
├── gui/SFHTrainer/          # WinForms 源码
│   ├── MainForm.cs          # 主界面、应用逻辑
│   ├── DisplayNames.cs      # 中文名映射、排序规则
│   ├── TrainerController.cs # 协议封装
│   └── Protocol/            # 管道客户端、状态模型、错误文案
├── gui/test/mock_agent.py   # 无游戏时的假代理（联调 UI 用）
├── docs/AGENT_PROTOCOL.md   # 协议文档（含诊断命令）
├── README.md                # 面向用户的说明
└── HANDOFF.md               # 本文件
```

---

## 4. 关键实现知识（接手必读）

### 4.1 数据模型与偏移

`GI.SD`（静态字段）→ `SD` 实例：

| SD 偏移 | 含义 |
| --- | --- |
| +16 | 当前职业对象 |
| +24 | 金钱 |
| +40 | 职业数据字典 |
| +48 | 物品字典 `Dictionary<uint, SItemInfo>` |
| +168 | `Initialized`（READY 标志，必须为 1 才能写/保存） |

`SItemInfo`（**值类型 struct**，相对结构体起始的偏移）：

| 偏移 | 字段 |
| --- | --- |
| +0 | `WeaponData` 指针 |
| +8 | `EquipData` 指针 |
| +16 | `ItemID` |
| +24 | `RewardID` |
| +32 | `Value`（float） |
| +36 | `Level` |
| +40 | `Rarity` |
| +48 | `WeaponMods`（`SModInfo[]`） |
| +56 | `WeaponCamo`（`CamoData*`） |

> ⚠️ `il2cpp_class_get_field_from_name` + `il2cpp_field_get_offset` 返回值**含 16 字节对象头**，
> 所以上面这些偏移 = 反射值 − 16。不要直接用反射值。

`WeaponData`（**按武器型号共享的模板对象**，这点极其重要）：

| 偏移 | 字段 |
| --- | --- |
| +24 | `Name` |
| +32 | `PerfectName` |
| +48 | `Type` |
| +416 | `Level` |
| +420 | `Rarity` |
| +424 | `DamageScaled` |
| +456 | `Camo` |

**坑**：
- `WeaponData.Rarity` / `WeaponData.Camo` 是**全型号共享**的运行时字段；
- `GetLocalizedName()` 会读 `Rarity` → 写它会把武器名换掉（`.50 M107` → 「坦克毁灭者」）；
- 所以只写 `SItemInfo` 自己的字段，**不要碰模板**。

`SModInfo`：`{ ModData* ptr; int rarity; }`，元素 16 字节。
词条强度 = 稀有度倍率：`0→×1`、`1→×1.5`、`2→×2`、`3→×3`（4 以上 `GetValue` 返回 0）。

### 4.2 本地化

`Assets.SimpleLocalization.Scripts.LocalizationManager.Dictionary`（`Dictionary<string, Dictionary<string,string>>`）。
key 前缀：`Mod.<name>`、`WeapName.<asset>`、`EquipName.<asset>`、`CamoName.<asset>`、`CamoDesc.<asset>`、`RarityName.*`。
代理启动时把语言表抓成扁平数组供 GUI 用。

### 4.3 线程模型（重要，别改坏）

- 代理线程初始化时 `il2cpp_thread_attach`，**初始化完成后立刻 detach**；
- 每条命令执行时 attach，执行完立刻 detach；
- `DllMain(DLL_PROCESS_DETACH)` 时置 `g_running = 0`。

原因：线程如果常驻在 IL2CPP 托管线程列表里并阻塞在管道等待，游戏 shutdown 会等它 → **退出卡死（未响应）**。
这个坑已由提交 `38eb7a0` 修复，改动时别退回旧写法。

### 4.4 协议（详见 docs/AGENT_PROTOCOL.md）

- 命令：`PING` / `STATE` / `SETMONEY` / `SETMOD` / `SETRARITY` / `SETCAMO` / `SAVE` / `REFRESH` / `QUIT`
- 诊断：`DUMPCLASS <类名>`（字段名+偏移）、`DUMPWD <itemId>`（字段值）、`DUMPMETHODS <类名>`（方法名）
- 错误码：1 not-ready、2 bad-args、3 item-not-found、4 no-weapon-data、5 index-invalid、6 write-failed、7 no-mod-slot

---

## 5. 构建与调试

```powershell
# 1) 编译代理（需要 64 位 MinGW-w64 gcc）
cd agent
gcc -shared -O2 -o sfh_agent.dll sfh_agent.c

# 2) 发布单文件 GUI（代理会被嵌入 exe）
cd gui\SFHTrainer
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ..\..\dist
```

调试小工具（当时放在 `C:\temp`，可能已清理）：

- `pipe.ps1`：PowerShell 命名管道客户端，`powershell -File pipe.ps1 "STATE" "PING"`
- `inject.py`：按进程名注入 DLL
- **注意**：命名管道服务端只允许 1 个连接实例，GUI 开着时脚本会连不上，先关 GUI。

---

## 6. 已知问题 / 待办

| 优先级 | 事项 | 线索 |
| --- | --- | --- |
| 高 | **Release v1.0.0 附件里的 `SFHTrainer.exe` 是旧版（419,430 字节）**，缺退出卡死修复 | 到 Releases → Edit → 删旧附件 → 拖 `F:\下载\SFHTrainer.exe`（419,942）→ Update |
| 中 | 给「无词条」武器加词条 | 手工造数组必崩。可研究 `SD.AddItem`（6 个重载）/ `CreateRandomShopItem` / `GetRandomWeapon`，看能否走游戏自己的生成流程；`SItemInfo` 只有 `ClearStats` / `ToSaveInfo` 两个方法，`WeaponData` 有 `ApplyModsLevelCamo` / `GetMod` |
| 低 | 皮肤是否对所有武器都生效 | 已能写入并持久化，但游戏内显示效果只由用户肉眼验证过一部分 |
| 低 | GitHub 账号 2FA 未启用 | 2026-12-02 前不启用会被限制推送等操作 |

---

## 7. 风险与注意事项

- 只改**自己的单机存档**；联机使用有封号风险；
- 改动前先备份 `%USERPROFILE%\AppData\LocalLow\Sky9 Games\Strike Force Heroes\`；
- 游戏退出时如果出现「未响应」，先确认用的是 **419,942 字节的新版**；旧版有这个已修复的 bug；
- 杀毒软件会对「释放 DLL + 注入改内存」报可疑，属正常误报，源码无任何网络功能；
- 手工造数组那次崩溃**没有损坏存档**（崩溃时存档未落盘，且游戏有 `SaveGame_Backup*` 自备份）。

---

## 8. 会话之外的话

- 代理源码里保留了 `DUMPCLASS` / `DUMPWD` / `DUMPMETHODS` 三个诊断命令，体积可忽略，留着排查方便；
- `gui/test/mock_agent.py` 可以在没有游戏的情况下把 UI 跑起来（假数据），改界面时很有用；
- 项目没有 git 子模块、没有外部构建脚本，`git clone` + 上面两条命令即可重现。
