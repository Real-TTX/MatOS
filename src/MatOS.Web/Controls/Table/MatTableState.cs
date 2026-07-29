namespace MatOS.Web.Controls.Table;

public sealed class MatColumn
{
    public string? Field { get; set; }
    public string? Title { get; set; }
    public bool Sortable { get; set; }
    public string? Align { get; set; }   // left | center | right
    public string? Width { get; set; }
    public string? Format { get; set; }  // .NET-Formatstring
    public string? Template { get; set; } // bool | badges | actions
}

/// <summary>Von Eltern (mat-table) angelegtes, von Kindern (mat-column/mat-filters) mutiertes State-Objekt.
/// Als geteilte Referenz platziert -> Mutationen der Kinder sind für den Eltern-TagHelper sichtbar.</summary>
public sealed class MatTableState
{
    public List<MatColumn> Columns { get; } = new();
    public string? FiltersHtml { get; set; }
}
