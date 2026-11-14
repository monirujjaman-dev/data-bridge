using DataBridge.Infrastructure.Logging;

namespace DataBridge.Infrastructure.Tests.Logging;

public sealed class LogRedactorTests
{
    [Theory]
    [InlineData("Server=db01;Database=Sales;User ID=app;Password=hunter2;Encrypt=True", "Password=[redacted];")]
    [InlineData("Server=db01;Pwd=hunter2", "Pwd=[redacted]")]
    [InlineData("server=db01; password = hunter2 ; encrypt=true", "password = [redacted];")]
    [InlineData("Password=\"hun;ter2\";Server=db01", "Password=[redacted];Server=db01")]
    [InlineData("Password='hun;ter2';Server=db01", "Password=[redacted];Server=db01")]
    [InlineData("Password=\"a\"\"b;c\";Server=db01", "Password=[redacted];Server=db01")]
    [InlineData("Client Secret=abc123", "Client Secret=[redacted]")]
    [InlineData("access_token=abc123&x=1", "access_token=[redacted]")]
    [InlineData("ApiKey = abc123", "ApiKey = [redacted]")]
    public void Redacts_secrets_in_key_value_text(string text, string expectedFragment)
    {
        var redacted = LogRedactor.Redact(text);

        Assert.Contains(expectedFragment, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hun;ter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"Password":"hunter2","Server":"db01"}""", """{"Password":"[redacted]","Server":"db01"}""")]
    [InlineData("""{"userPassword": "hun\"ter2"}""", """{"userPassword": "[redacted]"}""")]
    [InlineData("""{"api_key":"abc123"}""", """{"api_key":"[redacted]"}""")]
    public void Redacts_secrets_in_json(string json, string expected) =>
        Assert.Equal(expected, LogRedactor.Redact(json));

    [Theory]
    [InlineData("""{"@x":"Login failed: Password=hunter2\n ---> Inner detail"}""", """{"@x":"Login failed: Password=[redacted]\n ---> Inner detail"}""")]
    [InlineData("""{"@x":"Password=\"hun;ter2\";Server=db01"}""", """{"@x":"Password=[redacted];Server=db01"}""")]
    [InlineData("""{"@m":"Pwd=hunter2"}""", """{"@m":"Pwd=[redacted]"}""")]
    [InlineData("""{"@m":"Pwd=hun\\ter2;x"}""", """{"@m":"Pwd=[redacted];x"}""")]
    public void Redacts_secrets_inside_json_escaped_text_without_eating_what_follows(string json, string expected) =>
        Assert.Equal(expected, LogRedactor.Redact(json));

    [Theory]
    [InlineData("Server=db01;Database=Sales;User ID=app;Encrypt=True")]
    [InlineData("Created connection profile 0f8fad5b; password storage: StoredSecurely")]
    [InlineData("""{"CredentialReference":"DataBridge/connection-profile/x","ProfileId":"y"}""")]
    [InlineData("Rows scanned=1000000; batches=10")]
    public void Leaves_text_without_secrets_unchanged(string text) =>
        Assert.Equal(text, LogRedactor.Redact(text));

    [Theory]
    [InlineData("Password", true)]
    [InlineData("password", true)]
    [InlineData("UserPassword", true)]
    [InlineData("Pwd", true)]
    [InlineData("ClientSecret", true)]
    [InlineData("client_secret", true)]
    [InlineData("AccessToken", true)]
    [InlineData("api-key", true)]
    [InlineData("PrivateKey", true)]
    [InlineData("CredentialReference", false)]
    [InlineData("ProfileId", false)]
    [InlineData("UserName", false)]
    [InlineData("ConnectTimeoutSeconds", false)]
    public void Recognizes_sensitive_names(string name, bool sensitive) =>
        Assert.Equal(sensitive, LogRedactor.IsSensitiveName(name));
}
