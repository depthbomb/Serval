#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;

namespace Serval.Sample.EntityFramework;

public sealed record NoteInput(string? Title, string? Body);

[JsonSerializable(typeof(Note))]
[JsonSerializable(typeof(Note[]))]
[JsonSerializable(typeof(NoteInput))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class NotesJsonContext : JsonSerializerContext;

[Controller]
public sealed class NotesController : ControllerBase
{
    private readonly NotesDbContext _database;

    public NotesController(NotesDbContext database)
    {
        _database = database;
    }

    [Get("/")]
    public ValueTask<IActionResult> IndexAsync(RequestContext context) => TextAsync("""
                                                                                    EF Core + Microsoft.Extensions.Hosting
                                                                                    POST /notes          JSON: {"title":"Try EF Core","body":"Saved between runs"}
                                                                                    GET /notes?offset=0&limit=20
                                                                                    GET /notes/1
                                                                                    PUT /notes/1         JSON: {"title":"Updated","body":"Still persisted"}
                                                                                    DELETE /notes/1
                                                                                    Send Content-Type: application/json when writing.
                                                                                    Titles allow 1-120 characters; bodies allow up to 4000 characters.
                                                                                    Configure --ConnectionStrings:Notes "Data Source=your-file.db" and --port 8111.
                                                                                    The default notes.db lives beside the executable. This is a fixed-schema demo.
                                                                                    """);

    [Get("/notes")]
    public async ValueTask<IActionResult> ListAsync(RequestContext context)
    {
        var offset = context.Request.Query.ContainsKey("offset") ? context.Request.QueryValue<int>("offset") : 0;
        var limit  = context.Request.Query.ContainsKey("limit") ? context.Request.QueryValue<int>("limit") : 20;
        AbortIf(offset < 0 || limit is < 1 or > 100, StatusCodes.BadRequest, "Offset must be nonnegative; limit must be 1 through 100.");

        var notes = await _database.Notes
                                   .AsNoTracking()
                                   .OrderBy(note => note.Id)
                                   .Skip(offset)
                                   .Take(limit)
                                   .ToArrayAsync(context.RequestAborted);
        return Json(notes, NotesJsonContext.Default.NoteArray);
    }

    [Get("/notes/{id:int:min(1)}", Name = "notes.show")]
    public async ValueTask<IActionResult> GetAsync(RequestContext context)
    {
        var id   = RouteValue<int>("id");
        var note = await _database.Notes.AsNoTracking().SingleOrDefaultAsync(note => note.Id == id, context.RequestAborted);
        AbortIf(note is null, StatusCodes.NotFound, "Note not found.");

        return Json(note, NotesJsonContext.Default.Note);
    }

    [Post("/notes")]
    public async ValueTask<IActionResult> CreateAsync(RequestContext context)
    {
        var input = await ReadInputAsync(context);
        var note = new Note
        {
            Title = input.Title!.Trim(),
            Body  = input.Body ?? "",
        };
        _database.Notes.Add(note);
        await _database.SaveChangesAsync(context.RequestAborted);

        context.Response.Headers[HeaderNames.Location] = context.Urls.Path("notes.show", new Dictionary<string, object?>
        {
            ["id"] = note.Id,
        });
        return Json(note, NotesJsonContext.Default.Note, StatusCodes.Created);
    }

    [Put("/notes/{id:int:min(1)}")]
    public async ValueTask<IActionResult> ReplaceAsync(RequestContext context)
    {
        var input = await ReadInputAsync(context);
        var note  = await _database.Notes.FindAsync([RouteValue<int>("id")], context.RequestAborted) ?? throw new HttpErrorException(StatusCodes.NotFound, "Note not found.");
        note.Title = input.Title!.Trim();
        note.Body  = input.Body ?? "";

        await _database.SaveChangesAsync(context.RequestAborted);

        return Json(note, NotesJsonContext.Default.Note);
    }

    [Delete("/notes/{id:int:min(1)}")]
    public async ValueTask<IActionResult> DeleteAsync(RequestContext context)
    {
        var note = await _database.Notes.FindAsync([RouteValue<int>("id")], context.RequestAborted) ?? throw new HttpErrorException(StatusCodes.NotFound, "Note not found.");
        _database.Notes.Remove(note);

        await _database.SaveChangesAsync(context.RequestAborted);

        return StatusCode(StatusCodes.NoContent);
    }

    private async ValueTask<NoteInput> ReadInputAsync(RequestContext context)
    {
        var input = await context.Request.ReadJsonAsync(NotesJsonContext.Default.NoteInput, context.RequestAborted);
        AbortIf(string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 120 || input.Body?.Length > 4000, StatusCodes.UnprocessableContent, "Provide a title of 1-120 characters and a body of at most 4000 characters.");

        return input;
    }
}
