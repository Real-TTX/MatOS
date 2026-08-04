namespace MatOS.Web.Docker;

/// <summary>Container label keys understood by Matcad's Docker discovery. matOS stamps
/// these on app containers it creates so Matcad routes them through Caddy (per-subdomain).
/// Verified against Matcad's Config/ConfigModels.cs (DockerLabels).</summary>
public static class MatcadLabels
{
    public const string Enable = "matcad.enable"; // "true"
    public const string Host = "matcad.host";     // <slug>-<instance>.<BaseDomain>
    public const string Port = "matcad.port";     // internal container port
    public const string Auth = "matcad.auth";     // name of a Matcad authentication
}

/// <summary>Labels matOS uses to recognise the containers it manages itself.</summary>
public static class MatosLabels
{
    public const string Managed = "matos.managed";   // "true"
    public const string App = "matos.app";           // catalog app id (M2)
    public const string Instance = "matos.instance"; // install instance id (M2)
    public const string Title = "matos.title";       // friendly display name
    public const string Icon = "matos.icon";         // icon hint / data URI (M2)
    public const string OnDemand = "matos.ondemand";  // "true" = start on open, stop on window close
}
