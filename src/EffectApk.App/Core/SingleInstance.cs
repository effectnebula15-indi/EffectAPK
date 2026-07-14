using System.IO;
using System.IO.Pipes;

namespace EffectApk.Core;

/// <summary>
/// Один экземпляр приложения на пользователя: двойной клик по второму .apk
/// не поднимает новый процесс, а передаёт путь работающему через именованный канал.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "EffectApk.Instance";
    private const string PipeName = "EffectApk.Pipe";

    /// <summary>Возвращает mutex, если мы — первый экземпляр; иначе null.</summary>
    public static Mutex? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out var createdNew);
        if (createdNew) return mutex;
        mutex.Dispose();
        return null;
    }

    public static void SendToPrimary(IEnumerable<string> paths)
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        client.Connect(3000);
        using var writer = new StreamWriter(client) { AutoFlush = true };
        foreach (var path in paths)
            writer.WriteLine(path);
    }

    public static async Task RunServerAsync(Action<string> onPath, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server);
                while (await reader.ReadLineAsync(ct) is { } line)
                    if (line.Length > 0)
                        onPath(line);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Error("Ошибка pipe-сервера", ex);
                await Task.Delay(1000, ct);
            }
        }
    }
}
