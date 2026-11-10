namespace pxdMediaManager.Services;

/// <summary>Where the app keeps its settings and caches.</summary>
public static class AppPaths
{
    /// <summary>
    /// %APPDATA%\pxdMediaManager (~/.config/pxdMediaManager on Linux). The PXDMM_DATA_DIR environment variable points it
    /// somewhere else, e.g. a throwaway folder for tests and screenshots.
    /// </summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("PXDMM_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pxdMediaManager");
}
