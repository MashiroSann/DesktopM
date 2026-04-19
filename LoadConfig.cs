using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

public static class LoadConfig
{
    private class ConfigNode
    {
        public string Name { get; set; } = "";
        public string LaunchPath { get; set; } = "";
        public List<ConfigNode> Children { get; set; } = new();
        public bool IsFolderSyntax { get; set; }
        public bool IsFolder => IsFolderSyntax && Children.Count > 0;
    }

    public class EditableNode
    {
        public string Name { get; set; } = "";
        public string LaunchPath { get; set; } = "";
        public List<EditableNode> Children { get; set; } = new();
        public bool IsFolder { get; set; }
    }

    public record struct DisplayItem(int Index, string Name, bool IsFolder);

    public static string Title1 { get; private set; } = "~ DesktopM ~";
    public static string Title2 { get; private set; } = "";
    public static ushort CurrentLayer { get; private set; } = 1;

    public static string GetConfigPath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config.ini");

    private static List<ConfigNode> _rootNodes = new();
    private static bool _parsed = false;
    private static List<ConfigNode> _currentNodes = new();
    private static readonly Stack<List<ConfigNode>> _history = new();

    public static void OpenConfigFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = GetConfigPath(),
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static void EnsureParsed()
    {
        if (!_parsed)
        {
            ParseConfig();
            _currentNodes = _rootNodes;
        }
    }

    public static List<DisplayItem> GetDisplayItems()
    {
        EnsureParsed();
        var sorted = GetSortedDisplay(_currentNodes);
        var result = new List<DisplayItem>();
        int idx = 1;
        foreach (var node in sorted)
            result.Add(new DisplayItem(idx++, node.Name, node.IsFolder));
        return result;
    }

    public static void NavigateInto(int displayIndex)
    {
        EnsureParsed();
        var sorted = GetSortedDisplay(_currentNodes);
        int idx = displayIndex - 1;
        if (idx >= 0 && idx < sorted.Count && sorted[idx].IsFolder)
        {
            _history.Push(_currentNodes);
            _currentNodes = sorted[idx].Children;
            CurrentLayer = (ushort)(_history.Count + 1);
        }
    }

    public enum LaunchResult { Success, NotFound, Failed, UacDenied }

    public static (LaunchResult result, string name) LaunchItem(int displayIndex, bool asAdmin)
    {
        EnsureParsed();
        var sorted = GetSortedDisplay(_currentNodes);
        int idx = displayIndex - 1;
        if (idx < 0 || idx >= sorted.Count || sorted[idx].IsFolder)
            return (LaunchResult.Failed, "");

        var node = sorted[idx];

        string fullPath = node.LaunchPath;

        if (!File.Exists(fullPath))
            return (LaunchResult.NotFound, $"{node.Name} → {fullPath}");

        try
        {
            ProcessStartInfo psi;
            if (asAdmin)
            {
                // Launch via cmd /c start with elevation to fully detach
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
                // Launch via cmd /c start to fully detach the child process
                // so it does not share this console window
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"\" \"{fullPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            Process.Start(psi);
            return (LaunchResult.Success, node.Name);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (LaunchResult.UacDenied, node.Name);
        }
        catch (Exception)
        {
            return (LaunchResult.Failed, node.Name);
        }
    }

    public static bool GoBack()
    {
        if (_history.Count > 0)
        {
            _currentNodes = _history.Pop();
            CurrentLayer = (ushort)(_history.Count + 1);
            return true;
        }
        return false;
    }

    private static List<ConfigNode> GetSortedDisplay(List<ConfigNode> nodes)
    {
        var filtered = nodes.Where(n => !(n.IsFolderSyntax && n.Children.Count == 0)).ToList();
        var folders = filtered.Where(n => n.IsFolder)
                              .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
        var shortcuts = filtered.Where(n => !n.IsFolder &&
                              n.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                              .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
        var programs = filtered.Where(n => !n.IsFolder &&
                             !n.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
        return folders.Concat<ConfigNode>(shortcuts).Concat(programs).ToList();
    }

    private static void ParseConfig()
    {
        string configPath = GetConfigPath();
        if (!File.Exists(configPath))
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("}");
            File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
        }

        string content = File.ReadAllText(configPath);

        content = StripComments(content);

        var title1Match = Regex.Match(content, @"#title1\s*=\s*""([^""]*)""");
        if (title1Match.Success)
            Title1 = title1Match.Groups[1].Value;

        var title2Match = Regex.Match(content, @"#title2\s*=\s*""([^""]*)""");
        if (title2Match.Success)
            Title2 = title2Match.Groups[1].Value;

        int pos = 0;
        while (pos < content.Length && content[pos] != '{') pos++;
        if (pos < content.Length)
            _rootNodes = ParseChildren(content, ref pos);

        _parsed = true;
    }

    private static List<ConfigNode> ParseChildren(string text, ref int pos)
    {
        var children = new List<ConfigNode>();
        pos++; // skip '{'

        while (pos < text.Length)
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) break;

            if (text[pos] == '}')
            {
                pos++;
                return children;
            }

            if (text[pos] == '"')
            {
                string name = ParseQuotedString(text, ref pos);
                SkipWhitespace(text, ref pos);

                var node = new ConfigNode { Name = name };

                if (pos < text.Length && text[pos] == ':')
                {
                    pos++; // skip ':'
                    SkipWhitespace(text, ref pos);
                    if (pos < text.Length && text[pos] == '{')
                    {
                        node.IsFolderSyntax = true;
                        node.Children = ParseChildren(text, ref pos);
                    }
                    else if (pos < text.Length && text[pos] == '"')
                    {
                        node.LaunchPath = ParseQuotedString(text, ref pos);
                    }
                }

                children.Add(node);
            }
            else
            {
                pos++;
            }
        }

        return children;
    }

    private static string ParseQuotedString(string text, ref int pos)
    {
        pos++; // skip opening '"'
        int start = pos;
        while (pos < text.Length && text[pos] != '"') pos++;
        string result = text[start..pos];
        if (pos < text.Length) pos++; // skip closing '"'
        return result;
    }

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
    }

    public static List<EditableNode> GetEditableTree()
    {
        EnsureParsed();
        return _rootNodes.Select(CloneToEditable).ToList();
    }

    private static EditableNode CloneToEditable(ConfigNode node)
    {
        return new EditableNode
        {
            Name = node.Name,
            LaunchPath = node.LaunchPath,
            IsFolder = node.IsFolderSyntax,
            Children = node.Children.Select(CloneToEditable).ToList()
        };
    }

    public static void SaveTree(List<EditableNode> tree)
    {
        EnsureParsed();
        var sb = new StringBuilder();
        if (Title1 != "~ DesktopM ~")
            sb.AppendLine($"#title1 = \"{Title1}\"");
        if (!string.IsNullOrEmpty(Title2))
            sb.AppendLine($"#title2 = \"{Title2}\"");
        sb.AppendLine("{");
        foreach (var node in tree)
            SerializeNode(sb, node, 1);
        sb.AppendLine("}");
        File.WriteAllText(GetConfigPath(), sb.ToString(), Encoding.UTF8);
    }

    private static void SerializeNode(StringBuilder sb, EditableNode node, int indent)
    {
        string pad = new string(' ', indent * 4);
        if (node.IsFolder)
        {
            sb.AppendLine(pad + "\"" + node.Name + "\":{");
            foreach (var child in node.Children)
                SerializeNode(sb, child, indent + 1);
            sb.AppendLine(pad + "}");
        }
        else
        {
            sb.AppendLine(pad + "\"" + node.Name + "\":\"" + node.LaunchPath + "\"");
        }
    }

    public static void Reload()
    {
        _parsed = false;
        _rootNodes = new();
        _currentNodes = new();
        _history.Clear();
        CurrentLayer = 1;
        Title1 = "~ DesktopM ~";
        Title2 = "";
    }

    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                inQuotes = !inQuotes;
                sb.Append(text[i]);
            }
            else if (!inQuotes && i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                if (i < text.Length) sb.Append('\n');
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }
}
