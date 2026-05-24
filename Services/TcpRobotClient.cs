using System.Net.Sockets;
using System.Text;
using MyRoboticsInspector.Models;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Default robot protocol over plain TCP. Wire format is a placeholder JSON-line protocol
/// — adapt EncodeCommand() once the actual firmware spec is known.
/// </summary>
public class TcpRobotClient : IRobotProtocol
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _client?.Connected == true;

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        await DisconnectAsync();

        _client = new TcpClient { NoDelay = true };
        try
        {
            await _client.ConnectAsync(host, port, ct);
            _stream = _client.GetStream();

            _readCts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_readCts.Token));

            ConnectionChanged?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Robota bağlanılamadı: {ex.Message}");
            _client?.Dispose();
            _client = null;
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            _readCts?.Cancel();
            if (_readTask is not null)
            {
                try { await _readTask.ConfigureAwait(false); } catch { }
            }
        }
        finally
        {
            _stream?.Dispose();
            _client?.Dispose();
            _stream = null;
            _client = null;
            _readTask = null;
            _readCts?.Dispose();
            _readCts = null;
            ConnectionChanged?.Invoke(this, false);
        }
    }

    public async Task SendAsync(RobotCommand command, CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Robot bağlı değil.");

        var payload = EncodeCommand(command);
        await _sendLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(payload, ct);
            await _stream.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Komut gönderilemedi: {ex.Message}");
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// PLACEHOLDER protocol — line-delimited JSON. Replace once the robot firmware
    /// spec is known (could be binary, ProtoBuf, MAVLink, vendor-specific, etc.).
    /// </summary>
    private static byte[] EncodeCommand(RobotCommand cmd)
    {
        var line = cmd.Value is float v
            ? $"{{\"cmd\":\"{cmd.Type}\",\"value\":{v.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}\n"
            : $"{{\"cmd\":\"{cmd.Type}\"}}\n";
        return Encoding.UTF8.GetBytes(line);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && _stream is not null)
            {
                int read = await _stream.ReadAsync(buffer, ct);
                if (read <= 0) break;
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                DataReceived?.Invoke(this, chunk);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Robot bağlantısı koptu: {ex.Message}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                ConnectionChanged?.Invoke(this, false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }
}
