using System.Text.Json.Serialization;

public class ExternalCreateTodoList
{
    [JsonPropertyName("name")] public string Name { get; set; } = null!;
    [JsonPropertyName("items")] public List<ExternalCreateTodoItem> Items { get; set; } = new();
}