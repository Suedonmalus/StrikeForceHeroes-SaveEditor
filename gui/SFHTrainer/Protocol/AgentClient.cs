using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace SFHTrainer.Protocol;

/// <summary>
/// 命名管道客户端：连 <c>\\.\pipe\SFHTrainer</c>，UTF-8、<c>\n</c> 分行、<c>\t</c> 分列。
/// 所有读写都在后台任务里完成，调用方 await 即可，不会阻塞 UI 线程。
/// </summary>
public sealed class AgentClient : IAsyncDisposable
{
    /// <summary>协议规定的管道名。</summary>
    public const string DefaultPipeName = "SFHTrainer";

    static readonly Regex ErrPattern = new(@"^ERR\s+(\d+)(?:\s+(.*))?$", RegexOptions.Compiled);

    readonly string _pipeName;
    readonly SemaphoreSlim _gate = new(1, 1);

    NamedPipeClientStream? _pipe;
    StreamReader? _reader;
    StreamWriter? _writer;
    Channel<string>? _incoming;
    CancellationTokenSource? _readCts;
    Task? _readLoop;
    int _connected;
    int _disposed;

    public AgentClient(string pipeName = DefaultPipeName) => _pipeName = pipeName;

    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    public string PipePath => $@"\\.\pipe\{_pipeName}";

    /// <summary>连接断开时触发（参数：原因）。可能在后台线程上触发。</summary>
    public event Action<string>? Disconnected;

    /// <summary>
    /// 连接管道。失败抛 <see cref="IOException"/>。
    /// </summary>
    public async Task ConnectAsync(int connectTimeoutMs = 5000, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        await DisposeConnectionAsync().ConfigureAwait(false);

        var pipe = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        try
        {
            await pipe.ConnectAsync(connectTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            pipe.Dispose();
            throw new IOException($"{connectTimeoutMs} 毫秒内连不上管道 {PipePath}：{ex.Message}", ex);
        }

        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 8192, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        _incoming = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        _readCts = new CancellationTokenSource();
        Volatile.Write(ref _connected, 1);
        _readLoop = Task.Run(() => ReadLoopAsync(_reader, _incoming.Writer, _readCts.Token));
    }

    /// <summary>
    /// 发送一条命令并读取响应。返回响应行（<paramref name="expectBlock"/> 时包含直到 <c>END</c> 的所有行）。
    /// 收到 <c>ERR</c> 时抛 <see cref="AgentProtocolException"/>。
    /// </summary>
    public async Task<IReadOnlyList<string>> SendAsync(
        string command, bool expectBlock = false, int timeoutMs = 8000, CancellationToken ct = default)
    {
        if (IsConnected == false || _writer is null || _incoming is null)
            throw new InvalidOperationException("代理未连接，无法发送命令。");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsConnected || _writer is null || _incoming is null)
                throw new InvalidOperationException("代理未连接，无法发送命令。");

            await _writer.WriteLineAsync(command.AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);

            var first = await ReadLineAsync(_incoming.Reader, timeoutMs, ct).ConfigureAwait(false);
            if (TryParseError(first, out var protocolError))
                throw protocolError;

            var lines = new List<string> { first };
            if (expectBlock)
            {
                for (var i = 0; i < 200_000; i++)
                {
                    var line = await ReadLineAsync(_incoming.Reader, timeoutMs, ct).ConfigureAwait(false);
                    lines.Add(line);
                    if (line.Trim() == "END")
                        return lines;
                }
                throw new IOException("STATE 响应异常：没有在合理行数内收到 END。");
            }

            return lines;
        }
        finally
        {
            _gate.Release();
        }
    }

    static async Task<string> ReadLineAsync(ChannelReader<string> reader, int timeoutMs, CancellationToken ct)
    {
        try
        {
            return await reader.ReadAsync(ct)
                .AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct)
                .ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new IOException("管道已断开（代理线程退出或游戏关闭）。");
        }
        catch (TimeoutException)
        {
            throw new IOException($"等待代理响应超时（{timeoutMs} 毫秒）。");
        }
    }

    static bool TryParseError(string line, out AgentProtocolException error)
    {
        var m = ErrPattern.Match(line.Trim());
        if (!m.Success)
        {
            error = null!;
            return false;
        }
        error = new AgentProtocolException(int.Parse(m.Groups[1].Value), m.Groups[2].Success ? m.Groups[2].Value : "");
        return true;
    }

    /// <summary>判断简单响应是否成功（<c>OK</c> / <c>OK &lt;value&gt;</c> / <c>PONG</c>）。</summary>
    public static bool IsOk(string line)
    {
        var t = line.Trim();
        return t == "OK" || t.StartsWith("OK ", StringComparison.Ordinal) || t.StartsWith("OK\t", StringComparison.Ordinal) || t == "PONG";
    }

    async Task ReadLoopAsync(StreamReader reader, ChannelWriter<string> writer, CancellationToken ct)
    {
        var reason = "代理主动关闭了连接。";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    reason = "代理端关闭了管道（EOF）。";
                    break;
                }
                await writer.WriteAsync(line, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "本地已关闭连接。";
        }
        catch (ObjectDisposedException)
        {
            reason = "本地已关闭连接。";
        }
        catch (Exception ex)
        {
            reason = $"读取管道失败：{ex.Message}";
        }
        finally
        {
            writer.TryComplete();
            Interlocked.Exchange(ref _connected, 0);
            if (Volatile.Read(ref _disposed) == 0)
            {
                try { Disconnected?.Invoke(reason); } catch { /* 事件订阅方异常不能拖垮读线程 */ }
            }
        }
    }

    async Task DisposeConnectionAsync()
    {
        Volatile.Write(ref _connected, 0);

        try { _readCts?.Cancel(); } catch { }

        if (_readLoop is not null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }

        // 先放掉读写器再关管道：StreamWriter.Dispose() 会 flush，管道已关时会抛 ObjectDisposedException。
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        try { _reader?.Dispose(); } catch { }
        _reader = null;
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;

        _readCts?.Dispose();
        _readCts = null;
        _readLoop = null;
        _incoming = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await DisposeConnectionAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
