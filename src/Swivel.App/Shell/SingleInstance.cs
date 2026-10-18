using System.IO.Pipes;
using System.Text.Json;
using Serilog;
using Swivel.App.Services;

namespace Swivel.App.Shell;

/// <summary>
/// One Swivel per user. Later launches (the Explorer menu runs <c>Swivel.exe --wheel paths</c>, once
/// per selected file on the classic menu) hand their paths to the running instance over a named pipe;
/// arrivals within a short window are gathered into one wheel.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly TimeSpan GatherWindow = TimeSpan.FromMilliseconds(400);

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _pending = [];
    private readonly Lock _lock = new();
    private Timer? _flush;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    private static string PipeName => AppPaths.InstanceName + "-wheel";

    /// <summary>Raised on a pool thread with one gathered batch of paths.</summary>
    public event Action<IReadOnlyList<string>>? WheelRequested;

    /// <summary>Returns the instance if this is the first Swivel, or null after handing <paramref name="paths"/> on.</summary>
    public static SingleInstance? Claim(IReadOnlyList<string> paths)
    {
        var mutex = new Mutex(true, @"Local\" + AppPaths.InstanceName, out bool first);
        if (first) return new SingleInstance(mutex);
        mutex.Dispose();
        if (paths.Count > 0) Send(paths);
        return null;
    }

    private static void Send(IReadOnlyList<string> paths)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(JsonSerializer.Serialize(paths));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // The running instance is busy starting or shutting down; nothing more to do.
        }
    }

    public void Listen() => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(pipe);
                if (await reader.ReadLineAsync(_stop.Token) is { } line && JsonSerializer.Deserialize<string[]>(line) is { } paths)
                    Gather(paths);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warning(ex, "Wheel hand-off pipe hiccup");
            }
        }
    });

    /// <summary>Queues paths for a wheel (also used for this instance's own --wheel arguments).</summary>
    public void Gather(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            _pending.AddRange(paths.Where(p => File.Exists(p) || Directory.Exists(p)));
            _flush ??= new Timer(_ => Flush());
            _flush.Change(GatherWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        string[] batch;
        lock (_lock)
        {
            batch = _pending.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _pending.Clear();
        }
        if (batch.Length > 0) WheelRequested?.Invoke(batch);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _flush?.Dispose();
        _mutex.Dispose();
    }
}
