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
/// One-way notifications to the MachineGuard service over a named pipe. The guard uses them to toggle the
/// Task Manager policy and to know when the owner closed the app on purpose. If the guard is not installed,
/// messages are silently dropped: the app works without it, just with weaker protection.
/// </summary>
public sealed class GuardClient
{
    public const string PipeName = "TelloGameRoom.MachineGuard";
    private string? _last;

    public void ReportLocked(bool locked) => Send(locked ? "STATE LOCKED" : "STATE UNLOCKED", onlyIfChanged: true);

    /// <summary>The owner entered the PIN: stop relaunching the app until the next sign-in or reboot.</summary>
    public void EnterMaintenance() => Send("MAINTENANCE ON", onlyIfChanged: false);

    private void Send(string message, bool onlyIfChanged)
    {
        if (onlyIfChanged && message == _last)
        {
            return;
        }

        _last = message;
        _ = Task.Run(() =>
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(500);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
                writer.WriteLine(message);
                writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                // Guard not running. Remember nothing so the next state change is sent again.
                _last = null;
            }
        });
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
