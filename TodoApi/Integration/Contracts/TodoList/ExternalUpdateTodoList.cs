using System.Text.Json.Serialization;

public class ExternalUpdateTodoList
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}