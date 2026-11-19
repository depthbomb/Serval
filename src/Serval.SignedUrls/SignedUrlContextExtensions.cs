#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Serval.Http;
using Serval.Http.Constants;

namespace Serval.SignedUrls;

/// <summary>Validates the current request through its registered signed URL service.</summary>
public static class SignedUrlContextExtensions
{
    extension(RequestContext context)
    {
        /// <summary>Validates the request URL and returns verified data on success.</summary>
        /// <returns>The signature, URL, and expiration validation result.</returns>
        public SignedUrlValidationResult ValidateSignedUrl()
            => context.Services.GetRequiredService<ISignedUrlService>().Validate(context.Request);

        /// <summary>Checks whether the current request has a valid, unexpired signed URL.</summary>
        /// <returns>True when all checks pass.</returns>
        public bool HasValidSignedUrl() => context.Services.GetRequiredService<ISignedUrlService>().IsValid(context.Request);

        /// <summary>Requires a valid signed URL and returns its authenticated data.</summary>
        /// <returns>The verified route and query data.</returns>
        /// <exception cref="HttpErrorException">Validation failed; the status is 403.</exception>
        public SignedUrlData RequireSignedUrl()
        {
            var result = context.ValidateSignedUrl();
            return !result.IsValid ? throw new HttpErrorException(StatusCodes.Forbidden, "A valid signed URL is required.") : result.Data!;
        }
    }
}
