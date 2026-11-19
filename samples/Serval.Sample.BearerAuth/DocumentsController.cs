#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using Serval.Authentication;
using Serval.Authentication.Authorization;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;

namespace Serval.Sample.BearerAuth;

[Controller]
public sealed class DocumentsController : ControllerBase
{
    private readonly DocumentStore                _documents;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(DocumentStore documents, ILogger<DocumentsController> logger)
    {
        _documents = documents;
        _logger    = logger;
    }

    [Get("/documents/{id:int:min(1)}")]
    public ValueTask<IActionResult> ViewAsync(RequestContext context) => CheckAsync(context, DocumentPermission.View);

    [Post("/documents/{id:int:min(1)}/edit-preview")]
    public ValueTask<IActionResult> EditPreviewAsync(RequestContext context) => CheckAsync(context, DocumentPermission.Edit);

    private async ValueTask<IActionResult> CheckAsync(RequestContext context, DocumentPermission permission)
    {
        // The action owns the order: authenticate, load the resource, authorize, respond.
        var authentication = await context.AuthenticateAsync("Bearer");
        if (!authentication.Succeeded)
        {
            await context.ChallengeAsync("Bearer");
            return Empty();
        }

        var document = _documents.Find(RouteValue<int>("id"));

        AbortIf(document is null, StatusCodes.NotFound, "Document not found.");

        var decision = await context.AuthorizeAsync(document, permission);
        if (!decision.Succeeded)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Document {DocumentId} permission {Permission} denied: {Kind}, {Reason}.", document.Id, permission, decision.FailureKind, decision.FailureReason);
            }

            // Diagnostic reasons stay in the log. The application chooses the public error.
            throw new HttpErrorException(StatusCodes.Forbidden, "This document operation is not allowed.", "document_access_denied");
        }

        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return Json(new
        {
            document,
            permission = permission.ToString(),
            message    = "Permission granted. No document was changed.",
        });
    }
}
