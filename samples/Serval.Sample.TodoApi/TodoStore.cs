#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text.Json.Serialization;

namespace Serval.Sample.TodoApi;

public sealed record Todo(int Id, string Title, bool Completed);

public sealed record TodoInput(string? Title, bool Completed = false);

[JsonSerializable(typeof(Todo))]
[JsonSerializable(typeof(Todo[]))]
[JsonSerializable(typeof(TodoInput))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class TodoJsonContext : JsonSerializerContext;

// This singleton is shared by requests. A lock keeps each operation atomic.
// The data is intentionally temporary and resets when the sample restarts.
public sealed class TodoStore
{
    private          int                   _nextId;
    private readonly Lock                  _gate  = new();
    private readonly Dictionary<int, Todo> _items = [];

    public Todo[] List(bool? completed, int offset, int limit)
    {
        lock (_gate)
        {
            return [.. _items.Values.Where(todo => completed is null || todo.Completed == completed).OrderBy(todo => todo.Id).Skip(offset).Take(limit)];
        }
    }

    public Todo? Find(int id)
    {
        lock (_gate)
        {
            return _items.GetValueOrDefault(id);
        }
    }

    public Todo Add(string title)
    {
        lock (_gate)
        {
            var todo = new Todo(++_nextId, title, false);
            _items.Add(todo.Id, todo);
            return todo;
        }
    }

    public Todo? Replace(int id, string title, bool completed)
    {
        lock (_gate)
        {
            if (!_items.ContainsKey(id))
            {
                return null;
            }

            return _items[id] = new Todo(id, title, completed);
        }
    }

    public bool Delete(int id)
    {
        lock (_gate)
        {
            return _items.Remove(id);
        }
    }
}
