using System.Text.Json.Serialization;

public class ExternalUpdateTodoItem
{
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("completed")] public bool? Completed { get; set; }
    [JsonPropertyName("source_id")] public string? SourceId { get; set; }
}