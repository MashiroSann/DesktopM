/// <summary>
/// 用户指令系统 —— 识别并以 "/" 开头的输入，路由到注册的指令处理器。
/// 便于以后加入新的指令：只需调用 Register() 即可。
/// </summary>
public static class CommandSystem
{
    /// <summary>指令执行后向主循环传递的结果</summary>
    public enum CommandResult { None, Refresh, EditConfig }

    public delegate void CommandHandler(string args);

    private static readonly Dictionary<string, CommandHandler> _commands =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上一次指令执行的结果（由 WaitForInput 消费后重置）</summary>
    public static CommandResult LastResult { get; set; } = CommandResult.None;

    /// <summary>注册一个指令。command 不含斜杠前缀，如 "edit"。</summary>
    public static void Register(string command, CommandHandler handler)
    {
        _commands[command] = handler;
    }

    /// <summary>
    /// 尝试将用户输入作为指令执行。
    /// 返回 true 表示输入以 "/" 开头且被成功处理；
    /// 返回 false 表示不是指令（交由其他模式处理）。
    /// 执行结果可通过 <see cref="LastResult"/> 获取。
    /// </summary>
    public static bool TryExecute(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.StartsWith('/'))
            return false;

        // 去掉开头的 '/'
        string cmd = input[1..];

        // 分离指令名和参数
        string cmdName;
        string args;
        int spaceIdx = cmd.IndexOf(' ');
        if (spaceIdx >= 0)
        {
            cmdName = cmd[..spaceIdx];
            args = cmd[(spaceIdx + 1)..].Trim();
        }
        else
        {
            cmdName = cmd;
            args = "";
        }

        if (_commands.TryGetValue(cmdName, out var handler))
        {
            handler(args);
            return true;
        }

        return false; // 未知指令
    }
}
