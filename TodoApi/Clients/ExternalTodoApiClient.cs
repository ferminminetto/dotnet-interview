using System.Text.Json;
using TodoApi.Integration;

public sealed class ExternalTodoApiClient(HttpClient http) : IExternalTodoApiClient
{
    private readonly HttpClient _http = http;

    public async Task<List<ExternalTodoList>> ListTodoListsAsync(CancellationToken ct)
        => await _http.GetFromJsonAsync<List<ExternalTodoList>>("/todolists", SerializerOptions(), ct)
           ?? new List<ExternalTodoList>();

    public async Task<ExternalTodoList> CreateTodoListAsync(ExternalCreateTodoList body, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("/todolists", body, SerializerOptions(), ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ExternalTodoList>(SerializerOptions(), ct))!;
    }

    public async Task<ExternalTodoList> UpdateTodoListAsync(string todolistId, ExternalUpdateTodoList body, CancellationToken ct)
    {
        var resp = await _http.PatchAsJsonAsync($"/todolists/{todolistId}", body, SerializerOptions(), ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ExternalTodoList>(SerializerOptions(), ct))!;
    }

    public async Task DeleteTodoListAsync(string todolistId, CancellationToken ct)
    {
        var resp = await _http.DeleteAsync($"/todolists/{todolistId}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ExternalTodoItem> UpdateTodoItemAsync(string todolistId, string todoitemId, ExternalUpdateTodoItem body, CancellationToken ct)
    {
        var resp = await _http.PatchAsJsonAsync($"/todolists/{todolistId}/todoitems/{todoitemId}", body, SerializerOptions(), ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ExternalTodoItem>(SerializerOptions(), ct))!;
    }

    public async Task DeleteTodoItemAsync(string todolistId, string todoitemId, CancellationToken ct)
    {
        var resp = await _http.DeleteAsync($"/todolists/{todolistId}/todoitems/{todoitemId}", ct);
        resp.EnsureSuccessStatusCode();
    }

    private static JsonSerializerOptions SerializerOptions() => new()
    {
        PropertyNameCaseInsensitive = true
    };
}