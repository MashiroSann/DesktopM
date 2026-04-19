using System.Runtime.InteropServices;
using System.Text;

public static class ConfigEditor
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
    private const uint RIGHTMOST_BUTTON_PRESSED = 0x0002;
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushConsoleInputBuffer(IntPtr hConsoleInput);

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

    // File dialog
    [StructLayout(LayoutKind.Sequential)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        public IntPtr lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

    #endregion

    private class EditNode
    {
        public string Name { get; set; } = "";
        public string LaunchPath { get; set; } = "";
        public List<EditNode> Children { get; set; } = new();
        public bool IsFolder { get; set; }
        public bool IsExpanded { get; set; } = true;
    }

    private class TreeLine
    {
        public EditNode Node = null!;
        public EditNode? Parent;
        public List<EditNode> Siblings = null!;
        public int Depth;
        public int Row;
        public string Text = "";
    }

    private static List<EditNode> _tree = new();
    private static List<TreeLine> _lines = new();
    private static int _highlightRow = -1;
    private static int _treeStartRow;
    private static int _footerRow;
    private static IntPtr _inputHandle;
    private static uint _savedMode;

    public static void Run()
    {
        _inputHandle = GetStdHandle(STD_INPUT_HANDLE);
        GetConsoleMode(_inputHandle, out _savedMode);
        SetConsoleMode(_inputHandle,
            ENABLE_EXTENDED_FLAGS | ENABLE_MOUSE_INPUT | ENABLE_PROCESSED_INPUT | ENABLE_WINDOW_INPUT);

        LoadTree();
        string? status = null;

        try
        {
            while (true)
            {
                Render(status);
                status = null;

                if (!HandleInput(ref status))
                    break;
            }
        }
        finally
        {
            SetConsoleMode(_inputHandle, _savedMode);
        }

        SaveToFile();
        LoadConfig.Reload();
    }

    #region Load / Save

    private static void LoadTree()
    {
        var source = LoadConfig.GetEditableTree();
        _tree = source.Select(ConvertFromEditable).ToList();
    }

    private static EditNode ConvertFromEditable(LoadConfig.EditableNode n)
    {
        return new EditNode
        {
            Name = n.Name,
            LaunchPath = n.LaunchPath,
            IsFolder = n.IsFolder,
            IsExpanded = true,
            Children = n.Children.Select(ConvertFromEditable).ToList()
        };
    }

    private static void SaveToFile()
    {
        var exportTree = _tree.Select(ConvertToEditable).ToList();
        LoadConfig.SaveTree(exportTree);
    }

    private static LoadConfig.EditableNode ConvertToEditable(EditNode n)
    {
        return new LoadConfig.EditableNode
        {
            Name = n.Name,
            LaunchPath = n.LaunchPath,
            IsFolder = n.IsFolder,
            Children = n.Children.Select(ConvertToEditable).ToList()
        };
    }

    #endregion

    #region Rendering

    private static void Render(string? status)
    {
        Console.Clear();
        Console.ResetColor();
        _lines.Clear();
        _highlightRow = -1;

        int w = ConsoleWidth();
        int row = 0;

        // Title
        string title = "═══ 配置编辑器 ═══";
        Console.SetCursorPosition(Math.Max(0, (w - GetDisplayWidth(title)) / 2), row++);
        Console.Write(title);
        row++; // blank

        _treeStartRow = row;

        if (_tree.Count == 0)
        {
            Console.SetCursorPosition(2, row++);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("(空) 在此处右键添加项目");
            Console.ResetColor();
        }
        else
        {
            RenderNodes(_tree, null, 0, ref row);
        }

        row++; // blank

        // Status message
        if (status != null)
        {
            Console.SetCursorPosition(0, row++);
            bool isErr = status.Contains("取消") || status.Contains("失败");
            Console.ForegroundColor = isErr ? ConsoleColor.Red : ConsoleColor.Green;
            Console.Write(status);
            Console.ResetColor();
            row++;
        }

        // Footer
        _footerRow = row;
        Console.SetCursorPosition(0, row);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("[左键] 展开/折叠  [右键] 操作菜单  [ESC] 保存并返回");
        Console.ResetColor();
    }

    private static void RenderNodes(List<EditNode> nodes, EditNode? parent, int depth, ref int row)
    {
        foreach (var node in nodes)
        {
            string indent = new string(' ', depth * 4 + 2);
            string marker = node.IsFolder ? (node.IsExpanded ? "▼ " : "► ") : "  ";
            string text = indent + marker + node.Name;

            Console.SetCursorPosition(0, row);
            Console.ForegroundColor = node.IsFolder ? ConsoleColor.Cyan : ConsoleColor.White;
            Console.Write(text);
            Console.ResetColor();

            _lines.Add(new TreeLine
            {
                Node = node,
                Parent = parent,
                Siblings = parent?.Children ?? _tree,
                Depth = depth,
                Row = row,
                Text = text
            });

            row++;

            if (node.IsFolder && node.IsExpanded)
                RenderNodes(node.Children, node, depth + 1, ref row);
        }
    }

    #endregion

    #region Input Handling

    private static bool HandleInput(ref string? status)
    {
        var records = new INPUT_RECORD[1];

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

                UpdateHighlight(mx, my);

                bool isClick = (m.dwEventFlags & MOUSE_MOVED_FLAG) == 0;

                // Left click - toggle folder expand/collapse
                if (isClick && (m.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0)
                {
                    var line = FindLine(my);
                    if (line != null && line.Node.IsFolder)
                    {
                        line.Node.IsExpanded = !line.Node.IsExpanded;
                        return true;
                    }
                }

                // Right click - context menu
                if (isClick && (m.dwButtonState & RIGHTMOST_BUTTON_PRESSED) != 0)
                {
                    var line = FindLine(my);
                    HandleContextMenu(mx, my, line, ref status);
                    return true;
                }
            }
            else if (rec.EventType == KEY_EVENT && rec.KeyEvent.bKeyDown != 0)
            {
                // Ignore shift keys to prevent accidental console selection
                if (rec.KeyEvent.wVirtualKeyCode == 0x10 ||
                    rec.KeyEvent.wVirtualKeyCode == 0xA0 ||
                    rec.KeyEvent.wVirtualKeyCode == 0xA1)
                    continue;

                if (rec.KeyEvent.wVirtualKeyCode == 0x1B) // ESC
                    return false;
            }
        }
    }

    private static TreeLine? FindLine(int row)
    {
        return _lines.FirstOrDefault(l => l.Row == row);
    }

    private static void UpdateHighlight(int mx, int my)
    {
        int newRow = -1;
        var line = FindLine(my);
        if (line != null) newRow = my;

        if (newRow == _highlightRow) return;

        // Restore old
        if (_highlightRow >= 0)
        {
            var old = _lines.FirstOrDefault(l => l.Row == _highlightRow);
            if (old != null)
            {
                Console.SetCursorPosition(0, old.Row);
                Console.ForegroundColor = old.Node.IsFolder ? ConsoleColor.Cyan : ConsoleColor.White;
                Console.Write(old.Text);
                Console.ResetColor();
            }
        }

        // Highlight new
        if (newRow >= 0 && line != null)
        {
            Console.SetCursorPosition(0, line.Row);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write(line.Text);
            Console.ResetColor();
        }

        _highlightRow = newRow;
    }

    #endregion

    #region Context Menu

    private static void HandleContextMenu(int mx, int my, TreeLine? target, ref string? status)
    {
        var menuItems = new List<(string label, int action)>();

        if (target == null || target.Node.IsFolder)
        {
            menuItems.Add(("添加文件", 1));
            menuItems.Add(("添加文件夹", 2));
            if (target != null && target.Node.IsFolder)
            {
                menuItems.Add(("重命名", 5));
                menuItems.Add(("删除文件夹", 4));
            }
        }
        else
        {
            menuItems.Add(("重命名", 5));
            menuItems.Add(("修改链接", 6));
            menuItems.Add(("删除文件", 3));
        }

        int maxLabelWidth = menuItems.Max(m => GetDisplayWidth(m.label));
        int contentWidth = maxLabelWidth + 2;
        int menuX = Math.Min(mx, Math.Max(0, ConsoleWidth() - contentWidth - 3));
        int menuY = Math.Min(my, Math.Max(0, ConsoleHeight() - menuItems.Count - 3));

        var labels = menuItems.Select(m => m.label).ToList();
        DrawMenu(menuX, menuY, labels, contentWidth);

        int selected = WaitForMenuClick(menuX, menuY, menuItems.Count, contentWidth, labels);

        if (selected < 0) return;

        int action = menuItems[selected].action;
        EditNode? folder = (target != null && target.Node.IsFolder) ? target.Node : null;

        switch (action)
        {
            case 1: // 添加文件
                AddFile(folder, ref status);
                break;
            case 2: // 添加文件夹
                AddFolder(folder, ref status);
                break;
            case 3: // 删除文件
                DeleteFile(target!, ref status);
                break;
            case 4: // 删除文件夹
                DeleteFolder(target!, ref status);
                break;
            case 5: // 重命名
                RenameNode(target!, ref status);
                break;
            case 6: // 修改链接
                EditLink(target!, ref status);
                break;
        }
    }

    private static void DrawMenu(int x, int y, List<string> items, int contentWidth)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.BackgroundColor = ConsoleColor.DarkBlue;

        Console.SetCursorPosition(x, y);
        Console.Write("┌" + new string('─', contentWidth) + "┐");

        for (int i = 0; i < items.Count; i++)
            DrawMenuItem(x, y + 1 + i, items[i], contentWidth, false);

        Console.SetCursorPosition(x, y + 1 + items.Count);
        Console.ForegroundColor = ConsoleColor.White;
        Console.BackgroundColor = ConsoleColor.DarkBlue;
        Console.Write("└" + new string('─', contentWidth) + "┘");

        Console.ResetColor();
    }

    private static void DrawMenuItem(int menuX, int rowY, string item, int contentWidth, bool highlight)
    {
        Console.SetCursorPosition(menuX, rowY);
        if (highlight)
        {
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.White;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.BackgroundColor = ConsoleColor.DarkBlue;
        }

        string text = " " + item;
        int padNeeded = contentWidth - GetDisplayWidth(text);
        string padded = text + new string(' ', Math.Max(0, padNeeded));
        Console.Write("│" + padded + "│");

        Console.ResetColor();
    }

    private static int WaitForMenuClick(int menuX, int menuY, int itemCount, int contentWidth, List<string> items)
    {
        var records = new INPUT_RECORD[1];
        int highlighted = -1;

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

                int itemIdx = -1;
                if (mx > menuX && mx < menuX + contentWidth + 1 &&
                    my > menuY && my <= menuY + itemCount)
                {
                    itemIdx = my - menuY - 1;
                }

                // Hover highlight
                if (itemIdx != highlighted)
                {
                    if (highlighted >= 0 && highlighted < itemCount)
                        DrawMenuItem(menuX, menuY + 1 + highlighted, items[highlighted], contentWidth, false);
                    if (itemIdx >= 0 && itemIdx < itemCount)
                        DrawMenuItem(menuX, menuY + 1 + itemIdx, items[itemIdx], contentWidth, true);
                    highlighted = itemIdx;
                }

                bool isClick = (m.dwEventFlags & MOUSE_MOVED_FLAG) == 0;

                if (isClick && (m.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0)
                {
                    if (itemIdx >= 0) return itemIdx;
                    return -1; // clicked outside
                }

                if (isClick && (m.dwButtonState & RIGHTMOST_BUTTON_PRESSED) != 0)
                    return -1;
            }
            else if (rec.EventType == KEY_EVENT && rec.KeyEvent.bKeyDown != 0)
            {
                if (rec.KeyEvent.wVirtualKeyCode == 0x1B)
                    return -1;
            }
        }
    }

    #endregion

    #region Operations

    private static void AddFile(EditNode? parentFolder, ref string? status)
    {
        string? name = PromptInput("输入显示名称 (ESC取消): ");
        if (string.IsNullOrWhiteSpace(name))
        {
            status = "已取消添加";
            return;
        }

        string? filePath = ShowOpenFileDialog();
        if (filePath == null)
        {
            status = "已取消选择文件";
            return;
        }

        var node = new EditNode { Name = name, LaunchPath = filePath };

        if (parentFolder != null)
            parentFolder.Children.Add(node);
        else
            _tree.Add(node);

        SaveToFile();
        LoadConfig.Reload();
        status = "已添加: " + name;
    }

    private static void AddFolder(EditNode? parentFolder, ref string? status)
    {
        string? name = PromptInput("输入文件夹名称 (ESC取消): ");
        if (string.IsNullOrWhiteSpace(name))
        {
            status = "已取消添加";
            return;
        }

        var node = new EditNode { Name = name, IsFolder = true, IsExpanded = true };

        if (parentFolder != null)
            parentFolder.Children.Add(node);
        else
            _tree.Add(node);

        SaveToFile();
        LoadConfig.Reload();
        status = "已添加文件夹: " + name;
    }

    private static void DeleteFile(TreeLine line, ref string? status)
    {
        line.Siblings.Remove(line.Node);
        SaveToFile();
        LoadConfig.Reload();
        status = "已删除: " + line.Node.Name;
    }

    private static void DeleteFolder(TreeLine line, ref string? status)
    {
        if (!ConfirmDialog("确认删除文件夹 \"" + line.Node.Name + "\" 及其所有内容？(Y/N)"))
        {
            status = "已取消删除";
            return;
        }

        line.Siblings.Remove(line.Node);
        SaveToFile();
        LoadConfig.Reload();
        status = "已删除文件夹: " + line.Node.Name;
    }

    private static void RenameNode(TreeLine line, ref string? status)
    {
        string? newName = PromptInput("输入新名称 (ESC取消): ");
        if (string.IsNullOrWhiteSpace(newName))
        {
            status = "已取消重命名";
            return;
        }

        string oldName = line.Node.Name;
        line.Node.Name = newName;
        SaveToFile();
        LoadConfig.Reload();
        status = "已重命名: " + oldName + " → " + newName;
    }

    private static void EditLink(TreeLine line, ref string? status)
    {
        string? filePath = ShowOpenFileDialog();
        if (filePath == null)
        {
            status = "已取消修改链接";
            return;
        }

        line.Node.LaunchPath = filePath;
        SaveToFile();
        LoadConfig.Reload();
        status = "已修改链接: " + line.Node.Name + " → " + filePath;
    }

    #endregion

    #region UI Helpers

    private static string? PromptInput(string prompt)
    {
        int row = _footerRow + 2;
        Console.SetCursorPosition(0, row);
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(prompt);
        Console.ResetColor();

        int promptWidth = GetDisplayWidth(prompt);
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

            // Ignore shift keys to prevent accidental console selection
            if (vk == 0x10 || vk == 0xA0 || vk == 0xA1) continue;

            if (vk == 0x0D) // Enter
            {
                return sb.Length > 0 ? sb.ToString() : null;
            }
            else if (vk == 0x1B) // ESC
            {
                return null;
            }
            else if (vk == 0x08) // Backspace
            {
                if (sb.Length > 0)
                {
                    char removed = sb[sb.Length - 1];
                    sb.Remove(sb.Length - 1, 1);
                    int displayPos = GetDisplayWidth(sb.ToString());
                    int charWidth = IsFullWidth(removed) ? 2 : 1;
                    Console.SetCursorPosition(promptWidth + displayPos, row);
                    Console.Write(new string(' ', charWidth));
                    Console.SetCursorPosition(promptWidth + displayPos, row);
                }
            }
            else if (c >= 32)
            {
                int curWidth = GetDisplayWidth(sb.ToString());
                sb.Append(c);
                Console.SetCursorPosition(promptWidth + curWidth, row);
                Console.Write(c);
            }
        }
    }

    private static bool ConfirmDialog(string message)
    {
        int row = _footerRow + 2;
        Console.SetCursorPosition(0, row);
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write(message);
        Console.ResetColor();

        var records = new INPUT_RECORD[1];
        while (true)
        {
            ReadConsoleInput(_inputHandle, records, 1, out uint read);
            if (read == 0) continue;

            var rec = records[0];
            if (rec.EventType != KEY_EVENT || rec.KeyEvent.bKeyDown == 0) continue;

            char ch = char.ToUpper((char)rec.KeyEvent.UnicodeChar);
            if (ch == 'Y') return true;
            if (ch == 'N' || rec.KeyEvent.wVirtualKeyCode == 0x1B) return false;
        }
    }

    private static string? ShowOpenFileDialog()
    {
        IntPtr fileBuffer = Marshal.AllocCoTaskMem(520);
        for (int i = 0; i < 520; i += 2)
            Marshal.WriteInt16(fileBuffer, i, 0);

        string filterStr = "所有文件 (*.*)\0*.*\0可执行文件 (*.exe)\0*.exe\0快捷方式 (*.lnk)\0*.lnk\0";
        byte[] filterBytes = Encoding.Unicode.GetBytes(filterStr + "\0");
        IntPtr filterPtr = Marshal.AllocCoTaskMem(filterBytes.Length);
        Marshal.Copy(filterBytes, 0, filterPtr, filterBytes.Length);

        IntPtr titlePtr = Marshal.StringToCoTaskMemUni("选择应用程序或快捷方式");

        var ofn = new OPENFILENAME();
        ofn.lStructSize = Marshal.SizeOf<OPENFILENAME>();
        ofn.hwndOwner = GetConsoleWindow();
        ofn.lpstrFilter = filterPtr;
        ofn.lpstrFile = fileBuffer;
        ofn.nMaxFile = 260;
        ofn.lpstrTitle = titlePtr;
        ofn.Flags = 0x00080000 | 0x00001000; // OFN_EXPLORER | OFN_FILEMUSTEXIST

        string? result = null;

        // Temporarily restore console mode for the dialog
        SetConsoleMode(_inputHandle, _savedMode);

        try
        {
            if (GetOpenFileName(ref ofn))
                result = Marshal.PtrToStringUni(fileBuffer);
        }
        finally
        {
            FlushConsoleInputBuffer(_inputHandle);
            SetConsoleMode(_inputHandle,
                ENABLE_EXTENDED_FLAGS | ENABLE_MOUSE_INPUT | ENABLE_PROCESSED_INPUT | ENABLE_WINDOW_INPUT);
            Marshal.FreeCoTaskMem(fileBuffer);
            Marshal.FreeCoTaskMem(filterPtr);
            Marshal.FreeCoTaskMem(titlePtr);
        }

        return result;
    }

    #endregion

    #region Display Width

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

    private static int ConsoleWidth()
    {
        try { return Console.WindowWidth; } catch { return 80; }
    }

    private static int ConsoleHeight()
    {
        try { return Console.WindowHeight; } catch { return 25; }
    }

    #endregion
}
