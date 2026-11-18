using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using TelloGameRoom.Machine.Interop;

namespace TelloGameRoom.Machine.Services;

/// <summary>
/// Blocks the keyboard shortcuts that would let a customer leave the lock screen: Win keys, Alt+Tab, Alt+Esc,
/// Alt+F4, Ctrl+Esc and Ctrl+Shift+Esc. Ctrl+Alt+Del is handled by Windows itself and cannot be intercepted;
/// the MachineGuard service disables Task Manager while locked to close that door.
/// </summary>
public sealed class KeyboardBlocker : IDisposable
{
    // Kept in a field so the garbage collector cannot collect the delegate while Windows still calls it.
    private readonly Native.LowLevelKeyboardProc _callback;
    private IntPtr _hook;

    public KeyboardBlocker() => _callback = HookProc;

    public bool IsActive => _hook != IntPtr.Zero;

    /// <summary>Must be called on a thread with a message loop (the UI thread).</summary>
    public void Enable()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _callback, Native.GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Warning("Could not install the keyboard hook (error {Error})", Marshal.GetLastWin32Error());
        }
    }

    public void Disable()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void Dispose() => Disable();

    private static bool IsDown(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var alt = (info.Flags & Native.LLKHF_ALTDOWN) != 0;
            var blocked = info.VkCode switch
            {
                Native.VK_LWIN or Native.VK_RWIN or Native.VK_APPS => true,
                Native.VK_TAB or Native.VK_F4 => alt,
                Native.VK_ESCAPE => alt || IsDown(Native.VK_CONTROL),
                _ => false,
            };
            if (blocked)
            {
                return 1;
            }
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}

/// <summary>
/// Notifications to the MachineGuard service over a named pipe. The guard uses them to toggle the Task Manager
/// policy and to know when the owner closed the app on purpose. Each message is acknowledged by the guard, so the
/// app knows it arrived. If the guard is not installed, messages fail quietly (logged): the app works without it,
/// just with weaker protection.
/// </summary>
public sealed class GuardClient
{
    public const string PipeName = "TelloGameRoom.MachineGuard";
    private const int Attempts = 3;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1);

    // One message at a time: the guard serves a single pipe instance.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastDelivered;
    private bool _failureLogged;

    public void ReportLocked(bool locked)
    {
        var message = locked ? "STATE LOCKED" : "STATE UNLOCKED";
        _ = Task.Run(async () =>
        {
            if (message != _lastDelivered && await SendAsync(message))
            {
                _lastDelivered = message;
            }
        });
    }

    /// <summary>The owner entered the PIN: stop relaunching the app until the next sign-in or reboot.</summary>
    /// <returns>True when the guard confirmed it, or false when it could not be reached.</returns>
    public async Task<bool> EnterMaintenanceAsync()
    {
        var delivered = await SendAsync("MAINTENANCE ON");
        if (delivered)
        {
            _lastDelivered = null;
        }

        return delivered;
    }

    private async Task<bool> SendAsync(string message)
    {
        await _gate.WaitAsync();
        try
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(AttemptTimeout);
                    await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(timeout.Token);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
                    await writer.WriteLineAsync(message);
                    await writer.FlushAsync(timeout.Token);
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                    var reply = await reader.ReadLineAsync(timeout.Token);
                    if (reply == "OK")
                    {
                        _failureLogged = false;
                        return true;
                    }

                    Serilog.Log.Warning("MachineGuard did not accept {Message}: {Reply}", message, reply ?? "(no reply)");
                    return false;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
                {
                    if (attempt == Attempts)
                    {
                        // Logged once per outage, so a PC without the guard does not fill its log.
                        if (!_failureLogged || message == "MAINTENANCE ON")
                        {
                            Serilog.Log.Warning("Could not reach MachineGuard to send {Message}: {Error}", message, ex.Message);
                            _failureLogged = true;
                        }

                        return false;
                    }

                    await Task.Delay(200);
                }
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Runs the remote power commands. Standard Windows users are allowed to shut down a workstation.</summary>
public static class PowerCommands
{
    public static void Restart() => Run("/r /t 5 /f");

    public static void Shutdown() => Run("/s /t 5 /f");

    private static void Run(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error(ex, "Could not run shutdown {Arguments}", arguments);
        }
    }
}
