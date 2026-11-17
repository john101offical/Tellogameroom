using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Core.Sessions;

namespace TelloGameRoom.Machine.Services;

/// <summary>Where this PC connects and who it is. The token is kept encrypted with Windows DPAPI.</summary>
public sealed record MachineIdentityFile(string ServerUrl, Guid MachineId, string MachineName, string ProtectedToken, string[] PinnedCertificates);

/// <summary>Server address pre-seeded by the installer (<c>/SERVER=</c>), so staff only type the pairing code.</summary>
public sealed record ServerPreset(string? ServerUrl, string[]? PinnedCertificates);

/// <summary>
/// Files under <c>%ProgramData%\TelloGameRoom\Machine</c>. The installer grants standard users write access to this
/// folder, because the Machine app runs as the (non-admin) customer account.
/// </summary>
public sealed class MachineStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TelloGameRoom.Machine.v1");
    private readonly Lock _sync = new();

    public MachineStore()
    {
        Directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TelloGameRoom", "Machine");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public string LogDirectory => Path.Combine(Directory, "logs");

    public string LogoPath => Path.Combine(Directory, "logo.bin");

    public MachineIdentityFile? LoadIdentity() => Read<MachineIdentityFile>("identity.json");

    public void SaveIdentity(string serverUrl, PairMachineResponse paired, string[] pins)
    {
        // LocalMachine scope: readable by the customer account the app runs under after any Windows user switch,
        // but useless when the file is copied to another computer.
        var protectedToken = Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(paired.Token), Entropy, DataProtectionScope.LocalMachine));
        Write("identity.json", new MachineIdentityFile(serverUrl, paired.MachineId, paired.MachineName, protectedToken, pins));
    }

    public static string? Unprotect(MachineIdentityFile identity)
    {
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(identity.ProtectedToken), Entropy, DataProtectionScope.LocalMachine));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public void ClearIdentity()
    {
        Delete("identity.json");
        Delete("session.json");
    }

    public ServerPreset? LoadPreset() => Read<ServerPreset>("server.json");

    public MachineConfigDto? LoadConfig() => Read<MachineConfigDto>("config.json");

    public void SaveConfig(MachineConfigDto config) => Write("config.json", config);

    public PersistedMachineSession? LoadSession() => Read<PersistedMachineSession>("session.json");

    public void SaveSession(PersistedMachineSession? session)
    {
        if (session is null)
        {
            Delete("session.json");
        }
        else
        {
            Write("session.json", session);
        }
    }

    private T? Read<T>(string name)
        where T : class
    {
        lock (_sync)
        {
            try
            {
                var path = Path.Combine(Directory, name);
                return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), TelloJson.Options) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private void Write<T>(string name, T value)
    {
        lock (_sync)
        {
            try
            {
                // Write-then-rename so a power cut never leaves a half-written file.
                var path = Path.Combine(Directory, name);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(value, TelloJson.Options));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Warning(ex, "Could not write {File}", name);
            }
        }
    }

    private void Delete(string name)
    {
        lock (_sync)
        {
            try
            {
                File.Delete(Path.Combine(Directory, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Warning(ex, "Could not delete {File}", name);
            }
        }
    }
}
