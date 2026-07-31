namespace MatOS.Web.Config;

/// <summary>An icon's placement on the desktop (pixels from the top-left of the icon field).</summary>
public class IconPos
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>Per-user desktop icon placements. Persisted as desktop-layout.json so each
/// user's arrangement survives restarts. Keyed by user id -> (icon key -> position).</summary>
public class DesktopLayoutStore
{
    public Dictionary<string, Dictionary<string, IconPos>> Users { get; set; } = new();
}

/// <summary>Which app icons each user has pinned to the desktop (keyed by user id -> icon keys).
/// Container/stack apps are only shown on the desktop when pinned.</summary>
public class DesktopPinsStore
{
    public Dictionary<string, List<string>> Users { get; set; } = new();
}

/// <summary>An iOS/macOS-style folder holding one or more app icons. The folder itself has an
/// icon key ("folder:{id}") that participates in placement/pinning; the child keys inside are
/// hidden from the desktop while they're in a folder.</summary>
public class DesktopFolder
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Folder";
    public List<string> Keys { get; set; } = new();
}

public class DesktopFoldersStore
{
    public Dictionary<string, List<DesktopFolder>> Users { get; set; } = new();
}

/// <summary>A desktop widget the user has placed on their desktop. Type identifies the widget
/// implementation on the client (e.g. "clock", "cpu", "memory", "containers"); Config carries
/// arbitrary widget-specific settings; W/H are size buckets (1..3, small/medium/large).</summary>
public class DesktopWidget
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 2;
    public int H { get; set; } = 2;
    public Dictionary<string, string> Config { get; set; } = new();
}

public class DesktopWidgetsStore
{
    public Dictionary<string, List<DesktopWidget>> Users { get; set; } = new();
}
