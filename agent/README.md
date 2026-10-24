# sfh_agent（注入代理）

`sfh_agent.c` 是注入游戏进程的 C 代理，负责 IL2CPP 内存读写与命名管道 `\\.\pipe\SFHTrainer`。
`sfh_agent.dll` 是它的构建产物，**构建 GUI 时会被嵌入 `SFHTrainer.exe`**，所以发布时只需要一个 exe。

## 重新构建

需要 64 位 MinGW-w64 的 `gcc`（推荐 [w64devkit](https://github.com/skeeto/w64devkit/releases)）：

```powershell
gcc -shared -O2 -o sfh_agent.dll sfh_agent.c
```

然后把 `sfh_agent.dll` 留在本目录，重新构建/发布 GUI 即可自动嵌入：

```powershell
cd ..\gui\SFHTrainer
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ..\..\dist
```

当代理有改动时，**必须重新发布 GUI**，否则 `dist\SFHTrainer.exe` 里还是旧的代理。

## 协议

代理与 GUI 之间的完整命令/响应格式见 [../docs/AGENT_PROTOCOL.md](../docs/AGENT_PROTOCOL.md)。
