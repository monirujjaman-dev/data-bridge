using CommunityToolkit.Mvvm.ComponentModel;
using DataBridge.Application.Connections;
using DataBridge.Core.Connections;
using DataBridge.Shared.Errors;

namespace DataBridge.Desktop.ViewModels;

/// <summary>An authentication choice in the editor.</summary>
/// <param name="Type">The authentication type.</param>
/// <param name="Label">How it reads in the UI.</param>
public sealed record AuthenticationOption(AuthenticationType Type, string Label)
{
    public static IReadOnlyList<AuthenticationOption> All { get; } =
    [
        new(AuthenticationType.SqlAuthentication, "SQL Server authentication"),
        new(AuthenticationType.Integrated, "Integrated (Windows or Kerberos)"),
    ];

    public static AuthenticationOption For(AuthenticationType type) => All.Single(option => option.Type == type);
}

/// <summary>
/// The fields of the profile being created or edited. Holds the typed password only until it is saved;
/// a saved password is never loaded back into the editor.
/// </summary>
public sealed partial class ConnectionProfileEditorViewModel : ViewModelBase
{
    private static readonly Dictionary<string, string> _fieldByErrorCode = new(StringComparer.Ordinal)
    {
        [ConnectionProfileErrors.NameRequired.Code] = nameof(Name),
        [ConnectionProfileErrors.NameTooLong.Code] = nameof(Name),
        [ConnectionProfileErrors.NameInvalidCharacters.Code] = nameof(Name),
        [ConnectionProfileErrors.NameAlreadyExists.Code] = nameof(Name),
        [ConnectionProfileErrors.ServerRequired.Code] = nameof(Server),
        [ConnectionProfileErrors.ServerTooLong.Code] = nameof(Server),
        [ConnectionProfileErrors.DatabaseNameRequired.Code] = nameof(DatabaseName),
        [ConnectionProfileErrors.DatabaseNameTooLong.Code] = nameof(DatabaseName),
        [ConnectionProfileErrors.UserNameRequired.Code] = nameof(UserName),
        [ConnectionProfileErrors.UserNameTooLong.Code] = nameof(UserName),
        [ConnectionProfileErrors.ConnectTimeoutOutOfRange.Code] = nameof(ConnectTimeoutSeconds),
        [ConnectionProfileErrors.ApplicationNameTooLong.Code] = nameof(ApplicationName),
    };

    [ObservableProperty]
    public partial Guid? ProfileId { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string? Name { get; set; }

    [ObservableProperty]
    public partial string? Server { get; set; }

    [ObservableProperty]
    public partial string? DatabaseName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesSqlAuthentication))]
    public partial AuthenticationOption Authentication { get; set; } = AuthenticationOption.All[0];

    [ObservableProperty]
    public partial string? UserName { get; set; }

    [ObservableProperty]
    public partial string? Password { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordHint))]
    public partial bool HasSavedPassword { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnencrypted))]
    public partial bool Encrypt { get; set; } = ConnectionSettings.Default.Encrypt;

    [ObservableProperty]
    public partial bool TrustServerCertificate { get; set; } = ConnectionSettings.Default.TrustServerCertificate;

    [ObservableProperty]
    public partial decimal? ConnectTimeoutSeconds { get; set; } = ConnectionSettings.DefaultConnectTimeoutSeconds;

    [ObservableProperty]
    public partial string? ApplicationName { get; set; }

    [ObservableProperty]
    public partial string? NameError { get; private set; }

    [ObservableProperty]
    public partial string? ServerError { get; private set; }

    [ObservableProperty]
    public partial string? DatabaseNameError { get; private set; }

    [ObservableProperty]
    public partial string? UserNameError { get; private set; }

    [ObservableProperty]
    public partial string? ConnectTimeoutSecondsError { get; private set; }

    [ObservableProperty]
    public partial string? ApplicationNameError { get; private set; }

    /// <summary>Errors that belong to no single field.</summary>
    [ObservableProperty]
    public partial string? GeneralError { get; private set; }

    public bool IsNew => ProfileId is null;

    public string Title => IsNew ? "New connection profile" : $"Edit {Name}";

    public bool UsesSqlAuthentication => Authentication.Type == AuthenticationType.SqlAuthentication;

    public bool IsUnencrypted => !Encrypt;

    public string PasswordHint => HasSavedPassword
        ? "A password is saved. Leave this empty to keep it."
        : "No password is saved. Leave this empty to be asked each time you connect.";

    /// <summary>Clears the editor for a new profile.</summary>
    public void StartNew()
    {
        ProfileId = null;
        Name = null;
        Server = null;
        DatabaseName = null;
        Authentication = AuthenticationOption.All[0];
        UserName = null;
        Password = null;
        HasSavedPassword = false;
        Encrypt = ConnectionSettings.Default.Encrypt;
        TrustServerCertificate = ConnectionSettings.Default.TrustServerCertificate;
        ConnectTimeoutSeconds = ConnectionSettings.DefaultConnectTimeoutSeconds;
        ApplicationName = null;
        ClearErrors();
        NotifyIdentityChanged();
    }

    /// <summary>Shows a stored profile. The saved password stays in the credential store.</summary>
    public void Load(DatabaseConnection profile, bool hasSessionPassword)
    {
        ArgumentNullException.ThrowIfNull(profile);

        ProfileId = profile.Id;
        Name = profile.Name;
        Server = profile.Server;
        DatabaseName = profile.DatabaseName;
        Authentication = AuthenticationOption.For(profile.AuthenticationType);
        UserName = profile.UserName;
        Password = null;
        HasSavedPassword = profile.CredentialReference is not null || hasSessionPassword;
        Encrypt = profile.Settings.Encrypt;
        TrustServerCertificate = profile.Settings.TrustServerCertificate;
        ConnectTimeoutSeconds = profile.Settings.ConnectTimeoutSeconds;
        ApplicationName = profile.Settings.ApplicationName;
        ClearErrors();
        NotifyIdentityChanged();
    }

    /// <summary>The use-case input. A password is passed only for SQL authentication and only if one was typed.</summary>
    public ConnectionProfileInput ToInput()
    {
        var password = UsesSqlAuthentication && !string.IsNullOrEmpty(Password) ? new Secret(Password) : null;
        return new ConnectionProfileInput(
            Name,
            Server,
            DatabaseName,
            Authentication.Type,
            UsesSqlAuthentication ? UserName : null,
            password,
            new ConnectionSettings(
                Encrypt,
                TrustServerCertificate,
                ConnectTimeoutSeconds is { } seconds ? (int)Math.Clamp(seconds, int.MinValue, int.MaxValue) : 0,
                ApplicationName));
    }

    /// <summary>Puts each error next to its field; errors that belong to no field go to <see cref="GeneralError"/>.</summary>
    public void ShowErrors(IEnumerable<Error> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        ClearErrors();
        var general = new List<string>();
        foreach (var error in errors)
        {
            switch (_fieldByErrorCode.GetValueOrDefault(error.Code))
            {
                case nameof(Name): NameError ??= error.Message; break;
                case nameof(Server): ServerError ??= error.Message; break;
                case nameof(DatabaseName): DatabaseNameError ??= error.Message; break;
                case nameof(UserName): UserNameError ??= error.Message; break;
                case nameof(ConnectTimeoutSeconds): ConnectTimeoutSecondsError ??= error.Message; break;
                case nameof(ApplicationName): ApplicationNameError ??= error.Message; break;
                default: general.Add(error.Message); break;
            }
        }

        GeneralError = general.Count == 0 ? null : string.Join(" ", general);
    }

    public void ClearErrors()
    {
        NameError = null;
        ServerError = null;
        DatabaseNameError = null;
        UserNameError = null;
        ConnectTimeoutSecondsError = null;
        ApplicationNameError = null;
        GeneralError = null;
    }

    private void NotifyIdentityChanged()
    {
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(Title));
    }
}
