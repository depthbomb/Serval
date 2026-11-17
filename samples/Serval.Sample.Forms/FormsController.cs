#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Cryptography;
using Serval.Binding;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;

namespace Serval.Sample.Forms;

[Controller]
public sealed class FormsController : ControllerBase
{
    [Get("/")]
    public ValueTask<IActionResult> Index()
    {
        Response.Headers[HeaderNames.ContentType] = "text/html; charset=utf-8";
        return TextAsync("""
                         <!doctype html>
                         <html lang="en">
                         <meta charset="utf-8">
                         <title>Serval forms</title>
                         <h1>Forms, JSON, and uploaded files</h1>
                         <h2>URL-encoded form</h2>
                         <form method="post" action="/submit/42?limit=5">
                           <label>Username <input name="username" value="reese" required></label>
                           <input type="hidden" name="ids" value="1"><input type="hidden" name="ids" value="2">
                           <button>Submit fields</button>
                         </form>
                         <h2>Multipart form</h2>
                         <form method="post" enctype="multipart/form-data" action="/submit/42?limit=5">
                           <label>Username <input name="username" value="reese" required></label>
                           <label>Files <input type="file" name="files" multiple></label>
                           <button>Submit files</button>
                         </form>
                         <h2>Streaming multipart files</h2>
                         <form method="post" enctype="multipart/form-data" action="/stream">
                           <label>Files <input type="file" name="files" multiple></label>
                           <button>Hash files as they arrive</button>
                         </form>
                         <h2>JSON</h2>
                         <button id="json">Send the same fields as JSON</button>
                         <pre id="result"></pre>
                         <script>
                           document.querySelector('#json').addEventListener('click', async () => {
                             const response = await fetch('/submit/42?limit=5', {
                               method: 'POST', headers: {'Content-Type': 'application/json'},
                               body: JSON.stringify({username: 'reese', ids: [1, 2]})
                             });
                             document.querySelector('#result').textContent = await response.text();
                           });
                         </script>
                         <p>The same handler reads all three formats. Files are hashed and discarded after the request.</p>
                         <p>Limits: 8 MiB total, 4 MiB per file, four files, and 32 field values.</p>
                         <p>POST /bound with form fields username and ids to try attribute binding.</p>
                         </html>
                         """);
    }

    [Post("/submit/{id:int:min(1)}")]
    public async ValueTask<IActionResult> Submit()
    {
        var id       = RouteValue<int>("id");
        var limit    = QueryValue("limit", 10);
        var post     = await ReadPostAsync();
        var username = post.Get<string>("username");

        AbortIf(string.IsNullOrWhiteSpace(username) || username.Length > 80, StatusCodes.UnprocessableContent, "Username must contain 1 through 80 characters.");

        var files = new List<object>();
        foreach (var file in post.Files)
        {
            await using var content = file.OpenReadStream();
            var             digest  = await SHA256.HashDataAsync(content, RequestAborted);
            files.Add(new
            {
                file.Name,
                file.FileName,
                file.ContentType,
                file.Length,
                sha256 = Convert.ToHexString(digest)
            });
        }

        return Json(new
        {
            id,
            limit,
            username,
            ids = post.GetValues<int>("ids"),
            files
        });
    }

    [Post("/bound")]
    public ValueTask<IActionResult> Bound([FromForm] string username, [FromForm] int[] ids)
        => JsonAsync(new
        {
            username,
            ids
        });

    [Post("/stream")]
    public async ValueTask<IActionResult> StreamFiles()
    {
        var files = new List<object>();
        await Request.ReadMultipartAsync(async (section, ct) =>
        {
            if (section.FileName is not null)
            {
                var digest = await SHA256.HashDataAsync(section.Body, ct);
                files.Add(new
                {
                    section.Name,
                    section.FileName,
                    sha256 = Convert.ToHexString(digest)
                });
            }
        }, RequestAborted);
        return Json(new
        {
            files
        });
    }
}
