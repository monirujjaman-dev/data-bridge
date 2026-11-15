using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataBridge.Application.Connections;
using DataBridge.Core.Connections;
using Microsoft.Extensions.Logging;

namespace DataBridge.Desktop.ViewModels;

/// <summary>A row in the profile list: the profile plus the text the list shows.</summary>
public sealed record ConnectionProfileItem(DatabaseConnection Profile)
{
    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    /// <summary>The second line: server, database, authentication and environment label.</summary>
    public string Detail =>
        $"{Profile.Server} · {Profile.DatabaseName} · "
        + (Profile.AuthenticationType == AuthenticationType.Integrated ? "Integrated" : Profile.UserName)
        + (Profile.Environment == EnvironmentLabel.None ? "" : $" · {Profile.Environment}");
}

/// <summary>
/// The Connections screen (FR-CON-006): lists profiles and creates, edits and deletes them through the
/// Application use cases. Every operation runs off the UI thread and can be cancelled.
/// </summary>
public sealed partial class ConnectionsViewModel : ViewModelBase
{
    private readonly ListConnectionProfiles _list;
    private readonly CreateConnectionProfile _create;
    private readonly UpdateConnectionProfile _update;
    private readonly DeleteConnectionProfile _delete;
    private readonly TestConnection _test;
    private readonly SessionSecretCache _sessionSecrets;
    private readonly ILogger<ConnectionsViewModel> _logger;

    public ConnectionsViewModel(
        ListConnectionProfiles list,
        CreateConnectionProfile create,
        UpdateConnectionProfile update,
        DeleteConnectionProfile delete,
        TestConnection test,
        SessionSecretCache sessionSecrets,
        ILogger<ConnectionsViewModel> logger)
    {
        _list = list;
        _create = create;
        _update = update;
        _delete = delete;
        _test = test;
        _sessionSecrets = sessionSecrets;
        _logger = logger;
        Editor.StartNew();

        // A test result describes the fields as they were tested; any edit makes it stale.
        Editor.PropertyChanged += (_, eventArgs) =>
        {
            if (!IsBusy && eventArgs.PropertyName is { } name && !name.EndsWith("Error", StringComparison.Ordinal))
            {
                ClearTestResult();
            }
        };
    }

    public ObservableCollection<ConnectionProfileItem> Profiles { get; } = [];

    public ConnectionProfileEditorViewModel Editor { get; } = new();

    [ObservableProperty]
    public partial ConnectionProfileItem? SelectedProfile { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(NewProfileCommand), nameof(RequestDeleteCommand), nameof(ConfirmDeleteCommand), nameof(LoadCommand), nameof(TestConnectionCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    /// <summary>What the status bar says about the current or last operation.</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>
    /// A caution the user must read: for example that a password is kept for this session only because the
    /// OS credential store is unavailable (FR-CON-009), or that a saved password could not be removed.
    /// </summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>An operation failed unexpectedly. Details are in the log, never secrets here.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RequestDeleteCommand))]
    public partial bool IsConfirmingDelete { get; private set; }

    /// <summary>What the server reported in the last successful connection test (FR-CON-008).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial ResolvedDatabaseIdentity? TestedIdentity { get; private set; }

    /// <summary>Why the last connection test failed, in words the user can act on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial string? TestError { get; private set; }

    public bool HasTestResult => TestedIdentity is not null || TestError is not null;

    public bool IsIdle => !IsBusy;

    public bool HasProfiles => Profiles.Count > 0;

    public string ProfileCountText => Profiles.Count == 1 ? "1 profile" : $"{Profiles.Count} profiles";

    [RelayCommand(CanExecute = nameof(IsIdle), IncludeCancelCommand = true)]
    private Task LoadAsync(CancellationToken cancellationToken) =>
        RunAsync("load profiles", "Loading profiles…", async token =>
        {
            var profiles = await RunInBackgroundAsync(_list.ExecuteAsync, token);
            ReplaceProfiles(profiles, select: SelectedProfile?.Id);
            StatusText = null;
        }, cancellationToken);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void NewProfile()
    {
        SelectedProfile = null;
        IsConfirmingDelete = false;
        ClearMessages();
        Editor.StartNew();
    }

    [RelayCommand(CanExecute = nameof(CanSave), IncludeCancelCommand = true)]
    private Task SaveAsync(CancellationToken cancellationToken) =>
        RunAsync("save profile", "Saving profile…", async token =>
        {
            var input = Editor.ToInput();
            var profileId = Editor.ProfileId;
            var result = await RunInBackgroundAsync(
                innerToken => profileId is { } id
                    ? _update.ExecuteAsync(id, input, innerToken)
                    : _create.ExecuteAsync(input, innerToken),
                token);

            if (result.IsFailure)
            {
                Editor.ShowErrors(result.Errors);
                StatusText = "The profile was not saved. Correct the highlighted fields.";
                return;
            }

            var saved = result.Value;
            var profiles = await RunInBackgroundAsync(_list.ExecuteAsync, token);
            ReplaceProfiles(profiles, select: saved.Profile.Id);
            Notice = NoticeFor(saved);
            StatusText = $"Saved {saved.Profile.Name}.";
        }, cancellationToken);

    /// <summary>Connects with the profile as entered, saved or not, and shows what the server reports. Saves nothing.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle), IncludeCancelCommand = true)]
    private Task TestConnectionAsync(CancellationToken cancellationToken) =>
        RunAsync("test the connection", "Connecting…", async token =>
        {
            ClearTestResult();
            Editor.ClearErrors();
            var input = Editor.ToInput();
            var profileId = Editor.ProfileId;
            var result = await RunInBackgroundAsync(innerToken => _test.ExecuteAsync(profileId, input, innerToken), token);
            if (result.IsSuccess)
            {
                TestedIdentity = result.Value;
                StatusText = $"Connected to {result.Value.DatabaseName} on {result.Value.ServerName}.";
                return;
            }

            if (result.Errors.All(error => error.Code.StartsWith("Connection.", StringComparison.Ordinal)))
            {
                TestError = string.Join(" ", result.Errors.Select(error => error.Message));
            }
            else
            {
                Editor.ShowErrors(result.Errors);
            }

            StatusText = "The connection test failed.";
        }, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanRequestDelete))]
    private void RequestDelete()
    {
        ClearMessages();
        IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    /// <summary>Cancels whichever operation is running (the status bar's Cancel button).</summary>
    [RelayCommand]
    private void CancelBusy()
    {
        LoadCommand.Cancel();
        TestConnectionCommand.Cancel();
        SaveCommand.Cancel();
        ConfirmDeleteCommand.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanConfirmDelete), IncludeCancelCommand = true)]
    private Task ConfirmDeleteAsync(CancellationToken cancellationToken) =>
        RunAsync("delete profile", "Deleting profile…", async token =>
        {
            var item = SelectedProfile!;
            IsConfirmingDelete = false;
            var result = await RunInBackgroundAsync(innerToken => _delete.ExecuteAsync(item.Id, innerToken), token);
            if (result.IsFailure)
            {
                Editor.ShowErrors(result.Errors);
            }
            else
            {
                Notice = result.Value.Warning;
            }

            var profiles = await RunInBackgroundAsync(_list.ExecuteAsync, token);
            ReplaceProfiles(profiles, select: null);
            StatusText = result.IsSuccess ? $"Deleted {item.Name}." : "The profile could not be deleted.";
        }, cancellationToken);

    partial void OnSelectedProfileChanged(ConnectionProfileItem? value)
    {
        IsConfirmingDelete = false;
        if (value is null)
        {
            return;
        }

        ClearMessages();
        Editor.Load(value.Profile, hasSessionPassword: _sessionSecrets.Find(value.Id) is not null);
        RequestDeleteCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave() => IsIdle && !IsConfirmingDelete;

    private bool CanRequestDelete() => IsIdle && !IsConfirmingDelete && SelectedProfile is not null;

    private bool CanConfirmDelete() => IsIdle && SelectedProfile is not null;

    private static string? NoticeFor(ConnectionProfileSaved saved)
    {
        var notices = new List<string>();
        if (saved.SecretStorage.Kind == SecretStorageKind.SessionOnly)
        {
            notices.Add(
                "The password was not saved: the OS credential store is unavailable "
                + $"({saved.SecretStorage.Reason}) DataBridge keeps it until you close the app and will ask "
                + "for it again next time. It is never written to disk in plain text.");
        }

        if (saved.Warning is not null)
        {
            notices.Add(saved.Warning);
        }

        return notices.Count == 0 ? null : string.Join(" ", notices);
    }

    private void ReplaceProfiles(IReadOnlyList<DatabaseConnection> profiles, Guid? select)
    {
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(new ConnectionProfileItem(profile));
        }

        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(ProfileCountText));

        var selected = Profiles.FirstOrDefault(item => item.Id == select);

        // Re-selecting the same id must reload the editor with the stored values.
        SelectedProfile = null;
        if (selected is not null)
        {
            SelectedProfile = selected;
        }
        else
        {
            Editor.StartNew();
        }
    }

    private void ClearMessages()
    {
        Notice = null;
        ErrorMessage = null;
        Editor.ClearErrors();
        ClearTestResult();
    }

    private void ClearTestResult()
    {
        TestedIdentity = null;
        TestError = null;
    }

    /// <summary>Runs one operation with busy state, status text, cancellation and error reporting.</summary>
    private async Task RunAsync(
        string operation,
        string progressText,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusText = progressText;
        try
        {
            await work(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DesktopLog.OperationCancelled(_logger, operation);
            StatusText = "Cancelled.";
        }
#pragma warning disable CA1031 // The UI must survive any failure; it is logged with full detail.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            DesktopLog.OperationFailed(_logger, exception, operation);
            ErrorMessage = $"Could not {operation}. The details are in the log file.";
            StatusText = null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
