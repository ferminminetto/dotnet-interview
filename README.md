# Setup
Recommenden hybrid approach: run the DB in a container via docker and the app locally.
- Clone the repository.
- Navigate to `.devcontainer/` and run `docker-compose up sqlserver`
- Run the app locally (e.g. via Visual Studio or `dotnet run`).
- *RECOMMENDED*: Explore tests in `TodoApi.Tests` project to understand how the Sync service is used.

# Design and Reasoning for TodoApi Synchronization

## 1. High-Level Overview

The solution implements a background synchronization service (`ExternalTodoApiSyncService`) that periodically reconciles data between the local database and an external API. Each sync cycle includes:
- Loading lists and items from both sources.
- Efficient indexing for O(1) matching.
- Reconciliation and linking by identifiers.
- Propagation of changes (create, update, delete) in both directions.
- Persisting local changes.

The architecture uses a `BackgroundService` that runs the sync cycle every N seconds, configurable via options.

## 2. Key Design Decisions

- **Periodic sync:** A simple timer is used instead of queues or event-driven approaches, prioritizing clarity and predictability.
- **Dictionary/HashSet indexing:** To avoid O(n^2) lookups and minimize unnecessary API calls.
- **Last Writer Wins (LWW):** Conflict resolution between local and external changes is based on the most recent timestamp.
- **Decoupled configuration:** HTTP client resilience is configured via extension methods and the new .NET 8 pipeline (`AddStandardResilienceHandler`), keeping `Program.cs` clean.
- **Fake client for testing:** Enables development and testing without relying on the external API.
- **Error handling:** Each operation is wrapped in try-catch blocks to ensure the sync process continues despite individual failures. Since it will be executing in a background service each x seconds (configurable), it is crucial that one failure does not halt the entire sync process.

## 3. Resilience and Error Handling

- **Retries and circuit breaker:** The HTTP client uses standard resilience policies (retries, global timeout, circuit breaker) to handle transient failures and prevent overwhelming the external system.
- **Partial failure handling:** Each critical operation (delete, update, create) catches and logs exceptions, allowing the sync cycle to continue with remaining data.
- **Detailed logging:** Errors and key events are logged for diagnostics.

## 4. Edge Cases

- **Idempotent deletes:** Attempts to delete entities that no longer exist externally (404) are treated as successful.
- **Tombstones:** Locally deleted items and lists are kept as tombstones until remote deletion is confirmed.
- **SourceId linking:** If a local entity lacks an `ExternalId`, it is linked by `SourceId` to avoid duplicates.
- **Update conflicts:** Resolved via LWW, preventing overwriting of more recent changes.

## 5. Areas for Improvement

-  **Performance and scalability**: The current implementation is performant in the main algorithm using indexes but loads all lists and items from both the local and external sources on every sync cycle. This approach is simple but does not scale well for large datasets. In a real production scenario we should: 
- - Implement incremental sync, fetching only records changed since the last sync (using timestamps, change tracking, or webhooks).
- - Use pagination when querying the external API to avoid loading everything at once.
- - Consider batching updates and deletes to reduce API calls and database load.
- **Monitoring and alerts:** Add active monitoring to detect and notify about persistent failures.

## 6. Assumptions

- Identifiers (`Id`, `SourceId`, `ExternalId`) are unique and consistent.
- Both local and external systems may have concurrent changes; LWW is sufficient for conflict resolution.
- The sync cycle can run in parallel without risk of data corruption.