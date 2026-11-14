namespace DataBridge.Infrastructure.Security;

/// <summary>
/// An OS credential store call failed. The message names the operation and the native error code only;
/// it never contains the credential reference value or the secret.
/// </summary>
public sealed class CredentialStoreException : Exception
{
    public CredentialStoreException()
    {
    }

    public CredentialStoreException(string message)
        : base(message)
    {
    }

    public CredentialStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CredentialStoreException(string message, int nativeErrorCode)
        : base(message) => NativeErrorCode = nativeErrorCode;

    /// <summary>The OS error code (Win32 error, macOS <c>OSStatus</c> or GLib error code), when there is one.</summary>
    public int? NativeErrorCode { get; }
}
