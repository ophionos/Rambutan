using System.Diagnostics;
using System.Text;

namespace Rambutan.Streams;

internal static class ProcessRunner
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr);

    public static async Task<Result> RunAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken ct,
        string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            psi.Environment[name] = value;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var stdOut = process.StandardOutput.ReadToEndAsync(ct);
        var stdErr = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            // Let the readers observe the closed streams before the process is
            // disposed under them; the kill makes this prompt.
            try { await Task.WhenAll(stdOut, stdErr); } catch { }
            throw;
        }

        return new Result(process.ExitCode, await stdOut, await stdErr);
    }
}
