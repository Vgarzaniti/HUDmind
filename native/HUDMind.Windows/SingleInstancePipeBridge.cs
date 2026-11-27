using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace HUDMind.Windows;

/// <summary>
/// Current-user-ACL-restricted named pipe bridge so launching a second HUDMind instance
/// wakes and focuses the already-running instance cleanly instead of showing a dead popup.
/// </summary>
public sealed class SingleInstancePipeServer : IDisposable
{
    public const string DefaultPipeName = "HUDMind_Native_SingleInstance_Pipe_v1";

    private readonly string _pipeName;
    private readonly Action<string> _onCommand;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenTask;
    private volatile bool _disposed;

    public SingleInstancePipeServer(string pipeName, Action<string> onCommand)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName.Trim();
        _onCommand = onCommand ?? throw new ArgumentNullException(nameof(onCommand));
        _listenTask = Task.Run(ListenLoopAsync);
    }

    public static bool TrySignalExistingInstance(
        string pipeName = DefaultPipeName,
        string command = "SHOW",
        int timeoutMs = 450)
    {
        try
        {
            var resolved = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName.Trim();
            using var client = new NamedPipeClientStream(".", resolved, PipeDirection.Out, PipeOptions.None);
            client.Connect(Math.Clamp(timeoutMs, 100, 3000));
            var msg = (string.IsNullOrWhiteSpace(command) ? "SHOW" : command.Trim()) + "\n";
            var bytes = Encoding.UTF8.GetBytes(msg);
            if (bytes.Length > 64)
                bytes = bytes[..64];
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenLoopAsync()
    {
        var buffer = new byte[64];
        while (!_cts.IsCancellationRequested && !_disposed)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateCurrentUserSecuredServer();
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                int read = await server.ReadAsync(buffer.AsMemory(0, buffer.Length), _cts.Token).ConfigureAwait(false);
                if (read > 0)
                {
                    var cmd = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                    if (!string.IsNullOrEmpty(cmd))
                    {
                        try { _onCommand(cmd); } catch { }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (_cts.IsCancellationRequested || _disposed)
                    break;
                try { await Task.Delay(120, _cts.Token).ConfigureAwait(false); } catch { break; }
            }
            finally
            {
                try { server?.Dispose(); } catch { }
            }
        }
    }

    private NamedPipeServerStream CreateCurrentUserSecuredServer()
    {
        var userSid = WindowsIdentity.GetCurrent().User;
        if (userSid is not null)
        {
            var ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.FullControl, AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 256,
                outBufferSize: 256,
                ps);
        }

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            256,
            256);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _listenTask.Wait(TimeSpan.FromMilliseconds(250)); } catch { }
        _cts.Dispose();
    }
}
