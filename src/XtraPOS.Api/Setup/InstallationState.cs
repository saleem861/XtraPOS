namespace XtraPOS.Api.Setup;

public static class InstallationState
{
    public const string FileName = "installation-configured";

    public static bool IsConfigured(string contentRoot)
    {
        return File.Exists(Path(contentRoot));
    }

    public static void MarkConfigured(string contentRoot)
    {
        var path = Path(contentRoot);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Configured");
    }

    public static void Clear(string contentRoot)
    {
        var path = Path(contentRoot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static string Path(string contentRoot)
    {
        return System.IO.Path.Combine(contentRoot, "App_Data", FileName);
    }
}
