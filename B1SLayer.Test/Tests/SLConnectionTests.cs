using Flurl.Http;
using Flurl.Http.Testing;
using System.Net;
using System.Net.Http;

namespace B1SLayer.Test;

public class SLConnectionTests : TestBase
{
    [Fact]
    public void ConfigureHandler_CanBeSet()
    {
        bool wasCalled = false;
        SLConnectionV1.ConfigureHandler = handler =>
        {
            wasCalled = true;
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(10);
            handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60);
            handler.ConnectTimeout = TimeSpan.FromSeconds(10);
        };

        Assert.NotNull(SLConnectionV1.ConfigureHandler);

        // Invoke it manually to verify the delegate works
        var testHandler = new SocketsHttpHandler();
        SLConnectionV1.ConfigureHandler(testHandler);

        Assert.True(wasCalled);
        Assert.Equal(TimeSpan.FromMinutes(10), testHandler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(60), testHandler.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), testHandler.ConnectTimeout);

        // Clean up
        SLConnectionV1.ConfigureHandler = null;
        testHandler.Dispose();
    }

    [Fact]
    public void ConfigureHandler_IsNullByDefault()
    {
        var connection = new SLConnection("https://sapserver:50000/b1s/v1", "CompanyDB", "manager", "12345");
        Assert.Null(connection.ConfigureHandler);
    }

    [Theory]
    [MemberData(nameof(SLConnections))]
    public async Task TransportFailure_IsRetried(SLConnection slConnection)
    {
        slConnection.NumberOfAttempts = 3;

        // First call: timeout (transport failure, no HTTP response)
        // Second call: succeed
        HttpTest.SimulateTimeout();
        HttpTest.RespondWith("{}");

        var result = await slConnection.Request("Orders").GetAsync<object>();

        // Login + timeout attempt + login (re-auth) + successful attempt = multiple calls
        // The key assertion: it did NOT throw, meaning the retry worked
        Assert.NotNull(result);
    }

    [Theory]
    [MemberData(nameof(SLConnections))]
    public async Task TransportFailure_ExhaustsRetries_Throws(SLConnection slConnection)
    {
        slConnection.NumberOfAttempts = 2;

        // All attempts timeout
        HttpTest.SimulateTimeout();
        HttpTest.SimulateTimeout();

        await Assert.ThrowsAsync<FlurlHttpTimeoutException>(async () =>
            await slConnection.Request("Orders").GetAsync<object>());
    }

    [Theory]
    [MemberData(nameof(SLConnections))]
    public async Task NonRetryableHttpError_DoesNotRetry(SLConnection slConnection)
    {
        slConnection.NumberOfAttempts = 3;

        // 400 Bad Request is not in HttpStatusCodesToRetry
        HttpTest.RespondWith(status: 400, body: "Bad Request");

        await Assert.ThrowsAsync<FlurlHttpException>(async () =>
            await slConnection.Request("Orders").GetAsync<object>());

        // Should have been called only once (login) + once (the 400 request), no retries
        HttpTest.ShouldHaveCalled("*/Orders").Times(1);
    }

    [Theory]
    [MemberData(nameof(SLConnections))]
    public async Task RetryableHttpError_IsRetried(SLConnection slConnection)
    {
        slConnection.NumberOfAttempts = 3;

        // 502 Bad Gateway is in HttpStatusCodesToRetry
        HttpTest.RespondWith(status: 502, body: "Bad Gateway");
        HttpTest.RespondWith(status: 502, body: "Bad Gateway");
        HttpTest.RespondWith("{}");

        var result = await slConnection.Request("Orders").GetAsync<object>();
        Assert.NotNull(result);
    }
}
