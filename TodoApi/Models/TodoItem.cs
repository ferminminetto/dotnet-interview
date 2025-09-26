namespace TodoApi.Models
{
    public class TodoItem
    {
        public long Id { get; set; }
        public required string Name { get; set; }
        public bool IsComplete { get; set; }
        public long TodoListId { get; set; }
        // ExternalId is used to map to the External TodoApi.
        public string? ExternalId { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
        // To enable optimistic concurrency control
        public Byte[]? RowVersion { get; set; }
    }
}
