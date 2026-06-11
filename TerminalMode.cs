using System.Diagnostics;

/// <summary>
/// 仿 Linux/Unix 命令行输入模式。
/// 支持指令：cd、ls、cc、./程序名，以及 -admin 启动参数。
/// </summary>
public static class TerminalMode
{
    /// <summary>当前工作目录</summary>
    public static string CurrentDirectory { get; private set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    /// <summary>处理终端输入后的结果类型</summary>
    public enum ActionResult
    {
        None,           // 无操作
        Refresh,        // 需要刷新显示（目录切换、ls 等）
        SwitchToEdit    // 切换到配置编辑器
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
            // 去掉 -admin 标志，保留其余部分
            processed = processed.Replace("-admin", "", StringComparison.OrdinalIgnoreCase).Trim();
        }

        // ── cc —— 取消启动命令（清除 admin 状态）──
        if (processed.Equals("cc", StringComparison.OrdinalIgnoreCase))
        {
            statusMessage = admin ? "Admin 标志已清除。" : "就绪。";
            return ActionResult.Refresh;
        }

        // ── ls —— 列出当前目录 ──
        if (processed.Equals("ls", StringComparison.OrdinalIgnoreCase))
        {
            return ActionResult.Refresh;
        }

        // ── cd .. —— 返回上级目录 ──
        if (processed.Equals("cd ..", StringComparison.OrdinalIgnoreCase) ||
            processed.Equals("cd..", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(CurrentDirectory);
            if (parent != null)
            {
                CurrentDirectory = parent.FullName;
                return ActionResult.Refresh;
            }
            statusMessage = "已在根目录。";
            return ActionResult.Refresh;
        }

        // ── cd <path> —— 进入指定目录 ──
        if (processed.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
        {
            string pathArg = processed[3..].Trim();

            // 去掉可能的引号
            if (pathArg.Length >= 2 && pathArg.StartsWith('"') && pathArg.EndsWith('"'))
                pathArg = pathArg[1..^1];

            string newPath;
            if (Path.IsPathRooted(pathArg))
                newPath = pathArg;
            else
                newPath = Path.GetFullPath(Path.Combine(CurrentDirectory, pathArg));

            if (Directory.Exists(newPath))
            {
                CurrentDirectory = newPath;
                return ActionResult.Refresh;
            }

            statusMessage = $"目录不存在: {pathArg}";
            return ActionResult.Refresh;
        }

        // ── ./程序名 或 .\程序名 —— 启动当前目录下的程序 ──
        string programName = processed;
        if (processed.StartsWith("./") || processed.StartsWith(".\\"))
            programName = processed[2..];

        string? fullPath = ResolveProgram(programName);

        if (fullPath != null)
        {
            LaunchProgram(fullPath, admin);
            string name = Path.GetFileName(fullPath);
            statusMessage = admin
                ? $"已启动: {name}（管理员权限）"
                : $"已启动: {name}";
            return ActionResult.Refresh;
        }

        // 未识别的命令
        statusMessage = $"未识别的命令: {processed}";
        return ActionResult.Refresh;
    }

    /// <summary>获取当前目录下的所有文件和文件夹（用于显示）</summary>
    public static List<DirectoryEntry> GetDirectoryContents()
    {
        var result = new List<DirectoryEntry>();

        try
        {
            foreach (var dir in Directory.GetDirectories(CurrentDirectory))
            {
                result.Add(new DirectoryEntry
                {
                    Name = Path.GetFileName(dir),
                    IsDirectory = true
                });
            }

            foreach (var file in Directory.GetFiles(CurrentDirectory))
            {
                result.Add(new DirectoryEntry
                {
                    Name = Path.GetFileName(file),
                    IsDirectory = false
                });
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (Exception) { }

        // 排序：文件夹在前，然后文件，均按名称不区分大小写
        result.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    public struct DirectoryEntry
    {
        public string Name;
        public bool IsDirectory;
    }

    #region Helpers

    /// <summary>解析程序路径：尝试在当前目录下匹配程序名</summary>
    private static string? ResolveProgram(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // 已经是完整路径
        if (Path.IsPathRooted(name))
        {
            if (File.Exists(name))
                return name;
            return null;
        }

        string fullPath = Path.GetFullPath(Path.Combine(CurrentDirectory, name));

        // 精确匹配
        if (File.Exists(fullPath))
            return fullPath;

        // 尝试追加常见扩展名
        string[] extensions = { ".exe", ".lnk", ".bat", ".cmd", ".com", ".msc" };
        foreach (string ext in extensions)
        {
            string withExt = fullPath + ext;
            if (File.Exists(withExt))
                return withExt;
        }

        return null;
    }

    /// <summary>启动程序（通过 cmd /c start 完全分离子进程）</summary>
    private static void LaunchProgram(string fullPath, bool asAdmin)
    {
        try
        {
            ProcessStartInfo psi;
            if (asAdmin)
            {
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"\" \"{fullPath}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"\" \"{fullPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC denied — silently ignored
        }
        catch (Exception)
        {
            // Other errors — silently ignored
        }
    }

    #endregion
}
