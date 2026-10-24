# 战火英雄重置版存档修改器（SFHTrainer）

《Strike Force Heroes》（战火英雄）单机存档修改器：改金钱、改武器词条 / 稀有度 / 皮肤，然后让游戏把存档写回磁盘。

> 基于 **Deepseek-v4.1-flash1017** 制作。

- **绿色单文件**：只有一个 `SFHTrainer.exe`，注入代理已内嵌，复制到哪都能运行，不需要安装。
- **只改你自己的单机存档**：不联网、不改游戏文件。
- 联机对局使用有封号风险，请只在单机模式用。

---

## 文件

| 文件 | 作用 |
| --- | --- |
| `dist/SFHTrainer.exe` | 修改器主程序（.NET 8 WinForms，单文件，内含注入代理） |

源码（可选，想自己重新构建时用）：

- `agent/`：注入代理 C 源码 + 编译好的 `sfh_agent.dll`（构建 GUI 时会嵌入 exe）
- `gui/`：WinForms 界面源码与自测脚本
- `docs/`：GUI ↔ 代理通信协议、GUI 开发说明

---

## 软件依赖

| 依赖 | 为什么需要 | 怎么装 |
| --- | --- | --- |
| **Windows 10 / 11 x64** | 修改器和代理都是 x64 程序 | 不支持 32 位系统 / Linux / macOS |
| **.NET 8 Desktop Runtime（x64）** | `SFHTrainer.exe` 是 .NET 8 WinForms 程序，缺了打不开 | [下载页](https://dotnet.microsoft.com/download/dotnet/8.0) 选 **Desktop Runtime** x64；直链：<https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe>。不需要 ASP.NET Core / SDK，一路「安装」即可 |

`.NET 9` 之类更高版本不能替代 .NET 8 桌面运行时（Windows 上运行时按大版本并存）。

---

## 使用

1. **先备份存档**（见下方「注意事项」），然后启动游戏，进到主菜单或读档完成，让存档完全加载。
2. 双击 `SFHTrainer.exe`（放哪都行，不需要安装）。
3. 点「注入代理」：程序会把内嵌的代理释放到 `%LOCALAPPDATA%\SFHTrainer\agent\`，注入游戏并连上命名管道。
4. 注入成功后：

   | 面板 | 说明 |
   | --- | --- |
   | 金钱 | 输入 0–999999 点「应用金额」，或直接点「设为 999999」 |
   | 武器列表 | 列出当前存档物品，名称显示游戏内中文；上方「分类」可按类型筛选；护甲/装备类不可改词条 |
   | 词条槽 | 每件武器 4 个槽位，词条下拉为中文描述 + 基础数值（如「伤害4%」），再选词条稀有度 |
   | 武器稀有度 | 单独下拉：普通 / 稀有 / 史诗 / 传说 / 原型 / 大便 / 创意工坊 |
   | 皮肤 | 游戏内全部迷彩（北极、沙漠、丛林、数码……也包括角色/工坊皮肤），也可以选「无皮肤」 |

   词条强度就是词条稀有度倍率：`普通 ×1`、`稀有 ×1.5`、`史诗 ×2`、`传说 ×3`（例如 Damage 基础 4%，传说为 12%）。
5. 点「应用到该武器」写入稀有度、皮肤和 4 个槽位，再点 **「保存到游戏存档」**（调用游戏自己的 `SD.Save()`）。
   **不点保存，改动只在内存里，退出游戏就没了。**

> 游戏未加载完存档时界面会显示「未就绪」并禁用写操作，等 READY 变绿即可。

---

## 卸载

绿色软件，没有安装过程：

1. 删除 `SFHTrainer.exe`。
2. 可选：删除运行缓存 `%LOCALAPPDATA%\SFHTrainer\`（代理释放文件）。

不会留下注册表项、服务或开机启动项。

---

## 注意事项（很重要）

- **只在单机模式下使用。** 联机使用有封号风险，也可能因为数据异常被判异常。
- **先备份存档再改。** 本作存档在 `%USERPROFILE%\AppData\LocalLow\Sky9 Games\Strike Force Heroes\`
  （`SaveGame.sfh` 及若干 `SaveGame_Backup*.sfh`），把整个目录复制一份，或先在 Steam 里**关闭云同步**再操作。
  本修改器不提供自动备份。
- 注入需要和游戏**同架构（x64）**；默认以当前用户权限运行即可。只有游戏本身是以管理员身份启动的，
  才需要右键「以管理员身份运行」`SFHTrainer.exe`。
- 杀毒软件可能对「释放 DLL + 注入改内存」报可疑行为，这是这类工具的常态；请在杀软里给 exe 加白名单。
  源码里没有任何网络功能。
- 不要把这个工具放进游戏目录，也不要放到 `C:\Windows`、`C:\Program Files`、`C:\ProgramData`。

---

## 目录结构

```
战火英雄修改器/
├── dist/
│   └── SFHTrainer.exe          # 唯一需要分发的文件
├── agent/                      # 注入代理源码 + 编译产物（构建时嵌入 exe）
│   ├── sfh_agent.c
│   ├── sfh_agent.dll
│   └── README.md               # 重新编译代理的说明
├── gui/                        # WinForms 源码、自测 mock、界面截图
├── docs/                       # 通信协议、GUI 开发说明
└── README.md
```

---

## 常见问题

| 现象 | 原因 / 处理 |
| --- | --- |
| 双击没反应，或提示缺 .NET | 装 [.NET 8 Desktop Runtime x64](https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe)（不是 SDK、不是 ASP.NET Core） |
| 注入失败 | 先确认游戏已启动并读档；确认 `%LOCALAPPDATA%\SFHTrainer\agent` 可写；必要时以管理员身份运行 |
| 点注入后一直「未连接」 | 游戏可能还在加载；等主菜单/读档完成再点一次「重新注入」 |
| 改完没生效 | 忘了点「保存到游戏存档」；或者重新进仓库 / 重载场景才刷新显示 |
| 杀软拦截 / SmartScreen 提示 | 无数字签名的注入类工具常见误报；确认来源可信后选择「仍要运行」或加白名单 |

---

## 从源码构建（可选）

- 界面：需要 **.NET 8 SDK**。在 `gui/SFHTrainer` 下执行：

  ```powershell
  dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ..\..\dist
  ```

- 代理：需要 64 位 MinGW-w64 的 `gcc`，编译方法见 [agent/README.md](agent/README.md)。
  代理有改动时必须重新发布 GUI，否则 exe 里还是旧代理。
- 构建时 `agent/sfh_agent.dll` 会作为嵌入资源打进 exe，所以发布只需一个文件。

通信协议见 [docs/AGENT_PROTOCOL.md](docs/AGENT_PROTOCOL.md)。

