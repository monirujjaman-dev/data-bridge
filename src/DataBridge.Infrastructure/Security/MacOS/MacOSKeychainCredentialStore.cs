using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;

namespace DataBridge.Infrastructure.Security.MacOS;

/// <summary>
/// Stores secrets as generic passwords in the user's macOS Keychain through the <c>SecItem</c> API.
/// Items use service <see cref="ServiceName"/> and the credential reference as the account.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacOSKeychainCredentialStore : ICredentialStore
{
    public const string ServiceName = "DataBridge";

    private const string SecurityPath = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ProbeAccount = "DataBridge/credential-store-probe";
    private const string Label = "DataBridge connection profile password";

    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecNotAvailable = -25291;
    private const int ErrSecNoDefaultKeychain = -25307;
    private const int ErrSecInteractionNotAllowed = -25308;
    private const uint CFStringEncodingUtf8 = 0x08000100;

    private readonly Native _native;

    private MacOSKeychainCredentialStore(Native native) => _native = native;

    /// <summary>Loads Security.framework, or returns an unavailable store with the reason.</summary>
    public static ICredentialStore Create() =>
        Native.TryLoad(out var native, out var reason)
            ? new MacOSKeychainCredentialStore(native)
            : new UnavailableCredentialStore(reason);

    public Task<CredentialStoreStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                using var query = Query(ProbeAccount, returnData: false);
                var status = _native.SecItemCopyMatching(query.Handle, null);
                return status switch
                {
                    ErrSecSuccess or ErrSecItemNotFound => CredentialStoreStatus.Available,
                    ErrSecNoDefaultKeychain => CredentialStoreStatus.Unavailable("There is no default macOS keychain."),
                    ErrSecNotAvailable => CredentialStoreStatus.Unavailable("The macOS keychain is not available."),
                    ErrSecInteractionNotAllowed => CredentialStoreStatus.Unavailable(
                        "The macOS keychain is locked and cannot be unlocked in this session."),
                    _ => CredentialStoreStatus.Unavailable($"The macOS keychain reported error {status}."),
                };
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
            () =>
            {
                using var query = Query(reference, returnData: false);
                var status = _native.SecItemDelete(query.Handle);
                if (status is not (ErrSecSuccess or ErrSecItemNotFound))
                {
                    throw Failure("delete", status);
                }
            },
            cancellationToken);
    }

    private void Save(string reference, Secret secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret.Reveal());
        try
        {
            using var data = CFData(bytes);
            using var query = Query(reference, returnData: false);
            using var update = Dictionary([(_native.KSecValueData, data.Handle)]);

            var status = _native.SecItemUpdate(query.Handle, update.Handle);
            if (status == ErrSecItemNotFound)
            {
                using var service = CFString(ServiceName);
                using var account = CFString(reference);
                using var label = CFString(Label);
                using var item = Dictionary(
                [
                    (_native.KSecClass, _native.KSecClassGenericPassword),
                    (_native.KSecAttrService, service.Handle),
                    (_native.KSecAttrAccount, account.Handle),
                    (_native.KSecAttrLabel, label.Handle),
                    (_native.KSecValueData, data.Handle),
                ]);
                status = _native.SecItemAdd(item.Handle, null);
            }

            if (status != ErrSecSuccess)
            {
                throw Failure("save", status);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private Secret? Find(string reference)
    {
        using var query = Query(reference, returnData: true);
        nint result = 0;
        var status = _native.SecItemCopyMatching(query.Handle, &result);
        if (status == ErrSecItemNotFound)
        {
            return null;
        }

        if (status != ErrSecSuccess)
        {
            throw Failure("read", status);
        }

        using var data = new CFReference(_native, result);
        var length = (int)_native.CFDataGetLength(data.Handle);
        if (length == 0)
        {
            return null;
        }

        var bytes = new ReadOnlySpan<byte>(_native.CFDataGetBytePtr(data.Handle), length);
        return new Secret(Encoding.UTF8.GetString(bytes));
    }

    /// <summary>A query for the generic password with service <see cref="ServiceName"/> and the given account.</summary>
    private CFReference Query(string account, bool returnData)
    {
        using var service = CFString(ServiceName);
        using var accountString = CFString(account);
        var entries = new List<(nint Key, nint Value)>
        {
            (_native.KSecClass, _native.KSecClassGenericPassword),
            (_native.KSecAttrService, service.Handle),
            (_native.KSecAttrAccount, accountString.Handle),
        };
        if (returnData)
        {
            entries.Add((_native.KSecReturnData, _native.KCFBooleanTrue));
            entries.Add((_native.KSecMatchLimit, _native.KSecMatchLimitOne));
        }

        // The dictionary retains its keys and values, so the strings can be released when this returns.
        return Dictionary(entries);
    }

    private CFReference Dictionary(List<(nint Key, nint Value)> entries)
    {
        var keys = stackalloc nint[entries.Count];
        var values = stackalloc nint[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            keys[i] = entries[i].Key;
            values[i] = entries[i].Value;
        }

        var dictionary = _native.CFDictionaryCreate(
            0, keys, values, entries.Count, _native.KCFTypeDictionaryKeyCallBacks, _native.KCFTypeDictionaryValueCallBacks);
        return Created(dictionary, "dictionary");
    }

    private CFReference CFString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        fixed (byte* pointer = bytes)
        {
            return Created(_native.CFStringCreateWithBytes(0, pointer, bytes.Length, CFStringEncodingUtf8, 0), "string");
        }
    }

    private CFReference CFData(byte[] bytes)
    {
        fixed (byte* pointer = bytes)
        {
            return Created(_native.CFDataCreate(0, pointer, bytes.Length), "data");
        }
    }

    private CFReference Created(nint handle, string kind) =>
        handle == 0
            ? throw new CredentialStoreException($"Could not create a Core Foundation {kind}.")
            : new CFReference(_native, handle);

    /// <summary>The error names the operation and <c>OSStatus</c> only; never the reference value or secret.</summary>
    private static CredentialStoreException Failure(string operation, int status) =>
        new($"The macOS keychain could not {operation} the password (OSStatus {status}).", status);

    /// <summary>Owns one Core Foundation reference and releases it on dispose.</summary>
    private readonly struct CFReference(Native native, nint handle) : IDisposable
    {
        public nint Handle { get; } = handle;

        public void Dispose()
        {
            if (Handle != 0)
            {
                native.CFRelease(Handle);
            }
        }
    }

    /// <summary>Function pointers and constants resolved from Security.framework and CoreFoundation.</summary>
    private sealed class Native
    {
        public delegate* unmanaged<nint, nint*, int> SecItemCopyMatching;
        public delegate* unmanaged<nint, nint*, int> SecItemAdd;
        public delegate* unmanaged<nint, nint, int> SecItemUpdate;
        public delegate* unmanaged<nint, int> SecItemDelete;
        public delegate* unmanaged<nint, void> CFRelease;
        public delegate* unmanaged<nint, byte*, nint, uint, byte, nint> CFStringCreateWithBytes;
        public delegate* unmanaged<nint, byte*, nint, nint> CFDataCreate;
        public delegate* unmanaged<nint, nint> CFDataGetLength;
        public delegate* unmanaged<nint, byte*> CFDataGetBytePtr;
        public delegate* unmanaged<nint, nint*, nint*, nint, nint, nint, nint> CFDictionaryCreate;

        public nint KSecClass;
        public nint KSecClassGenericPassword;
        public nint KSecAttrService;
        public nint KSecAttrAccount;
        public nint KSecAttrLabel;
        public nint KSecValueData;
        public nint KSecReturnData;
        public nint KSecMatchLimit;
        public nint KSecMatchLimitOne;
        public nint KCFBooleanTrue;
        public nint KCFTypeDictionaryKeyCallBacks;
        public nint KCFTypeDictionaryValueCallBacks;

        public static bool TryLoad(out Native native, out string reason)
        {
            native = new Native();
            if (!NativeLibrary.TryLoad(SecurityPath, out var security)
                || !NativeLibrary.TryLoad(CoreFoundationPath, out var coreFoundation))
            {
                reason = "The macOS Security framework could not be loaded.";
                return false;
            }

            try
            {
                native.SecItemCopyMatching = (delegate* unmanaged<nint, nint*, int>)NativeLibrary.GetExport(security, "SecItemCopyMatching");
                native.SecItemAdd = (delegate* unmanaged<nint, nint*, int>)NativeLibrary.GetExport(security, "SecItemAdd");
                native.SecItemUpdate = (delegate* unmanaged<nint, nint, int>)NativeLibrary.GetExport(security, "SecItemUpdate");
                native.SecItemDelete = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(security, "SecItemDelete");
                native.CFRelease = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(coreFoundation, "CFRelease");
                native.CFStringCreateWithBytes = (delegate* unmanaged<nint, byte*, nint, uint, byte, nint>)NativeLibrary.GetExport(coreFoundation, "CFStringCreateWithBytes");
                native.CFDataCreate = (delegate* unmanaged<nint, byte*, nint, nint>)NativeLibrary.GetExport(coreFoundation, "CFDataCreate");
                native.CFDataGetLength = (delegate* unmanaged<nint, nint>)NativeLibrary.GetExport(coreFoundation, "CFDataGetLength");
                native.CFDataGetBytePtr = (delegate* unmanaged<nint, byte*>)NativeLibrary.GetExport(coreFoundation, "CFDataGetBytePtr");
                native.CFDictionaryCreate = (delegate* unmanaged<nint, nint*, nint*, nint, nint, nint, nint>)NativeLibrary.GetExport(coreFoundation, "CFDictionaryCreate");

                // These exports are global variables holding a CFTypeRef; read the pointer stored there.
                native.KSecClass = Constant(security, "kSecClass");
                native.KSecClassGenericPassword = Constant(security, "kSecClassGenericPassword");
                native.KSecAttrService = Constant(security, "kSecAttrService");
                native.KSecAttrAccount = Constant(security, "kSecAttrAccount");
                native.KSecAttrLabel = Constant(security, "kSecAttrLabel");
                native.KSecValueData = Constant(security, "kSecValueData");
                native.KSecReturnData = Constant(security, "kSecReturnData");
                native.KSecMatchLimit = Constant(security, "kSecMatchLimit");
                native.KSecMatchLimitOne = Constant(security, "kSecMatchLimitOne");
                native.KCFBooleanTrue = Constant(coreFoundation, "kCFBooleanTrue");

                // These exports are structs; the callbacks argument is their address.
                native.KCFTypeDictionaryKeyCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks");
                native.KCFTypeDictionaryValueCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks");
            }
            catch (EntryPointNotFoundException exception)
            {
                reason = $"The macOS Security framework is missing an expected function ({exception.Message}).";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static nint Constant(nint library, string name) => *(nint*)NativeLibrary.GetExport(library, name);
    }
}
