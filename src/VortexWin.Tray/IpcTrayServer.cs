namespace VortexWin.Tray;

using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VortexWin.Core.Ipc;

public class IpcTrayServer
{
    private bool _running;
    private readonly Form1 _mainForm;

    public IpcTrayServer(Form1 mainForm)
    {
        _mainForm = mainForm;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _running = true;

        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                var pipeSecurity = CreatePipeSecurity();
                var pipe = NamedPipeServerStreamAcl.Create(
                    IpcConstants.TrayPipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    IpcConstants.MaxMessageSize,
                    IpcConstants.MaxMessageSize,
                    pipeSecurity);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // Handle in background
                _ = Task.Run(() => HandleConnectionAsync(pipe, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    public void Stop() => _running = false;

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using (pipe)
            {
                byte[] lengthBytes = new byte[4];
                if (await pipe.ReadAsync(lengthBytes, ct).ConfigureAwait(false) < 4) return;

                int messageLength = BitConverter.ToInt32(lengthBytes, 0);
                if (messageLength <= 0 || messageLength > IpcConstants.MaxMessageSize) return;

                byte[] messageBytes = new byte[messageLength];
                int totalRead = 0;
                while (totalRead < messageLength)
                {
                    int read = await pipe.ReadAsync(
                        messageBytes.AsMemory(totalRead, messageLength - totalRead), ct)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    totalRead += read;
                }

                string json = Encoding.UTF8.GetString(messageBytes, 0, totalRead);
                var request = IpcRequest.Deserialize(json);
                if (request is null) return;

                if (request.Command == IpcCommand.ShowAlert && !string.IsNullOrEmpty(request.Payload))
                {
                    _mainForm.Invoke((System.Windows.Forms.MethodInvoker)delegate {
                        _mainForm.ShowAlert(request.Payload);
                    });
                }
            }
        }
        catch { }
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        
        // Allow current user
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser != null)
        {
            security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        // Allow SYSTEM (so Service running as SYSTEM can connect to Tray)
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(systemSid, PipeAccessRights.FullControl, AccessControlType.Allow));

        return security;
    }
}
