using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FluentAssertions;
using TodoApi.Integration;
using TodoApi.Integration.Fakes;
using TodoApi.Models;
using TodoApi.Options;
using TodoApi.Sync;
using TodoApi.Tests.TestUtils;

namespace TodoApi.Tests.Sync;

public class ExternalTodoApiSyncServiceTests
{
    [Fact]
    public async Task Sync_creates_remote_list_and_links_externalId_when_local_list_has_no_external_match()
    {
        /*
         * Testing that a local TodoList without ExternalId is pushed to remote
         */

        // Setup DI container with InMemory EF Core, fake external client, options and logging.
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());

        var dbName = $"SyncTests-{Guid.NewGuid()}";
        services.AddDbContext<TodoContext>(o => o.UseInMemoryDatabase(dbName));

        // Use the in-memory fake so we do not hit real HTTP.
        services.AddSingleton<IExternalTodoApiClient, FakeExternalTodoApiClient>();

        // Options required by the BackgroundService (period does not matter for single-run test)
        services.AddSingleton<IOptions<ExternalApiOptions>>(
            new OptionsWrapper<ExternalApiOptions>(new ExternalApiOptions
            {
                BaseUrl = "http://localhost",
                SyncPeriodSeconds = 3600,
                TimeoutSeconds = 5
            }));

        var provider = services.BuildServiceProvider();

        // Seed local data: one list without ExternalId and one item
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            ctx.TodoList.Add(new TodoList
            {
                Name = "Local Only",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Items = new List<TodoItem>
                {
                    new TodoItem
                    {
                        Name = "Local Item 1",
                        IsComplete = false,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    }
                }
            });
            await ctx.SaveChangesAsync();
        }

        var logger = provider.GetRequiredService<ILogger<ExternalTodoApiSyncService>>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = provider.GetRequiredService<IOptions<ExternalApiOptions>>();
        var syncService = new ExternalTodoApiSyncService(logger, scopeFactory, options);

        // Run a single sync cycle
        await syncService.RunOneSyncForTests(CancellationToken.None);

        // Local list now has ExternalId and remote has one list created
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            var list = await ctx.TodoList.Include(x => x.Items).SingleAsync();

            list.ExternalId.Should().NotBeNullOrWhiteSpace("sync should link the created remote id back to local");
            list.Name.Should().Be("Local Only");
            list.Items.Should().HaveCount(1);
        }

        var fake = (FakeExternalTodoApiClient)provider.GetRequiredService<IExternalTodoApiClient>();
        var remoteLists = await fake.ListTodoListsAsync(CancellationToken.None);
        remoteLists.Should().HaveCount(1, "sync should have created exactly one remote list");
        remoteLists[0].Name.Should().Be("Local Only");
        remoteLists[0].TodoItems.Should().HaveCount(1);
    }

    [Fact]
    public async Task Sync_imports_remote_list_when_local_db_is_empty()
    {
        /*
         * Testing that an external TodoList is pulled and created locally
         */

        // Arrange: DI and in-memory DB
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());

        var dbName = $"SyncTests-Pull-{Guid.NewGuid()}";
        services.AddDbContext<TodoContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IExternalTodoApiClient, FakeExternalTodoApiClient>();
        services.AddSingleton<IOptions<ExternalApiOptions>>(
            new OptionsWrapper<ExternalApiOptions>(new ExternalApiOptions
            {
                BaseUrl = "http://localhost",
                SyncPeriodSeconds = 3600,
                TimeoutSeconds = 5
            }));

        var provider = services.BuildServiceProvider();

        // Seed external (fake) with one list
        var fake = (FakeExternalTodoApiClient)provider.GetRequiredService<IExternalTodoApiClient>();
        var createdRemote = await fake.CreateTodoListAsync(new ExternalCreateTodoList
        {
            Name = "Remote Only",
            Items = new List<ExternalCreateTodoItem>
            {
                new() { Description = "Remote Task 1", Completed = true }
            }
        }, CancellationToken.None);

        // Sanity: local DB empty
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            (await ctx.TodoList.CountAsync()).Should().Be(0);
        }

        // Setup
        var logger = provider.GetRequiredService<ILogger<ExternalTodoApiSyncService>>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = provider.GetRequiredService<IOptions<ExternalApiOptions>>();
        var syncService = new ExternalTodoApiSyncService(logger, scopeFactory, options);

        // run one sync tick (should import remote list and create a local)
        await syncService.RunOneSyncForTests(CancellationToken.None);

        // Assert local DB now has the imported list with ExternalId and items mirrored
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            var list = await ctx.TodoList.Include(x => x.Items).SingleAsync();

            list.ExternalId.Should().Be(createdRemote.Id);
            list.Name.Should().Be("Remote Only");
            list.Items.Should().HaveCount(1);
            list.Items[0].Name.Should().Be("Remote Task 1");
            list.Items[0].IsComplete.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Sync_links_local_item_to_external_by_sourceId_without_duplication()
    {
        /*
         * Testing that a local TodoItem without ExternalId is linked to an existing external item
         * when the external item's source_id matches the local item's Id. The sync SHOULD NOT create duplicates nor push updates when the external is newer.
         */

        // isolated DI with InMemory DB and fake external client
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());

        var dbName = $"SyncTests-LinkItem-{Guid.NewGuid()}";
        services.AddDbContext<TodoContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IExternalTodoApiClient, FakeExternalTodoApiClient>();
        services.AddSingleton<IOptions<ExternalApiOptions>>(
            new OptionsWrapper<ExternalApiOptions>(new ExternalApiOptions
            {
                BaseUrl = "http://localhost",
                SyncPeriodSeconds = 3600,
                TimeoutSeconds = 5
            }));

        var provider = services.BuildServiceProvider();

        // Seed remote list first (no items yet)
        var fake = (FakeExternalTodoApiClient)provider.GetRequiredService<IExternalTodoApiClient>();
        var remote = await fake.CreateTodoListAsync(new ExternalCreateTodoList
        {
            Name = "Remote List"
        }, CancellationToken.None);

        // Seed local list linked to remote via ExternalId and one item without ExternalId
        long localItemId;
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            var localList = new TodoList
            {
                Name = "Local Linked",
                ExternalId = remote.Id,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
                Items = new List<TodoItem>
                {
                    new TodoItem
                    {
                        Name = "Local Item (no ExternalId)",
                        IsComplete = false,
                        CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                        UpdatedAt = DateTime.UtcNow.AddMinutes(-10) // make it older than external
                    }
                }
            };
            ctx.TodoList.Add(localList);
            await ctx.SaveChangesAsync();

            localItemId = localList.Items.Single().Id;
        }

        // Create external item whose source_id points to the local item Id
        await fake.UpdateTodoItemAsync(remote.Id!, "rem-item-1", new ExternalUpdateTodoItem
        {
            Description = "External Item",
            Completed = true,
            SourceId = localItemId.ToString()
        }, CancellationToken.None);

        var logger = provider.GetRequiredService<ILogger<ExternalTodoApiSyncService>>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = provider.GetRequiredService<IOptions<ExternalApiOptions>>();
        var syncService = new ExternalTodoApiSyncService(logger, scopeFactory, options);

        // run one sync tick
        await syncService.RunOneSyncForTests(CancellationToken.None);

        //  the local item is now linked to the external item by ExternalId and fields were pulled
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            var list = await ctx.TodoList.Include(x => x.Items).SingleAsync();

            list.ExternalId.Should().Be(remote.Id);

            var item = list.Items.Single();
            item.Id.Should().Be(localItemId);
            item.ExternalId.Should().Be("rem-item-1", "sync should link by matching source_id");
            item.Name.Should().Be("External Item", "external is newer so fields are pulled");
            item.IsComplete.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Sync_pushes_list_updates_when_local_is_newer()
    {
        /*
         * Testing that when the local TodoList is newer than the external one (by UpdatedAt),
         * the sync pushes changes to the external API and the remote list reflects local changes.
         */

        // Arrange: DI with InMemory DB and fake external client
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());

        var dbName = $"SyncTests-PushListLww-{Guid.NewGuid()}";
        services.AddDbContext<TodoContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IExternalTodoApiClient, FakeExternalTodoApiClient>();
        services.AddSingleton<IOptions<ExternalApiOptions>>(
            new OptionsWrapper<ExternalApiOptions>(new ExternalApiOptions
            {
                BaseUrl = "http://localhost",
                SyncPeriodSeconds = 3600,
                TimeoutSeconds = 5
            }));

        var provider = services.BuildServiceProvider();

        // Seed external (fake) with one list (remote baseline)
        var fake = (FakeExternalTodoApiClient)provider.GetRequiredService<IExternalTodoApiClient>();
        var remote = await fake.CreateTodoListAsync(new ExternalCreateTodoList
        {
            Name = "Remote Name"
        }, CancellationToken.None);

        // Seed local list linked to external, but with newer UpdatedAt and a different Name
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            ctx.TodoList.Add(new TodoList
            {
                Name = "Local Renamed",
                ExternalId = remote.Id,                 // link to the remote list
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow.AddMinutes(1), // make local strictly newer than remote
                Items = new List<TodoItem>()
            });
            await ctx.SaveChangesAsync();
        }

        // SUT
        var logger = provider.GetRequiredService<ILogger<ExternalTodoApiSyncService>>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = provider.GetRequiredService<IOptions<ExternalApiOptions>>();
        var syncService = new ExternalTodoApiSyncService(logger, scopeFactory, options);

        // Act: run one sync tick
        await syncService.RunOneSyncForTests(CancellationToken.None);

        // Assert: external list should now reflect the local name (push happened)
        var remoteLists = await fake.ListTodoListsAsync(CancellationToken.None);
        remoteLists.Should().HaveCount(1);
        remoteLists[0].Id.Should().Be(remote.Id);
        remoteLists[0].Name.Should().Be("Local Renamed", "local was newer, so sync should push changes to remote");
    }

    [Fact]
    public async Task Sync_pulls_list_updates_when_external_is_newer()
    {
        /*
         * Testing that when the external TodoList is newer than the local one (by UpdatedAt),
         * the sync pulls changes from the external API and the local list reflects remote changes.
         */

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());

        var dbName = $"SyncTests-PullListLww-{Guid.NewGuid()}";
        services.AddDbContext<TodoContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IExternalTodoApiClient, FakeExternalTodoApiClient>();
        services.AddSingleton<IOptions<ExternalApiOptions>>(
            new OptionsWrapper<ExternalApiOptions>(new ExternalApiOptions
            {
                BaseUrl = "http://localhost",
                SyncPeriodSeconds = 3600,
                TimeoutSeconds = 5
            }));

        var provider = services.BuildServiceProvider();

        // Seed external (fake) with one list (remote is newer: its UpdatedAt is "now")
        var fake = (FakeExternalTodoApiClient)provider.GetRequiredService<IExternalTodoApiClient>();
        var remote = await fake.CreateTodoListAsync(new ExternalCreateTodoList
        {
            Name = "Remote Renamed"
        }, CancellationToken.None);

        // Seed local list linked to external, but older and with a different Name
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            ctx.TodoList.Add(new TodoList
            {
                Name = "Local Old",
                ExternalId = remote.Id,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-10), // make local strictly older than remote
                Items = new List<TodoItem>()
            });
            await ctx.SaveChangesAsync();
        }

        var logger = provider.GetRequiredService<ILogger<ExternalTodoApiSyncService>>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = provider.GetRequiredService<IOptions<ExternalApiOptions>>();
        var syncService = new ExternalTodoApiSyncService(logger, scopeFactory, options);

        // Act: run one sync tick
        await syncService.RunOneSyncForTests(CancellationToken.None);

        // Assert: local list should now reflect the remote name (pull happened)
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            var list = await ctx.TodoList.SingleAsync();

            list.Name.Should().Be("Remote Renamed", "external was newer, so sync should pull changes to local");
            list.UpdatedAt.Should().Be(remote.UpdatedAt, "local UpdatedAt is set from external in pull path");
        }
    }

    [Fact]
    public async Task Sync_pushes_item_updates_when_local_item_is_newer()
    {
        /*
         * Testing that when the local TodoItem is newer than the external one (by UpdatedAt),
         * the sync pushes changes to the external API and the remote item reflects local changes.
         */

        // Arrange: DI with InMemory DB and fake external client
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());

        var dbName = $"SyncTests-PushItemLww-{Guid.NewGuid()}";
        services.AddDbContext<TodoContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IExternalTodoApiClient, FakeExternalTodoApiClient>();
        services.AddSingleton<IOptions<ExternalApiOptions>>(
            new OptionsWrapper<ExternalApiOptions>(new ExternalApiOptions
            {
                BaseUrl = "http://localhost",
                SyncPeriodSeconds = 3600,
                TimeoutSeconds = 5
            }));

        var provider = services.BuildServiceProvider();

        // Seed external (fake) with one list and one item (remote baseline: older than local)
        var fake = (FakeExternalTodoApiClient)provider.GetRequiredService<IExternalTodoApiClient>();
        var remoteList = await fake.CreateTodoListAsync(new ExternalCreateTodoList
        {
            Name = "Remote List"
        }, CancellationToken.None);

        // Create a remote item with initial values (will be older)
        var remoteItem = await fake.UpdateTodoItemAsync(remoteList.Id!, "rem-itm-1", new ExternalUpdateTodoItem
        {
            Description = "Remote Original",
            Completed = false,
            SourceId = "src-1"
        }, CancellationToken.None);

        // Seed local list linked to external and a local item with same ExternalId but newer UpdatedAt
        var localNewer = DateTime.UtcNow.AddMinutes(1); // ensure local > remote
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            ctx.TodoList.Add(new TodoList
            {
                Name = "Local List",
                ExternalId = remoteList.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = localNewer,
                Items = new List<TodoItem>
                {
                    new TodoItem
                    {
                        // Make local item strictly newer and with different values
                        Name = "Local Edited",
                        IsComplete = true,
                        ExternalId = remoteItem.Id, // link to remote item
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = localNewer
                    }
                }
            });
            await ctx.SaveChangesAsync();
        }

        // SUT
        var logger = provider.GetRequiredService<ILogger<ExternalTodoApiSyncService>>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = provider.GetRequiredService<IOptions<ExternalApiOptions>>();
        var syncService = new ExternalTodoApiSyncService(logger, scopeFactory, options);

        // Act: run one sync tick (should push item changes)
        await syncService.RunOneSyncForTests(CancellationToken.None);

        // Assert: external item should now reflect local fields (push happened)
        var remoteLists = await fake.ListTodoListsAsync(CancellationToken.None);
        remoteLists.Should().HaveCount(1);
        var reloadedList = remoteLists[0];
        var reloadedItem = reloadedList.TodoItems!.Single(i => i.Id == remoteItem.Id);

        reloadedItem.Description.Should().Be("Local Edited", "local item was newer, so sync should push description");
        reloadedItem.Completed.Should().BeTrue("local item was newer, so sync should push completed flag");
    }

    [Fact]
    public async Task Sync_pulls_item_updates_when_external_item_is_newer()
    {
        /*
         * Testing that when the external TodoItem is newer than the local one,
         * the sync pulls changes into the local item (no remote PATCH).
         */

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());
        var dbName = $"SyncTests-PullItemLww-{Guid.NewGuid()}";
        services.AddDbContext<TodoContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IExternalTodoApiClient, FakeExternalTodoApiClient>();
        services.AddSingleton<IOptions<ExternalApiOptions>>(
            new OptionsWrapper<ExternalApiOptions>(new ExternalApiOptions
            {
                BaseUrl = "http://localhost",
                SyncPeriodSeconds = 3600,
                TimeoutSeconds = 5
            }));

        var provider = services.BuildServiceProvider();

        // Seed remote list and a newer item
        var fake = (FakeExternalTodoApiClient)provider.GetRequiredService<IExternalTodoApiClient>();
        var remoteList = await fake.CreateTodoListAsync(new ExternalCreateTodoList { Name = "Remote List" }, CancellationToken.None);
        var remoteItem = await fake.UpdateTodoItemAsync(remoteList.Id!, "rem-itm-1", new ExternalUpdateTodoItem
        {
            Description = "Remote Newer",
            Completed = true,
            SourceId = "src-x"
        }, CancellationToken.None);

        // Seed local list and an older item with same ExternalId but different values
        var older = DateTime.UtcNow.AddMinutes(-5);
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            ctx.TodoList.Add(new TodoList
            {
                Name = "Local",
                ExternalId = remoteList.Id,
                CreatedAt = older,
                UpdatedAt = older,
                Items = new List<TodoItem>
                {
                    new TodoItem
                    {
                        Name = "Local Old",
                        IsComplete = false,
                        ExternalId = remoteItem.Id,
                        CreatedAt = older,
                        UpdatedAt = older
                    }
                }
            });
            await ctx.SaveChangesAsync();
        }

        var logger = provider.GetRequiredService<ILogger<ExternalTodoApiSyncService>>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = provider.GetRequiredService<IOptions<ExternalApiOptions>>();
        var sut = new ExternalTodoApiSyncService(logger, scopeFactory, options);

        // Act
        await sut.RunOneSyncForTests(CancellationToken.None);

        // local item reflects remote values
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            var list = await ctx.TodoList.Include(l => l.Items).SingleAsync();
            var item = list.Items.Single();

            item.ExternalId.Should().Be(remoteItem.Id);
            item.Name.Should().Be("Remote Newer");
            item.IsComplete.Should().BeTrue();
        }
    }
    
    [Fact]
    public async Task Sync_noop_when_local_and_external_are_equal_keeps_remote_snapshot_unchanged()
    {
        /*
         * Testing that when local and external are equal (names, items, timestamps),
         * the sync emits no Create/Update/Delete calls to the external API.
         * We assert this by taking a deep snapshot of the remote state before/after
         * and verifying they are identical.
         */

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());

        var dbName = $"SyncTests-Noop-{Guid.NewGuid()}";
        services.AddDbContext<TodoContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IExternalTodoApiClient, FakeExternalTodoApiClient>();
        services.AddSingleton<IOptions<ExternalApiOptions>>(
            new OptionsWrapper<ExternalApiOptions>(new ExternalApiOptions
            {
                BaseUrl = "http://localhost",
                SyncPeriodSeconds = 3600,
                TimeoutSeconds = 5
            }));

        var provider = services.BuildServiceProvider();

        // Seed remote with one list and one item
        var client = (FakeExternalTodoApiClient)provider.GetRequiredService<IExternalTodoApiClient>();
        var remote = await client.CreateTodoListAsync(new ExternalCreateTodoList
        {
            Name = "Synced List",
            Items = new List<ExternalCreateTodoItem>
            {
                new() { Description = "Synced Item", Completed = true, SourceId = "1" }
            }
        }, CancellationToken.None);

        // Take deep snapshot BEFORE
        var before = await client.ListTodoListsAsync(CancellationToken.None);
        var jsonBefore = System.Text.Json.JsonSerializer.Serialize(before);

        // Seed local with the same data and timestamps
        var snapshot = before.Single();
        var extItem = snapshot.TodoItems!.Single();

        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            ctx.TodoList.Add(new TodoList
            {
                Name = snapshot.Name!,
                ExternalId = snapshot.Id,
                CreatedAt = snapshot.CreatedAt ?? DateTime.UtcNow,
                UpdatedAt = snapshot.UpdatedAt ?? DateTime.UtcNow,
                Items = new List<TodoItem>
                {
                    new TodoItem
                    {
                        Name = extItem.Description!,
                        IsComplete = extItem.Completed ?? false,
                        ExternalId = extItem.Id,
                        CreatedAt = extItem.CreatedAt ?? DateTime.UtcNow,
                        UpdatedAt = extItem.UpdatedAt ?? DateTime.UtcNow
                    }
                }
            });
            await ctx.SaveChangesAsync();
        }

        var logger = provider.GetRequiredService<ILogger<ExternalTodoApiSyncService>>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = provider.GetRequiredService<IOptions<ExternalApiOptions>>();
        var sut = new ExternalTodoApiSyncService(logger, scopeFactory, options);

        // run sync (should be a no-op)
        await sut.RunOneSyncForTests(CancellationToken.None);

        // remote state did not change
        var after = await client.ListTodoListsAsync(CancellationToken.None);
        var jsonAfter = System.Text.Json.JsonSerializer.Serialize(after);

        jsonAfter.Should().Be(jsonBefore, "no remote writes should happen when local and external are identical");

        // And local remains identical to remote
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            var list = await ctx.TodoList.Include(l => l.Items).SingleAsync();

            list.Name.Should().Be(snapshot.Name);
            var item = list.Items.Single();
            item.Name.Should().Be(extItem.Description);
            item.IsComplete.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Sync_deletes_remote_item_when_local_item_marked_deleted()
    {
        /*
         * Testing that when a local TodoItem has DeletedAt set and ExternalId present,
         * the sync calls DELETE on the external API and the item disappears from the remote list.
         */

        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());
        var dbName = $"SyncTests-DelItem-{Guid.NewGuid()}";
        services.AddDbContext<TodoContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IExternalTodoApiClient, FakeExternalTodoApiClient>();
        services.AddSingleton<IOptions<ExternalApiOptions>>(
            new OptionsWrapper<ExternalApiOptions>(new ExternalApiOptions
            {
                BaseUrl = "http://localhost",
                SyncPeriodSeconds = 3600,
                TimeoutSeconds = 5
            }));

        var provider = services.BuildServiceProvider();

        // Seed remote list + item
        var client = (FakeExternalTodoApiClient)provider.GetRequiredService<IExternalTodoApiClient>();
        var remoteList = await client.CreateTodoListAsync(new ExternalCreateTodoList
        {
            Name = "List A",
            Items = new List<ExternalCreateTodoItem>()
        }, CancellationToken.None);

        var remoteItem = await client.UpdateTodoItemAsync(remoteList.Id!, "rem-itm-1", new ExternalUpdateTodoItem
        {
            Description = "To delete",
            Completed = false,
            SourceId = "src-1"
        }, CancellationToken.None);

        // Seed local: same list, item with DeletedAt set
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            ctx.TodoList.Add(new TodoList
            {
                Name = "List A",
                ExternalId = remoteList.Id,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
                Items = new List<TodoItem>
                {
                    new TodoItem
                    {
                        Name = "Local to delete",
                        IsComplete = false,
                        ExternalId = remoteItem.Id,
                        CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                        UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
                        DeletedAt = DateTime.UtcNow
                    }
                }
            });
            await ctx.SaveChangesAsync();
        }

        var logger = provider.GetRequiredService<ILogger<ExternalTodoApiSyncService>>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = provider.GetRequiredService<IOptions<ExternalApiOptions>>();
        var sut = new ExternalTodoApiSyncService(logger, scopeFactory, options);

        // Act
        await sut.RunOneSyncForTests(CancellationToken.None);

        // Assert: remote item was deleted
        var snapshot = (await client.ListTodoListsAsync(CancellationToken.None)).Single();
        snapshot.TodoItems!.Any(i => i.Id == remoteItem.Id).Should().BeFalse("deleted local item must be deleted remotely as well");
    }

    [Fact]
    public async Task Sync_deletes_remote_list_when_local_list_marked_deleted()
    {
        /*
         * Testing that when a local TodoList has DeletedAt set and ExternalId present,
         * the sync calls DELETE on the external API and the list disappears from the remote snapshot.
         */

        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());
        var dbName = $"SyncTests-DelList-{Guid.NewGuid()}";
        services.AddDbContext<TodoContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IExternalTodoApiClient, FakeExternalTodoApiClient>();
        services.AddSingleton<IOptions<ExternalApiOptions>>(
            new OptionsWrapper<ExternalApiOptions>(new ExternalApiOptions
            {
                BaseUrl = "http://localhost",
                SyncPeriodSeconds = 3600,
                TimeoutSeconds = 5
            }));

        var provider = services.BuildServiceProvider();

        // Seed remote list
        var client = (FakeExternalTodoApiClient)provider.GetRequiredService<IExternalTodoApiClient>();
        var remoteList = await client.CreateTodoListAsync(new ExternalCreateTodoList
        {
            Name = "List To Delete",
            Items = new List<ExternalCreateTodoItem>
            {
                new() { Description = "Won't matter", Completed = false }
            }
        }, CancellationToken.None);

        // Seed local: same list with DeletedAt set
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            ctx.TodoList.Add(new TodoList
            {
                Name = "List To Delete",
                ExternalId = remoteList.Id,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
                DeletedAt = DateTime.UtcNow, // mark list as deleted
                Items = new List<TodoItem>()
            });
            await ctx.SaveChangesAsync();
        }

        var logger = provider.GetRequiredService<ILogger<ExternalTodoApiSyncService>>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = provider.GetRequiredService<IOptions<ExternalApiOptions>>();
        var sut = new ExternalTodoApiSyncService(logger, scopeFactory, options);

        // Act
        await sut.RunOneSyncForTests(CancellationToken.None);

        // Assert: remote list was deleted
        var snapshot = await client.ListTodoListsAsync(CancellationToken.None);
        snapshot.Any(l => l.Id == remoteList.Id).Should().BeFalse("deleted local list must be deleted remotely as well");
    }

    [Fact]
    public async Task Sync_does_not_create_remote_for_deleted_local_list_without_externalId()
    {
        /*
         * Testing that a local TodoList marked as deleted and without ExternalId
         * is not created in the external API during the sync.
         */

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());
        var dbName = $"SyncTests-DelSkipCreate-{Guid.NewGuid()}";
        services.AddDbContext<TodoContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IExternalTodoApiClient, FakeExternalTodoApiClient>();
        services.AddSingleton<IOptions<ExternalApiOptions>>(
            new OptionsWrapper<ExternalApiOptions>(new ExternalApiOptions
            {
                BaseUrl = "http://localhost",
                SyncPeriodSeconds = 3600,
                TimeoutSeconds = 5
            }));

        var provider = services.BuildServiceProvider();

        // Seed local: deleted list without ExternalId
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
            ctx.TodoList.Add(new TodoList
            {
                Name = "Deleted Local Only",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
                DeletedAt = DateTime.UtcNow, // marked as deleted
                Items = new List<TodoItem>()
            });
            await ctx.SaveChangesAsync();
        }

        var logger = provider.GetRequiredService<ILogger<ExternalTodoApiSyncService>>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = provider.GetRequiredService<IOptions<ExternalApiOptions>>();
        var sut = new ExternalTodoApiSyncService(logger, scopeFactory, options);

        // Act
        await sut.RunOneSyncForTests(CancellationToken.None);

        // Assert: no remote list was created
        var client = provider.GetRequiredService<IExternalTodoApiClient>();
        var snapshot = await client.ListTodoListsAsync(CancellationToken.None);
        snapshot.Should().BeEmpty("deleted local lists without ExternalId should not be created remotely");
    }
}
