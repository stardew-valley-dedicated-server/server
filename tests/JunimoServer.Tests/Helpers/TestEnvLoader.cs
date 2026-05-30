namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Loads .env.test into the process environment. File values override existing env vars.
/// </summary>
public static class TestEnvLoader
{
    private static readonly Lazy<bool> _loaded = new(LoadIntoProcess);

    public static void EnsureLoaded() => _ = _loaded.Value;

    public static string? Get(string key)
    {
        EnsureLoaded();
        return Environment.GetEnvironmentVariable(key);
    }

    private static bool LoadIntoProcess()
    {
        // .env.test lives at the project root; anchor at ProjectRoot rather
        // than CWD so the file is found regardless of how the binary was
        // launched (`dotnet run` may set CWD to the bin output directory).
        var envFile = Path.Combine(ProjectRoot.Path, ".env.test");
        if (!File.Exists(envFile)) return true;

        DotNetEnv.Env.Load(envFile);
        return true;
    }
}
