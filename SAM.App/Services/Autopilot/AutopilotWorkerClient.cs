using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace SAM.App.Services.Autopilot;

/// <summary>Thrown when a worker can't be started, doesn't answer or exits. The message is user-facing.</summary>
public sealed class AutopilotWorkerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The engine's handle on one worker process: starts it, connects to its pipe and sends requests
/// one at a time. A request that times out or breaks the pipe kills the worker (its answer could
/// otherwise arrive late and be read as the next one's).
/// </summary>
internal sealed class AutopilotWorkerClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Process _process;
    private readonly ulong _steamId;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private string? _lastError;
    private bool _disposed;

    private AutopilotWorkerClient(uint appId, ulong steamId, Process process)
    {
        this.AppId = appId;
        this._steamId = steamId;
        this._process = process;
        this.StartedAt = DateTimeOffset.Now;
        this._process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data) == false)
            {
                this._lastError = e.Data.Trim();
            }
        };
    }

    public uint AppId { get; }

    public int ProcessId => this._process.Id;

    public DateTimeOffset StartedAt { get; }

    /// <summary>True once the pipe is connected and the worker answered a ping.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Whether the worker reports dry-run mode.</summary>
    public bool DryRun { get; private set; }

    public bool HasExited
    {
        get
        {
            try
            {
                return this._process.HasExited;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>Why the worker exited, from its exit code and the line it wrote to stderr. Call after <see cref="HasExited"/>.</summary>
    public string DescribeExit()
    {
        int code;
        try
        {
            this._process.WaitForExit(1000);
            code = this._process.ExitCode;
        }
        catch (Exception)
        {
            return this._lastError ?? Strings.Get("AutopilotEngine_WorkerStopped");
        }

        return code switch
        {
            AutopilotProtocol.ExitWindowOpen => Strings.Get("AutopilotEngine_WindowOpen"),
            AutopilotProtocol.ExitAlreadyRunning => Strings.Get("AutopilotEngine_AlreadyRunning"),
            AutopilotProtocol.ExitNoSchema => Strings.Get("AutopilotEngine_NoSchema"),
            _ => this._lastError ?? Strings.Format("AutopilotEngine_WorkerExited", code.ToString(CultureInfo.CurrentCulture)),
        };
    }

    /// <summary>The worker's exit code, or null while it runs.</summary>
    public int? ExitCode
    {
        get
        {
            try
            {
                return this._process.HasExited ? this._process.ExitCode : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Starts <c>SAM.exe &lt;appid&gt; --autopilot-worker &lt;this pid&gt;</c> (inheriting this process's environment, so dry-run carries over).</summary>
    public static AutopilotWorkerClient Start(uint appId, ulong steamId)
    {
        var path = Environment.ProcessPath ?? throw new AutopilotWorkerException(Strings.Format("AutopilotEngine_WorkerStartFailed", "no executable path"));
        var arguments = string.Create(CultureInfo.InvariantCulture, $"{appId} {Infrastructure.CommandLine.AutopilotWorkerOption} {Environment.ProcessId}");
        var info = new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception e)
        {
            throw new AutopilotWorkerException(Strings.Format("AutopilotEngine_WorkerStartFailed", e.Message), e);
        }

        if (process == null)
        {
            throw new AutopilotWorkerException(Strings.Format("AutopilotEngine_WorkerStartFailed", "Process.Start returned null"));
        }

        var client = new AutopilotWorkerClient(appId, steamId, process);
        try
        {
            process.BeginErrorReadLine();
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Autopilot: couldn't read the worker's stderr for {appId}", e);
        }

        return client;
    }

    /// <summary>
    /// Connects to the worker's pipe (retrying while it initializes Steam) and pings it. Throws
    /// <see cref="AutopilotWorkerException"/> if the worker exits or doesn't answer in time.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + ConnectTimeout;
        var pipeName = AutopilotProtocol.PipeName(this._steamId, this.AppId);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this.HasExited)
            {
                throw new AutopilotWorkerException(this.DescribeExit());
            }

            if (DateTimeOffset.Now > deadline)
            {
                this.Kill();
                throw new AutopilotWorkerException(Strings.Get("AutopilotEngine_WorkerNoAnswer"));
            }

            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(1000, cancellationToken);
            }
            catch (Exception e) when (e is TimeoutException or IOException)
            {
                await pipe.DisposeAsync();
                await Task.Delay(500, cancellationToken);
                continue;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }

            var encoding = new UTF8Encoding(false);
            this._pipe = pipe;
            this._reader = new StreamReader(pipe, encoding, false, 4096, leaveOpen: true);
            this._writer = new StreamWriter(pipe, encoding, 4096, leaveOpen: true) { NewLine = "\n" };
            break;
        }

        var pong = await this.SendAsync(new AutopilotRequest { Type = AutopilotProtocol.Ping }, TimeSpan.FromSeconds(10), cancellationToken);
        this.DryRun = pong.DryRun;
        this.IsReady = true;
    }

    /// <summary>
    /// Sends one request and waits for its answer. Throws <see cref="AutopilotWorkerException"/>
    /// (and kills the worker) on timeout, a broken pipe or an unreadable answer.
    /// </summary>
    public async Task<AutopilotResponse> SendAsync(AutopilotRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await this._gate.WaitAsync(cancellationToken);
        try
        {
            if (this._writer == null || this._reader == null || this._disposed)
            {
                throw new AutopilotWorkerException(Strings.Get("AutopilotEngine_WorkerNoAnswer"));
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            string? line;
            try
            {
                await this._writer.WriteLineAsync(AutopilotProtocol.Serialize(request).AsMemory(), timeoutSource.Token);
                await this._writer.FlushAsync(timeoutSource.Token);
                line = await this._reader.ReadLineAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested == false)
            {
                this.Kill();
                throw new AutopilotWorkerException(Strings.Get("AutopilotEngine_WorkerNoAnswer"));
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                this.Kill();
                throw new AutopilotWorkerException(this.HasExited ? this.DescribeExit() : Strings.Get("AutopilotEngine_WorkerNoAnswer"), e);
            }

            var response = AutopilotProtocol.ParseResponse(line);
            if (response == null)
            {
                this.Kill();
                throw new AutopilotWorkerException(line == null && this.HasExited ? this.DescribeExit() : Strings.Get("AutopilotEngine_WorkerNoAnswer"));
            }

            return response;
        }
        finally
        {
            this._gate.Release();
        }
    }

    /// <summary>Asks the worker to stop and waits up to <paramref name="timeout"/> for it to exit, then kills it.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (this.HasExited == false && this.IsReady)
        {
            try
            {
                await this.SendAsync(new AutopilotRequest { Type = AutopilotProtocol.Stop }, timeout, CancellationToken.None);
            }
            catch (AutopilotWorkerException)
            {
                // Killed already.
            }
        }

        var deadline = DateTimeOffset.Now + timeout;
        while (this.HasExited == false && DateTimeOffset.Now < deadline)
        {
            await Task.Delay(100);
        }

        this.Kill();
    }

    /// <summary>
    /// Synchronous stop for shutdown (Dispose on the UI thread): writes "stop" without waiting
    /// for the answer. The caller waits for the processes and kills what's left.
    /// </summary>
    public void RequestStop()
    {
        try
        {
            if (this.HasExited == false && this._writer != null && this._gate.Wait(0))
            {
                try
                {
                    this._writer.WriteLine(AutopilotProtocol.Serialize(new AutopilotRequest { Type = AutopilotProtocol.Stop }));
                    this._writer.Flush();
                }
                finally
                {
                    this._gate.Release();
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Broken pipe: the worker exits on its own (or gets killed).
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the process to exit. Blocking.</summary>
    public bool WaitForExit(TimeSpan timeout)
    {
        try
        {
            return this._process.WaitForExit(timeout);
        }
        catch (Exception)
        {
            return true;
        }
    }

    public void Kill()
    {
        try
        {
            if (this._process.HasExited == false)
            {
                this._process.Kill();
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Autopilot: couldn't kill the worker for {this.AppId}", e);
        }
    }

    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        try
        {
            this._reader?.Dispose();
            this._writer?.Dispose();
        }
        catch (Exception)
        {
            // A broken pipe can throw on flush.
        }

        this._pipe?.Dispose();
        this._process.Dispose();
    }
}
