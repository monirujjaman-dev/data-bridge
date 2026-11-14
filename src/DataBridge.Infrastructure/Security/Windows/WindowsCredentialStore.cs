using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;

namespace DataBridge.Infrastructure.Security.Windows;

/// <summary>
/// Stores secrets as generic credentials in Windows Credential Manager, scoped to the current user and
/// persisted on this machine. The secret is held as UTF-16 bytes in the credential blob.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int ErrorNoSuchLogonSession = 1312;

    /// <summary><c>CRED_MAX_CREDENTIAL_BLOB_SIZE</c>: 5 × 512 bytes.</summary>
    private const int MaxBlobBytes = 5 * 512;

    private const string ProbeTarget = "DataBridge/credential-store-probe";
    private const string Comment = "DataBridge connection profile password";

    public Task<CredentialStoreStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                // Reading a missing credential fails with ERROR_NOT_FOUND when the store works, and with
                // ERROR_NO_SUCH_LOGON_SESSION when there is no user profile to store into (for example a service).
                unsafe
                {
                    if (NativeMethods.CredRead(ProbeTarget, CredTypeGeneric, 0, out var credential))
                    {
                        NativeMethods.CredFree(credential);
                        return CredentialStoreStatus.Available;
                    }
                }

                var error = Marshal.GetLastPInvokeError();
                return error == ErrorNoSuchLogonSession
                    ? CredentialStoreStatus.Unavailable(
                        "Windows Credential Manager is not available for this sign-in session.")
                    : CredentialStoreStatus.Available;
            },
            cancellationToken);

    public Task SaveAsync(string reference, Secret secret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        ArgumentNullException.ThrowIfNull(secret);

        return Task.Run(() => Write(reference, secret), cancellationToken);
    }

    public Task<Secret?> FindAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        return Task.Run(() => Read(reference), cancellationToken);
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        return Task.Run(
            () =>
            {
                if (!NativeMethods.CredDelete(reference, CredTypeGeneric, 0))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != ErrorNotFound)
                    {
                        throw Failure("delete", error);
                    }
                }
            },
            cancellationToken);
    }

    private static unsafe void Write(string reference, Secret secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret.Reveal());
        try
        {
            if (blob.Length > MaxBlobBytes)
            {
                throw new ArgumentException(
                    $"The secret is too long for Windows Credential Manager (at most {MaxBlobBytes / 2} characters).",
                    nameof(secret));
            }

            fixed (char* target = reference)
            fixed (char* comment = Comment)
            fixed (byte* blobPointer = blob)
            {
                var credential = new NativeCredential
                {
                    Type = CredTypeGeneric,
                    TargetName = target,
                    Comment = comment,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = blobPointer,
                    Persist = CredPersistLocalMachine,
                };

                if (!NativeMethods.CredWrite(&credential, 0))
                {
                    throw Failure("save", Marshal.GetLastPInvokeError());
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    private static unsafe Secret? Read(string reference)
    {
        if (!NativeMethods.CredRead(reference, CredTypeGeneric, 0, out var credential))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw Failure("read", error);
        }

        try
        {
            if (credential->CredentialBlobSize == 0 || credential->CredentialBlob is null)
            {
                return null;
            }

            var value = new string((char*)credential->CredentialBlob, 0, (int)credential->CredentialBlobSize / 2);
            return new Secret(value);
        }
        finally
        {
            // The blob is freed by CredFree; clear it first so the plaintext does not linger in freed memory.
            new Span<byte>(credential->CredentialBlob, (int)credential->CredentialBlobSize).Clear();
            NativeMethods.CredFree(credential);
        }
    }

    /// <summary>The error names the operation and Win32 code only; never the reference value or secret.</summary>
    private static CredentialStoreException Failure(string operation, int error) =>
        new($"Windows Credential Manager could not {operation} the password (error {error}).", error);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public void* Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    private static unsafe partial class NativeMethods
    {
        [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredWrite(NativeCredential* credential, uint flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredRead(string targetName, uint type, uint flags, out NativeCredential* credential);

        [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredDelete(string targetName, uint type, uint flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
        public static partial void CredFree(void* buffer);
    }
}
