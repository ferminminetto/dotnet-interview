using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;
using TodoApi.Integration;
using TodoApi.Models;
using TodoApi.Mapping;
using TodoApi.Options;

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

    // One Cycle: Pull (external/local) → Reconcile → Push/Apply
    private async Task SyncOnce(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TodoContext>();
        var client = scope.ServiceProvider.GetRequiredService<IExternalTodoApiClient>();

        // Pull phase
        var externalLists = await LoadExternalAsync(client, ct);
        var localLists = await LoadLocalAsync(context, ct);

        // Build indexes for fast matching
        var (extById, extBySourceId) = BuildExternalIndexes(externalLists);
        var (localByExternalId, localById) = BuildLocalIndexes(localLists);

        // Reconcile linking (by source_id)
        LinkListsBySourceId(localLists, extBySourceId);

        // Graceful deletes (local → remote) before any create/update
        await DeleteMarkedAsync(client, localLists, ct);

        // Create missing local entities based on external snapshot
        CreateMissingLocal(context, externalLists, localByExternalId, localById);

        // Create missing remote entities based on local snapshot (skip deleted)
        await CreateMissingRemoteAsync(client, localLists, extById, extBySourceId, ct);

        // Apply updates both ways (lists and items) with LWW (Last Writer Wins) strategy
        await SyncListsAndItemsAsync(client, localLists, extById, ct);

        // Persist changes
        await context.SaveChangesAsync(ct);

        _logger.LogInformation("Sync: completed. Local lists: {LocalCount} | External lists: {ExternalCount}",
            localLists.Count, externalLists.Count);
    }

    private static async Task<List<ExternalTodoList>> LoadExternalAsync(IExternalTodoApiClient client, CancellationToken ct)
        => await client.ListTodoListsAsync(ct);

    private static async Task<List<TodoList>> LoadLocalAsync(TodoContext context, CancellationToken ct)
        => await context.TodoList.Include(t => t.Items).ToListAsync(ct);

    // Build both external indexes in a single pass
    private static (Dictionary<string, ExternalTodoList> byId, Dictionary<string, ExternalTodoList> bySourceId)
        BuildExternalIndexes(List<ExternalTodoList> externalLists)
    {
        var byId = new Dictionary<string, ExternalTodoList>(StringComparer.Ordinal);
        var bySourceId = new Dictionary<string, ExternalTodoList>(StringComparer.Ordinal);
        foreach (var e in externalLists)
        {
            if (!string.IsNullOrWhiteSpace(e.Id)) byId[e.Id!] = e;
            if (!string.IsNullOrWhiteSpace(e.SourceId)) bySourceId[e.SourceId!] = e;
        }
        return (byId, bySourceId);
    }

    // Build both local indexes in a single pass
    private static (Dictionary<string, TodoList> byExternalId, Dictionary<long, TodoList> byId)
        BuildLocalIndexes(List<TodoList> localLists)
    {
        var byExternalId = new Dictionary<string, TodoList>(StringComparer.Ordinal);
        var byId = new Dictionary<long, TodoList>();
        foreach (var l in localLists)
        {
            byId[l.Id] = l;
            if (!string.IsNullOrWhiteSpace(l.ExternalId)) byExternalId[l.ExternalId!] = l;
        }
        return (byExternalId, byId);
    }

    private static void LinkListsBySourceId(List<TodoList> localLists, Dictionary<string, ExternalTodoList> extBySourceId)
    {
        // If one of the lists has ExternalId but the other doesn't, link them by SourceId/Id.
        foreach (var list in localLists.Where(l => string.IsNullOrWhiteSpace(l.ExternalId)))
        {
            if (extBySourceId.TryGetValue(list.Id.ToString(), out var ext))
            {
                list.ExternalId = ext.Id;
                list.UpdatedAt = DateTime.UtcNow;
            }
        }
    }

    // Graceful deletes: local → remote
    private async Task DeleteMarkedAsync(IExternalTodoApiClient client, List<TodoList> localLists, CancellationToken ct)
    {
        // Delete lists marked as deleted (skip items of a deleted list)
        foreach (var list in localLists.Where(l => IsDeleted(l) && !string.IsNullOrWhiteSpace(l.ExternalId)))
        {
            try
            {
                await client.DeleteTodoListAsync(list.ExternalId!, ct);
                _logger.LogInformation("Remote delete list succeeded. LocalId={LocalId} ExternalId={ExternalId}", list.Id, list.ExternalId);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Treat 404 as success (idempotent)
                _logger.LogInformation("Remote delete list idempotent (404). LocalId={LocalId} ExternalId={ExternalId}", list.Id, list.ExternalId);
            }
            catch (Exception ex)
            {
                // Partial failure: keep tombstone and continue
                _logger.LogWarning(ex, "Remote delete list failed. LocalId={LocalId} ExternalId={ExternalId}", list.Id, list.ExternalId);
            }
        }

        // Delete items marked as deleted (only for lists not deleted)
        foreach (var list in localLists.Where(l => !IsDeleted(l) && !string.IsNullOrWhiteSpace(l.ExternalId)))
        {
            foreach (var item in list.Items.Where(i => IsDeleted(i) && !string.IsNullOrWhiteSpace(i.ExternalId)))
            {
                try
                {
                    await client.DeleteTodoItemAsync(list.ExternalId!, item.ExternalId!, ct);
                    _logger.LogInformation("Remote delete item succeeded. ListExtId={ListExtId} ItemExtId={ItemExtId}", list.ExternalId, item.ExternalId);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogInformation("Remote delete item idempotent (404). ListExtId={ListExtId} ItemExtId={ItemExtId}", list.ExternalId, item.ExternalId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Remote delete item failed. ListExtId={ListExtId} ItemExtId={ItemExtId}", list.ExternalId, item.ExternalId);
                }
            }
        }
    }

    private static bool IsDeleted(TodoList l) => l.DeletedAt != default;
    private static bool IsDeleted(TodoItem i) => i.DeletedAt.HasValue;

    private static void CreateMissingLocal(
        TodoContext context,
        List<ExternalTodoList> externalLists,
        Dictionary<string, TodoList> localByExternalId,
        Dictionary<long, TodoList> localById)
    {
        foreach (var ext in externalLists)
        {
            TodoList? linkedLocal = null;

            if (!string.IsNullOrWhiteSpace(ext.Id) &&
                localByExternalId.TryGetValue(ext.Id!, out var byExt))
            {
                linkedLocal = byExt;
            }
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
    }

    private static async Task CreateMissingRemoteAsync(
        IExternalTodoApiClient client,
        List<TodoList> localLists,
        Dictionary<string, ExternalTodoList> extById,
        Dictionary<string, ExternalTodoList> extBySourceId,
        CancellationToken ct)
    {
        foreach (var local in localLists)
        {
            // Do not create remotely if local is marked as deleted
            if (IsDeleted(local)) continue;

            var existsRemotely =
                (!string.IsNullOrWhiteSpace(local.ExternalId) && extById.ContainsKey(local.ExternalId!))
                || extBySourceId.ContainsKey(local.Id.ToString());

            if (!existsRemotely)
            {
                var body = TodoListMapper.ToCreateBody(local);
                var created = await client.CreateTodoListAsync(body, ct);

                local.ExternalId = created.Id;
                local.UpdatedAt = DateTime.UtcNow;
            }
        }
    }

    private static async Task SyncListsAndItemsAsync(
        IExternalTodoApiClient client,
        List<TodoList> localLists,
        Dictionary<string, ExternalTodoList> extById,
        CancellationToken ct)
    {
        foreach (var local in localLists.Where(l => !string.IsNullOrWhiteSpace(l.ExternalId)))
        {
            // Skip LWW if list is marked as deleted (delete path already handled)
            if (IsDeleted(local)) continue;

            if (!extById.TryGetValue(local.ExternalId!, out var ext))
                continue;

            // LWW for List
            var extUpdated = ext.UpdatedAt ?? DateTime.MinValue;
            var localUpdated = local.UpdatedAt;

            if (localUpdated > extUpdated)
            {
                await client.UpdateTodoListAsync(ext.Id!, TodoListMapper.ToUpdateBody(local), ct);
            }
            else if (extUpdated > localUpdated)
            {
                local.Name = ext.Name ?? local.Name;
                local.UpdatedAt = extUpdated;
            }

            // Items LWW and reconciliation
            await SyncItemsLwwAndReconcileAsync(local, ext, client, ct);
        }
    }

    // Make it async and avoid O(n^2) linking by building extItemsBySourceId
    private static async Task SyncItemsLwwAndReconcileAsync(
        TodoList local,
        ExternalTodoList ext,
        IExternalTodoApiClient client,
        CancellationToken ct)
    {
        var extItems = ext.TodoItems ?? [];
        var extItemsById = new Dictionary<string, ExternalTodoItem>(StringComparer.Ordinal);
        var extItemsBySourceId = new Dictionary<string, ExternalTodoItem>(StringComparer.Ordinal);
        foreach (var ei in extItems)
        {
            if (!string.IsNullOrWhiteSpace(ei.Id)) extItemsById[ei.Id!] = ei;
            if (!string.IsNullOrWhiteSpace(ei.SourceId)) extItemsBySourceId[ei.SourceId!] = ei;
        }

        // Indexes O(1) for local items
        var localActive = local.Items.Where(i => !IsDeleted(i)).ToList();
        var localItemsByExtId = localActive.Where(i => !string.IsNullOrWhiteSpace(i.ExternalId))
            .ToDictionary(i => i.ExternalId!, i => i, StringComparer.Ordinal);

        // Hashsets for Id and SourceId of all local items (includes tombstones)
        var localIds = new HashSet<string>(local.Items.Select(i => i.Id.ToString()), StringComparer.Ordinal);

        // Link for source_id when local item has no ExternalId
        foreach (var li in localActive.Where(i => string.IsNullOrWhiteSpace(i.ExternalId)))
        {
            if (extItemsBySourceId.TryGetValue(li.Id.ToString(), out var match))
            {
                li.ExternalId = match.Id;
            }
        }

        // Create locally the items that exist only externally (avoids O(n^2))
        foreach (var extItem in extItems)
        {
            var matched =
                (!string.IsNullOrWhiteSpace(extItem.Id) && localItemsByExtId.ContainsKey(extItem.Id!)) ||
                (!string.IsNullOrWhiteSpace(extItem.SourceId) && localIds.Contains(extItem.SourceId!)); // incluye tombstones

            if (!matched)
            {
                local.Items.Add(TodoItemMapper.ToEntity(extItem));
            }
        }

        // Push/Pull for items (LWW)
        await PushPullItemsAsync(local, extItemsById, client, ext.Id!, ct);
    }

    private static async Task PushPullItemsAsync(
        TodoList local,
        Dictionary<string, ExternalTodoItem> extItemsById,
        IExternalTodoApiClient client,
        string externalListId,
        CancellationToken ct)
    {
        foreach (var li in local.Items.Where(i => !IsDeleted(i) && !string.IsNullOrWhiteSpace(i.ExternalId)))
        {
            var hasExt = extItemsById.TryGetValue(li.ExternalId!, out var ei);
            if (!hasExt || ei is null) continue;

            var liUpdated = li.UpdatedAt ?? DateTime.MinValue;
            var eiUpdated = ei.UpdatedAt ?? DateTime.MinValue;

            if (liUpdated > eiUpdated)
            {
                await client.UpdateTodoItemAsync(externalListId, ei.Id!, TodoItemMapper.ToUpdateBody(li), ct);
            }
            else if (eiUpdated > liUpdated)
            {
                li.Name = ei.Description ?? li.Name;
                li.IsComplete = ei.Completed ?? li.IsComplete;
                li.UpdatedAt = eiUpdated;
            }
        }
    }
}