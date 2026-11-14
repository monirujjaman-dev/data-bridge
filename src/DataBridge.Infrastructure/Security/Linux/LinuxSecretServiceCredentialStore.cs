using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;

namespace DataBridge.Infrastructure.Security.Linux;

/// <summary>
/// Stores secrets in the freedesktop.org Secret Service (GNOME Keyring, KWallet and others) through
/// libsecret, in the default collection, under schema <see cref="SchemaName"/> with one attribute,
/// <see cref="ReferenceAttribute"/>, holding the credential reference.
/// </summary>
/// <remarks>
/// Uses the non-variadic <c>secret_password_*v_sync</c> functions, because variadic native calls are not
/// portable from .NET. Headless or minimal systems often have no Secret Service; the store then reports
/// itself unavailable and passwords are kept for the session only (FR-CON-009).
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class LinuxSecretServiceCredentialStore : ICredentialStore
{
    public const string SchemaName = "DataBridge.ConnectionProfilePassword";
    public const string ReferenceAttribute = "reference";

    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGLib = "libglib-2.0.so.0";
    private const string ProbeReference = "DataBridge/credential-store-probe";
    private const string Label = "DataBridge connection profile password";

    // SecretSchema on LP64: name (8), flags (4 + 4 padding), attributes[32] of {name (8), type (4 + 4)},
    // reserved (4 + 4), reserved1..7 (7 × 8).
    private const int SchemaSize = 8 + 8 + (32 * 16) + 8 + (7 * 8);
    private const int SchemaAttributesOffset = 16;
    private const int SecretSchemaAttributeString = 0;
    private const int SecretSchemaNone = 0;

    private readonly Native _native;
    private readonly nint _schema;

    private LinuxSecretServiceCredentialStore(Native native)
    {
        _native = native;

        // The schema must outlive every libsecret call, so it lives for the rest of the process.
        _schema = (nint)NativeMemory.AllocZeroed(SchemaSize);
        *(nint*)_schema = Marshal.StringToCoTaskMemUTF8(SchemaName);
        *(int*)(_schema + 8) = SecretSchemaNone;
        *(nint*)(_schema + SchemaAttributesOffset) = Marshal.StringToCoTaskMemUTF8(ReferenceAttribute);
        *(int*)(_schema + SchemaAttributesOffset + 8) = SecretSchemaAttributeString;
    }

    /// <summary>Loads libsecret and GLib, or returns an unavailable store with the reason.</summary>
    public static ICredentialStore Create() =>
        Native.TryLoad(out var native, out var reason)
            ? new LinuxSecretServiceCredentialStore(native)
            : new UnavailableCredentialStore(reason);

    public Task<CredentialStoreStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                // A lookup of a missing item succeeds with no result when the service works, and fails
                // when there is no D-Bus session or no Secret Service provider.
                var message = WithAttributes(ProbeReference, attributes =>
                {
                    nint error = 0;
                    var password = _native.LookupSync(_schema, attributes, 0, &error);
                    if (password != 0)
                    {
                        _native.PasswordFree(password);
                    }

                    return error == 0 ? null : TakeErrorMessage(error);
                });

                return message is null
                    ? CredentialStoreStatus.Available
                    : CredentialStoreStatus.Unavailable($"The Linux Secret Service is not available ({message}).");
            },
            cancellationToken);

    public Task SaveAsync(string reference, Secret secret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        ArgumentNullException.ThrowIfNull(secret);

        return Task.Run(() => Save(reference, secret), cancellationToken);
    }

    public Task<Secret?> FindAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        return Task.Run(() => Find(reference), cancellationToken);
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        return Task.Run(
            () => WithAttributes(reference, attributes =>
            {
                // Returns FALSE with no error when nothing matched, which is not a failure.
                nint error = 0;
                _native.ClearSync(_schema, attributes, 0, &error);
                return error == 0 ? 0 : throw Failure("delete", error);
            }),
            cancellationToken);
    }

    private void Save(string reference, Secret secret)
    {
        var password = NullTerminatedUtf8(secret.Reveal());
        var label = NullTerminatedUtf8(Label);
        try
        {
            WithAttributes(reference, attributes =>
            {
                fixed (byte* passwordPointer = password)
                fixed (byte* labelPointer = label)
                {
                    nint error = 0;
                    var stored = _native.StoreSync(_schema, attributes, 0, labelPointer, passwordPointer, 0, &error);
                    if (error != 0)
                    {
                        throw Failure("save", error);
                    }

                    return stored != 0 ? 0 : throw new CredentialStoreException("The Linux Secret Service did not save the password.");
                }
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    private Secret? Find(string reference) =>
        WithAttributes(reference, attributes =>
        {
            nint error = 0;
            var password = _native.LookupSync(_schema, attributes, 0, &error);
            if (error != 0)
            {
                throw Failure("read", error);
            }

            if (password == 0)
            {
                return null;
            }

            try
            {
                var value = Marshal.PtrToStringUTF8(password);
                return string.IsNullOrEmpty(value) ? null : new Secret(value);
            }
            finally
            {
                // secret_password_free clears the memory before freeing it.
                _native.PasswordFree(password);
            }
        });

    /// <summary>Runs <paramref name="action"/> with a GHashTable holding the reference attribute.</summary>
    private T WithAttributes<T>(string reference, Func<nint, T> action)
    {
        var key = Marshal.StringToCoTaskMemUTF8(ReferenceAttribute);
        var value = Marshal.StringToCoTaskMemUTF8(reference);
        var table = _native.HashTableNew(_native.StrHash, _native.StrEqual);
        try
        {
            // The table has no destroy functions; it borrows the strings, which are freed below.
            _native.HashTableInsert(table, key, value);
            return action(table);
        }
        finally
        {
            _native.HashTableUnref(table);
            Marshal.FreeCoTaskMem(key);
            Marshal.FreeCoTaskMem(value);
        }
    }

    /// <summary>Reads and frees a <c>GError</c>; its layout is {GQuark domain, gint code, gchar* message}.</summary>
    private string TakeErrorMessage(nint error)
    {
        try
        {
            return Marshal.PtrToStringUTF8(*(nint*)(error + 8)) ?? "unknown error";
        }
        finally
        {
            _native.ErrorFree(error);
        }
    }

    /// <summary>The error names the operation and the GLib code only; never the reference value or secret.</summary>
    private CredentialStoreException Failure(string operation, nint error)
    {
        var code = *(int*)(error + 4);
        _native.ErrorFree(error);
        return new CredentialStoreException(
            $"The Linux Secret Service could not {operation} the password (error {code}).", code);
    }

    private static byte[] NullTerminatedUtf8(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    /// <summary>Function pointers resolved from libsecret and GLib.</summary>
    private sealed class Native
    {
        public delegate* unmanaged<nint, nint, nint, byte*, byte*, nint, nint*, int> StoreSync;
        public delegate* unmanaged<nint, nint, nint, nint*, nint> LookupSync;
        public delegate* unmanaged<nint, nint, nint, nint*, int> ClearSync;
        public delegate* unmanaged<nint, void> PasswordFree;
        public delegate* unmanaged<nint, nint, nint> HashTableNew;
        public delegate* unmanaged<nint, nint, nint, int> HashTableInsert;
        public delegate* unmanaged<nint, void> HashTableUnref;
        public delegate* unmanaged<nint, void> ErrorFree;
        public nint StrHash;
        public nint StrEqual;

        public static bool TryLoad(out Native native, out string reason)
        {
            native = new Native();
            if (!NativeLibrary.TryLoad(LibSecret, out var secret))
            {
                reason = "libsecret is not installed, so the Linux Secret Service cannot be used.";
                return false;
            }

            if (!NativeLibrary.TryLoad(LibGLib, out var glib))
            {
                reason = "GLib could not be loaded, so the Linux Secret Service cannot be used.";
                return false;
            }

            try
            {
                native.StoreSync = (delegate* unmanaged<nint, nint, nint, byte*, byte*, nint, nint*, int>)NativeLibrary.GetExport(secret, "secret_password_storev_sync");
                native.LookupSync = (delegate* unmanaged<nint, nint, nint, nint*, nint>)NativeLibrary.GetExport(secret, "secret_password_lookupv_sync");
                native.ClearSync = (delegate* unmanaged<nint, nint, nint, nint*, int>)NativeLibrary.GetExport(secret, "secret_password_clearv_sync");
                native.PasswordFree = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(secret, "secret_password_free");
                native.HashTableNew = (delegate* unmanaged<nint, nint, nint>)NativeLibrary.GetExport(glib, "g_hash_table_new");
                native.HashTableInsert = (delegate* unmanaged<nint, nint, nint, int>)NativeLibrary.GetExport(glib, "g_hash_table_insert");
                native.HashTableUnref = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(glib, "g_hash_table_unref");
                native.ErrorFree = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(glib, "g_error_free");
                native.StrHash = NativeLibrary.GetExport(glib, "g_str_hash");
                native.StrEqual = NativeLibrary.GetExport(glib, "g_str_equal");
            }
            catch (EntryPointNotFoundException exception)
            {
                reason = $"The installed libsecret is missing an expected function ({exception.Message}).";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
