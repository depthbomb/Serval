#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Serval.Authentication;
using Serval.Authentication.Authorization;
using Serval.Hosting;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Tests.Support;

namespace Serval.Tests.Authentication;

public sealed class AuthorizationVoterTests
{
    [Fact]
    public async Task VoterFailuresReachTheCentralRequestExceptionHandler()
    {
        await using var app = new SecurityTestApplication(builder =>
        {
            builder.AddAuthentication().AddVoter<ThrowingVoter>();
            builder.AddController<object>("GET", "/", null, async (_, context) =>
            {
                await context.AuthorizeAsync(new Document("alice", false), Permission.View);
                return new TextResult("unreachable");
            });
        });
        await using var request = app.Request();
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request.Context.Request, request.Response, CancellationToken.None);
        Assert.Equal(500, request.Response.StatusCode);
        Assert.Equal(MimeTypes.ProblemJson, request.Response.Headers[HeaderNames.ContentType]);
        Assert.DoesNotContain("Lookup failed.", request.Response.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DenialWinsRegardlessOfRegistrationOrder(bool denyFirst)
    {
        await using var app = new SecurityTestApplication(builder =>
        {
            var authentication = builder.AddAuthentication();
            if (denyFirst)
            {
                authentication.AddVoter<LockVoter>().AddVoter<OwnerVoter>();
            }
            else
            {
                authentication.AddVoter<OwnerVoter>().AddVoter<LockVoter>();
            }
        });
        await using var request = app.Request();
        request.Context.User = SecurityTestApplication.User();
        var decision = await request.Context.AuthorizeAsync(new Document("alice", true), Permission.Edit);
        Assert.False(decision.Succeeded);
        Assert.Equal(AuthorizationFailureKind.Denied, decision.FailureKind);
        Assert.Equal("The document is locked.", decision.FailureReason);
        Assert.False(request.Response.HasStarted);
        Assert.Equal(200, request.Response.StatusCode);
    }

    [Fact]
    public async Task GrantWithAbstentionsSucceedsAndVotesAreNotCachedForChangedResources()
    {
        await using var app     = new SecurityTestApplication(builder => builder.AddAuthentication().AddVoter<OwnerVoter>().AddVoter<LockVoter>());
        await using var request = app.Request();
        request.Context.User = SecurityTestApplication.User();
        var allowed = await request.Context.AuthorizeAsync(new Document("alice", false), Permission.Edit);
        Assert.True(allowed.Succeeded);
        Assert.Null(allowed.FailureKind);
        Assert.Null(allowed.FailureReason);
        Assert.False((await request.Context.AuthorizeAsync(new Document("bob", false), Permission.Edit)).Succeeded);
        Assert.False((await request.Context.AuthorizeAsync(new Document("alice", true), Permission.Edit)).Succeeded);
    }

    [Fact]
    public async Task MissingVotersAndUnsupportedPairsFailClosed()
    {
        await using var empty        = new SecurityTestApplication(builder => builder.AddAuthentication());
        await using var emptyRequest = empty.Request();
        var             missing      = await emptyRequest.Context.AuthorizeAsync(new Document("alice", false), Permission.View);
        Assert.Equal(AuthorizationFailureKind.NoApplicableVoter, missing.FailureKind);
        var service = new AuthorizationService(new AuthenticationOptions());
        Assert.True(await service.AuthorizeAsync(emptyRequest.Context, SecurityTestApplication.User()));
        Assert.Equal(AuthorizationFailureKind.NoApplicableVoter,
            (await service.AuthorizeAsync(emptyRequest.Context, SecurityTestApplication.User(), new Document("alice", false), Permission.Edit)).FailureKind);

        await using var app     = new SecurityTestApplication(builder => builder.AddAuthentication().AddVoter<OwnerVoter>());
        await using var request = app.Request();
        request.Context.User = SecurityTestApplication.User();
        Assert.Equal(AuthorizationFailureKind.NoApplicableVoter, (await request.Context.AuthorizeAsync(new object(), Permission.View)).FailureKind);
        Assert.Equal(AuthorizationFailureKind.NoApplicableVoter, (await request.Context.AuthorizeAsync(null, Permission.View)).FailureKind);
        Assert.Equal(AuthorizationFailureKind.NoApplicableVoter, (await request.Context.AuthorizeAsync(new Document("alice", false), OtherPermission.View)).FailureKind);
        Assert.Equal(AuthorizationFailureKind.NoApplicableVoter, (await request.Context.AuthorizeAsync(new Document("alice", false), Permission.Delete)).FailureKind);
    }

    [Fact]
    public async Task InvalidEnumValuesCannotBeGrantedByABroadVoter()
    {
        await using var app     = new SecurityTestApplication(builder => builder.AddAuthentication().AddVoter<PublicVoter>());
        await using var request = app.Request();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => request.Context.AuthorizeAsync(new Document("alice", false), (Permission)100).AsTask());
    }

    [Fact]
    public async Task AuthorizationDoesNotAuthenticateOrChangeThePrincipal()
    {
        var authentications = 0;
        await using var app = new SecurityTestApplication(builder => builder.AddAuthentication(options => options.DefaultScheme = "test")
                                                                            .AddBearer("test", options => options.ValidateToken = (_, _) =>
                                                                            {
                                                                                authentications++;
                                                                                return ValueTask.FromResult(SecurityTestApplication.Success());
                                                                            }).AddVoter<PublicVoter>());
        await using var request   = app.Request(headers: headers => headers["Authorization"] = "Bearer token");
        var             principal = request.Context.User;
        var             decision  = await request.Context.AuthorizeAsync(new Document("alice", false), Permission.View);
        Assert.True(decision.Succeeded);
        Assert.Equal(0, authentications);
        Assert.Same(principal, request.Context.User);
        Assert.False(request.Context.User.Identity!.IsAuthenticated);
    }

    [Fact]
    public async Task VotersUseRequestScopesAndDuplicateRegistrationsDoNotDoubleVote()
    {
        await using var app = new SecurityTestApplication(builder =>
        {
            builder.Services.AddScoped<Probe>();
            builder.AddAuthentication().AddVoter<ScopedVoter>().AddVoter<ScopedVoter>();
        });
        Probe first;
        await using (var request = app.Request())
        {
            first = request.Context.Services.GetRequiredService<Probe>();
            Assert.Single(request.Context.Services.GetServices<IAuthorizationVoter>());
            await request.Context.AuthorizeAsync(new Document("alice", false), Permission.View);
            Assert.Equal(1, first.Calls);
            Assert.False(first.Disposed);
        }

        Assert.True(first.Disposed);

        await using var next   = app.Request();
        var             second = next.Context.Services.GetRequiredService<Probe>();
        Assert.NotSame(first, second);
        await next.Context.AuthorizeAsync(new Document("alice", false), Permission.View);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task CancellationBeforeAndDuringAVotePreventsSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        await using var app = new SecurityTestApplication(builder =>
        {
            builder.Services.AddSingleton(cancellation);
            builder.AddAuthentication().AddVoter<CancellingVoter>();
        });
        await using var request = app.Request(cancellationToken: cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.Context.AuthorizeAsync(new Document("alice", false), Permission.View).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.Context.AuthorizeAsync(new Document("alice", false), Permission.View).AsTask());
    }

    [Fact]
    public async Task VoterExceptionsAndNullDecisionsAreNeverTreatedAsAbstentions()
    {
        await using var throwing = new SecurityTestApplication(builder => builder.AddAuthentication().AddVoter<ThrowingVoter>());
        await using var request  = throwing.Request();
        var             error    = await Assert.ThrowsAsync<InvalidOperationException>(() => request.Context.AuthorizeAsync(new Document("alice", false), Permission.View).AsTask());
        Assert.Equal("Lookup failed.", error.Message);

        await using var invalid        = new SecurityTestApplication(builder => builder.AddAuthentication().AddVoter<NullVoter>());
        await using var invalidRequest = invalid.Request();
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalidRequest.Context.AuthorizeAsync(new Document("alice", false), Permission.View).AsTask());
    }

    [Fact]
    public async Task CustomPolicyServicesHandlePoliciesAndRejectUnsupportedResourceChecks()
    {
        await using var app = new SecurityTestApplication(builder =>
        {
            builder.Services.AddScoped<IAuthorizationService, PolicyService>();
            builder.AddAuthentication();
        });
        await using var request = app.Request();
        Assert.True(await request.Context.AuthorizeAsync("allowed"));
        Assert.False(await request.Context.AuthorizeAsync("denied"));
        await Assert.ThrowsAsync<NotSupportedException>(() => request.Context.AuthorizeAsync(new Document("alice", false), Permission.View).AsTask());
    }

    private enum Permission
    {
        View,
        Edit,
        Delete,
    }

    private enum OtherPermission
    {
        View,
    }

    private sealed record Document(string Owner, bool Locked);

    private sealed class OwnerVoter : AuthorizationVoter<Document, Permission>
    {
        protected override bool Supports(Document resource, Permission permission) => permission is Permission.View or Permission.Edit;

        protected override ValueTask<AuthorizationVote> VoteAsync(RequestContext context, ClaimsPrincipal principal, Document resource, Permission permission)
            => ValueTask.FromResult(principal.Identity?.Name == resource.Owner ? AuthorizationVote.Grant() : AuthorizationVote.Deny("Not the owner."));
    }

    private sealed class LockVoter : AuthorizationVoter<Document, Permission>
    {
        protected override ValueTask<AuthorizationVote> VoteAsync(RequestContext context, ClaimsPrincipal principal, Document resource, Permission permission)
            => ValueTask.FromResult(resource.Locked ? AuthorizationVote.Deny("The document is locked.") : AuthorizationVote.Abstain());
    }

    private sealed class PublicVoter : AuthorizationVoter<Document, Permission>
    {
        protected override ValueTask<AuthorizationVote> VoteAsync(RequestContext context, ClaimsPrincipal principal, Document resource, Permission permission)
            => ValueTask.FromResult(AuthorizationVote.Grant());
    }

    private sealed class Probe : IDisposable
    {
        public int  Calls    { get; set; }
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class ScopedVoter(Probe probe) : AuthorizationVoter<Document, Permission>
    {
        protected override ValueTask<AuthorizationVote> VoteAsync(RequestContext context, ClaimsPrincipal principal, Document resource, Permission permission)
        {
            probe.Calls++;
            return ValueTask.FromResult(AuthorizationVote.Grant());
        }
    }

    private sealed class CancellingVoter(CancellationTokenSource cancellation) : AuthorizationVoter<Document, Permission>
    {
        protected override async ValueTask<AuthorizationVote> VoteAsync(RequestContext context, ClaimsPrincipal principal, Document resource, Permission permission)
        {
            await Task.Yield();
            await cancellation.CancelAsync();
            return AuthorizationVote.Grant();
        }
    }

    private sealed class ThrowingVoter : IAuthorizationVoter
    {
        public ValueTask<AuthorizationVote> VoteAsync(RequestContext context, ClaimsPrincipal principal, object? resource, Enum permission)
            => throw new InvalidOperationException("Lookup failed.");
    }

    private sealed class NullVoter : IAuthorizationVoter
    {
        public ValueTask<AuthorizationVote> VoteAsync(RequestContext context, ClaimsPrincipal principal, object? resource, Enum permission)
            => ValueTask.FromResult<AuthorizationVote>(null!);
    }

    private sealed class PolicyService : IAuthorizationService
    {
        public ValueTask<bool> AuthorizeAsync(RequestContext context, ClaimsPrincipal principal, string? policy = null)
            => ValueTask.FromResult(policy == "allowed");
    }
}
