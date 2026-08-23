using System.Diagnostics;

namespace RocketWiki.SqlServer.Tests;

/// <summary>
/// One bounded, cached probe of the Docker daemon, evaluated the first time any
/// <see cref="SqlServerFactAttribute"/> is constructed (i.e. at test discovery).
///
/// Docker presence IS the switch for this whole project - deliberately not an
/// environment-variable opt-in, which CI could silently forget and thereby lose the
/// tier without anyone noticing. Instead:
///  - no working Docker  → every test skips, each with a reason naming this probe;
///  - working Docker     → every test runs, no configuration needed.
/// The CI job closes the remaining hole from the other side: it fails outright if
/// this project executes zero tests (a skip on the runner means Docker broke there,
/// which must be a red build, never a quietly green one).
///
/// The probe is `docker info --format {{.ServerVersion}}` with a hard timeout - a
/// real round-trip to the daemon (honoring DOCKER_HOST/context config exactly as the
/// CLI does), not a socket/pipe existence check: a broken Docker Desktop install can
/// leave the pipe present with nothing healthy behind it, and "present but broken"
/// must skip, not fail. A daemon that answers `docker info` is one Testcontainers
/// can use.
/// </summary>
internal static class DockerAvailability
{
    private static readonly Lazy<string?> LazySkipReason = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Null when the daemon answered; otherwise the skip reason.</summary>
    public static string? SkipReason => LazySkipReason.Value;

    public static bool IsAvailable => SkipReason is null;

    private static string? Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info --format {{.ServerVersion}}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return "No working Docker daemon (the docker CLI failed to start).";
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort - the verdict below stands either way.
                }

                return "No working Docker daemon (`docker info` did not answer within 5s).";
            }

            if (process.ExitCode != 0)
            {
                var stderr = process.StandardError.ReadToEnd();
                return $"No working Docker daemon (`docker info` exited {process.ExitCode}: {FirstLine(stderr)}).";
            }

            return null;
        }
        catch (Exception ex)
        {
            // Typically Win32Exception: no docker CLI on PATH at all.
            return $"No working Docker daemon ({ex.GetType().Name}: {FirstLine(ex.Message)}).";
        }
    }

    private static string FirstLine(string message)
    {
        var trimmed = message.Trim();
        var newlineIndex = trimmed.IndexOfAny(['\r', '\n']);
        return newlineIndex < 0 ? trimmed : trimmed[..newlineIndex];
    }
}
