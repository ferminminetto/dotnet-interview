using System.Collections.Concurrent;
using TodoApi.Integration;

namespace TodoApi.Integration.Fakes;

// Simple in-memory fake for IExternalTodoApiClient.
// Thread-safe and suitable for tests and local runs.
// All comments in English by convention.
public sealed class FakeExternalTodoApiClient : IExternalTodoApiClient
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, ExternalTodoList> _lists = new();

    public Task<List<ExternalTodoList>> ListTodoListsAsync(CancellationToken ct)
    {
        // Return a snapshot copy to avoid external mutation of internal state.
        lock (_lock)
        {
            var snapshot = _lists.Values
                .Select(CloneList)
                .ToList();
            return Task.FromResult(snapshot);
        }
    }

    public Task<ExternalTodoList> CreateTodoListAsync(ExternalCreateTodoList body, CancellationToken ct)
    {
        lock (_lock)
        {
            var id = $"ext-{Guid.NewGuid():N}";
            var now = DateTime.UtcNow;

            var list = new ExternalTodoList
            {
                Id = id,
                Name = body.Name,
                CreatedAt = now,
                UpdatedAt = now,
                TodoItems = (body.Items ?? new()).Select(i => new ExternalTodoItem
                {
                    Id = $"external-{Guid.NewGuid():N}",
                    SourceId = i.SourceId,
                    Description = i.Description,
                    Completed = i.Completed,
                    CreatedAt = now,
                    UpdatedAt = now
                }).ToList()
            };

            _lists[id] = list;
            return Task.FromResult(CloneList(list));
        }
    }

    public Task<ExternalTodoList> UpdateTodoListAsync(string todolistId, ExternalUpdateTodoList body, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_lists.TryGetValue(todolistId, out var list))
            {
                // Create-if-missing behavior to keep fake permissive in dev
                list = new ExternalTodoList { Id = todolistId, Name = body.Name ?? "unnamed", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, TodoItems = new() };
                _lists[todolistId] = list;
            }

            if (!string.IsNullOrWhiteSpace(body.Name))
                list.Name = body.Name!;
            list.UpdatedAt = DateTime.UtcNow;

            return Task.FromResult(CloneList(list));
        }
    }

    public Task DeleteTodoListAsync(string todolistId, CancellationToken ct)
    {
        lock (_lock)
        {
            _lists.TryRemove(todolistId, out _);
            return Task.CompletedTask;
        }
    }

    public Task<ExternalTodoItem> UpdateTodoItemAsync(string todolistId, string todoitemId, ExternalUpdateTodoItem body, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_lists.TryGetValue(todolistId, out var list))
            {
                // Create list if missing to keep fake permissive
                list = new ExternalTodoList { Id = todolistId, Name = "autocreated", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, TodoItems = new() };
                _lists[todolistId] = list;
            }

            var now = DateTime.UtcNow;
            var item = list.TodoItems?.FirstOrDefault(x => x.Id == todoitemId);
            if (item is null)
            {
                item = new ExternalTodoItem
                {
                    Id = todoitemId,
                    SourceId = body.SourceId,
                    Description = body.Description,
                    Completed = body.Completed ?? false,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                list.TodoItems ??= new List<ExternalTodoItem>();
                list.TodoItems.Add(item);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(body.Description))
                    item.Description = body.Description!;
                if (body.Completed.HasValue)
                    item.Completed = body.Completed.Value;
                if (!string.IsNullOrWhiteSpace(body.SourceId))
                    item.SourceId = body.SourceId!;
                item.UpdatedAt = now;
            }

            list.UpdatedAt = now;
            return Task.FromResult(CloneItem(item));
        }
    }

    public Task DeleteTodoItemAsync(string todolistId, string todoitemId, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_lists.TryGetValue(todolistId, out var list) && list.TodoItems is not null)
            {
                list.TodoItems.RemoveAll(x => x.Id == todoitemId);
                list.UpdatedAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }
    }

    // Helpers to avoid leaking references outside the fake
    private static ExternalTodoList CloneList(ExternalTodoList src) => new()
    {
        Id = src.Id,
        SourceId = src.SourceId,
        Name = src.Name,
        CreatedAt = src.CreatedAt,
        UpdatedAt = src.UpdatedAt,
        TodoItems = src.TodoItems?.Select(CloneItem).ToList() ?? new()
    };

    private static ExternalTodoItem CloneItem(ExternalTodoItem src) => new()
    {
        Id = src.Id,
        SourceId = src.SourceId,
        Description = src.Description,
        Completed = src.Completed,
        CreatedAt = src.CreatedAt,
        UpdatedAt = src.UpdatedAt
    };
}