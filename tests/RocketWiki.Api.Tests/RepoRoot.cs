namespace RocketWiki.Api.Tests;

/// <summary>
/// Locates the repo root from wherever the test assembly happens to run
/// (bin/Debug/net10.0/, a different configuration, CI's working directory,
/// whatever) by walking up from the assembly's own location until it finds
/// the checked-in solution file. Avoids hard-coding "../../.." style relative
/// paths that break the moment someone changes build output layout.
/// </summary>
internal static class RepoRoot
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RocketWiki.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate RocketWiki.sln by walking up from '{AppContext.BaseDirectory}'.");
    }
}
