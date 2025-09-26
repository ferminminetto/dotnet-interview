using TodoApi.Models;

namespace TodoApi.Mapping;

public static class TodoListMapper
{
    public static TodoList ToEntity(ExternalTodoList ext)
    {
        return new TodoList
        {
            Name = ext.Name ?? "Unnamed",
            ExternalId = ext.Id,
            CreatedAt = ext.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = ext.UpdatedAt ?? DateTime.UtcNow,
            Items = (ext.TodoItems ?? [])
                .Select(TodoItemMapper.ToEntity)
                .ToList()
        };
    }

    public static ExternalCreateTodoList ToCreateBody(TodoList list)
    {
        return new ExternalCreateTodoList
        {
            Name = list.Name,
            Items = list.Items.Select(TodoItemMapper.ToCreateBody).ToList()
        };
    }

    public static ExternalUpdateTodoList ToUpdateBody(TodoList list)
    {
        return new ExternalUpdateTodoList
        {
            Name = list.Name
        };
    }
}
