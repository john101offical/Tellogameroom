using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace TelloGameRoom.MachineGuard;

/// <summary>What the guard needs to know about, and do in, the interactive Windows session at the physical console.</summary>
internal static class UserSession
{
    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string PolicyValue = "DisableTaskMgr";

    /// <summary>The console session id, or null when nobody is signed in there.</summary>
    public static uint? ActiveSessionId()
    {
        var id = Native.WTSGetActiveConsoleSessionId();
        if (id == Native.NoSession || !Native.WTSQueryUserToken(id, out var token))
        {
            return null;
        }

        Native.CloseHandle(token);
        return id;
    }

    public static bool IsRunning(string processName, uint sessionId)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Any(p => (uint)p.SessionId == sessionId);
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    /// <summary>Starts a program on the desktop of the user signed in to the given session, with that user's rights (not SYSTEM's).</summary>
    public static void Launch(string exePath, uint sessionId)
    {
        if (!Native.WTSQueryUserToken(sessionId, out var userToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed");
        }

        var primary = IntPtr.Zero;
        var environment = IntPtr.Zero;
        try
        {
            if (!Native.DuplicateTokenEx(userToken, Native.TokenAllAccess, IntPtr.Zero, Native.SecurityImpersonation, Native.TokenPrimary, out primary))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed");
            }

            if (!Native.CreateEnvironmentBlock(out environment, primary, false))
            {
                environment = IntPtr.Zero;
            }

            var startup = new Native.STARTUPINFO { cb = Marshal.SizeOf<Native.STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var commandLine = new StringBuilder($"\"{exePath}\"");
            if (!Native.CreateProcessAsUser(primary, null, commandLine, IntPtr.Zero, IntPtr.Zero, false, Native.CreateUnicodeEnvironment,
                    environment, Path.GetDirectoryName(exePath), ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed");
            }

            Native.CloseHandle(info.hThread);
            Native.CloseHandle(info.hProcess);
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                Native.DestroyEnvironmentBlock(environment);
            }

            if (primary != IntPtr.Zero)
            {
                Native.CloseHandle(primary);
            }

            Native.CloseHandle(userToken);
        }
    }

    /// <summary>
    /// Turns Task Manager off or on for the user of a session by writing the policy into that user's registry hive.
    /// Standard users cannot change this key themselves, which is why the service does it.
    /// </summary>
    public static void SetTaskManagerDisabled(uint sessionId, bool disabled)
    {
        if (!Native.WTSQueryUserToken(sessionId, out var token))
        {
            return;
        }

        try
        {
            using var identity = new WindowsIdentity(token);
            if (identity.User is { } sid)
            {
                SetPolicy(sid.Value, disabled);
            }
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    /// <summary>Re-enables Task Manager for every loaded user profile. Run by the uninstaller.</summary>
    public static void RestoreTaskManagerForEveryone()
    {
        foreach (var sid in Registry.Users.GetSubKeyNames().Where(n => n.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !n.EndsWith("_Classes", StringComparison.Ordinal)))
        {
            SetPolicy(sid, disabled: false);
        }
    }

    private static void SetPolicy(string sid, bool disabled)
    {
        if (disabled)
        {
            using var key = Registry.Users.CreateSubKey($@"{sid}\{PolicyKey}");
            key.SetValue(PolicyValue, 1, RegistryValueKind.DWord);
        }
        else
        {
            using var key = Registry.Users.OpenSubKey($@"{sid}\{PolicyKey}", writable: true);
            key?.DeleteValue(PolicyValue, throwOnMissingValue: false);
        }
    }
}
