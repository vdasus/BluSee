---
tags: [claude, dotnet, standards]
---

## Stack
- .NET last LTS (10+) / C# last (14+)
- ASP.NET Core when needed
- EF Core (writes, complex aggregates)
- Dapper (reads, hot paths, projections)
- SQLite
- Serilog -> json, configurable by config file
- xUnit, Autofixture, NSubstitute, FluentAssertions

# .NET Coding Standards

Reusable standards for AI assistants on .NET backend projects.
Project-specific overrides live in the project's `CLAUDE.md`.

Composition root: `Program.cs` + `CompositionRoot.cs`.

## Architecture

### Layers (Clean Architecture)
```
Domain/                # Entities, ValueObjects, domain services. No external deps.
ApplicationServices/   # Use cases. One folder per feature: *Service, *Request, *Response.
Infrastructure/        # EF, Dapper, HTTP, brokers, file I/O. Implements interfaces from inner layers.
Api/                   # Composition root, controllers, middleware.
```

### Dependency direction
- Inner layers know nothing about outer.
- `Domain` -> no deps (except `CSharpFunctionalExtensions` and validation libs).
- `ApplicationServices` -> `Domain` + interfaces it defines for Infrastructure.
- `Infrastructure` -> implements interfaces from `Domain` / `ApplicationServices`.
- `Api` -> composes everything (only place where concretes are wired).

## Code style

### Primary constructors (C# 12+)
```csharp
// CORRECT
public class OrderService(ILogger<OrderService> logger, IOrderRepository repo) : IOrderService
{
    public async Task<Result<Order>> GetAsync(OrderId id, CancellationToken ct)
        => await repo.FindAsync(id, ct);
}
```

### Naming
| Element        | Convention                            |
|----------------|---------------------------------------|
| Interfaces     | `I` prefix (`IOrderService`)          |
| Private fields | `_camelCase` (`_logger`)              |
| Methods        | `PascalCase`, async suffix `Async`    |
| Parameters     | `camelCase`                           |
| Constants      | `UPPER_SNAKE_CASE` (NEW_ID)           |
| Exception var  | `ex`                                  |

### Comments
- Self-documenting code first. Comment **why**, not **what**.
- `//TODO` — future work, no urgency.
- `//ASAP` — must address before next release.
- Annotate non-obvious regex with the pattern intent.

## Result\<T> and Railway-Oriented Programming

Use `CSharpFunctionalExtensions.Result<T>` for expected failures. Throw only for unexpected.

```csharp
public async Task<Result<Order>> GetAsync(OrderId id, CancellationToken ct)
{
    try
    {
        var order = await repo.FindAsync(id, ct);
        return order is null
            ? Result.Failure<Order>($"Order {id} not found")
            : Result.Success(order);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to load order {OrderId}", id);
        return Result.Failure<Order>(ex.Message);
    }
}

// Chaining
return await GetAsync(id, ct)
    .Ensure(o => o.IsActive, "Order is inactive")
    .Map(o => o.WithDiscount(0.1m))
    .Bind(o => SaveAsync(o, ct));
```

Rules:
- Methods returning `Result<T>` MUST NOT throw for business failures.
- Methods NOT returning `Result<T>` MAY throw — caller treats as exceptional.
- Always log exceptions before converting to `Result.Failure`.

## Async / Await

- Suffix async methods with `Async`.
- Never `async void` except event handlers.
- Always accept and propagate `CancellationToken`.
- `ConfigureAwait(false)` **only in shared library code** that may be consumed outside ASP.NET Core. ASP.NET Core 8+ has no `SynchronizationContext`; it's noise in application code there.
- Never `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` (deadlock + thread starvation).

```csharp
public async Task<Result<Data>> ExecuteAsync(string input, CancellationToken ct)
{
    var data = await service.RunAsync(input, ct);  // ct propagated
    return Result.Success(data);
}
```

## Logging (Serilog, structured)

```csharp
// CORRECT — placeholders, structured fields
logger.LogInformation("Processing order {OrderId} for {CustomerId}", orderId, customerId);
logger.LogError(ex, "Failed to process {OrderId}", orderId);

// WRONG — interpolation kills structured logging
logger.LogInformation($"Processing order {orderId}");
```

Levels:
- `Trace` — verbose diagnostic, dev only
- `Debug` — flow detail, dev / staging
- `Information` — meaningful business events
- `Warning` — unexpected but handled
- `Error` — failure with recovery
- `Critical` — application unable to continue

Never log: secrets, tokens, full PII, full request bodies for sensitive endpoints.
Configure mostly in config file, not in code

## Dependency Injection

```csharp
// In CompositionRoot
services.AddSingleton<IClock, SystemClock>();
services.AddScoped<IOrderRepository, OrderRepository>();
services.AddTransient<IOrderService, OrderService>();
services.Configure<DbOptions>(config.GetSection("Database"));  // IOptions<T>
```

Lifetime guide:
- **Singleton** — stateless, thread-safe (configuration, clocks, factories)
- **Scoped** — per-request (DbContext, unit-of-work, request-scoped repos)
- **Transient** — short-lived; may depend on scoped

Configuration:
- `IOptions<T>` — singleton snapshot at startup
- `IOptionsSnapshot<T>` — per-request rebind
- `IOptionsMonitor<T>` — live reload with change notifications

## File I/O

Use `System.IO.Abstractions.IFileSystem` everywhere. Direct `File.*` is forbidden outside `Program.cs`.

```csharp
public class ReportWriter(IFileSystem fs) : IReportWriter
{
    public Result Write(string path, string content)
    {
        try { fs.File.WriteAllText(path, content); return Result.Success(); }
        catch (Exception ex) { return Result.Failure(ex.Message); }
    }
}
```

## Database

### EF Core (writes, complex aggregates)
- DbContext lifetime is **Scoped**.
- Migrations append-only; never edit existing.
- No `IQueryable` leaking out of repository — return materialized collections or domain types.
- Use `AsNoTracking()` for read-only queries.
- Avoid lazy loading; use explicit `Include` or projection.

### Dapper (reads, hot paths, projections)
```csharp
public async Task<Result<IReadOnlyList<OrderRow>>> GetRowsAsync(Guid customerId, CancellationToken ct)
{
    try
    {
        await using var conn = await connectionFactory.CreateAsync(ct);
        var rows = await conn.QueryAsync<OrderRow>(new CommandDefinition(
            Sql.GetOrders, new { customerId }, cancellationToken: ct));
        return Result.Success<IReadOnlyList<OrderRow>>(rows.ToList());
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to load orders for {CustomerId}", customerId);
        return Result.Failure<IReadOnlyList<OrderRow>>(ex.Message);
    }
}
```
- Always parameterized.
- `await using` for connections.
- SQL in `const string` constants or `.sql` resource files, not inline at call sites.

## Domain modeling

### Entities
- Have identity (`Id`).
- Mutable through methods, never public setters.
- Enforce invariants in methods.

```csharp
public sealed class Order
{
    public OrderId Id { get; }
    public OrderStatus Status { get; private set; }

    private Order(OrderId id, OrderStatus status) => (Id, Status) = (id, status);

    public static Result<Order> Create(OrderId id) =>
        Result.Success(new Order(id, OrderStatus.Draft));

    public Result Submit()
    {
        if (Status != OrderStatus.Draft)
            return Result.Failure($"Cannot submit order in {Status} status");
        Status = OrderStatus.Submitted;
        return Result.Success();
    }
}
```

### Value objects
- Immutable, equality by value.
- Self-validating in factory.
- Examples: `Email`, `Money`, `OrderId`.

```csharp
public sealed class Email : ValueObject
{
    public string Value { get; }
    private Email(string value) => Value = value;

    public static Result<Email> Create(string? input) =>
        string.IsNullOrWhiteSpace(input) ? Result.Failure<Email>("Email is required")
        : !MailAddress.TryCreate(input, out _) ? Result.Failure<Email>("Email is invalid")
        : Result.Success(new Email(input));

    protected override IEnumerable<object> GetEqualityComponents() { yield return Value; }
}
```

Use `MailAddress.TryCreate` instead of regex for email validation.

### Factory methods
- All construction via static `Create(...)` returning `Result<T>`.
- Public constructors only for entities with no invariants (rare).

## Error handling strategy

| Failure type            | Mechanism                                  |
|-------------------------|--------------------------------------------|
| Business rule violation | `Result.Failure<T>("reason")`              |
| Infrastructure failure  | `try/catch` -> log -> `Result.Failure`     |
| Programmer error        | `ArgumentException` / let it crash         |
| Cancellation            | Let `OperationCanceledException` propagate |

Validate inputs at the use case boundary (Application layer), not in domain methods. Domain assumes already-valid input.

## Testing

Stack: xUnit + NSubstitute + FluentAssertions. Optional: AutoFixture, Testcontainers.

```csharp
public class OrderServiceTests
{
    private readonly IOrderRepository _repo = Substitute.For<IOrderRepository>();
    private readonly ILogger<OrderService> _logger = Substitute.For<ILogger<OrderService>>();
    private readonly OrderService _sut;

    public OrderServiceTests() => _sut = new OrderService(_logger, _repo);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_WhenNotFound_ReturnsFailure()
    {
        var id = OrderId.New();
        _repo.FindAsync(id, Arg.Any<CancellationToken>()).Returns((Order?)null);

        var result = await _sut.GetAsync(id, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain(id.ToString());
    }
}
```

Conventions:
- Test name: `Method_Condition_ExpectedResult`.
- No Arrange / Act / Assert comments — blank lines separate phases.
- `[Trait("Category", "Unit"|"Integration")]` on every test.
- For verifying log output, prefer `Microsoft.Extensions.Logging.Testing.FakeLogger` (.NET 8+) over substitute.
- Integration tests use Testcontainers, not shared dev DB.

## Performance notes

- **Profile before optimizing.** Don't speculate.
- Hot paths: avoid LINQ allocations, use `Span<T>` / `Memory<T>` where measurable.
- `SemaphoreSlim` for bounded parallelism:

```csharp
using var gate = new SemaphoreSlim(maxParallelism);
var tasks = items.Select(async item =>
{
    await gate.WaitAsync(ct);
    try { await ProcessAsync(item, ct); }
    finally { gate.Release(); }
});
await Task.WhenAll(tasks);
```

- For large parallel async work over collections, use `Parallel.ForEachAsync` (.NET 6+).
- High-throughput logging: prefer Serilog `PeriodicBatching` sinks over naive async wrapping.

## Security

- Never log secrets, tokens, full PII.
- Parameterized queries always.
- Validate and bound all external input at the API boundary.
- Encrypt secrets at rest (Data Protection API, Azure Key Vault, AWS KMS, etc.).
- HTTPS only; HSTS in production.

## Required `.csproj` settings

```xml
<PropertyGroup>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <LangVersion>latest</LangVersion>
  <AnalysisLevel>latest-recommended</AnalysisLevel>
</PropertyGroup>
```

## Quick reference — Result<T>

```csharp
Result.Success(value)
Result.Failure<T>("error")

result.IsSuccess / result.IsFailure
result.Value      // when IsSuccess
result.Error      // when IsFailure

result
    .Ensure(x => x.IsValid, "invalid")
    .Map(x => Transform(x))
    .Bind(x => NextResult(x))
    .Tap(x => logger.LogInformation("ok {X}", x))
    .TapError(e => logger.LogWarning("failed: {Error}", e));
```
