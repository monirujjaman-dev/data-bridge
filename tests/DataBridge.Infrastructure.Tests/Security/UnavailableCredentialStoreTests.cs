using DataBridge.Application.Connections;
using DataBridge.Infrastructure.Security;

namespace DataBridge.Infrastructure.Tests.Security;

public sealed class UnavailableCredentialStoreTests
{
    private const string Reason = "No secret service is running.";

    private readonly UnavailableCredentialStore _store = new(Reason);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reports_unavailable_with_the_reason()
    {
        var status = await _store.GetStatusAsync(Token);

        Assert.False(status.IsAvailable);
        Assert.Equal(Reason, status.UnavailableReason);
    }

    [Fact]
    public async Task Refuses_to_save_rather_than_falling_back_to_plaintext()
    {
        var exception = await Assert.ThrowsAsync<CredentialStoreException>(() =>
            _store.SaveAsync("DataBridge/tests/x", new Secret("hunter2"), Token));

        Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finds_nothing() =>
        Assert.Null(await _store.FindAsync("DataBridge/tests/x", Token));

    [Fact]
    public async Task Refuses_to_delete() =>
        await Assert.ThrowsAsync<CredentialStoreException>(() => _store.DeleteAsync("DataBridge/tests/x", Token));
}
