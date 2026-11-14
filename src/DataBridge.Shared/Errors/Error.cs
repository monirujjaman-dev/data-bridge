namespace DataBridge.Shared.Errors;

/// <summary>
/// An expected, user-actionable failure. <see cref="Code"/> is stable and machine-readable;
/// <see cref="Message"/> is safe to show to the user and must never contain secrets or row values.
/// </summary>
/// <param name="Code">Stable identifier, for example <c>ConnectionProfile.NameRequired</c>.</param>
/// <param name="Message">Human-readable description without secrets or row values.</param>
public sealed record Error(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}
