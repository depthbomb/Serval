#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Binding;
using Serval.Generated;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;

namespace Serval.Sample.TodoApi;

[Controller]
public sealed class TodosController : ControllerBase
{
    private readonly TodoStore _store;

    public TodosController(TodoStore store)
    {
        _store = store;
    }

    [Get("/")]
    public ValueTask<IActionResult> IndexAsync(RequestContext context) => TextAsync("""
                                                                                    In-memory todo API with generated JSON metadata
                                                                                    POST /todos          JSON: {"title":"Try Serval"} -> 201 + Location
                                                                                    GET /todos?completed=false&offset=0&limit=20
                                                                                    GET /todos/1
                                                                                    PUT /todos/1         JSON: {"title":"Try Serval","completed":true}
                                                                                    DELETE /todos/1      -> 204
                                                                                    Send Content-Type: application/json when writing.
                                                                                    Empty titles return 422; missing items return 404; bodies over 16 KiB return 413.
                                                                                    This sample has no authentication or persistence. Data resets on restart.
                                                                                    """);

    [Get("/todos")]
    public ValueTask<IActionResult> ListAsync(RequestContext context, [FromQuery] bool? completed, [FromQuery] int offset = 0, [FromQuery] int limit = 20)
    {
        AbortIf(offset < 0 || limit is < 1 or > 100, StatusCodes.BadRequest, "Offset must be nonnegative; limit must be 1 through 100.");
        return JsonAsync(_store.List(completed, offset, limit));
    }

    [Get("/todos/{id:int:min(1)}", Name = "todos.show")]
    public ValueTask<IActionResult> GetAsync(RequestContext context, [FromRoute] int id)
    {
        var todo = _store.Find(id);
        AbortIf(todo is null, StatusCodes.NotFound, "Todo not found.");
        return JsonAsync(todo);
    }

    [Post("/todos")]
    public ValueTask<IActionResult> CreateAsync(RequestContext context, [FromBody] TodoInput input)
    {
        ValidateInput(input);
        var todo = _store.Add(input.Title!.Trim());
        context.Response.Headers[HeaderNames.Location] = context.Urls.TodosShow(todo.Id);
        return JsonAsync(todo, statusCode: StatusCodes.Created);
    }

    [Put("/todos/{id:int:min(1)}")]
    public ValueTask<IActionResult> ReplaceAsync(RequestContext context, [FromRoute] int id, [FromBody] TodoInput input)
    {
        ValidateInput(input);
        var todo = _store.Replace(id, input.Title!.Trim(), input.Completed);
        AbortIf(todo is null, StatusCodes.NotFound, "Todo not found.");
        return JsonAsync(todo);
    }

    [Delete("/todos/{id:int:min(1)}")]
    public ValueTask<IActionResult> DeleteAsync(RequestContext context, [FromRoute] int id)
    {
        AbortUnless(_store.Delete(id), StatusCodes.NotFound, "Todo not found.");
        return ValueTask.FromResult<IActionResult>(StatusCode(StatusCodes.NoContent));
    }

    private static void ValidateInput(TodoInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 120)
        {
            throw new HttpErrorException(StatusCodes.UnprocessableContent, "Title must contain 1 through 120 characters.", "invalid_title");
        }
    }
}
