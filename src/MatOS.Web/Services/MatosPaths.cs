namespace MatOS.Web.Services;

/// <summary>Central path resolution for the data volume (config, keys, backups).
/// Everything matOS persists lives here so it survives container restarts.</summary>
public static class MatosPaths
{
    public static string DataDir(IHostEnvironment env)
        => Environment.GetEnvironmentVariable("MATOS_DATA_DIR")
           ?? (env.IsDevelopment() ? Path.Combine(env.ContentRootPath, "data") : "/app/data");

    public static string KeysDir(IHostEnvironment env) => Path.Combine(DataDir(env), "keys");
}
