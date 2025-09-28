using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using TodoApi.Integration;
using TodoApi.Tests.TestUtils;

namespace TodoApi.Tests.Clients;

public class ExternalTodoApiClientTests
{
    [Fact]
    public async Task ListTodoListsAsync_returns_lists_from_server()
    {
        /*
         * Verifies that GET /todolists is invoked and the JSON payload is deserialized
         * into a list containing one TodoList with one TodoItem.
         */

        // Arrange
        var handler = new TestHttpMessageHandler((req, ct) =>
        {
            req.Method.Should().Be(HttpMethod.Get);
            req.RequestUri!.AbsolutePath.Should().Be("/todolists");

            var json = """
            [
              {
                "id": "ext-1",
                "name": "Remote List",
                "created_at": "2025-01-01T00:00:00Z",
                "updated_at": "2025-01-01T00:00:00Z",
                "items": [
                  {
                    "id": "item-1",
                    "description": "Do something",
                    "completed": true,
                    "created_at": "2025-01-01T00:00:00Z",
                    "updated_at": "2025-01-01T00:00:00Z"
                  }
                ]
              }
            ]
            """;
            return TestHttpMessageHandler.Json(HttpStatusCode.OK, json);
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var sut = new ExternalTodoApiClient(http);

        // Act
        var lists = await sut.ListTodoListsAsync(CancellationToken.None);

        // Assert
        lists.Should().HaveCount(1);
        lists[0].Id.Should().Be("ext-1");
        lists[0].Name.Should().Be("Remote List");
        lists[0].TodoItems.Should().HaveCount(1);
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateTodoListAsync_posts_body_and_parses_response()
    {
        /*
         * Verifies that POST /todolists is called with a JSON body containing the list name
         * and that the response is deserialized into the created ExternalTodoList.
         */

        // Arrange
        var handler = new TestHttpMessageHandler((req, ct) =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.Should().Be("/todolists");

            // Optional: assert request body contains the list name
            var body = req.Content!.ReadAsStringAsync(ct).Result;
            body.Should().Contain("List A");

            var json = """
            {
              "id": "ext-123",
              "name": "List A",
              "created_at": "2025-01-01T00:00:00Z",
              "updated_at": "2025-01-01T00:00:00Z",
              "items": []
            }
            """;
            return TestHttpMessageHandler.Json(HttpStatusCode.OK, json);
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var sut = new ExternalTodoApiClient(http);

        var body = new ExternalCreateTodoList
        {
            Name = "List A",
            Items = new List<ExternalCreateTodoItem>
            {
                new() { Description = "Task1", Completed = false, SourceId = "1" }
            }
        };

        // Act
        var created = await sut.CreateTodoListAsync(body, CancellationToken.None);

        // Assert
        created.Id.Should().Be("ext-123");
        created.Name.Should().Be("List A");
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task UpdateTodoListAsync_uses_PATCH_and_returns_updated_list()
    {
        /*
         * Verifies that PATCH /todolists/{id} is used to update a list and that the client
         * returns the updated ExternalTodoList from the response payload.
         */

        // Arrange
        var handler = new TestHttpMessageHandler((req, ct) =>
        {
            req.Method.Method.Should().Be("PATCH");
            req.RequestUri!.AbsolutePath.Should().Be("/todolists/ext-42");

            var json = """
            {
              "id": "ext-42",
              "name": "Renamed",
              "created_at": "2025-01-01T00:00:00Z",
              "updated_at": "2025-01-02T00:00:00Z",
              "items": []
            }
            """;
            return TestHttpMessageHandler.Json(HttpStatusCode.OK, json);
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var sut = new ExternalTodoApiClient(http);

        var updated = await sut.UpdateTodoListAsync("ext-42", new ExternalUpdateTodoList { Name = "Renamed" }, CancellationToken.None);

        updated.Id.Should().Be("ext-42");
        updated.Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task UpdateTodoItemAsync_uses_PATCH_and_returns_updated_item()
    {
        /*
         * Verifies that PATCH /todolists/{listId}/todoitems/{itemId} is used to update an item
         * and that the client returns the updated ExternalTodoItem with new fields.
         */

        // Arrange
        var handler = new TestHttpMessageHandler((req, ct) =>
        {
            req.Method.Method.Should().Be("PATCH");
            req.RequestUri!.AbsolutePath.Should().Be("/todolists/ext-1/todoitems/itm-9");

            var json = """
            {
              "id": "itm-9",
              "description": "Updated",
              "completed": true,
              "created_at": "2025-01-01T00:00:00Z",
              "updated_at": "2025-01-02T00:00:00Z"
            }
            """;
            return TestHttpMessageHandler.Json(HttpStatusCode.OK, json);
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var sut = new ExternalTodoApiClient(http);

        var body = new ExternalUpdateTodoItem { Description = "Updated", Completed = true, SourceId = "2" };
        var item = await sut.UpdateTodoItemAsync("ext-1", "itm-9", body, CancellationToken.None);

        item.Id.Should().Be("itm-9");
        item.Description.Should().Be("Updated");
        item.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task Methods_throw_on_non_success_status_codes()
    {
        /*
         * Verifies that client methods throw HttpRequestException when the server
         * responds with non-success HTTP status codes like 500
         */

        // Arrange
        var handler = new TestHttpMessageHandler((req, ct) =>
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var sut = new ExternalTodoApiClient(http);

        // Act + Assert
        await FluentActions.Invoking(() => sut.CreateTodoListAsync(new ExternalCreateTodoList { Name = "X" }, CancellationToken.None))
            .Should().ThrowAsync<HttpRequestException>();
    }
}