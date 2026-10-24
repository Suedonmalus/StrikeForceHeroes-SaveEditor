using System.Diagnostics;
using System.IO;
using SFHTrainer.Injection;
using SFHTrainer.Protocol;

namespace SFHTrainer;

public enum LogKind
{
    Info,
    Ok,
    Warn,
    Error,
}

/// <summary>
/// 全部业务逻辑：进程检测 → 注入 → 管道连接 → STATE 拉取 → 各命令 → 刷新。
/// 不含任何 UI 代码，MainForm 与自测程序都用它，保证自测覆盖的就是界面实际走的代码路径。
/// </summary>
public sealed class TrainerController : IAsyncDisposable
{
    public const int MoneyCap = 999999;
    public const int CommandTimeoutMs = 8000;

    readonly AgentClient _client;
    bool _wiredDisconnected;

    /// <param name="pipeName">
    /// 管道名。生产固定为协议规定的 <c>SFHTrainer</c>；只在自动化自测时用 <c>--pipe-name</c> 指向 mock 服务端，
    /// 避免自测误连到游戏里正在运行的代理。
    /// </param>
    public TrainerController(string pipeName = AgentClient.DefaultPipeName)
    {
        _client = new AgentClient(pipeName);
        AgentDllPath = AgentPayload.EnsureExtracted();
        AgentDllEmbedded = AgentPayload.IsEmbedded;
        _client.Disconnected += OnClientDisconnected;
        _wiredDisconnected = true;
    }

    public AgentClient Client => _client;

    /// <summary>代理 DLL 路径：嵌入资源释放到 %LOCALAPPDATA%\SFHTrainer\agent\，或回退到程序目录。</summary>
    public string AgentDllPath { get; }

    /// <summary>true = 代理来自 exe 内嵌资源（单文件分发）；false = 同目录外部 DLL。</summary>
    public bool AgentDllEmbedded { get; }

    public TargetProcess? Target { get; private set; }

    public GameState? State { get; private set; }

    /// <summary>日志（可能在后台线程触发，订阅方需自行切回 UI 线程）。</summary>
    public event Action<string, LogKind>? Logged;

    /// <summary>进程 / 连接 / 状态发生变化（可能在后台线程触发）。</summary>
    public event Action? StatusChanged;

    void Log(string message, LogKind kind) => Logged?.Invoke(message, kind);

    void RaiseStatus() => StatusChanged?.Invoke();

    void OnClientDisconnected(string reason)
    {
        Log($"代理连接已断开：{reason}", LogKind.Warn);
        RaiseStatus();
    }

    // ---------------------------------------------------------------- 进程

    /// <summary>重新检测目标进程（不启动、不关闭游戏）。</summary>
    public TargetProcess? DetectProcess()
    {
        Target = ProcessInjector.FindTarget();
        if (Target is null)
        {
            Log("未检测到游戏进程 Strike Force Heroes.exe —— 请先启动游戏，再点「重新检测」。", LogKind.Warn);
        }
        else
        {
            var title = string.IsNullOrWhiteSpace(Target.WindowTitle) ? string.Empty : $"，窗口「{Target.WindowTitle}」";
            Log($"已检测到游戏进程：{Target.ExeName}（PID {Target.Pid}{title}）。", LogKind.Ok);
        }
        RaiseStatus();
        return Target;
    }

    // ---------------------------------------------------------------- 注入 + 连接

    /// <summary>注入并连接。返回是否成功连上代理。</summary>
    public async Task<bool> InjectAndConnectAsync(CancellationToken ct = default)
    {
        if (_client.IsConnected)
        {
            Log("代理已经连接，无需重复注入。", LogKind.Info);
            return true;
        }

        var target = DetectProcess();
        if (target is null)
            return false;

        if (!File.Exists(AgentDllPath))
        {
            Log($"代理 DLL 缺失：{AgentDllPath}。请把 sfh_agent.dll 放到本程序同目录后重试。", LogKind.Error);
            RaiseStatus();
            return false;
        }

        Log($"正在注入 {Path.GetFileName(AgentDllPath)} → PID {target.Pid} ……", LogKind.Info);
        var result = await Task.Run(() => ProcessInjector.Inject(target.Pid, AgentDllPath), ct).ConfigureAwait(true);

        if (!result.Success)
        {
            var codeText = result.Win32Error != 0 ? $"（Win32 错误码 {result.Win32Error}）" : string.Empty;
            Log($"注入失败 @ {result.Stage}{codeText}：{result.Message}", LogKind.Error);
            RaiseStatus();
            return false;
        }

        Log(result.Message, LogKind.Ok);
        RaiseStatus();

        if (!await ConnectWithRetryAsync(ct).ConfigureAwait(true))
            return false;

        // 连上后先 PING 确认代理主循环活着，再拉 STATE。
        try
        {
            var reply = await _client.SendAsync("PING", false, 4000, ct).ConfigureAwait(true);
            var line = reply.Count > 0 ? reply[0].Trim() : string.Empty;
            if (line == "PONG") Log("代理连通性测试通过（PONG）。", LogKind.Ok);
            else Log($"代理返回了非预期响应：{line}", LogKind.Warn);
        }
        catch (Exception ex)
        {
            Log($"PING 失败：{Describe(ex)}", LogKind.Warn);
        }

        await RefreshStateAsync(ct).ConfigureAwait(true);
        RaiseStatus();
        return true;
    }

    /// <summary>按协议：连不上时最多重试 10 次。</summary>
    public async Task<bool> ConnectWithRetryAsync(CancellationToken ct = default)
    {
        const int maxAttempts = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _client.ConnectAsync(5000, ct).ConfigureAwait(true);
                Log($"已连接代理管道 {_client.PipePath}。", LogKind.Ok);
                RaiseStatus();
                return true;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                Log($"第 {attempt}/{maxAttempts} 次连接管道失败：{ex.Message}", LogKind.Warn);
                if (attempt == maxAttempts)
                {
                    Log("连接管道失败：代理没有在本机创建 \\\\.\\pipe\\SFHTrainer。"
                        + "请确认 sfh_agent.dll 是 64 位版本、注入成功，且游戏已进入主菜单/已读档。", LogKind.Error);
                    RaiseStatus();
                    return false;
                }
                try { await Task.Delay(600, ct).ConfigureAwait(true); } catch (OperationCanceledException) { return false; }
            }
        }
        return false;
    }

    /// <summary>不注入，只尝试连接（用于代理已在游戏里时手动重连）。</summary>
    public async Task<bool> ConnectOnlyAsync(CancellationToken ct = default)
    {
        if (_client.IsConnected) return true;
        var ok = await ConnectWithRetryAsync(ct).ConfigureAwait(true);
        if (ok) await RefreshStateAsync(ct).ConfigureAwait(true);
        return ok;
    }

    // ---------------------------------------------------------------- 命令

    /// <summary>存档是否已完全加载（协议 v1.1 的 READY）；没有数据时按未就绪处理。</summary>
    public bool GameReady => State?.Ready ?? false;

    /// <summary>拉取一次完整 STATE 并缓存。<paramref name="silent"/> 用于自动轮询时抑制成功日志。</summary>
    public async Task<bool> RefreshStateAsync(CancellationToken ct = default, bool silent = false)
    {
        if (!_client.IsConnected)
        {
            if (!silent) Log("代理未连接，无法读取 STATE。请先点「注入代理」。", LogKind.Warn);
            return false;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var lines = await _client.SendAsync("STATE", expectBlock: true, timeoutMs: 12_000, ct).ConfigureAwait(true);
            var state = StateParser.Parse(lines);
            var becameReady = State is not null && !State.Ready && state.Ready;
            State = state;
            sw.Stop();
            if (!silent)
            {
                Log($"已读取状态：金钱 {DisplayNames.Money(state.Money)}，物品 {state.Items.Count} 件"
                    + $"（武器 {state.Items.Count(i => i.IsWeapon)} 件），词条 {state.Mods.Count} 条，"
                    + $"职业 {state.CurrentClass}，就绪 {(state.Ready ? "是" : "否（存档加载中）")}（{sw.ElapsedMilliseconds} ms）。",
                    state.Ready ? LogKind.Ok : LogKind.Warn);
            }
            else if (becameReady)
            {
                Log("游戏已就绪（READY=1），可以修改了。", LogKind.Ok);
            }
            RaiseStatus();
            return true;
        }
        catch (AgentProtocolException ex)
        {
            Log($"读取状态失败：{ex.Message}", LogKind.Error);
        }
        catch (FormatException ex)
        {
            Log($"STATE 响应格式不符合协议：{ex.Message}", LogKind.Error);
        }
        catch (Exception ex)
        {
            Log($"读取状态失败：{Describe(ex)}", LogKind.Error);
        }
        RaiseStatus();
        return false;
    }

    /// <summary>SETMONEY（代理会把值 clamp 到 0..999999）。</summary>
    public async Task<bool> SetMoneyAsync(int money, CancellationToken ct = default)
    {
        if (!EnsureReady()) return false;
        if (money < 0 || money > MoneyCap)
        {
            Log($"金额 {money} 超出范围（0..{MoneyCap}），已取消发送。", LogKind.Warn);
            return false;
        }

        var ok = await SendSimpleAsync($"SETMONEY {money}", $"设置金钱为 {money}", "SETMONEY", ct).ConfigureAwait(true);
        if (ok) await RefreshStateAsync(ct).ConfigureAwait(true);
        return ok;
    }

    /// <summary>SETRARITY：改武器自身稀有度（0..6）。</summary>
    public async Task<bool> SetRarityAsync(int itemId, int rarity, CancellationToken ct = default)
    {
        if (!EnsureReady()) return false;
        if (rarity < 0 || rarity > 6)
        {
            Log($"稀有度 {rarity} 超出范围（0..6），已取消发送。", LogKind.Warn);
            return false;
        }
        var ok = await SendSimpleAsync($"SETRARITY {itemId} {rarity}",
            $"把物品 {itemId} 的稀有度设为 {DisplayNames.RarityName(State, rarity)}", "SETRARITY", ct).ConfigureAwait(true);
        if (ok) await RefreshStateAsync(ct).ConfigureAwait(true);
        return ok;
    }

    /// <summary>SETCAMO：改武器皮肤；camoIndex=-1 表示无皮肤。</summary>
    public async Task<bool> SetCamoAsync(int itemId, int camoIndex, CancellationToken ct = default)
    {
        if (!EnsureReady()) return false;
        var label = camoIndex < 0 ? "无皮肤" : (State?.FindCamo(camoIndex)?.Name ?? $"#{camoIndex}");
        var ok = await SendSimpleAsync($"SETCAMO {itemId} {camoIndex}",
            $"把物品 {itemId} 的皮肤设为「{label}」", "SETCAMO", ct).ConfigureAwait(true);
        if (ok) await RefreshStateAsync(ct).ConfigureAwait(true);
        return ok;
    }

    /// <summary>逐槽发送 SETMOD，最后统一刷新一次 STATE。</summary>
    public async Task<bool> ApplyModsAsync(int itemId, IReadOnlyList<ModSlot> slots, CancellationToken ct = default)
    {
        if (!EnsureReady()) return false;
        if (slots.Count == 0)
        {
            Log("没有要应用的槽位。", LogKind.Warn);
            return false;
        }

        var allOk = true;
        foreach (var slot in slots)
        {
            ct.ThrowIfCancellationRequested();
            var modName = State?.FindMod(slot.ModIndex)?.Name ?? $"#{slot.ModIndex}";
            try
            {
                var reply = await _client.SendAsync(
                    $"SETMOD {itemId} {slot.Slot} {slot.ModIndex} {slot.Rarity}",
                    expectBlock: false, timeoutMs: CommandTimeoutMs, ct).ConfigureAwait(true);
                var line = reply.Count > 0 ? reply[0].Trim() : string.Empty;
                if (AgentClient.IsOk(line))
                {
                    Log($"SETMOD 槽 {slot.Slot + 1} → {modName} / {DisplayNames.RarityLabel(State, slot.Rarity)}：{line}",
                        LogKind.Ok);
                }
                else
                {
                    allOk = false;
                    Log($"SETMOD 槽 {slot.Slot + 1} 返回非预期响应：{line}", LogKind.Warn);
                }
            }
            catch (AgentProtocolException ex)
            {
                allOk = false;
                Log($"SETMOD 槽 {slot.Slot + 1}（{modName}）失败：{ex.Message}", LogKind.Error);
                if (ex.Code == 1) break; // 游戏未就绪，后续槽位没有意义
            }
            catch (Exception ex)
            {
                allOk = false;
                Log($"SETMOD 槽 {slot.Slot + 1} 失败：{Describe(ex)}", LogKind.Error);
                break;
            }
        }

        await RefreshStateAsync(ct).ConfigureAwait(true);
        return allOk;
    }

    /// <summary>SAVE：调用游戏 SD.Save() 落盘。</summary>
    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        if (!EnsureReady()) return false;
        Log("正在让游戏写入存档（SAVE）……", LogKind.Info);
        var ok = await SendSimpleAsync("SAVE", "保存到游戏存档", "SAVE", ct).ConfigureAwait(true);
        if (ok) Log("存档写入完成。改动现在会保留到下次启动。", LogKind.Ok);
        await RefreshStateAsync(ct).ConfigureAwait(true);
        return ok;
    }

    /// <summary>REFRESH（当前实现为空操作）。</summary>
    public async Task<bool> RequestUiRefreshAsync(CancellationToken ct = default)
    {
        if (!EnsureConnected()) return false;
        return await SendSimpleAsync("REFRESH", "请求刷新游戏界面", "REFRESH", ct).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- 收尾

    /// <summary>发送 QUIT 并断开（窗口关闭时调用）。</summary>
    public async Task QuitAsync()
    {
        if (_client.IsConnected)
        {
            try
            {
                await _client.SendAsync("QUIT", false, 2000).ConfigureAwait(false);
                Log("已通知代理退出（QUIT）。", LogKind.Info);
            }
            catch (Exception ex)
            {
                Log($"发送 QUIT 失败（忽略）：{Describe(ex)}", LogKind.Warn);
            }
        }
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    bool EnsureConnected()
    {
        if (_client.IsConnected) return true;
        Log("代理未连接，命令未发送。请先点「注入代理」。", LogKind.Warn);
        return false;
    }

    /// <summary>写操作的前置检查：已连接 + 已读到状态 + READY=1。</summary>
    bool EnsureReady()
    {
        if (!EnsureConnected()) return false;
        if (State is null)
        {
            Log("还没有读到状态数据，请先点「刷新」。", LogKind.Warn);
            return false;
        }
        if (!State.Ready)
        {
            Log("游戏未就绪（READY=0，存档还在加载），写操作已取消；就绪后界面会自动启用。", LogKind.Warn);
            return false;
        }
        return true;
    }

    async Task<bool> SendSimpleAsync(string command, string human, string tag, CancellationToken ct)
    {
        try
        {
            var reply = await _client.SendAsync(command, false, CommandTimeoutMs, ct).ConfigureAwait(true);
            var line = reply.Count > 0 ? reply[0].Trim() : string.Empty;
            if (AgentClient.IsOk(line))
            {
                Log($"{tag} 成功（{human}）：{line}", LogKind.Ok);
                return true;
            }
            Log($"{tag} 返回非预期响应：{line}", LogKind.Warn);
            return false;
        }
        catch (AgentProtocolException ex)
        {
            Log($"{tag} 失败（{human}）：{ex.Message}", LogKind.Error);
            return false;
        }
        catch (Exception ex)
        {
            Log($"{tag} 失败（{human}）：{Describe(ex)}", LogKind.Error);
            return false;
        }
    }

    /// <summary>把异常翻译成给用户看的中文。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        AgentProtocolException p => p.Message,
        TimeoutException => "等待代理响应超时。",
        IOException io => io.Message,
        InvalidOperationException io => io.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    public async ValueTask DisposeAsync()
    {
        if (_wiredDisconnected)
        {
            _client.Disconnected -= OnClientDisconnected;
            _wiredDisconnected = false;
        }
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
