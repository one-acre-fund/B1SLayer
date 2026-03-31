using Flurl.Http;
using Flurl.Http.Testing;
using System.Net;
using System.Net.Http;

namespace B1SLayer.Test;

public class SLConnectionTests : TestBase
{
    // ──────────────────────────────────────────────
    // 1: ConfigureHandler rebuilds the Client
    // ──────────────────────────────────────────────

    [Fact]
    public void ConfigureHandler_IsNullByDefault()
    {
        var connection = new SLConnection("https://sapserver:50000/b1s/v1", "CompanyDB", "manager", "12345");
        Assert.Null(connection.ConfigureHandler);
    }

    [Fact]
    public void ConfigureHandler_WhenSet_RebuildsFlurlClient()
    {
        var connection = new SLConnection("https://sapserver:50000/b1s/v1", "CompanyDB", "manager", "12345");
        var originalClient = connection.Client;

        connection.ConfigureHandler = handler =>
        {
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(10);
        };

        // Client must be a NEW instance after setting ConfigureHandler
        Assert.NotSame(originalClient, connection.Client);
    }

    [Fact]
    public void ConfigureHandler_ConfigIsAppliedToHandler()
    {
        bool wasCalled = false;
        TimeSpan capturedLifetime = default;
        TimeSpan capturedIdleTimeout = default;
        TimeSpan capturedConnectTimeout = default;

        var connection = new SLConnection("https://sapserver:50000/b1s/v1", "CompanyDB", "manager", "12345");
        connection.ConfigureHandler = handler =>
        {
            wasCalled = true;
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(10);
            handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60);
            handler.ConnectTimeout = TimeSpan.FromSeconds(10);

            // Capture values to verify they were set
            capturedLifetime = handler.PooledConnectionLifetime;
            capturedIdleTimeout = handler.PooledConnectionIdleTimeout;
            capturedConnectTimeout = handler.ConnectTimeout;
        };

        Assert.True(wasCalled, "ConfigureHandler delegate was never invoked");
        Assert.Equal(TimeSpan.FromMinutes(10), capturedLifetime);
        Assert.Equal(TimeSpan.FromSeconds(60), capturedIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), capturedConnectTimeout);
    }

    [Fact]
    public void ConfigureHandler_ClientSettingsSetAfter_ArePreservedOnNewClient()
    {
        // This mirrors the consumer pattern:
        //   connection.ConfigureHandler = ...;     // rebuilds Client
        //   connection.Client.Settings.Timeout = ...; // sets on the NEW client
        var connection = new SLConnection("https://sapserver:50000/b1s/v1", "CompanyDB", "manager", "12345");
        connection.ConfigureHandler = handler =>
        {
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(10);
        };

        var timeout = TimeSpan.FromSeconds(45);
        connection.Client.Settings.Timeout = timeout;

        Assert.Equal(timeout, connection.Client.Settings.Timeout);
    }

    [Fact]
    public void ConfigureHandler_ClientSettingsSetBefore_AreLostOnRebuild()
    {
        // Important edge case: settings set BEFORE ConfigureHandler are on the OLD client.
        // The setter rebuilds Client, so those settings are lost.
        var connection = new SLConnection("https://sapserver:50000/b1s/v1", "CompanyDB", "manager", "12345");

        var customTimeout = TimeSpan.FromSeconds(99);
        connection.Client.Settings.Timeout = customTimeout;
        Assert.Equal(customTimeout, connection.Client.Settings.Timeout);

        // Setting ConfigureHandler rebuilds the client, losing the previous Timeout
        connection.ConfigureHandler = handler =>
        {
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        };

        Assert.NotEqual(customTimeout, connection.Client.Settings.Timeout);
    }

    [Fact]
    public void ConfigureHandler_SetMultipleTimes_LastOneWins()
    {
        TimeSpan capturedLifetime = default;

        var connection = new SLConnection("https://sapserver:50000/b1s/v1", "CompanyDB", "manager", "12345");
        connection.ConfigureHandler = handler =>
        {
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        };

        var clientAfterFirst = connection.Client;

        connection.ConfigureHandler = handler =>
        {
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(20);
            capturedLifetime = handler.PooledConnectionLifetime;
        };

        // Client rebuilt again
        Assert.NotSame(clientAfterFirst, connection.Client);
        Assert.Equal(TimeSpan.FromMinutes(20), capturedLifetime);
    }

    [Fact]
    public void ConfigureHandler_SetToNull_DoesNotThrow()
    {
        var connection = new SLConnection("https://sapserver:50000/b1s/v1", "CompanyDB", "manager", "12345");

        connection.ConfigureHandler = handler =>
        {
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(10);
        };

        // Setting to null should rebuild client without calling any handler delegate
        var ex = Record.Exception(() => connection.ConfigureHandler = null);
        Assert.Null(ex);
        Assert.Null(connection.ConfigureHandler);
    }

    // ──────────────────────────────────────────────
    //  2: Transport failure retry in ExecuteRequest
    // ──────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(SLConnections))]
    public async Task TransportFailure_IsRetried_SucceedsOnSecondAttempt(SLConnection slConnection)
    {
        slConnection.NumberOfAttempts = 3;

        // 1st attempt: timeout (transport failure, HttpResponseMessage is null)
        // 2nd attempt: success
        HttpTest.SimulateTimeout();
        HttpTest.RespondWith("{}");

        var result = await slConnection.Request("Orders").GetAsync<object>();

        // Key assertion: did not throw, meaning retry worked
        Assert.NotNull(result);
    }

    [Theory]
    [MemberData(nameof(SLConnections))]
    public async Task TransportFailure_SucceedsOnLastAttempt(SLConnection slConnection)
    {
        slConnection.NumberOfAttempts = 3;

        // Timeouts on attempts 1 and 2, succeeds on attempt 3
        HttpTest.SimulateTimeout();
        HttpTest.SimulateTimeout();
        HttpTest.RespondWith("{}");

        var result = await slConnection.Request("Orders").GetAsync<object>();
        Assert.NotNull(result);
    }

    [Theory]
    [MemberData(nameof(SLConnections))]
    public async Task TransportFailure_AllAttemptsExhausted_Throws(SLConnection slConnection)
    {
        slConnection.NumberOfAttempts = 2;

        HttpTest.SimulateTimeout();
        HttpTest.SimulateTimeout();

        await Assert.ThrowsAsync<FlurlHttpTimeoutException>(
            () => slConnection.Request("Orders").GetAsync<object>()
        );
    }

    [Theory]
    [MemberData(nameof(SLConnections))]
    public async Task TransportFailure_NumberOfAttemptsOne_NoRetry(SLConnection slConnection)
    {
        slConnection.NumberOfAttempts = 1;

        HttpTest.SimulateTimeout();

        // With only 1 attempt, there is no retry
        await Assert.ThrowsAsync<FlurlHttpTimeoutException>(
            () => slConnection.Request("Orders").GetAsync<object>()
        );
    }

    [Theory]
    [MemberData(nameof(SLConnections))]
    public async Task NonRetryableHttpError_BreaksImmediately(SLConnection slConnection)
    {
        slConnection.NumberOfAttempts = 3;

        // 400 Bad Request is NOT in HttpStatusCodesToRetry
        HttpTest.RespondWith(status: 400, body: "Bad Request");

        await Assert.ThrowsAsync<FlurlHttpException>(
            () => slConnection.Request("Orders").GetAsync<object>()
        );

        // Should only have called the Orders endpoint once (no retries)
        HttpTest.ShouldHaveCalled("*/Orders").Times(1);
    }

    [Theory]
    [MemberData(nameof(SLConnections))]
    public async Task RetryableHttpError_IsRetried_SucceedsEventually(SLConnection slConnection)
    {
        slConnection.NumberOfAttempts = 3;

        // 502 Bad Gateway IS in HttpStatusCodesToRetry
        HttpTest.RespondWith(status: 502, body: "Bad Gateway");
        HttpTest.RespondWith(status: 502, body: "Bad Gateway");
        HttpTest.RespondWith("{}");

        var result = await slConnection.Request("Orders").GetAsync<object>();
        Assert.NotNull(result);
    }

    [Theory]
    [MemberData(nameof(SLConnections))]
    public async Task Unauthorized401_TriggersReLogin_ThenRetries(SLConnection slConnection)
    {
        slConnection.NumberOfAttempts = 3;

        // 1st attempt: 401 (triggers re-login, then retry)
        // 2nd attempt: success
        HttpTest.RespondWith(status: 401, body: "Unauthorized");
        HttpTest.RespondWith("{}");

        var result = await slConnection.Request("Orders").GetAsync<object>();
        Assert.NotNull(result);

        // Login should have been called at least twice (initial + re-login after 401)
        HttpTest.ShouldHaveCalled("*/Login");
    }

    [Theory]
    [MemberData(nameof(SLConnections))]
    public async Task TransportFailure_FollowedByNonRetryableError_StopsRetrying(SLConnection slConnection)
    {
        slConnection.NumberOfAttempts = 3;

        // 1st attempt: transport failure (retryable)
        // 2nd attempt: 400 Bad Request (NOT retryable, should stop)
        HttpTest.SimulateTimeout();
        HttpTest.RespondWith(status: 400, body: "Bad Request");

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => slConnection.Request("Orders").GetAsync<object>()
        );

        // Should NOT have used all 3 attempts
        HttpTest.ShouldHaveCalled("*/Orders").Times(2);
    }
}

/// <summary>
/// Tests for  3 (SLException from login failures is retried).
/// Does NOT inherit from TestBase so login responses can be fully controlled.
/// </summary>
public class SLConnectionLoginRetryTests : IDisposable
{
    private readonly HttpTest _httpTest;
    private static readonly SLLoginResponse _loginResponse = new()
    {
        SessionId = "00000000-0000-0000-0000-000000000000",
        Version = "1000000",
        SessionTimeout = 30
    };

    private const string SapErrorJson =
        """{"error":{"code":"-1","message":{"lang":"en-us","value":"SAML Login Failed"}}}""";

    public SLConnectionLoginRetryTests()
    {
        _httpTest = new HttpTest();
    }

    public void Dispose()
    {
        _httpTest.Dispose();
    }

    private static SLConnection CreateConnection(int numberOfAttempts = 3)
    {
        return new SLConnection(
            "https://sapserver:50000/b1s/v1", "CompanyDB", "manager", "12345",
            numberOfAttempts: numberOfAttempts
        );
    }

    [Fact]
    public async Task LoginFailure500_IsRetried_SucceedsOnSecondAttempt()
    {
        var connection = CreateConnection(numberOfAttempts: 3);

        // Attempt 1: Login returns 500 with SAP error JSON → SLException
        // Attempt 2: Login succeeds → actual request succeeds
        _httpTest.ForCallsTo("*/Login")
            .RespondWith(body: SapErrorJson, status: 500)
            .RespondWithJson(_loginResponse);

        _httpTest.ForCallsTo("*/Orders")
            .RespondWith("{}");

        var result = await connection.Request("Orders").GetAsync<object>();
        Assert.NotNull(result);
    }

    [Fact]
    public async Task LoginFailure500_AllAttemptsExhausted_Throws()
    {
        var connection = CreateConnection(numberOfAttempts: 2);

        // Both attempts: Login returns 500 with SAP error JSON
        _httpTest.ForCallsTo("*/Login")
            .RespondWith(body: SapErrorJson, status: 500)
            .RespondWith(body: SapErrorJson, status: 500);

        var ex = await Assert.ThrowsAsync<SLException>(
            () => connection.Request("Orders").GetAsync<object>()
        );

        Assert.Equal("SAML Login Failed", ex.Message);
    }

    [Fact]
    public async Task LoginFailure500_NumberOfAttemptsOne_NoRetry()
    {
        var connection = CreateConnection(numberOfAttempts: 1);

        _httpTest.ForCallsTo("*/Login")
            .RespondWith(body: SapErrorJson, status: 500);

        var ex = await Assert.ThrowsAsync<SLException>(
            () => connection.Request("Orders").GetAsync<object>()
        );

        Assert.Equal("SAML Login Failed", ex.Message);

        // Only one Login call should have been made
        _httpTest.ShouldHaveCalled("*/Login").Times(1);
    }

    [Fact]
    public async Task LoginFailure_NonRetryableStatus_BreaksImmediately()
    {
        var connection = CreateConnection(numberOfAttempts: 3);

        // 400 is NOT in HttpStatusCodesToRetry, so the SLException catch should break
        var error400Json =
            """{"error":{"code":"400","message":{"lang":"en-us","value":"Bad Request"}}}""";

        _httpTest.ForCallsTo("*/Login")
            .RespondWith(body: error400Json, status: 400);

        var ex = await Assert.ThrowsAsync<SLException>(
            () => connection.Request("Orders").GetAsync<object>()
        );

        Assert.Equal("Bad Request", ex.Message);
        // Should only have tried login once since 400 is not retryable
        _httpTest.ShouldHaveCalled("*/Login").Times(1);
    }

    [Fact]
    public async Task LoginFailure500_ThenTimeout_ThenSuccess()
    {
        var connection = CreateConnection(numberOfAttempts: 4);

        // Attempt 1: Login 500 (SLException, retried)
        // Attempt 2: Login succeeds, but request times out (transport failure, retried)
        // Attempt 3: Login succeeds (session cached), request succeeds
        _httpTest.ForCallsTo("*/Login")
            .RespondWith(body: SapErrorJson, status: 500)
            .RespondWithJson(_loginResponse)
            .RespondWithJson(_loginResponse);

        _httpTest.ForCallsTo("*/Orders")
            .SimulateTimeout()
            .RespondWith("{}");

        var result = await connection.Request("Orders").GetAsync<object>();
        Assert.NotNull(result);
    }

    [Fact]
    public async Task LoginTimeout_IsRetriedAsFlurlHttpException()
    {
        // When login itself times out (no HTTP response at all),
        // ExecuteLoginAsync's catch block sees HttpResponseMessage == null,
        // rethrows the FlurlHttpException directly (not wrapped in SLException).
        // This should be caught by the FlurlHttpException catch in ExecuteRequest
        // as a transport failure and retried.
        var connection = CreateConnection(numberOfAttempts: 3);

        _httpTest.ForCallsTo("*/Login")
            .SimulateTimeout()
            .RespondWithJson(_loginResponse);

        _httpTest.ForCallsTo("*/Orders")
            .RespondWith("{}");

        var result = await connection.Request("Orders").GetAsync<object>();
        Assert.NotNull(result);
    }

    [Fact]
    public async Task LoginFailure500_ExceptionContainsOriginalFlurlException()
    {
        var connection = CreateConnection(numberOfAttempts: 1);

        _httpTest.ForCallsTo("*/Login")
            .RespondWith(body: SapErrorJson, status: 500);

        var ex = await Assert.ThrowsAsync<SLException>(
            () => connection.Request("Orders").GetAsync<object>()
        );

        // Verify the exception chain is intact
        Assert.IsType<FlurlHttpException>(ex.InnerException);
        var innerFlurl = (FlurlHttpException)ex.InnerException;
        Assert.Equal(500, innerFlurl.StatusCode);
    }

    [Fact]
    public async Task LoginFailure_InvalidJsonBody_FallsBackToFlurlRetry()
    {
        // When login returns 500 but the body is NOT valid SAP error JSON,
        // ExecuteLoginAsync's inner catch catches the JSON parse error
        // and rethrows the original FlurlHttpException.
        // This hits the FlurlHttpException catch in ExecuteRequest,
        // where 500 is in HttpStatusCodesToRetry, so it should be retried.
        var connection = CreateConnection(numberOfAttempts: 3);

        _httpTest.ForCallsTo("*/Login")
            .RespondWith(body: "not json", status: 500)
            .RespondWithJson(_loginResponse);

        _httpTest.ForCallsTo("*/Orders")
            .RespondWith("{}");

        var result = await connection.Request("Orders").GetAsync<object>();
        Assert.NotNull(result);
    }
}
