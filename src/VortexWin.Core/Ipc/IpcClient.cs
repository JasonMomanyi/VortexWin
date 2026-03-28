using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace VortexWin.Core.Ipc;

/// <summary>
/// Named Pipe IPC client for Tray and Settings apps to communicate with the Service.
/// ACL-restricted to current user SID.
/// </summary>
public sealed class IpcClient : IDisposable
{
    private NamedPipeClientStream? _pipeClient;
    private readonly string _pipeName;

    public IpcClient(string pipeName = IpcConstants.PipeName)
    {
        _pipeName = pipeName;
    }

    /// <summary>
    /// Send a request to the Vortex Win service and receive a response.
    /// </summary>
    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            _pipeClient = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await _pipeClient.ConnectAsync(IpcConstants.ConnectionTimeoutMs, ct).ConfigureAwait(false);

            // Write request
            string requestJson = request.Serialize();
            byte[] requestBytes = Encoding.UTF8.GetBytes(requestJson);
            byte[] lengthPrefix = BitConverter.GetBytes(requestBytes.Length);

            await _pipeClient.WriteAsync(lengthPrefix, ct).ConfigureAwait(false);
            await _pipeClient.WriteAsync(requestBytes, ct).ConfigureAwait(false);
            await _pipeClient.FlushAsync(ct).ConfigureAwait(false);

            // Read response
            byte[] responseLengthBytes = new byte[4];
            int bytesRead = await _pipeClient.ReadAsync(responseLengthBytes, ct).ConfigureAwait(false);
            if (bytesRead < 4)
                return new IpcResponse { Status = IpcStatus.Error, Message = "Failed to read response length." };

            int responseLength = BitConverter.ToInt32(responseLengthBytes, 0);
            if (responseLength <= 0 || responseLength > IpcConstants.MaxMessageSize)
                return new IpcResponse { Status = IpcStatus.Error, Message = "Invalid response length." };

            byte[] responseBytes = new byte[responseLength];
            int totalRead = 0;
            while (totalRead < responseLength)
            {
                int read = await _pipeClient.ReadAsync(
                    responseBytes.AsMemory(totalRead, responseLength - totalRead), ct).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            string responseJson = Encoding.UTF8.GetString(responseBytes, 0, totalRead);
            return IpcResponse.Deserialize(responseJson) ??
                   new IpcResponse { Status = IpcStatus.Error, Message = "Failed to deserialize response." };
        }
        catch (TimeoutException)
        {
            return new IpcResponse { Status = IpcStatus.Timeout, Message = "Service connection timed out." };
        }
        catch (Exception ex)
        {
            return new IpcResponse { Status = IpcStatus.Error, Message = ex.Message };
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>
    /// Convenience method to query the current service status.
    /// </summary>
    public async Task<ServiceStatusDto?> GetStatusAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(new IpcRequest { Command = IpcCommand.QueryStatus }, ct)
            .ConfigureAwait(false);

        if (response.Status == IpcStatus.Ok && response.Data is not null)
            return ServiceStatusDto.Deserialize(response.Data);

        return null;
    }

    public void Dispose()
    {
        _pipeClient?.Dispose();
        _pipeClient = null;
    }
}
