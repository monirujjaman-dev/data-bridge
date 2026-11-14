using DataBridge.Application.Abstractions;
using DataBridge.Infrastructure.Security.Linux;
using DataBridge.Infrastructure.Security.MacOS;
using DataBridge.Infrastructure.Security.Windows;

namespace DataBridge.Infrastructure.Security;

/// <summary>Creates the OS-native credential store for the current platform (NFR-SEC-001).</summary>
public static class CredentialStoreFactory
{
    /// <summary>
    /// Windows Credential Manager, the macOS Keychain or the Linux Secret Service. When the platform's
    /// store cannot be loaded, returns a store that reports itself unavailable; there is never a plaintext
    /// fallback.
    /// </summary>
    public static ICredentialStore CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacOSKeychainCredentialStore.Create();
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxSecretServiceCredentialStore.Create();
        }

        return new UnavailableCredentialStore("This operating system has no supported secure credential store.");
    }
}
