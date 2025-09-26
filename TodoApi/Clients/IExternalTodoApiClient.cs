using System.Net.Http.Json;

namespace TodoApi.Integration;


public interface IExternalTodoApiClient
{
    Task<List<ExternalTodoList>> ListTodoListsAsync(CancellationToken ct);
    Task<ExternalTodoList> CreateTodoListAsync(ExternalCreateTodoList body, CancellationToken ct);
    Task<ExternalTodoList> UpdateTodoListAsync(string todolistId, ExternalUpdateTodoList body, CancellationToken ct);
    Task DeleteTodoListAsync(string todolistId, CancellationToken ct);

    Task<ExternalTodoItem> UpdateTodoItemAsync(string todolistId, string todoitemId, ExternalUpdateTodoItem body, CancellationToken ct);
    Task DeleteTodoItemAsync(string todolistId, string todoitemId, CancellationToken ct);
}

