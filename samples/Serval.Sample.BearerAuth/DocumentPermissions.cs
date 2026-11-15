#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;
using Serval.Authentication.Authorization;

namespace Serval.Sample.BearerAuth;

public enum DocumentPermission
{
    View,
    Edit,
}

public sealed record Document(int Id, string OwnerId, string Title, bool Locked);

public sealed class DocumentStore
{
    private readonly IReadOnlyDictionary<int, Document> _documents = new Dictionary<int, Document>
    {
        [1] = new(1, "bob", "Bob's draft", false),
        [2] = new(2, "alice", "Approved report", true),
        [3] = new(3, "alice", "Alice's draft", false),
    };

    public Document? Find(int id) => _documents.GetValueOrDefault(id);
}

public sealed class DocumentAccessVoter : AuthorizationVoter<Document, DocumentPermission>
{
    protected override ValueTask<AuthorizationVote> VoteAsync(RequestContext context, ClaimsPrincipal principal, Document resource, DocumentPermission permission)
    {
        if (!principal.Identities.Any(identity => identity.IsAuthenticated))
        {
            return ValueTask.FromResult(AuthorizationVote.Deny("An authenticated user is required."));
        }

        var owner  = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value == resource.OwnerId;
        var editor = principal.IsInRole("editor");
        var vote = permission switch
        {
            DocumentPermission.View when owner || editor => AuthorizationVote.Grant(),
            DocumentPermission.Edit when editor          => AuthorizationVote.Grant(),
            _                                            => AuthorizationVote.Deny("The user lacks access to this document operation."),
        };
        return ValueTask.FromResult(vote);
    }
}

public sealed class LockedDocumentVoter : AuthorizationVoter<Document, DocumentPermission>
{
    protected override bool Supports(Document resource, DocumentPermission permission) => permission == DocumentPermission.Edit;

    protected override ValueTask<AuthorizationVote> VoteAsync(RequestContext context, ClaimsPrincipal principal, Document resource, DocumentPermission permission)
        => ValueTask.FromResult(resource.Locked ? AuthorizationVote.Deny("Approved documents cannot be edited.") : AuthorizationVote.Abstain());
}
