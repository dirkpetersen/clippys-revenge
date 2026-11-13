using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace ClippysRevenge.Core;

public sealed class SingleInstance : IDisposable
{
    public const string DefaultMutexName = @"Local\ClippysRevenge";
    public const string ShowSettingsCommand = "show-settings";
    public const int MaxCommandLength = 64;

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();
    private Task? _loop;
    private bool _disposed;

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    public static string DefaultPipeName { get; } = "ClippysRevenge-" + Process.GetCurrentProcess().SessionId;

    public static SingleInstance? TryAcquire(string mutexName = DefaultMutexName, string? pipeName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(mutexName);
        if (pipeName is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(pipeName);
        }
        var mutex = new Mutex(false, mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, pipeName ?? DefaultPipeName);
    }

    public void StartListening(Action onShowSettings)
    {
        ArgumentNullException.ThrowIfNull(onShowSettings);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop is not null)
            {
                throw new InvalidOperationException("Already listening.");
            }
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(onShowSettings, token));
        }
    }

    public static bool SignalExisting(string command, string? pipeName = null, int timeoutMs = 2000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(command + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        Task? loop;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            loop = _loop;
        }
        _cts.Cancel();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Loop failures are already swallowed; nothing to report on shutdown.
        }
        _cts.Dispose();
        _mutex.Dispose();
    }

    private async Task RunAsync(Action callback, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await using (server.ConfigureAwait(false))
                {
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    var line = await ReadLineAsync(server, token).ConfigureAwait(false);
                    if (string.Equals(line, ShowSettingsCommand, StringComparison.Ordinal))
                    {
                        try
                        {
                            callback();
                        }
                        catch
                        {
                            // A failing callback must not end the loop.
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                try
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Reads one line of at most MaxCommandLength chars within 2 s; null otherwise.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var buf = new byte[1];
        var bytes = new List<byte>();
        try
        {
            while (bytes.Count <= MaxCommandLength * 4)
            {
                var n = await stream.ReadAsync(buf, timeout.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    return null;
                }
                if (buf[0] == (byte)'\n')
                {
                    var s = Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                    return s.Length <= MaxCommandLength ? s : null;
                }
                bytes.Add(buf[0]);
            }
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
    }
}
