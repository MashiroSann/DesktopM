using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

[SupportedOSPlatform("windows")]
public static class Install
{
    #region Native Interop

    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_MOUSE_INPUT = 0x0010;
    private const uint ENABLE_WINDOW_INPUT = 0x0008;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;

    private const ushort KEY_EVENT = 0x0001;
    private const ushort MOUSE_EVENT = 0x0002;

    private const uint FROM_LEFT_1ST_BUTTON_PRESSED = 0x0001;
    private const uint MOUSE_MOVED_FLAG = 0x0001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleInput(
        IntPtr hConsoleInput, [Out] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSE_EVENT_RECORD
    {
        public COORD dwMousePosition;
        public uint dwButtonState;
        public uint dwControlKeyState;
        public uint dwEventFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public ushort UnicodeChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    #endregion

    private const string RegistryKeyPath = @"Software\ShiroNoir\DesktopM";
    private const string RegistryValueName = "Path";

    private static IntPtr _inputHandle;
    private static uint _savedMode;

    /// <summary>
    /// Checks installation status and handles install/update flow.
    /// Returns true if the program should continue to the main UI.
    /// Returns false if the program should exit (e.g. after copying itself elsewhere).
    /// </summary>
    public static bool CheckAndRun()
    {
        string currentExePath = Environment.ProcessPath!;
        string? registryPath = GetRegistryPath();

        if (registryPath == null)
        {
            // Not installed
            return HandleNotInstalled(currentExePath);
        }

        // Registry path exists — check if it matches current exe
        if (string.Equals(Path.GetFullPath(currentExePath), Path.GetFullPath(registryPath),
                StringComparison.OrdinalIgnoreCase))
        {
            // Paths match — already installed, proceed directly
            return true;
        }

        // Paths don't match — offer update
        return HandleUpdate(currentExePath, registryPath);
    }

    private static string? GetRegistryPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
        return key?.GetValue(RegistryValueName) as string;
    }

    private static void SetRegistryPath(string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
        key.SetValue(RegistryValueName, path, RegistryValueKind.String);
    }

    #region Not Installed

    private static bool HandleNotInstalled(string currentExePath)
    {
        SetupConsole();
        try
        {
            Console.Clear();
            Console.ResetColor();
            int row = 0;

            Console.SetCursorPosition(0, row++);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("本应用尚未安装。");
            Console.ResetColor();
            row++;

            // Draw buttons: [安装] [跳过]
            int btnInstallCol = 2;
            string btnInstall = "[安装]";
            int btnInstallEnd = btnInstallCol + GetDisplayWidth(btnInstall);

            int btnSkipCol = btnInstallEnd + 4;
            string btnSkip = "[跳过]";
            int btnSkipEnd = btnSkipCol + GetDisplayWidth(btnSkip);

            int btnRow = row;
            Console.SetCursorPosition(btnInstallCol, btnRow);
            Console.Write(btnInstall);
            Console.SetCursorPosition(btnSkipCol, btnRow);
            Console.Write(btnSkip);

            int choice = WaitForButtonClick(btnRow,
                (btnInstallCol, btnInstallEnd), btnInstall,
                (btnSkipCol, btnSkipEnd), btnSkip);

            if (choice == 1) // 跳过
                return true;

            // User chose 安装
            return PerformInstall(currentExePath);
        }
        finally
        {
            RestoreConsole();
        }
    }

    private static bool PerformInstall(string currentExePath)
    {
        Console.Clear();
        Console.ResetColor();
        int row = 0;

        string currentDir = Path.GetDirectoryName(currentExePath)!;

        Console.SetCursorPosition(0, row++);
        Console.Write("将会安装到本程序现在的所在位置：");
        Console.SetCursorPosition(0, row++);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write(currentDir);
        Console.ResetColor();
        row++;

        Console.SetCursorPosition(0, row++);
        Console.Write("请输入一个名称，用于在\u201c运行\u201d中快速打开 (ESC取消): ");

        string? name = ReadInput(row);
        if (name == null)
            return true; // cancelled, just proceed

        // Sanitize name — remove invalid filename chars
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c.ToString(), "");
        if (string.IsNullOrWhiteSpace(name))
            return true;

        // Ensure .exe extension
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";

        string newExePath = Path.Combine(currentDir, name);

        // Rename current exe
        if (!string.Equals(Path.GetFullPath(currentExePath), Path.GetFullPath(newExePath),
                StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Move(currentExePath, newExePath, overwrite: true);
            }
            catch (Exception ex)
            {
                row += 2;
                Console.SetCursorPosition(0, row);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"重命名失败: {ex.Message}");
                Console.ResetColor();
                WaitForAnyKey();
                return false;
            }
        }

        // Add directory to user PATH
        AddToUserPath(currentDir);

        // Write registry
        SetRegistryPath(newExePath);

        row += 2;
        Console.SetCursorPosition(0, row++);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("安装完成！");
        Console.ResetColor();
        Console.SetCursorPosition(0, row++);
        Console.Write($"你现在可以通过 Win+R 输入 \"{Path.GetFileNameWithoutExtension(name)}\" 来快速打开本程序。");
        Console.SetCursorPosition(0, row++);
        Console.Write("按任意键继续...");
        WaitForAnyKey();

        return true;
    }

    #endregion

    #region Update

    private static bool HandleUpdate(string currentExePath, string registryPath)
    {
        SetupConsole();
        try
        {
            Console.Clear();
            Console.ResetColor();
            int row = 0;

            Console.SetCursorPosition(0, row++);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("检测到已安装的版本路径与当前程序不同：");
            Console.ResetColor();
            Console.SetCursorPosition(0, row++);
            Console.Write("已安装: ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(registryPath);
            Console.ResetColor();
            Console.SetCursorPosition(0, row++);
            Console.Write("当  前: ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(currentExePath);
            Console.ResetColor();
            row++;

            Console.SetCursorPosition(0, row++);
            Console.Write("是否用本程序更新已安装的版本？");

            int btnRow = row;
            int btnYesCol = 2;
            string btnYes = "[是]";
            int btnYesEnd = btnYesCol + GetDisplayWidth(btnYes);

            int btnNoCol = btnYesEnd + 4;
            string btnNo = "[否]";
            int btnNoEnd = btnNoCol + GetDisplayWidth(btnNo);

            Console.SetCursorPosition(btnYesCol, btnRow);
            Console.Write(btnYes);
            Console.SetCursorPosition(btnNoCol, btnRow);
            Console.Write(btnNo);

            int choice = WaitForButtonClick(btnRow,
                (btnYesCol, btnYesEnd), btnYes,
                (btnNoCol, btnNoEnd), btnNo);

            if (choice == 1) // 否
                return true;

            // User chose 是 — perform update
            return PerformUpdate(currentExePath, registryPath);
        }
        finally
        {
            RestoreConsole();
        }
    }

    private static bool PerformUpdate(string currentExePath, string registryPath)
    {
        string targetName = Path.GetFileNameWithoutExtension(registryPath);
        string targetDir = Path.GetDirectoryName(registryPath)!;
        string sourceDir = Path.GetDirectoryName(currentExePath)!;

        // Files to copy, with "DesktopM" prefix renamed to the target name
        string[] filesToCopy =
        [
            "DesktopM.deps.json",
            "DesktopM.dll",
            "DesktopM.exe",
            "DesktopM.pdb",
            "DesktopM.runtimeconfig.json"
        ];

        try
        {
            // Ensure target directory exists
            Directory.CreateDirectory(targetDir);

            foreach (var file in filesToCopy)
            {
                string sourcePath = Path.Combine(sourceDir, file);
                if (!File.Exists(sourcePath)) continue;

                // Replace "DesktopM" prefix with the registered name
                string destFileName = targetName + file.Substring("DesktopM".Length);
                string destPath = Path.Combine(targetDir, destFileName);
                File.Copy(sourcePath, destPath, overwrite: true);
            }

            // Update registry
            SetRegistryPath(Path.Combine(targetDir, targetName + ".exe"));

            // Ensure target dir is in user PATH
            AddToUserPath(targetDir);
        }
        catch (Exception ex)
        {
            int row = 10;
            Console.SetCursorPosition(0, row);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write($"更新失败: {ex.Message}");
            Console.ResetColor();
            WaitForAnyKey();
            return true;
        }

        int r = 8;
        Console.SetCursorPosition(0, r++);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("更新完成！");
        Console.ResetColor();
        Console.SetCursorPosition(0, r++);
        Console.Write("按任意键继续...");
        WaitForAnyKey();

        return true;
    }

    #endregion

    #region User PATH

    private static void AddToUserPath(string directory)
    {
        string currentPath = Environment.GetEnvironmentVariable("PATH",
            EnvironmentVariableTarget.User) ?? "";

        // Check if already in PATH
        var paths = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in paths)
        {
            if (string.Equals(Path.GetFullPath(p.Trim()), Path.GetFullPath(directory),
                    StringComparison.OrdinalIgnoreCase))
                return;
        }

        string newPath = currentPath.TrimEnd(';') + ";" + directory;
        Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.User);
    }

    #endregion

    #region Console UI Helpers

    private static void SetupConsole()
    {
        _inputHandle = GetStdHandle(STD_INPUT_HANDLE);
        GetConsoleMode(_inputHandle, out _savedMode);
        SetConsoleMode(_inputHandle,
            ENABLE_EXTENDED_FLAGS | ENABLE_MOUSE_INPUT | ENABLE_PROCESSED_INPUT | ENABLE_WINDOW_INPUT);
    }

    private static void RestoreConsole()
    {
        SetConsoleMode(_inputHandle, _savedMode);
    }

    /// <summary>
    /// Waits for user to click one of two buttons or press 1/2 keys.
    /// Returns 0 for first button, 1 for second button.
    /// </summary>
    private static int WaitForButtonClick(int btnRow,
        (int start, int end) btn0, string btn0Text,
        (int start, int end) btn1, string btn1Text)
    {
        var records = new INPUT_RECORD[1];
        int highlighted = -1;
        string[] texts = [btn0Text, btn1Text];
        (int start, int end)[] btns = [btn0, btn1];

        while (true)
        {
            ReadConsoleInput(_inputHandle, records, 1, out uint read);
            if (read == 0) continue;

            var rec = records[0];

            if (rec.EventType == MOUSE_EVENT)
            {
                var m = rec.MouseEvent;
                int mx = m.dwMousePosition.X;
                int my = m.dwMousePosition.Y;

                // Determine hover
                int hoverIdx = -1;
                if (my == btnRow)
                {
                    if (mx >= btn0.start && mx < btn0.end) hoverIdx = 0;
                    else if (mx >= btn1.start && mx < btn1.end) hoverIdx = 1;
                }

                // Update highlight
                if (hoverIdx != highlighted)
                {
                    if (highlighted >= 0)
                    {
                        Console.SetCursorPosition(btns[highlighted].start, btnRow);
                        Console.ResetColor();
                        Console.Write(texts[highlighted]);
                    }

                    if (hoverIdx >= 0)
                    {
                        Console.SetCursorPosition(btns[hoverIdx].start, btnRow);
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.Write(texts[hoverIdx]);
                        Console.ResetColor();
                    }

                    highlighted = hoverIdx;
                }

                // Click
                if ((m.dwEventFlags & MOUSE_MOVED_FLAG) == 0 &&
                    (m.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0)
                {
                    if (hoverIdx >= 0) return hoverIdx;
                }
            }
            else if (rec.EventType == KEY_EVENT && rec.KeyEvent.bKeyDown != 0)
            {
                ushort vk = rec.KeyEvent.wVirtualKeyCode;
                if (vk == 0x10 || vk == 0xA0 || vk == 0xA1) continue; // Ignore shift

                char c = (char)rec.KeyEvent.UnicodeChar;
                if (c == '1') return 0;
                if (c == '2') return 1;
            }
        }
    }

    /// <summary>
    /// Reads a line of text input. Returns null if ESC is pressed.
    /// </summary>
    private static string? ReadInput(int row)
    {
        Console.SetCursorPosition(0, row);
        Console.Write("> ");

        var sb = new StringBuilder();
        var records = new INPUT_RECORD[1];

        while (true)
        {
            ReadConsoleInput(_inputHandle, records, 1, out uint read);
            if (read == 0) continue;

            var rec = records[0];
            if (rec.EventType != KEY_EVENT || rec.KeyEvent.bKeyDown == 0) continue;

            ushort vk = rec.KeyEvent.wVirtualKeyCode;
            char c = (char)rec.KeyEvent.UnicodeChar;

            if (vk == 0x10 || vk == 0xA0 || vk == 0xA1) continue; // Ignore shift

            if (vk == 0x0D) // Enter
                return sb.Length > 0 ? sb.ToString() : null;

            if (vk == 0x1B) // ESC
                return null;

            if (vk == 0x08) // Backspace
            {
                if (sb.Length > 0)
                {
                    char removed = sb[sb.Length - 1];
                    sb.Remove(sb.Length - 1, 1);
                    int displayPos = GetDisplayWidth(sb.ToString());
                    int charWidth = IsFullWidth(removed) ? 2 : 1;
                    Console.SetCursorPosition(2 + displayPos, row);
                    Console.Write(new string(' ', charWidth));
                    Console.SetCursorPosition(2 + displayPos, row);
                }
            }
            else if (c >= 32) // Printable
            {
                int curWidth = GetDisplayWidth(sb.ToString());
                sb.Append(c);
                Console.SetCursorPosition(2 + curWidth, row);
                Console.Write(c);
            }
        }
    }

    private static void WaitForAnyKey()
    {
        var records = new INPUT_RECORD[1];
        while (true)
        {
            ReadConsoleInput(_inputHandle, records, 1, out uint read);
            if (read == 0) continue;
            var rec = records[0];
            if (rec.EventType == KEY_EVENT && rec.KeyEvent.bKeyDown != 0)
            {
                ushort vk = rec.KeyEvent.wVirtualKeyCode;
                if (vk == 0x10 || vk == 0xA0 || vk == 0xA1) continue;
                return;
            }
            if (rec.EventType == MOUSE_EVENT &&
                (rec.MouseEvent.dwEventFlags & MOUSE_MOVED_FLAG) == 0 &&
                (rec.MouseEvent.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0)
                return;
        }
    }

    private static int GetDisplayWidth(string text)
    {
        int w = 0;
        foreach (char c in text)
            w += IsFullWidth(c) ? 2 : 1;
        return w;
    }

    private static bool IsFullWidth(char c)
    {
        return (c >= 0x1100 && c <= 0x115F) ||
               (c >= 0x2E80 && c <= 0x9FFF) ||
               (c >= 0xAC00 && c <= 0xD7AF) ||
               (c >= 0xF900 && c <= 0xFAFF) ||
               (c >= 0xFE30 && c <= 0xFE4F) ||
               (c >= 0xFF01 && c <= 0xFF60) ||
               (c >= 0xFFE0 && c <= 0xFFE6);
    }

    #endregion
}
