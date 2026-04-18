using System.Runtime.InteropServices;
using System.Text;

public static class ConsoleUI
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
    private const uint LEFT_CTRL_PRESSED = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleInput(
        IntPtr hConsoleInput,
        [Out] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

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

    private class HitArea
    {
        public int Row;
        public int ColStart;
        public int ColEnd;
        public string Label = "";
        public int ItemIndex;
        public bool IsFolder;
        public bool IsBack;
        public bool IsEditConfig;
    }

    private enum InputAction { None, NavigateFolder, LaunchProgram, GoBack, EditConfig }

    private static readonly List<HitArea> _hitAreas = new();
    private static int _highlightedIdx = -1;
    private static int _promptRow;
    private static IntPtr _inputHandle;
    private static uint _originalMode;

    public static void Run()
    {
        Console.Title = "DesktopM";
        Console.OutputEncoding = Encoding.UTF8;
        Console.CursorVisible = false;
        SetupConsole();

        try
        {
            string? statusMessage = null;

            while (true)
            {
                RenderPage(statusMessage);
                statusMessage = null;

                var (action, index, admin) = WaitForInput();

                switch (action)
                {
                    case InputAction.NavigateFolder:
                        LoadConfig.NavigateInto(index);
                        break;

                    case InputAction.LaunchProgram:
                        // Temporarily restore console mode for UAC dialog
                        if (admin) RestoreConsole();
                        try
                        {
                            var (result, name) = LoadConfig.LaunchItem(index, admin);
                            statusMessage = result switch
                            {
                                LoadConfig.LaunchResult.Success => admin
                                    ? $"Launch [{index}] {name} successfully! (Launched with Admin)"
                                    : $"Launch [{index}] {name} successfully!",
                                LoadConfig.LaunchResult.NotFound => $"Path not found: {name}",
                                LoadConfig.LaunchResult.UacDenied => $"UAC authorization denied for [{index}] {name}.",
                                _ => $"Failed to launch [{index}] {name}."
                            };
                        }
                        catch (Exception ex)
                        {
                            statusMessage = $"Failed to launch: {ex.Message}";
                        }
                        finally
                        {
                            if (admin) SetupConsole();
                        }
                        break;

                    case InputAction.GoBack:
                        if (LoadConfig.CurrentLayer > 1)
                            LoadConfig.GoBack();
                        break;

                    case InputAction.EditConfig:
                        ConfigEditor.Run();
                        break;
                }
            }
        }
        finally
        {
            RestoreConsole();
            Console.CursorVisible = true;
            Console.ResetColor();
        }
    }

    private static void SetupConsole()
    {
        _inputHandle = GetStdHandle(STD_INPUT_HANDLE);
        GetConsoleMode(_inputHandle, out _originalMode);

        // Enable mouse input; ENABLE_EXTENDED_FLAGS without ENABLE_QUICK_EDIT_MODE disables Quick Edit
        uint newMode = ENABLE_EXTENDED_FLAGS | ENABLE_MOUSE_INPUT
                     | ENABLE_PROCESSED_INPUT | ENABLE_WINDOW_INPUT;
        SetConsoleMode(_inputHandle, newMode);
    }

    private static void RestoreConsole()
    {
        SetConsoleMode(_inputHandle, _originalMode);
    }

    private static int GetDisplayWidth(string text)
    {
        int width = 0;
        foreach (char c in text)
            width += IsFullWidth(c) ? 2 : 1;
        return width;
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

    private static void RenderPage(string? statusMessage)
    {
        Console.Clear();
        Console.ResetColor();
        _hitAreas.Clear();
        _highlightedIdx = -1;

        int width;
        try { width = Console.WindowWidth; } catch { width = 80; }

        int row = 0;

        // ── Items (parse config first so titles are available) ──
        var items = LoadConfig.GetDisplayItems();
        var folders = items.Where(i => i.IsFolder).ToList();
        var nonFolders = items.Where(i => !i.IsFolder).ToList();

        // ── Title ──
        string title = LoadConfig.Title1;
        Console.SetCursorPosition(Math.Max(0, (width - GetDisplayWidth(title)) / 2), row++);
        Console.Write(title);

        string subtitle = LoadConfig.Title2;
        if (!string.IsNullOrEmpty(subtitle))
        {
            Console.SetCursorPosition(Math.Max(0, (width - GetDisplayWidth(subtitle)) / 2), row++);
            Console.Write(subtitle);
        }

        // ── Big separator ──
        Console.SetCursorPosition(0, row++);
        Console.Write(new string('═', width - 1));

        row++; // blank line

        if (items.Count == 0)
        {
            Console.SetCursorPosition(0, row++);
            Console.Write("(empty)");
        }
        else
        {
            // Folders
            if (folders.Count > 0)
            {
                int col = 0;
                Console.SetCursorPosition(0, row);
                for (int i = 0; i < folders.Count; i++)
                {
                    string label = $"[{folders[i].Index}] {folders[i].Name}";
                    int labelWidth = GetDisplayWidth(label);
                    _hitAreas.Add(new HitArea
                    {
                        Row = row, ColStart = col, ColEnd = col + labelWidth,
                        Label = label, ItemIndex = folders[i].Index,
                        IsFolder = true, IsBack = false
                    });
                    Console.Write(label);
                    col += labelWidth;
                    if (i < folders.Count - 1) { Console.Write("  "); col += 2; }
                }
                row++;
            }

            // Short separator
            if (folders.Count > 0 && nonFolders.Count > 0)
            {
                Console.SetCursorPosition(0, row++);
                Console.Write(new string('─', Math.Min(24, width - 1)));
            }

            // Non-folders (shortcuts then programs, already sorted by GetDisplayItems)
            if (nonFolders.Count > 0)
            {
                int col = 0;
                Console.SetCursorPosition(0, row);
                for (int i = 0; i < nonFolders.Count; i++)
                {
                    string label = $"[{nonFolders[i].Index}] {nonFolders[i].Name}";
                    int labelWidth = GetDisplayWidth(label);
                    _hitAreas.Add(new HitArea
                    {
                        Row = row, ColStart = col, ColEnd = col + labelWidth,
                        Label = label, ItemIndex = nonFolders[i].Index,
                        IsFolder = false, IsBack = false
                    });
                    Console.Write(label);
                    col += labelWidth;
                    if (i < nonFolders.Count - 1) { Console.Write("  "); col += 2; }
                }
                row++;
            }
        }

        row++; // blank line

        // [X] Back (shown only when not at root)
        if (LoadConfig.CurrentLayer > 1)
        {
            const string backLabel = "[X] Back";
            Console.SetCursorPosition(0, row);
            _hitAreas.Add(new HitArea
            {
                Row = row, ColStart = 0, ColEnd = backLabel.Length,
                Label = backLabel, ItemIndex = -1,
                IsFolder = false, IsBack = true
            });
            Console.Write(backLabel);
            row++;
        }

        // [E] Edit Config
        {
            const string editLabel = "[E] Edit Config";
            Console.SetCursorPosition(0, row);
            _hitAreas.Add(new HitArea
            {
                Row = row, ColStart = 0, ColEnd = editLabel.Length,
                Label = editLabel, ItemIndex = -1,
                IsFolder = false, IsBack = false, IsEditConfig = true
            });
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(editLabel);
            Console.ResetColor();
            row++;
        }

        // Status message
        if (statusMessage != null)
        {
            row++;
            Console.SetCursorPosition(0, row++);
            bool isError = statusMessage.Contains("not found", StringComparison.OrdinalIgnoreCase)
                        || statusMessage.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                        || statusMessage.StartsWith("UAC", StringComparison.OrdinalIgnoreCase);
            Console.ForegroundColor = isError ? ConsoleColor.Red : ConsoleColor.Green;
            Console.Write(statusMessage);
            Console.ResetColor();
        }

        // Prompt
        row++;
        _promptRow = row;
        Console.SetCursorPosition(0, _promptRow);
        Console.Write("> ");
    }

    private static (InputAction action, int index, bool admin) WaitForInput()
    {
        var inputBuf = new StringBuilder();
        var records = new INPUT_RECORD[1];
        var items = LoadConfig.GetDisplayItems();
        int maxIndex = items.Count;

        while (true)
        {
            ReadConsoleInput(_inputHandle, records, 1, out uint read);
            if (read == 0) continue;

            var rec = records[0];

            // ── Mouse ──
            if (rec.EventType == MOUSE_EVENT)
            {
                var mouse = rec.MouseEvent;
                int mx = mouse.dwMousePosition.X;
                int my = mouse.dwMousePosition.Y;

                // Hover highlight on every mouse event
                UpdateHighlight(mx, my, GetDisplayWidth(inputBuf.ToString()));

                // Click: button just pressed (not a move-only event)
                if ((mouse.dwEventFlags & MOUSE_MOVED_FLAG) == 0 &&
                    (mouse.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0)
                {
                    for (int i = 0; i < _hitAreas.Count; i++)
                    {
                        var area = _hitAreas[i];
                        if (my == area.Row && mx >= area.ColStart && mx < area.ColEnd)
                        {
                            bool ctrlHeld = (mouse.dwControlKeyState & LEFT_CTRL_PRESSED) != 0;
                            if (area.IsBack)
                                return (InputAction.GoBack, 0, false);
                            if (area.IsEditConfig)
                                return (InputAction.EditConfig, 0, false);
                            if (area.IsFolder)
                                return (InputAction.NavigateFolder, area.ItemIndex, false);
                            return (InputAction.LaunchProgram, area.ItemIndex, ctrlHeld);
                        }
                    }
                }
            }
            // ── Keyboard ──
            else if (rec.EventType == KEY_EVENT && rec.KeyEvent.bKeyDown != 0)
            {
                ushort vk = rec.KeyEvent.wVirtualKeyCode;
                char c = (char)rec.KeyEvent.UnicodeChar;

                if (vk == 0x0D) // Enter
                {
                    string input = inputBuf.ToString().Trim();
                    if (string.IsNullOrEmpty(input)) continue;

                    bool admin = false;
                    if (input.Contains("-admin", StringComparison.OrdinalIgnoreCase))
                    {
                        admin = true;
                        input = input.Replace("-admin", "", StringComparison.OrdinalIgnoreCase).Trim();
                    }

                    if (input.Equals("x", StringComparison.OrdinalIgnoreCase))
                        return (InputAction.GoBack, 0, false);

                    if (ushort.TryParse(input, out ushort target) && target >= 1 && target <= maxIndex)
                    {
                        var item = items.First(i => i.Index == target);
                        if (item.IsFolder)
                            return (InputAction.NavigateFolder, target, false);
                        return (InputAction.LaunchProgram, target, admin);
                    }

                    // Regex match: try input as regex pattern
                    try
                    {
                        var regex = new System.Text.RegularExpressions.Regex(
                            input, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        var matches = items.Where(i => regex.IsMatch(i.Name)).ToList();
                        if (matches.Count == 1)
                        {
                            var match = matches[0];
                            if (match.IsFolder)
                                return (InputAction.NavigateFolder, match.Index, false);
                            return (InputAction.LaunchProgram, match.Index, admin);
                        }
                    }
                    catch { /* invalid regex, ignore */ }

                    // No match or multiple matches → treat as go back
                    return (InputAction.GoBack, 0, false);
                }
                else if (vk == 0x08) // Backspace
                {
                    if (inputBuf.Length > 0)
                    {
                        char removed = inputBuf[inputBuf.Length - 1];
                        inputBuf.Remove(inputBuf.Length - 1, 1);
                        int displayPos = GetDisplayWidth(inputBuf.ToString());
                        int charWidth = IsFullWidth(removed) ? 2 : 1;
                        Console.SetCursorPosition(2 + displayPos, _promptRow);
                        Console.Write(new string(' ', charWidth));
                        Console.SetCursorPosition(2 + displayPos, _promptRow);
                    }
                }
                else if (vk == 0x1B) // Escape
                {
                    return (InputAction.GoBack, 0, false);
                }
                else if (c >= 32) // Printable
                {
                    int curDisplayWidth = GetDisplayWidth(inputBuf.ToString());
                    inputBuf.Append(c);
                    Console.SetCursorPosition(2 + curDisplayWidth, _promptRow);
                    Console.Write(c);
                }
            }
        }
    }

    private static void UpdateHighlight(int mx, int my, int inputLen)
    {
        int newIdx = -1;
        for (int i = 0; i < _hitAreas.Count; i++)
        {
            var area = _hitAreas[i];
            if (my == area.Row && mx >= area.ColStart && mx < area.ColEnd)
            { newIdx = i; break; }
        }

        if (newIdx == _highlightedIdx) return;

        // Restore old item color
        if (_highlightedIdx >= 0 && _highlightedIdx < _hitAreas.Count)
        {
            var old = _hitAreas[_highlightedIdx];
            Console.SetCursorPosition(old.ColStart, old.Row);
            if (old.IsEditConfig)
                Console.ForegroundColor = ConsoleColor.DarkGray;
            else
                Console.ResetColor();
            Console.Write(old.Label);
            Console.ResetColor();
        }

        // Draw new item in gold
        if (newIdx >= 0)
        {
            var area = _hitAreas[newIdx];
            Console.SetCursorPosition(area.ColStart, area.Row);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write(area.Label);
            Console.ResetColor();
        }

        _highlightedIdx = newIdx;
        Console.SetCursorPosition(2 + inputLen, _promptRow);
    }
}
