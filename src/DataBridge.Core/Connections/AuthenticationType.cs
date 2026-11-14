namespace DataBridge.Core.Connections;

/// <summary>How a connection authenticates to the server.</summary>
public enum AuthenticationType
{
    /// <summary>User name and password; the password lives in the OS credential store.</summary>
    SqlAuthentication,

    /// <summary>The operating-system identity of the current user (Windows integrated / Kerberos).</summary>
    Integrated,
}
