namespace Blocwerk.Core.Helpers;

/// <summary>Finds the recognition model files the app ships next to its assemblies.</summary>
public static class ModelPathResolver
{
    /// <summary>
    /// Where the app's detector reads its model from: a rooted path as it is, a relative one next to the app's assemblies
    /// when it is there, else as given (relative to the working directory).
    /// </summary>
    /// <param name="configuredPath">The configured path.</param>
    /// <returns>The path the detector opens.</returns>
    public static string Resolve(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var assemblyDir = Path.GetDirectoryName(typeof(ModelPathResolver).Assembly.Location) ?? ".";
        var resolved = Path.Join(assemblyDir, configuredPath);
        return File.Exists(resolved) ? resolved : configuredPath;
    }
}
