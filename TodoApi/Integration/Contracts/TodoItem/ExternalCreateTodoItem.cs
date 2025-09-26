using System.Text.Json.Serialization;

public class ExternalCreateTodoItem
{
    [JsonPropertyName("description")] public string Description { get; set; } = null!;
    [JsonPropertyName("completed")] public bool Completed { get; set; }
    [JsonPropertyName("source_id")] public string? SourceId { get; set; }
}