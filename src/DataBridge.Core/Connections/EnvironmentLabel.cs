namespace DataBridge.Core.Connections;

/// <summary>
/// What kind of environment a profile points at, chosen by the user (ADR-0015). Shown wherever the profile appears.
/// <see cref="Production"/> makes approval of a plan that writes to it stricter.
/// </summary>
public enum EnvironmentLabel
{
    /// <summary>Not labelled.</summary>
    None,
    Development,
    Test,
    Staging,
    Production,
}
