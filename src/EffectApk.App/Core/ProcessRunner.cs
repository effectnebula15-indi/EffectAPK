using System.Diagnostics;
using System.IO;
using System.Text;

namespace EffectApk.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

public static class ProcessRunner
{
    /// <summary>Запускает процесс, ждёт завершения, возвращает вывод. Таймаут убивает всё дерево процессов.</summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan? timeout = null,
        string? stdin = null,
        Action<string>? onOutputLine = null,
        IDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(fileName, arguments, stdin != null, environment) };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (stdout) stdout.AppendLine(e.Data); onOutputLine?.Invoke(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (stderr) stderr.AppendLine(e.Data); onOutputLine?.Invoke(e.Data); } };

        Logger.Debug($"run: {fileName} {arguments}");
        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin != null)
        {
            try
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // процесс завершился, не дочитав stdin (например, sdkmanager принял меньше «y», чем мы дали)
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"«{Path.GetFileName(fileName)} {arguments}» не завершился за {timeout}.");
        }

        Logger.Debug($"exit={process.ExitCode} за {stopwatch.ElapsedMilliseconds} мс: {Path.GetFileName(fileName)} {arguments}");
        lock (stdout) lock (stderr)
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Запускает долгоживущий фоновый процесс (эмулятор, scrcpy) и возвращает его, не дожидаясь завершения.</summary>
    public static Process StartDetached(string fileName, string arguments, Action<string>? onOutputLine = null)
    {
        var process = new Process
        {
            StartInfo = CreateStartInfo(fileName, arguments, redirectStdin: false, environment: null),
            EnableRaisingEvents = true,
        };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onOutputLine?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutputLine?.Invoke(e.Data); };

        Logger.Debug($"start: {fileName} {arguments}");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Logger.Info($"Фоновый процесс запущен: pid={process.Id}, {Path.GetFileName(fileName)}");
        return process;
    }

    private static ProcessStartInfo CreateStartInfo(
        string fileName, string arguments, bool redirectStdin, IDictionary<string, string>? environment)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (environment != null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;
        return psi;
    }
}
