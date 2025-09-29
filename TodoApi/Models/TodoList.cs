namespace TodoApi.Models;

public class TodoList
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public IList<TodoItem> Items { get; set; } = new List<TodoItem>();
    // ExternalId is used to map to the External TodoApi.
    public string? ExternalId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    // To enable optimistic concurrency control
    public Byte[]? RowVersion { get; set; }
}
