using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatOS.Web.Controls.Table;

[HtmlTargetElement("mat-column", ParentTag = "mat-table")]
public class MatColumnTagHelper : TagHelper
{
    public string? Field { get; set; }
    public string? Title { get; set; }
    public bool Sortable { get; set; }
    public string? Align { get; set; }
    public string? Width { get; set; }
    public string? Format { get; set; }
    public string? Template { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (context.Items.TryGetValue(typeof(MatTableState), out var raw) && raw is MatTableState state)
        {
            state.Columns.Add(new MatColumn
            {
                Field = Field,
                Title = Title ?? Field,
                Sortable = Sortable,
                Align = Align,
                Width = Width,
                Format = Format,
                Template = Template
            });
        }
        output.SuppressOutput();
    }
}
