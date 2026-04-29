using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Drover.App.Models;

/// <summary>
/// One entry in the file-explorer tree. Directories lazy-load their children
/// the first time they're expanded — a placeholder child is inserted up-front
/// so the TreeView's expander chevron renders without us walking the tree.
/// </summary>
public sealed partial class FileNode : ObservableObject
{
    private static readonly System.Collections.Generic.HashSet<string> IgnoredNames =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "bin", "obj", ".vs", ".idea",
            ".vscode-test", "dist", "out",
        };

    private bool _childrenLoaded;

    public FileNode(string fullPath, bool isDirectory, string rootPath)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        RootPath = rootPath;
        Name = System.IO.Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(Name)) Name = fullPath; // drive root
        if (isDirectory) Children.Add(Placeholder);
    }

    public string Name { get; }
    public string FullPath { get; }
    public string RootPath { get; }
    public bool IsDirectory { get; }

    public string RelativePath
    {
        get
        {
            if (string.IsNullOrEmpty(RootPath)) return FullPath;
            try
            {
                var rel = System.IO.Path.GetRelativePath(RootPath, FullPath);
                return rel.Replace('\\', '/');
            }
            catch { return FullPath; }
        }
    }

    public ObservableCollection<FileNode> Children { get; } = new();

    /// <summary>Pack URI to the SVG icon for this node — vscode-icons set, bundled as Resource.</summary>
    public Uri IconUri => _iconUri ??= ResolveIconUri();
    private Uri? _iconUri;

    private Uri ResolveIconUri()
    {
        var name = IsDirectory
            ? FolderIconName(Name)
            : FileIconName(Name);
        return new Uri($"pack://application:,,,/Resources/FileIcons/{name}.svg", UriKind.Absolute);
    }

    private static readonly System.Collections.Generic.Dictionary<string, string> ExtIcons =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            [".cs"]        = "file_type_csharp",
            [".csproj"]    = "file_type_csproj",
            [".sln"]       = "file_type_sln",
            [".xaml"]      = "file_type_xaml",
            [".xml"]       = "file_type_xml",
            [".config"]    = "file_type_config",
            [".props"]     = "file_type_config",
            [".targets"]   = "file_type_config",
            [".json"]      = "file_type_json",
            [".md"]        = "file_type_markdown",
            [".markdown"]  = "file_type_markdown",
            [".txt"]       = "file_type_text",
            [".log"]       = "file_type_log",
            [".js"]        = "file_type_js_official",
            [".mjs"]       = "file_type_js_official",
            [".cjs"]       = "file_type_js_official",
            [".jsx"]       = "file_type_reactjs",
            [".ts"]        = "file_type_typescript_official",
            [".tsx"]       = "file_type_reactts",
            [".html"]      = "file_type_html",
            [".htm"]       = "file_type_html",
            [".css"]       = "file_type_css",
            [".scss"]      = "file_type_scss",
            [".sass"]      = "file_type_scss",
            [".py"]        = "file_type_python",
            [".yml"]       = "file_type_yaml",
            [".yaml"]      = "file_type_yaml",
            [".toml"]      = "file_type_toml",
            [".sh"]        = "file_type_shell",
            [".bash"]      = "file_type_shell",
            [".ps1"]       = "file_type_powershell",
            [".psm1"]      = "file_type_powershell",
            [".bat"]       = "file_type_bat",
            [".cmd"]       = "file_type_bat",
            [".png"]       = "file_type_image",
            [".jpg"]       = "file_type_image",
            [".jpeg"]      = "file_type_image",
            [".gif"]       = "file_type_image",
            [".bmp"]       = "file_type_image",
            [".webp"]      = "file_type_image",
            [".ico"]       = "file_type_image",
            [".svg"]       = "file_type_svg",
            [".dll"]       = "file_type_binary",
            [".exe"]       = "file_type_binary",
            [".pdb"]       = "file_type_binary",
            [".pdf"]       = "file_type_pdf2",
            [".zip"]       = "file_type_zip",
            [".7z"]        = "file_type_zip",
            [".tar"]       = "file_type_zip",
            [".gz"]        = "file_type_zip",
        };

    private static readonly System.Collections.Generic.Dictionary<string, string> SpecialFileIcons =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            [".gitignore"]      = "file_type_git",
            [".gitattributes"]  = "file_type_git",
            [".gitmodules"]     = "file_type_git",
        };

    private static readonly System.Collections.Generic.Dictionary<string, string> FolderIcons =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["src"]            = "folder_type_src",
            ["source"]         = "folder_type_src",
            ["sources"]        = "folder_type_src",
            ["lib"]            = "folder_type_src",
            ["app"]            = "folder_type_src",
            ["views"]          = "folder_type_view",
            ["view"]           = "folder_type_view",
            ["pages"]          = "folder_type_view",
            ["screens"]        = "folder_type_view",
            ["components"]     = "folder_type_component",
            ["controls"]       = "folder_type_component",
            ["widgets"]        = "folder_type_component",
            ["tests"]          = "folder_type_test",
            ["test"]           = "folder_type_test",
            ["__tests__"]      = "folder_type_test",
            ["spec"]           = "folder_type_test",
            ["specs"]          = "folder_type_test",
            ["dist"]           = "folder_type_dist",
            ["build"]          = "folder_type_dist",
            ["out"]            = "folder_type_dist",
            ["bin"]            = "folder_type_dist",
            ["obj"]            = "folder_type_dist",
            ["node_modules"]   = "folder_type_node",
            [".git"]           = "folder_type_git",
        };

    private static string FileIconName(string fileName)
    {
        if (SpecialFileIcons.TryGetValue(fileName, out var s)) return s;
        var ext = System.IO.Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && ExtIcons.TryGetValue(ext, out var n) ? n : "default_file";
    }

    private static string FolderIconName(string folderName)
        => FolderIcons.TryGetValue(folderName, out var n) ? n : "default_folder";

    /// <summary>Single sentinel child added to every directory so the expander
    /// chevron shows. Replaced with real children on first expand.</summary>
    private static readonly FileNode Placeholder = new("(loading)", isDirectory: false, rootPath: string.Empty);

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && IsDirectory && !_childrenLoaded) LoadChildren();
    }

    /// <summary>Replace cached children with a fresh enumeration. Safe to call
    /// while expanded — the TreeView preserves expansion via ItemContainerStyle
    /// IsExpanded binding.</summary>
    public void Reload()
    {
        if (!IsDirectory) return;
        _childrenLoaded = false;
        Children.Clear();
        if (IsExpanded) LoadChildren();
        else Children.Add(Placeholder);
    }

    private void LoadChildren()
    {
        _childrenLoaded = true;
        Children.Clear();
        try
        {
            var dirs = System.IO.Directory.EnumerateDirectories(FullPath)
                .Where(p => !IsIgnored(System.IO.Path.GetFileName(p)))
                .OrderBy(p => p, System.StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs) Children.Add(new FileNode(d, isDirectory: true, RootPath));

            var files = System.IO.Directory.EnumerateFiles(FullPath)
                .OrderBy(p => p, System.StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) Children.Add(new FileNode(f, isDirectory: false, RootPath));
        }
        catch
        {
            // Permission denied / deleted while enumerating — show empty.
        }
    }

    private static bool IsIgnored(string? name)
        => !string.IsNullOrEmpty(name) && (IgnoredNames.Contains(name) || name.StartsWith('.') && name is ".git" or ".vs" or ".idea");
}
