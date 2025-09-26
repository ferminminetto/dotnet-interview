using TodoApi.Models;

namespace TodoApi.Mapping;

public static class TodoItemMapper
{
    public static TodoItem ToEntity(ExternalTodoItem ext)
    {
        return new TodoItem
        {
            Name = ext.Description ?? "Unnamed item",
            IsComplete = ext.Completed ?? false,
            ExternalId = ext.Id,
            CreatedAt = ext.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = ext.UpdatedAt
        };
    }

    public static ExternalCreateTodoItem ToCreateBody(TodoItem item)
    {
        return new ExternalCreateTodoItem
        {
            Description = item.Name,
            Completed = item.IsComplete,
            SourceId = item.Id == 0 ? null : item.Id.ToString()
        };
    }

    public static ExternalUpdateTodoItem ToUpdateBody(TodoItem item)
    {
        return new ExternalUpdateTodoItem
        {
            Description = item.Name,
            Completed = item.IsComplete,
            SourceId = item.Id == 0 ? null : item.Id.ToString()
        };
    }
}
