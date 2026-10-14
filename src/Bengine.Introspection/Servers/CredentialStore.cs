namespace Bengine.Introspection.Servers;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Tmds.DBus.Protocol;

/// <summary>A place to keep secrets outside the server list file.</summary>
public interface ICredentialStore
{
    /// <summary>Gets a short name of the store, for display ("GNOME Keyring", "Windows Credential Manager").</summary>
    string Name { get; }

    /// <summary>Reads a secret.</summary>
    /// <param name="key">The secret's key.</param>
    /// <returns>The secret, or <c>null</c> when there is none.</returns>
    string? Get(string key);

    /// <summary>Stores (or replaces) a secret.</summary>
    /// <param name="key">The secret's key.</param>
    /// <param name="label">A human-readable label for the OS's password manager.</param>
    /// <param name="secret">The secret.</param>
    /// <returns><c>true</c> if stored.</returns>
    bool Set(string key, string label, string secret);

    /// <summary>Removes a secret, if present.</summary>
    /// <param name="key">The secret's key.</param>
    void Delete(string key);
}

/// <summary>
/// Picks the operating system's password store: the Secret Service (GNOME
/// Keyring, KWallet) on Linux over D-Bus, the Credential Manager on Windows,
/// and the login Keychain on macOS. Returns <c>null</c> when none is reachable
/// (a headless server, a locked keyring), in which case passwords stay in the
/// owner-only server list file. <c>BENGINE_KEYRING=off</c> disables it.
/// </summary>
public static class CredentialStores
{
    private static readonly Lazy<ICredentialStore?> Detected = new(Detect);

    /// <summary>Gets the operating system's store, or <c>null</c>.</summary>
    public static ICredentialStore? System => Detected.Value;

    private static ICredentialStore? Detect()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("BENGINE_KEYRING"), "off", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            ICredentialStore? store = OperatingSystem.IsWindows() ? new WindowsCredentialStore()
                : OperatingSystem.IsMacOS() ? new MacKeychainStore()
                : OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is { Length: > 0 } ? new SecretServiceStore()
                : null;

            // Probe once: a store that cannot answer a lookup is not usable.
            store?.Get("bengine-probe");
            return store;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure to reach the store means there is none to use.
            return null;
        }
    }
}

/// <summary>
/// The freedesktop Secret Service (GNOME Keyring, KWallet) over the session
/// D-Bus, with the "plain" session algorithm — the secret only crosses the
/// local bus to the user's own keyring daemon. Items are tagged
/// <c>application=bengine</c> and <c>key=…</c>. A locked collection that
/// would need an interactive prompt is treated as unavailable.
/// </summary>
public sealed class SecretServiceStore : ICredentialStore
{
    private const string Bus = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string DefaultCollection = "/org/freedesktop/secrets/aliases/default";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly string _application;

    /// <summary>Creates the store.</summary>
    /// <param name="application">The application attribute items are tagged with.</param>
    public SecretServiceStore(string application = "bengine") => _application = application;

    /// <inheritdoc />
    public string Name => "Secret Service (GNOME Keyring / KWallet)";

    /// <inheritdoc />
    public string? Get(string key) => Run(async connection =>
    {
        var session = await OpenSessionAsync(connection);
        var (unlocked, locked) = await SearchAsync(connection, key);
        if (unlocked.Length == 0)
        {
            return locked.Length == 0 ? null : throw new InvalidOperationException("The keyring is locked.");
        }

        return await GetSecretAsync(connection, unlocked[0], session);
    });

    /// <inheritdoc />
    public bool Set(string key, string label, string secret) => Run(async connection =>
    {
        var session = await OpenSessionAsync(connection);
        var prompt = await connection.CallMethodAsync(CreateItem(connection, session, key, label, secret), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            reader.ReadObjectPath();
            return reader.ReadObjectPathAsString();
        }, null);
        return prompt == "/";
    });

    /// <inheritdoc />
    public void Delete(string key) => Run(async connection =>
    {
        var (unlocked, _) = await SearchAsync(connection, key);
        foreach (var item in unlocked)
        {
            await connection.CallMethodAsync(DeleteItem(connection, item), static (_, _) => 0, null);
        }

        return 0;
    });

    private MessageBuffer CreateItem(Connection connection, string session, string key, string label, string secret)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, DefaultCollection, "org.freedesktop.Secret.Collection", "CreateItem", "a{sv}(oayays)b", MessageFlags.None);
        var properties = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Label");
        writer.WriteVariantString(label);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Attributes");
        writer.WriteVariant(new Dict<string, string> { ["application"] = _application, ["key"] = key }.AsVariant());
        writer.WriteDictionaryEnd(properties);
        writer.WriteStructureStart();
        writer.WriteObjectPath(session);
        writer.WriteArray(Array.Empty<byte>());
        writer.WriteArray(Encoding.UTF8.GetBytes(secret));
        writer.WriteString("text/plain; charset=utf8");
        writer.WriteBool(true);
        return writer.CreateMessage();
    }

    private static MessageBuffer DeleteItem(Connection connection, string item)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, item, "org.freedesktop.Secret.Item", "Delete", null, MessageFlags.None);
        return writer.CreateMessage();
    }

    private static T Run<T>(Func<Connection, Task<T>> work) => Task.Run(async () =>
    {
        using var connection = new Connection(Address.Session!);
        await connection.ConnectAsync();
        var call = work(connection);
        if (await Task.WhenAny(call, Task.Delay(Timeout)) != call)
        {
            throw new TimeoutException("The keyring did not answer.");
        }

        return await call;
    }).GetAwaiter().GetResult();

    private static Task<string> OpenSessionAsync(Connection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ServicePath, "org.freedesktop.Secret.Service", "OpenSession", "sv", MessageFlags.None);
        writer.WriteString("plain");
        writer.WriteVariantString(string.Empty);
        return connection.CallMethodAsync(writer.CreateMessage(), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            reader.ReadVariantValue();
            return reader.ReadObjectPathAsString();
        }, null);
    }

    private Task<(string[] Unlocked, string[] Locked)> SearchAsync(Connection connection, string key)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ServicePath, "org.freedesktop.Secret.Service", "SearchItems", "a{ss}", MessageFlags.None);
        var attributes = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("application");
        writer.WriteString(_application);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("key");
        writer.WriteString(key);
        writer.WriteDictionaryEnd(attributes);
        return connection.CallMethodAsync(writer.CreateMessage(), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            var unlocked = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
            var locked = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
            return (unlocked, locked);
        }, null);
    }

    private static Task<string?> GetSecretAsync(Connection connection, string item, string session)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ServicePath, "org.freedesktop.Secret.Service", "GetSecrets", "aoo", MessageFlags.None);
        writer.WriteArray(new[] { new ObjectPath(item) });
        writer.WriteObjectPath(session);
        return connection.CallMethodAsync(writer.CreateMessage(), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            var end = reader.ReadDictionaryStart();
            while (reader.HasNext(end))
            {
                reader.AlignStruct();
                reader.ReadObjectPath();
                reader.AlignStruct();
                reader.ReadObjectPath();
                reader.ReadArrayOfByte();
                var value = reader.ReadArrayOfByte();
                reader.ReadString();
                return (string?)Encoding.UTF8.GetString(value);
            }

            return null;
        }, null);
    }
}

/// <summary>The Windows Credential Manager (generic credentials named <c>bengine:…</c>).</summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int GenericType = 1;
    private const int PersistLocalMachine = 2;

    /// <inheritdoc />
    public string Name => "Windows Credential Manager";

    /// <inheritdoc />
    public string? Get(string key)
    {
        if (!CredRead("bengine:" + key, GenericType, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.CredentialBlobSize == 0 ? string.Empty : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// <inheritdoc />
    public bool Set(string key, string label, string secret)
    {
        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = "bengine:" + key,
                Comment = label,
                CredentialBlobSize = (uint)(secret.Length * 2),
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName,
            };
            return CredWrite(ref credential, 0);
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    /// <inheritdoc />
    public void Delete(string key) => CredDelete("bengine:" + key, GenericType, 0);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }
}

/// <summary>The macOS login Keychain, through the <c>security</c> tool (service <c>bengine</c>).</summary>
public sealed class MacKeychainStore : ICredentialStore
{
    /// <inheritdoc />
    public string Name => "macOS Keychain";

    /// <inheritdoc />
    public string? Get(string key)
    {
        var (code, output) = Security("find-generic-password", "-s", "bengine", "-a", key, "-w");
        return code == 0 ? output.TrimEnd('\n') : null;
    }

    /// <inheritdoc />
    public bool Set(string key, string label, string secret) =>
        Security("add-generic-password", "-U", "-s", "bengine", "-a", key, "-l", label, "-w", secret).Code == 0;

    /// <inheritdoc />
    public void Delete(string key) => Security("delete-generic-password", "-s", "bengine", "-a", key);

    private static (int Code, string Output) Security(params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new IOException("Could not run the security tool.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return (process.ExitCode, output);
    }
}
