/// <summary>
/// 仿 Linux/Unix 命令行输入模式，以配置文件目录树为数据源。
/// 支持指令：cd、ls、cc、./程序名，以及 -admin 启动参数。
/// 与正则匹配模式共享同一套配置目录结构。
/// </summary>
public static class TerminalMode
{
    /// <summary>当前导航路径的面包屑显示（如 "~ / 工作 / 工具"）</summary>
    public static string CurrentPath
    {
        get
        {
            if (_pathStack.Count == 0)
                return "~";
            return "~ / " + string.Join(" / ", _pathStack);
        }
    }

    private static readonly List<string> _pathStack = new();

    /// <summary>处理终端输入后的结果类型</summary>
    public enum ActionResult
    {
        None,           // 无操作
        Refresh,        // 需要刷新显示
        SwitchToEdit    // 切换到配置编辑器
    }

    /// <summary>切换到终端模式时调用，确保从根层级开始</summary>
    public static void ResetToRoot()
    {
        _pathStack.Clear();
        // 确保回到配置树的根层级
        while (LoadConfig.CurrentLayer > 1)
            LoadConfig.GoBack();
    }

    /// <summary>
    /// 处理一行终端模式输入。返回需要执行的动作。
    /// </summary>
    public static ActionResult ProcessInput(string input, out string? statusMessage)
    {
        statusMessage = null;

        if (string.IsNullOrWhiteSpace(input))
            return ActionResult.None;

        string original = input.Trim();

        // ── 解析 -admin 启动参数 ──
        bool admin = false;
        string processed = original;
        if (processed.Contains("-admin", StringComparison.OrdinalIgnoreCase))
        {
            admin = true;
            processed = processed.Replace("-admin", "", StringComparison.OrdinalIgnoreCase).Trim();
        }

        // ── cc —— 取消启动命令（清除 admin 状态）──
        if (processed.Equals("cc", StringComparison.OrdinalIgnoreCase))
        {
            statusMessage = admin ? "Admin 标志已清除。" : "就绪。";
            return ActionResult.Refresh;
        }

        // ── ls —— 列出当前目录（配置项），Refresh 触发重新渲染 ──
        if (processed.Equals("ls", StringComparison.OrdinalIgnoreCase))
        {
            return ActionResult.Refresh;
        }

        // ── cd .. —— 返回上级配置层级 ──
        if (processed.Equals("cd ..", StringComparison.OrdinalIgnoreCase) ||
            processed.Equals("cd..", StringComparison.OrdinalIgnoreCase))
        {
            if (LoadConfig.CurrentLayer > 1)
            {
                LoadConfig.GoBack();
                if (_pathStack.Count > 0)
                    _pathStack.RemoveAt(_pathStack.Count - 1);
                return ActionResult.Refresh;
            }
            statusMessage = "已在根层级。";
            return ActionResult.Refresh;
        }

        // ── cd <名称> —— 进入指定配置文件夹 ──
        if (processed.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
        {
            string folderName = processed[3..].Trim();

            // 去掉可能的引号
            if (folderName.Length >= 2 && folderName.StartsWith('"') && folderName.EndsWith('"'))
                folderName = folderName[1..^1];

            var items = LoadConfig.GetDisplayItems();
            // 按名称查找文件夹（忽略大小写）
            var match = items.FirstOrDefault(i =>
                i.IsFolder && i.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));

            if (match.Name != null)
            {
                LoadConfig.NavigateInto(match.Index);
                _pathStack.Add(match.Name);
                return ActionResult.Refresh;
            }

            statusMessage = $"文件夹不存在: {folderName}";
            return ActionResult.Refresh;
        }

        // ── ./程序名 或 .\程序名 —— 启动当前层级下的配置项 ──
        string programName = processed;
        if (processed.StartsWith("./") || processed.StartsWith(".\\"))
            programName = processed[2..];

        var allItems = LoadConfig.GetDisplayItems();
        var found = allItems.FirstOrDefault(i =>
            !i.IsFolder && i.Name.Equals(programName, StringComparison.OrdinalIgnoreCase));

        if (found.Name != null)
        {
            var (result, name) = LoadConfig.LaunchItem(found.Index, admin);
            statusMessage = result switch
            {
                LoadConfig.LaunchResult.Success => admin
                    ? $"已启动: {name}（管理员权限）"
                    : $"已启动: {name}",
                LoadConfig.LaunchResult.NotFound => $"路径未找到: {name}",
                LoadConfig.LaunchResult.UacDenied => $"UAC 授权被拒绝: {name}",
                _ => $"启动失败: {name}"
            };
            return ActionResult.Refresh;
        }

        // 未识别的命令
        statusMessage = $"未识别的命令: {processed}";
        return ActionResult.Refresh;
    }
}

