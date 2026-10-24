namespace SFHTrainer.Protocol;

/// <summary>
/// 协议层错误：对应 docs/AGENT_PROTOCOL.md 的 <c>ERR &lt;code&gt; &lt;message&gt;</c>。
/// Message 已经是中文提示，可直接展示给用户。
/// </summary>
public sealed class AgentProtocolException : Exception
{
    public int Code { get; }

    public AgentProtocolException(int code, string rawMessage)
        : base(BuildMessage(code, rawMessage))
    {
        Code = code;
        RawMessage = rawMessage ?? string.Empty;
    }

    public string RawMessage { get; }

    static string BuildMessage(int code, string? raw)
    {
        var zh = ErrorText(code);
        var detail = string.IsNullOrWhiteSpace(raw) ? string.Empty : $"（代理原文：{raw.Trim()}）";
        return $"[ERR {code}] {zh}{detail}";
    }

    /// <summary>把协议错误码翻译成中文提示。</summary>
    public static string ErrorText(int code) => code switch
    {
        1 => "游戏尚未就绪：代理找不到存档对象，请先进入游戏并读取存档，再点「刷新」。",
        2 => "命令参数不合法（参数个数或数值超出范围）。",
        3 => "找不到该物品（物品 ID 无效，或不在当前存档里）。",
        4 => "该物品没有武器数据（护甲 / 装备类），不能设置词条。",
        5 => "词条序号无效（必须是 0..19 之间的 MOD index）。",
        6 => "写入游戏内存失败。",
        _ => "未知错误。",
    };
}
