using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatOS.Web.Controls.Tabs;

[HtmlTargetElement("mat-tab", ParentTag = "mat-tabbar")]
public class MatTabTagHelper : TagHelper
{
    public string Key { get; set; } = "";
    public string? Title { get; set; }
    public string? Icon { get; set; }
    public bool Disabled { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var child = await output.GetChildContentAsync();
        if (context.Items.TryGetValue(typeof(MatTabBarState), out var raw) && raw is MatTabBarState state)
        {
            state.Tabs.Add(new MatTab
            {
                Key = Key,
                Title = Title ?? Key,
                Icon = Icon,
                Disabled = Disabled,
                PanelHtml = child.GetContent()
            });
        }
        output.SuppressOutput();
    }
}
