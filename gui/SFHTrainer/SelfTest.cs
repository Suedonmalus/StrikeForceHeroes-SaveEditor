using System.IO;
using System.Text;
using SFHTrainer.Protocol;

namespace SFHTrainer;

/// <summary>
/// 无界面自测：直接驱动 <see cref="TrainerController"/>（与界面按钮走同一条代码路径），
/// 连本地 mock 管道服务端跑一遍 读取 → 设置金钱 → 写词条 → 清空 → 保存 → 错误翻译 → QUIT。
/// 运行方式：SFHTrainer.exe --selftest [--selftest-out 报告路径]
/// </summary>
public static class SelfTest
{
    public static string DefaultReportPath => Path.Combine(AppContext.BaseDirectory, "selftest-report.txt");

    /// <summary>自测专用管道名：默认不用生产名，避免误连到游戏里正在运行的代理。</summary>
    public const string MockPipeName = "SFHTrainerMock";

    public static async Task<int> RunHeadlessAsync(string reportPath, string pipeName = MockPipeName)
    {
        var results = new List<(string Step, bool Ok, string Detail)>();
        var log = new StringBuilder();

        var controller = new TrainerController(pipeName);
        controller.Logged += (msg, kind) => log.AppendLine($"[{kind}] {msg}");

        void Check(string step, bool ok, string detail) => results.Add((step, ok, detail));

        try
        {
            Check("代理 DLL 路径", true, controller.AgentDllPath);
            Check("生产管道名常量（协议 §1）", AgentClient.DefaultPipeName == "SFHTrainer",
                $"DefaultPipeName={AgentClient.DefaultPipeName}；本次自测连接 {pipeName}");

            var connected = await controller.ConnectWithRetryAsync();
            Check("连接 \\\\.\\pipe\\" + pipeName, connected, connected ? "已连接 mock 服务端" : "连接失败");
            if (!connected)
                return Finish(reportPath, results, log);

            // PING
            var pong = false;
            try
            {
                var reply = await controller.Client.SendAsync("PING", false, 4000);
                pong = reply.Count > 0 && reply[0].Trim() == "PONG";
            }
            catch (Exception ex) { log.AppendLine($"PING 异常: {ex.Message}"); }
            Check("PING → PONG", pong, pong ? "连通性正常" : "PONG 未返回");

            // STATE
            var stateOk = await controller.RefreshStateAsync();
            var state = controller.State;
            Check("STATE 解析", stateOk && state is not null,
                state is null
                    ? "state 为空"
                    : $"金钱={state.Money}；物品={state.Items.Count}；武器={state.Items.Count(i => i.IsWeapon)}；"
                      + $"词条={state.Mods.Count}；稀有度={state.Rarities.Count}；职业={state.CurrentClass}");

            if (state is null)
                return Finish(reportPath, results, log);

            Check("词条目录 20 条", state.Mods.Count == 20, string.Join(",", state.Mods.Select(m => m.Index)));
            Check("稀有度 4 档", state.Rarities.Count == 4,
                string.Join(" | ", state.Rarities.Select(r => $"{r.Index}:{r.Name}{r.MultiplierText}")));
            Check("存在 type=-1 物品", state.Items.Any(i => !i.IsWeapon),
                string.Join(",", state.Items.Where(i => !i.IsWeapon).Select(i => $"{i.Id}:{i.Name}")));

            // 协议 v1.1：READY；以及空名字回退
            Check("READY 字段已解析", true, $"READY={(state.Ready ? 1 : 0)}");
            var unnamed = state.Items.FirstOrDefault(i => string.IsNullOrWhiteSpace(i.Name));
            if (unnamed is not null)
                Check("空名字回退为「未命名武器 #id」",
                    DisplayNames.ItemName(unnamed) == $"未命名武器 #{unnamed.Id}",
                    DisplayNames.ItemName(unnamed));
            else
                Check("空名字回退为「未命名武器 #id」", true, "mock 未提供空名字物品，跳过");

            // 未就绪时：写命令必须被拦下，且代理返回 ERR 1 能翻译成中文
            if (!state.Ready)
            {
                var blockedWrite = !await controller.SetMoneyAsync(123);
                Check("READY=0 时 GUI 拦截写操作", blockedWrite, "SETMONEY 未发送");

                var err1 = false; var msg1 = "";
                try { await controller.Client.SendAsync("SETMONEY 123", false, 4000); }
                catch (AgentProtocolException ex) { err1 = ex.Code == 1 && ex.Message.Contains("游戏尚未就绪"); msg1 = ex.Message; }
                Check("ERR 1 → 游戏尚未就绪", err1, msg1);

                var waitStart = DateTime.UtcNow;
                while (!controller.GameReady && (DateTime.UtcNow - waitStart).TotalSeconds < 20)
                {
                    await Task.Delay(500);
                    await controller.RefreshStateAsync(silent: true);
                }
                Check("等待 READY 变为 1", controller.GameReady,
                    $"耗时 {(DateTime.UtcNow - waitStart).TotalSeconds:0.0} 秒");
                state = controller.State!;
            }

            // SETMONEY
            var moneyOk = await controller.SetMoneyAsync(888888);
            Check("SETMONEY 888888", moneyOk && controller.State?.Money == 888888,
                $"MONEY={controller.State?.Money}");

            // clamp：超上限由代理负责；这里只验证越界值在 GUI 侧被拦下
            var blocked = !await controller.SetMoneyAsync(1000000);
            Check("越界金额被 GUI 拦截", blocked, "1000000 未发送");

            // SETMOD ×4
            var weapon = state.Items.First(i => i.IsWeapon);
            var slots = new List<ModSlot>
            {
                new(0, 0, 3),
                new(1, 5, 2),
                new(2, 12, 1),
                new(3, 19, 0),
            };
            var applyOk = await controller.ApplyModsAsync(weapon.Id, slots);
            var after = controller.State?.Items.FirstOrDefault(i => i.Id == weapon.Id);
            var slotsOk = after is not null && after.Slots.Count == 4
                && after.Slots[0] == new ModSlot(0, 0, 3)
                && after.Slots[1] == new ModSlot(1, 5, 2)
                && after.Slots[2] == new ModSlot(2, 12, 1)
                && after.Slots[3] == new ModSlot(3, 19, 0);
            Check("SETMOD ×4 回读一致", applyOk && slotsOk,
                after is null ? "读不到物品" : string.Join("，", after.Slots.Select(s => $"{s.Slot}:{s.ModIndex}/{s.Rarity}")));

            // 词条摘要文本（界面显示用的那条）
            Check("词条摘要渲染", after is not null && DisplayNames.SlotSummary(after, controller.State!).Contains('×'),
                after is null ? "—" : DisplayNames.SlotSummary(after, controller.State!));

            // SAVE
            var saveOk = await controller.SaveAsync();
            Check("SAVE 成功并刷新", saveOk, $"刷新后 MONEY={controller.State?.Money}");

            // REFRESH（空操作）
            var refreshOk = await controller.RequestUiRefreshAsync();
            Check("REFRESH 返回 OK", refreshOk, "协议规定为当前空操作");

            // 错误码 → 中文
            var err3 = false; var msg3 = "";
            try { await controller.Client.SendAsync($"SETMOD 999999 0 0 0", false, 4000); }
            catch (AgentProtocolException ex) { err3 = ex.Code == 3 && ex.Message.Contains("找不到该物品"); msg3 = ex.Message; }
            Check("ERR 3 → 找不到该物品", err3, msg3);

            var noWeapon = state.Items.First(i => !i.IsWeapon);
            var err4 = false; var msg4 = "";
            try { await controller.Client.SendAsync($"SETMOD {noWeapon.Id} 0 0 0", false, 4000); }
            catch (AgentProtocolException ex) { err4 = ex.Code == 4 && ex.Message.Contains("没有武器数据"); msg4 = ex.Message; }
            Check("ERR 4 → 该物品没有武器数据", err4, msg4);

            var err5 = false; var msg5 = "";
            try { await controller.Client.SendAsync($"SETMOD {weapon.Id} 0 99 0", false, 4000); }
            catch (AgentProtocolException ex) { err5 = ex.Code == 5 && ex.Message.Contains("词条序号无效"); msg5 = ex.Message; }
            Check("ERR 5 → 词条序号无效", err5, msg5);

            var err2 = false; var msg2 = "";
            try { await controller.Client.SendAsync("SETMONEY abc", false, 4000); }
            catch (AgentProtocolException ex) { err2 = ex.Code == 2; msg2 = ex.Message; }
            Check("ERR 2 → 参数不合法", err2, msg2);

            // 进程检测（只读，不注入）
            var target = controller.DetectProcess();
            Check("进程检测（只读）", true, target is null ? "未检测到游戏进程（正常）" : $"检测到 PID {target.Pid}");

            // QUIT
            await controller.QuitAsync();
            Check("QUIT 并断开", !controller.Client.IsConnected, "管道已释放");
        }
        catch (Exception ex)
        {
            Check("自测异常", false, $"{ex.GetType().Name}: {ex.Message}");
            log.AppendLine(ex.ToString());
        }
        finally
        {
            try { await controller.DisposeAsync(); } catch { }
        }

        return Finish(reportPath, results, log);
    }

    static int Finish(string reportPath, List<(string Step, bool Ok, string Detail)> results, StringBuilder log)
    {
        var failed = results.Count(r => !r.Ok);
        var pass = failed == 0 && results.Count > 0;

        var sb = new StringBuilder();
        sb.AppendLine("# SFHTrainer 无界面自测报告（--selftest）");
        sb.AppendLine($"# 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# 结果: {(pass ? "PASS" : "FAIL")}（{results.Count - failed}/{results.Count} 通过）");
        sb.AppendLine();
        foreach (var (step, ok, detail) in results)
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}\t{step}\t{detail}");
        sb.AppendLine();
        sb.AppendLine("--- 完整日志 ---");
        sb.Append(log);

        try
        {
            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 报告写不出去时，仍用退出码表达结果
        }

        return pass ? 0 : 1;
    }
}
