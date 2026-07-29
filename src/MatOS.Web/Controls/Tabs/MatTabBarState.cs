namespace MatOS.Web.Controls.Tabs;

public sealed class MatTab
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Icon { get; set; }
    public bool Disabled { get; set; }
    public string PanelHtml { get; set; } = "";
}

public sealed class MatTabBarState
{
    public List<MatTab> Tabs { get; } = new();
}
