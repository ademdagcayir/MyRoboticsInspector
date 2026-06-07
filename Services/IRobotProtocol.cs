using MyRoboticsInspector.Models;

namespace MyRoboticsInspector.Services;

public interface IRobotProtocol : IAsyncDisposable
{
    bool IsConnected { get; }

    event EventHandler<bool>? ConnectionChanged;
    event EventHandler<byte[]>? DataReceived;
    event EventHandler<string>? ErrorOccurred;

    Task ConnectAsync(string host, int port, CancellationToken ct = default);
    Task DisconnectAsync();
    Task SendAsync(RobotCommand command, CancellationToken ct = default);

    /// <summary>Ham topic'e düz metin yayınlar (firmware bare-topic protokolü için).</summary>
    Task PublishRawAsync(string topic, string payload, CancellationToken ct = default);
}
