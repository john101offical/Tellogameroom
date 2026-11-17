using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace TelloGameRoom.MachineGuard;

/// <summary>
/// Keeps the Machine app alive. Every two seconds it checks whether the app runs in the console session and starts
/// it if not (which also covers "start after logon"). It listens on a named pipe for the app's state so it can turn
/// Task Manager off while the PC is locked, and stand down when the owner closed the app with the admin PIN.
/// </summary>
public sealed class GuardWorker(ILogger<GuardWorker> logger, IConfiguration configuration) : BackgroundService
{
    public const string PipeName = "TelloGameRoom.MachineGuard";
    private const string MachineProcessName = "TelloGameRoom.Machine";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    private readonly string _machineExe = Path.GetFullPath(
        configuration["MachineExePath"] ?? Path.Combine(AppContext.BaseDirectory, MachineProcessName + ".exe"));

    private readonly Lock _sync = new();
    private uint? _maintenanceSession;
    private bool _locked = true;
    private uint? _policySession;
    private bool? _policyDisabled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MachineGuard started. Watching {Exe}", _machineExe);
        if (!File.Exists(_machineExe))
        {
            logger.LogError("The Machine app was not found at {Exe}", _machineExe);
        }

        var pipe = Task.Run(() => ListenAsync(stoppingToken), stoppingToken);
        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                Supervise();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Supervision check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        await pipe.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Never leave a customer account without Task Manager because the service stopped.
        try
        {
            UserSession.RestoreTaskManagerForEveryone();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not restore the Task Manager policy on stop");
        }

        return base.StopAsync(cancellationToken);
    }

    private void Supervise()
    {
        if (UserSession.ActiveSessionId() is not { } session)
        {
            return;
        }

        bool maintenance;
        bool locked;
        lock (_sync)
        {
            // Maintenance mode belongs to one sign-in: a new session (or a reboot) puts the guard back on duty.
            if (_maintenanceSession is { } m && m != session)
            {
                _maintenanceSession = null;
            }

            maintenance = _maintenanceSession == session;
            locked = _locked;
        }

        ApplyPolicy(session, disabled: locked && !maintenance);

        if (maintenance || UserSession.IsRunning(MachineProcessName, session) || !File.Exists(_machineExe))
        {
            return;
        }

        logger.LogWarning("The Machine app is not running in session {Session}; starting it", session);
        lock (_sync)
        {
            // Until the restarted app reports otherwise, assume the PC must be locked.
            _locked = true;
        }

        UserSession.Launch(_machineExe, session);
    }

    private void ApplyPolicy(uint session, bool disabled)
    {
        if (_policySession == session && _policyDisabled == disabled)
        {
            return;
        }

        UserSession.SetTaskManagerDisabled(session, disabled);
        _policySession = session;
        _policyDisabled = disabled;
        logger.LogInformation("Task Manager {State} for session {Session}", disabled ? "disabled" : "enabled", session);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = CreatePipe();
                await server.WaitForConnectionAsync(ct);
                if (!IsFromMachineApp(server, out var client))
                {
                    logger.LogWarning("Ignored a pipe connection from {Client}: only {Exe} may send messages", client, _machineExe);
                    continue;
                }

                using var reader = new StreamReader(server, new UTF8Encoding(false), leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var accepted = Handle(await reader.ReadLineAsync(timeout.Token));

                // Acknowledge, so the app can wait for this before it exits.
                await writer.WriteLineAsync(accepted ? "OK" : "UNKNOWN");
                await writer.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Pipe connection ended");
            }
        }
    }

    /// <returns>True when the message was understood.</returns>
    private bool Handle(string? message)
    {
        var session = UserSession.ActiveSessionId();
        lock (_sync)
        {
            switch (message?.Trim())
            {
                case "STATE LOCKED":
                    _locked = true;
                    break;
                case "STATE UNLOCKED":
                    _locked = false;
                    break;
                case "MAINTENANCE ON":
                    _maintenanceSession = session;
                    logger.LogWarning("Maintenance mode: the Machine app was closed with the admin PIN. The guard stands down until the next sign-in.");
                    break;
                default:
                    logger.LogWarning("Unknown pipe message {Message}", message);
                    return false;
            }
        }

        logger.LogInformation("Received {Message} from the Machine app", message?.Trim());
        return true;
    }

    /// <summary>The app runs as a standard user and the service as SYSTEM, so the pipe must let interactive users write to it.</summary>
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        // ReadWrite, not just Write: opening a pipe from .NET also asks for read-permissions and read-attributes rights,
        // and the guard writes an "OK" back so the app knows its message arrived. Only the installed Machine
        // executable is listened to (see IsFromMachineApp), whoever opens the pipe.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 256, 256, security);
    }

    /// <summary>Only the installed Machine executable may talk to the guard, so a script cannot switch it off.</summary>
    private bool IsFromMachineApp(NamedPipeServerStream server, out string client)
    {
        client = "an unknown process";
        try
        {
            if (!Native.GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var processId))
            {
                return false;
            }

            using var process = Process.GetProcessById((int)processId);
            client = process.MainModule?.FileName ?? $"process {processId}";
            return string.Equals(Path.GetFullPath(client), _machineExe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            client = ex.Message;
            return false;
        }
    }
}
