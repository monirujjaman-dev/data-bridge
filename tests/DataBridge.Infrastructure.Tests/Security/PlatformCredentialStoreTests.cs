using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Infrastructure.Security;

namespace DataBridge.Infrastructure.Tests.Security;

/// <summary>
/// Exercises the real OS credential store of the machine running the tests, using throwaway entries
/// that are always deleted. When the store is unavailable (for example Linux without a Secret Service)
/// the tests are skipped, unless <c>DATABRIDGE_REQUIRE_CREDENTIAL_STORE=true</c> is set, as in CI, where
/// an unavailable store fails the run.
/// </summary>
public sealed class PlatformCredentialStoreTests : IAsyncDisposable
{
    private const string RequireVariable = "DATABRIDGE_REQUIRE_CREDENTIAL_STORE";

    private readonly ICredentialStore _store = CredentialStoreFactory.CreateForCurrentPlatform();
    private readonly List<string> _references = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var reference in _references)
        {
            await _store.DeleteAsync(reference, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Store_is_available_or_explains_why_not()
    {
        var status = await _store.GetStatusAsync(Token);

        if (status.IsAvailable)
        {
            Assert.Null(status.UnavailableReason);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(status.UnavailableReason));
            Assert.False(IsRequired(), $"The credential store must be available here: {status.UnavailableReason}");
        }
    }

    [Fact]
    public async Task Saved_secret_can_be_read_back()
    {
        await RequireAvailableStoreAsync();
        var reference = NewReference();

        await _store.SaveAsync(reference, new Secret("s3cr3t!"), Token);

        Assert.Equal(new Secret("s3cr3t!"), await _store.FindAsync(reference, Token));
    }

    [Fact]
    public async Task Saving_again_replaces_the_secret()
    {
        await RequireAvailableStoreAsync();
        var reference = NewReference();

        await _store.SaveAsync(reference, new Secret("first"), Token);
        await _store.SaveAsync(reference, new Secret("second"), Token);

        Assert.Equal(new Secret("second"), await _store.FindAsync(reference, Token));
    }

    [Theory]
    [InlineData("pässwörd-ünïcödé")]
    [InlineData("密码🔐 with spaces and \"quotes\"")]
    [InlineData("x")]
    public async Task Secret_text_round_trips_exactly(string password)
    {
        await RequireAvailableStoreAsync();
        var reference = NewReference();

        await _store.SaveAsync(reference, new Secret(password), Token);

        Assert.Equal(password, (await _store.FindAsync(reference, Token))!.Reveal());
    }

    [Fact]
    public async Task Secrets_are_kept_apart_by_reference()
    {
        await RequireAvailableStoreAsync();
        var first = NewReference();
        var second = NewReference();

        await _store.SaveAsync(first, new Secret("one"), Token);
        await _store.SaveAsync(second, new Secret("two"), Token);
        await _store.DeleteAsync(first, Token);

        Assert.Null(await _store.FindAsync(first, Token));
        Assert.Equal(new Secret("two"), await _store.FindAsync(second, Token));
    }

    [Fact]
    public async Task Missing_secret_is_null_and_deleting_it_is_not_an_error()
    {
        await RequireAvailableStoreAsync();
        var reference = NewReference();

        Assert.Null(await _store.FindAsync(reference, Token));
        await _store.DeleteAsync(reference, Token);
    }

    [Fact]
    public async Task Deleted_secret_is_gone()
    {
        await RequireAvailableStoreAsync();
        var reference = NewReference();
        await _store.SaveAsync(reference, new Secret("s3cr3t!"), Token);

        await _store.DeleteAsync(reference, Token);

        Assert.Null(await _store.FindAsync(reference, Token));
    }

    [Fact]
    public async Task Long_sql_server_password_round_trips()
    {
        await RequireAvailableStoreAsync();
        var reference = NewReference();

        // SQL Server passwords are at most 128 characters.
        var password = new string('p', 128);
        await _store.SaveAsync(reference, new Secret(password), Token);

        Assert.Equal(password, (await _store.FindAsync(reference, Token))!.Reveal());
    }

    [Fact]
    public async Task Cancelled_calls_do_not_touch_the_store()
    {
        await RequireAvailableStoreAsync();
        var reference = NewReference();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.GetStatusAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.SaveAsync(reference, new Secret("s3cr3t!"), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.FindAsync(reference, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.DeleteAsync(reference, cancellation.Token));

        Assert.Null(await _store.FindAsync(reference, Token));
    }

    [Fact]
    public async Task Empty_reference_is_rejected()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _store.SaveAsync("", new Secret("x"), Token));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _store.FindAsync("", Token));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _store.DeleteAsync("", Token));
    }

    private static bool IsRequired() =>
        string.Equals(Environment.GetEnvironmentVariable(RequireVariable), "true", StringComparison.OrdinalIgnoreCase);

    private async Task RequireAvailableStoreAsync()
    {
        var status = await _store.GetStatusAsync(Token);
        if (status.IsAvailable)
        {
            return;
        }

        Assert.False(IsRequired(), $"The credential store must be available here: {status.UnavailableReason}");
        Assert.Skip($"No OS credential store on this machine: {status.UnavailableReason}");
    }

    /// <summary>A unique reference, recorded so the entry is deleted after the test.</summary>
    private string NewReference()
    {
        var reference = $"DataBridge/tests/{Guid.NewGuid():N}";
        _references.Add(reference);
        return reference;
    }
}
