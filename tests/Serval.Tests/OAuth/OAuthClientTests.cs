#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Serval.Http;
using Serval.OAuth;
using Serval.Tests.Support;

namespace Serval.Tests.OAuth;

public sealed class OAuthClientTests
{
    [Fact]
    public void AuthorizationRequestHasRandomStateAndS256Pkce()
    {
        using var client = new OAuthClient(Options());
        var       first  = client.CreateAuthorizationRequest(new Uri("https://app.test/callback"));
        var       second = client.CreateAuthorizationRequest(new Uri("https://app.test/callback"));
        var       query  = Query(first.AuthorizationUri);
        Assert.NotEqual(first.State, second.State);
        Assert.NotEqual(first.CodeVerifier, second.CodeVerifier);
        Assert.Equal("code", query["response_type"].Single());
        Assert.Equal("S256", query["code_challenge_method"].Single());
        Assert.Equal(Challenge(first.CodeVerifier), query["code_challenge"].Single());
        Assert.Equal("profile email", query["scope"].Single());
        Assert.DoesNotContain(first.CodeVerifier, first.AuthorizationUri.AbsoluteUri);
        Assert.DoesNotContain(first.CodeVerifier, first.ToString());
    }

    [Theory]
    [InlineData(OAuthClientAuthentication.None)]
    [InlineData(OAuthClientAuthentication.ClientSecretBasic)]
    [InlineData(OAuthClientAuthentication.ClientSecretPost)]
    public async Task CodeExchangeUsesTheSelectedClientAuthentication(OAuthClientAuthentication method)
    {
        using var handler = new OAuthTestBackchannel();
        using var http    = new HttpClient(handler);
        var       options = Options(http);
        options.ClientId             = "client :+";
        options.ClientAuthentication = method;
        options.ClientSecret         = method == OAuthClientAuthentication.None ? null : "secret :+";
        using var client        = new OAuthClient(options);
        var       authorization = client.CreateAuthorizationRequest(new Uri("https://app.test/callback"));
        var       tokens        = await client.ExchangeCodeAsync("code +", new Uri("https://app.test/callback"), authorization.CodeVerifier);
        var       request       = Assert.Single(handler.Requests);
        var       form          = Form(request.Body);
        Assert.Equal("POST", request.Method);
        Assert.Equal("authorization_code", form["grant_type"].Single());
        Assert.Equal("code +", form["code"].Single());
        Assert.Equal(authorization.CodeVerifier, form["code_verifier"].Single());
        Assert.Equal("https://app.test/callback", form["redirect_uri"].Single());
        if (method == OAuthClientAuthentication.ClientSecretBasic)
        {
            Assert.Equal("Basic " + Convert.ToBase64String("client+%3A%2B:secret+%3A%2B"u8), request.Authorization);
            Assert.False(form.ContainsKey("client_secret"));
            Assert.False(form.ContainsKey("client_id"));
        }
        else
        {
            Assert.Null(request.Authorization);
            Assert.Equal(options.ClientId, form["client_id"].Single());
            Assert.Equal(method == OAuthClientAuthentication.ClientSecretPost, form.ContainsKey("client_secret"));
        }

        Assert.Equal("access-secret", tokens.AccessToken);
        Assert.Equal("refresh-secret", tokens.RefreshToken);
        Assert.Equal(TimeSpan.FromHours(1), tokens.ExpiresIn);
        Assert.DoesNotContain("access-secret", tokens.ToString());
    }

    [Fact]
    public async Task RefreshClientCredentialsRevocationAndProfileRequestsUseTheirProperEndpoints()
    {
        using var handler = new OAuthTestBackchannel();
        using var http    = new HttpClient(handler);
        using var client  = new OAuthClient(Options(http));
        var       tokens  = await client.RefreshAsync("refresh-secret", ["profile"]);
        await client.ClientCredentialsAsync(["jobs"]);
        await client.RevokeAsync(tokens.AccessToken, "access_token");
        var profile  = await client.GetUserInformationAsync(tokens);
        var requests = handler.Requests.ToArray();
        Assert.Equal("refresh_token", Form(requests[0].Body)["grant_type"].Single());
        Assert.Equal("refresh-secret", Form(requests[0].Body)["refresh_token"].Single());
        Assert.Equal("client_credentials", Form(requests[1].Body)["grant_type"].Single());
        Assert.Equal("jobs", Form(requests[1].Body)["scope"].Single());
        Assert.Equal("/revoke", requests[2].Url.AbsolutePath);
        Assert.Equal("access-secret", Form(requests[2].Body)["token"].Single());
        Assert.Equal("GET", requests[3].Method);
        Assert.Equal("Bearer access-secret", requests[3].Authorization);
        Assert.Equal("provider-user", profile.GetProperty("id").GetString());
    }

    [Fact]
    public async Task ProviderParametersRequestHooksAndResponseParsingAreReplaceable()
    {
        using var handler = new OAuthTestBackchannel { Respond = _ => OAuthTestBackchannel.Json("{\"custom_token\":\"custom\"}") };
        using var http    = new HttpClient(handler);
        var       options = Options(http);
        options.AuthorizationParameters.Add("audience", "https://api.test/");
        options.TokenParameters.Add("resource", "service");
        options.PrepareRequest = context =>
        {
            context.Request.Headers.Add("X-Custom", context.Operation);
            return ValueTask.CompletedTask;
        };
        options.ReadTokenResponse = async (root, cancellationToken) =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return new OAuthTokenResponse { AccessToken = root.GetProperty("custom_token").GetString()!, TokenType = "Bearer" };
        };
        using var client        = new OAuthClient(options);
        var       authorization = client.CreateAuthorizationRequest(new Uri("https://app.test/callback"), parameters: new Dictionary<string, string> { ["prompt"] = "login" });
        Assert.Equal("https://api.test/", Query(authorization.AuthorizationUri)["audience"].Single());
        Assert.Equal("login", Query(authorization.AuthorizationUri)["prompt"].Single());
        Assert.Equal("custom", (await client.ClientCredentialsAsync()).AccessToken);
        var request = Assert.Single(handler.Requests);
        Assert.True(request.CustomHeader);
        Assert.Equal("service", Form(request.Body)["resource"].Single());
    }

    [Theory]
    [InlineData("state")]
    [InlineData("code_challenge")]
    [InlineData("redirect_uri")]
    [InlineData("response_type")]
    [InlineData("response_mode")]
    [InlineData("STATE")]
    public void ProtocolParametersCannotBeOverridden(string parameter)
    {
        var options = Options();
        options.AuthorizationParameters.Add(parameter, "unsafe");
        using var client = new OAuthClient(options);
        Assert.Throws<ArgumentException>(() => client.CreateAuthorizationRequest(new Uri("https://app.test/callback")));
    }

    [Fact]
    public void EndpointQueriesCannotSmuggleProtocolParameters()
    {
        var options = Options();
        options.AuthorizationEndpoint = new Uri("https://provider.test/authorize?%73tate=bad");
        using var client = new OAuthClient(options);
        Assert.Throws<ArgumentException>(() => client.CreateAuthorizationRequest(new Uri("https://app.test/callback")));
    }

    [Theory]
    [InlineData("http://provider.test/token")]
    [InlineData("https://user:secret@provider.test/token")]
    [InlineData("https://provider.test/token#fragment")]
    public void UnsafeEndpointsAreRejected(string endpoint)
    {
        var options = Options();
        options.TokenEndpoint = new Uri(endpoint);
        Assert.Throws<ArgumentException>(() => new OAuthClient(options));
    }

    [Fact]
    public void HttpExceptionIsLimitedToExplicitLoopbackDevelopment()
    {
        var options = Options();
        options.TokenEndpoint = new Uri("http://localhost:1234/token");
        Assert.Throws<ArgumentException>(() => new OAuthClient(options));
        options.AllowLoopbackHttp = true;
        using var client = new OAuthClient(options);
        options.TokenEndpoint = new Uri("http://provider.test/token");
        Assert.Throws<ArgumentException>(() => new OAuthClient(options));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"access_token\":\"one\",\"access_token\":\"two\",\"token_type\":\"Bearer\"}")]
    [InlineData("{\"access_token\":\"one\",\"token_type\":\"Bearer\",\"expires_in\":-1}")]
    [InlineData("{\"access_token\":\"one\",\"token_type\":\"Bearer\",\"expires_in\":\"3600\"}")]
    [InlineData("{\"access_token\":\"one\",\"token_type\":\"Bearer\",\"expires_in\":9223372036854775807}")]
    [InlineData("{\"access_token\":null,\"token_type\":\"Bearer\"}")]
    public async Task InvalidTokenResponsesFailClosed(string json)
    {
        using var handler = new OAuthTestBackchannel { Respond = _ => OAuthTestBackchannel.Json(json) };
        using var http    = new HttpClient(handler);
        using var client  = new OAuthClient(Options(http));
        await Assert.ThrowsAsync<OAuthProtocolException>(async () => await client.ClientCredentialsAsync());
    }

    [Fact]
    public async Task ErrorsDoNotExposeRawProviderDescriptionsOrTokens()
    {
        using var handler = new OAuthTestBackchannel { Respond = _ => OAuthTestBackchannel.Json("{\"error\":\"invalid_grant\",\"error_description\":\"secret details\"}", HttpStatusCode.BadRequest) };
        using var http    = new HttpClient(handler);
        using var client  = new OAuthClient(Options(http));
        var       error   = await Assert.ThrowsAsync<OAuthProtocolException>(async () => await client.RefreshAsync("secret token"));
        Assert.Equal("invalid_grant", error.Error);
        Assert.Equal(400, error.StatusCode);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task RedirectsAndOversizedResponsesAreRejected()
    {
        using var handler = new OAuthTestBackchannel { Respond = _ => new HttpResponseMessage(HttpStatusCode.Redirect) };
        using var http    = new HttpClient(handler);
        var       options = Options(http);
        options.MaximumResponseBytes = 64;
        using var client = new OAuthClient(options);
        Assert.Equal("unexpected_redirect", (await Assert.ThrowsAsync<OAuthProtocolException>(async () => await client.ClientCredentialsAsync())).Error);
        handler.Respond = _ => OAuthTestBackchannel.Json(new string('x', 65));
        Assert.Equal("response_too_large", (await Assert.ThrowsAsync<OAuthProtocolException>(async () => await client.ClientCredentialsAsync())).Error);
    }

    [Fact]
    public async Task TokenReaderIsAwaitedAndRetainsItsJsonAfterHttpResourcesAreDisposed()
    {
        using var handler = new OAuthTestBackchannel();
        using var http    = new HttpClient(handler);
        var       entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var       release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var       options = Options(http);
        options.ReadTokenResponse = async (root, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new OAuthTokenResponse { AccessToken = root.GetProperty("access_token").GetString()!, TokenType = "Bearer" };
        };
        using var client  = new OAuthClient(options);
        var       pending = client.ClientCredentialsAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal("access-secret", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).AccessToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokenReaderSharesCallerCancellationAndTheBackchannelDeadline(bool useTimeout)
    {
        using var handler      = new OAuthTestBackchannel();
        using var http         = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var       entered      = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var       options      = Options(http);
        options.BackchannelTimeout = TimeSpan.FromSeconds(useTimeout ? 1 : 30);
        options.ReadTokenResponse = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Cancellation should interrupt the callback.");
        };
        using var client  = new OAuthClient(options);
        var       pending = client.ClientCredentialsAsync(cancellationToken: cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!useTimeout)
        {
            await cancellation.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancelledOperationsDoNotInvokeHooksOrAcceptLateTokenResults()
    {
        using var handler      = new OAuthTestBackchannel();
        using var http         = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var       prepared     = false;
        var       options      = Options(http);
        options.PrepareRequest = _ =>
        {
            prepared = true;
            return ValueTask.CompletedTask;
        };
        options.ReadTokenResponse = async (_, _) =>
        {
            await cancellation.CancelAsync();
            return new OAuthTokenResponse { AccessToken = "late", TokenType = "Bearer" };
        };
        using var client = new OAuthClient(options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.ClientCredentialsAsync(cancellationToken: new CancellationToken(true)));
        Assert.False(prepared);
        Assert.Empty(handler.Requests);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.ClientCredentialsAsync(cancellationToken: cancellation.Token));
        Assert.True(prepared);
        Assert.Single(handler.Requests);
        client.Dispose();
        using var response = await http.GetAsync("https://provider.test/profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TokenReaderFailuresPropagateAndNullResultsAreRejected()
    {
        using var handler = new OAuthTestBackchannel();
        using var http    = new HttpClient(handler);
        var       options = Options(http);
        var       failure = new InvalidOperationException("Reader failed.");
        options.ReadTokenResponse = (_, _) => ValueTask.FromException<OAuthTokenResponse>(failure);
        using var client = new OAuthClient(options);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.ClientCredentialsAsync()));

        options.ReadTokenResponse = (_, _) => ValueTask.FromResult<OAuthTokenResponse>(null!);
        var error = await Assert.ThrowsAsync<OAuthProtocolException>(async () => await client.ClientCredentialsAsync());
        Assert.Equal("invalid_token_response", error.Error);
    }

    internal static OAuthClientOptions Options(HttpClient? http = null) => new()
    {
        ClientId                = "client",
        AuthorizationEndpoint   = new Uri("https://provider.test/authorize"),
        TokenEndpoint           = new Uri("https://provider.test/token"),
        UserInformationEndpoint = new Uri("https://provider.test/profile"),
        RevocationEndpoint      = new Uri("https://provider.test/revoke"),
        Scopes                  = { "profile", "email" },
        Backchannel             = http,
    };

    internal static IReadOnlyDictionary<string, string[]> Query(Uri uri) => new Request("GET", uri, HttpVersion.Version11, new Headers(), Stream.Null, 1024).Query;

    internal static IReadOnlyDictionary<string, string[]> Form(string form) => Query(new Uri("https://unused.test/?" + form));

    internal static string Challenge(string verifier) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
