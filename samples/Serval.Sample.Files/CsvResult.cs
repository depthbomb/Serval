#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Http.Constants;

namespace Serval.Sample.Files;

// The example uses application-owned values. Spreadsheet exports of untrusted data
// also need a formula-injection policy appropriate to the spreadsheet consumer.
public sealed class CsvResult : IActionResult
{
    private readonly IEnumerable<string[]> _rows;

    public CsvResult(IEnumerable<string[]> rows)
    {
        _rows = rows;
    }

    public async ValueTask ExecuteAsync(RequestContext context)
    {
        context.Response.Headers[HeaderNames.ContentType]        = "text/csv; charset=utf-8";
        context.Response.Headers[HeaderNames.ContentDisposition] = "attachment; filename=generated-report.csv";
        foreach (var row in _rows)
        {
            var line = string.Join(',', row.Select(value => "\"" + value.Replace("\"", "\"\"") + "\"")) + "\r\n";
            await context.Response.WriteTextAsync(line, context.RequestAborted);
        }
    }
}
