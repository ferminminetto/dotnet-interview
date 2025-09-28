using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TodoApi.Integration;
using TodoApi.Models;
using TodoApi.Mapping;
using TodoApi.Options; // Use centralized mappers

namespace TodoApi.Sync;

public sealed class ExternalTodoApiSyncService(
    ILogger<ExternalTodoApiSyncService> logger,
    IServiceScopeFactory scopeFactory,
    IOptions<ExternalApiOptions> options) : BackgroundService
{
    private readonly ILogger<ExternalTodoApiSyncService> _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ExternalApiOptions _options = options.Value;

    /*
     * This service runs periodically, every N seconds (configurable).
     * Each execution performs a full synchronization cycle.
    */
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ExternalTodoApiSyncService Started. Elapsed Time: {Seconds}s", _options.SyncPeriodSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, _options.SyncPeriodSeconds)));

        // First execution.
        await SafeSyncTick(stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SafeSyncTick(stoppingToken);
        }
    }

    /*
     * Safe wrapper to log and ignore exceptions per tick.
    */
    private async Task SafeSyncTick(CancellationToken ct)
    {
        try
        {
            await SyncOnce(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Service stopped.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed.");
        }
    }

    // Test-only wrapper to run a single sync tick from unit tests.
    internal Task RunOneSyncForTests(CancellationToken ct) => SyncOnce(ct);

    // One Cycle: Pull (externo/local) → Reconcile → Push/Apply
    private async Task SyncOnce(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TodoContext>();
        var client = scope.ServiceProvider.GetRequiredService<IExternalTodoApiClient>();

        _logger.LogInformation("Sync: loading external lists");
        var externalLists = await client.ListTodoListsAsync(ct);

        _logger.LogInformation("Sync: loading local lists");
        var localLists = await context.TodoList
            .Include(t => t.Items)
            .ToListAsync(ct);

        // External: Create indexes using Dictionaries so we can do fast lookups later.
        var extById = externalLists.Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .ToDictionary(x => x.Id!, x => x);
        var extBySourceId = externalLists.Where(x => !string.IsNullOrWhiteSpace(x.SourceId))
            .ToDictionary(x => x.SourceId!, x => x);

        // Same indexing for internal lists.
        var localByExternalId = localLists.Where(x => !string.IsNullOrWhiteSpace(x.ExternalId))
            .ToDictionary(x => x.ExternalId!, x => x);
        var localById = localLists.ToDictionary(x => x.Id, x => x);

        // If one of the lists has ExternalId but the other doesn't, we can link them by SourceId/Id.
        foreach (var list in localLists.Where(l => string.IsNullOrWhiteSpace(l.ExternalId)))
        {
            if (extBySourceId.TryGetValue(list.Id.ToString(), out var ext))
            {
                list.ExternalId = ext.Id;
                list.UpdatedAt = DateTime.UtcNow;
            }
        }

        // Create locally items that only exist externally.
        foreach (var ext in externalLists)
        {
            TodoList? linkedLocal = null;

            // Match by ExternalId first
            if (!string.IsNullOrWhiteSpace(ext.Id) &&
                localByExternalId.TryGetValue(ext.Id!, out var byExt))
            {
                linkedLocal = byExt;
            }
            // Match by SourceId/Id if still not matched
            else if (!string.IsNullOrWhiteSpace(ext.SourceId) &&
                     long.TryParse(ext.SourceId, out var sid) &&
                     localById.TryGetValue(sid, out var bySource))
            {
                linkedLocal = bySource;
            }

            if (linkedLocal is null)
            {
                var newList = TodoListMapper.ToEntity(ext);
                context.TodoList.Add(newList);
            }
        }

        // Create remotely the lists that exist locally but not externally (use mapper).
        foreach (var local in localLists)
        {
            var existsRemotely =
                (!string.IsNullOrWhiteSpace(local.ExternalId) && extById.ContainsKey(local.ExternalId!))
                || extBySourceId.ContainsKey(local.Id.ToString());

            if (!existsRemotely)
            {
                var body = TodoListMapper.ToCreateBody(local);
                var created = await client.CreateTodoListAsync(body, ct);

                // Link ExternalId from response
                local.ExternalId = created.Id;
                local.UpdatedAt = DateTime.UtcNow;
            }
        }

        // Updates using last-write-wins (lists)
        foreach (var local in localLists.Where(l => !string.IsNullOrWhiteSpace(l.ExternalId)))
        {
            if (!extById.TryGetValue(local.ExternalId!, out var ext))
                continue;

            var extUpdated = ext.UpdatedAt ?? DateTime.MinValue;
            var localUpdated = local.UpdatedAt;

            if (localUpdated > extUpdated)
            {
                // Push list fields (use mapper for body)
                await client.UpdateTodoListAsync(ext.Id!, TodoListMapper.ToUpdateBody(local), ct);
            }
            else if (extUpdated > localUpdated)
            {
                // Pull external changes into local
                local.Name = ext.Name ?? local.Name;
                local.UpdatedAt = extUpdated;
            }

            // Items LWW
            var extItems = ext.TodoItems ?? [];
            var extItemsById = extItems.Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .ToDictionary(i => i.Id!, i => i);

            var localItemsByExtId = local.Items.Where(i => !string.IsNullOrWhiteSpace(i.ExternalId))
                .ToDictionary(i => i.ExternalId!, i => i);

            // Link by source_id when local item has no ExternalId
            foreach (var li in local.Items.Where(i => string.IsNullOrWhiteSpace(i.ExternalId)))
            {
                var match = extItems.FirstOrDefault(i => i.SourceId == li.Id.ToString());
                if (match is not null) li.ExternalId = match.Id;
            }

            // Create locally the items that only exist externally (use mapper)
            foreach (var extItem in extItems)
            {
                var matchLocal = (!string.IsNullOrWhiteSpace(extItem.Id) && localItemsByExtId.TryGetValue(extItem.Id!, out var byExtItem))
                    ? byExtItem
                    : local.Items.FirstOrDefault(i => extItem.SourceId == i.Id.ToString());

                if (matchLocal is null)
                {
                    local.Items.Add(TodoItemMapper.ToEntity(extItem));
                }
            }

            // Push item updates where local is newer
            foreach (var li in local.Items.Where(i => !string.IsNullOrWhiteSpace(i.ExternalId)))
            {
                var hasExt = extItemsById.TryGetValue(li.ExternalId!, out var ei);
                if (!hasExt) continue;

                var liUpdated = li.UpdatedAt ?? DateTime.MinValue;
                var eiUpdated = ei.UpdatedAt ?? DateTime.MinValue;

                if (liUpdated > eiUpdated)
                {
                    await client.UpdateTodoItemAsync(ext.Id!, ei.Id!, TodoItemMapper.ToUpdateBody(li), ct);
                }
                else if (eiUpdated > liUpdated)
                {
                    li.Name = ei.Description ?? li.Name;
                    li.IsComplete = ei.Completed ?? li.IsComplete;
                    li.UpdatedAt = eiUpdated;
                }
            }

            // Optional: deletions policy (compare collections and decide where to delete)
        }

        // Persist local changes
        await context.SaveChangesAsync(ct);

        _logger.LogInformation("Sync: completed. Local lists: {LocalCount} | External lists: {ExternalCount}",
            localLists.Count, externalLists.Count);
    }
}