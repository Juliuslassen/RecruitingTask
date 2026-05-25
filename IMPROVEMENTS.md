# Improvements Not Made Due to Time

Things I'd pick up with more hours on the clock. None of them are required by
the README; all of them would meaningfully improve the solution.

For *decisions that were made* and *bugs that were fixed*, see
[`NOTES.md`](./NOTES.md).

## Libraries worth reaching for in a "real" version

- **`Microsoft.Extensions.Logging` (`ILogger` / `ILoggerProvider`).** The
  canonical logging abstraction in modern .NET. `LogInterface` is, in
  effect, a hand-rolled `ILogger`. In a greenfield project I'd implement
  this component as an `ILoggerProvider` so callers get DI integration,
  log levels, scopes, structured properties, and category-based filtering
  for free. Kept the hand-rolled interface here because the README
  explicitly fixes `LogInterface` as the public contract.
- **`Serilog` / `NLog`.** Battle-tested file loggers that already solve
  rolling files (by date or size), async pipelines, multiple sinks,
  buffered/batched writes, and retry on transient I/O failures. Building
  a file logger from scratch is something I'd do only when the README
  asks for it (as here) — otherwise reach for one of these.
- **`System.Threading.Channels` (`Channel<T>`).** The newer preferred
  primitive for producer/consumer in .NET, async-first by design. I chose
  `BlockingCollection<T>` because `LogInterface` is synchronous end-to-end
  and `GetConsumingEnumerable` fits that shape without any
  async-over-sync awkwardness, but `Channel<T>` would be the answer if
  the API were redesigned around `async`/`await`.
- **`Polly`.** Retry / circuit-breaker library. The natural fit for demand
  #4 if we wanted retries with backoff (e.g. "if a write fails, retry
  three times with exponential backoff before giving up") rather than
  the current "swallow and continue."
- **`FluentAssertions`.** Would make the test asserts read more naturally
  (`actualLines.Should().Be(messageCount)` instead of
  `Assert.Equal(messageCount, actualLines)`). Pure ergonomics, no
  functional change.

## Legacy `LogLine` cleanup

`LogLine` is the most obvious piece of inherited code that wasn't touched
because no test forced it. Issues:

- Empty `#region Private Fields` block — dead.
- `virtual LineText()`, `virtual CreateLineText()`, `virtual Timestamp` —
  extensibility hooks that nothing in the codebase overrides.
  `CreateLineText()` always returns `""`. It's a YAGNI relic.
- XML comment on `Timestamp` is truncated mid-sentence (`"Th"`).
- The class is essentially an immutable pair of values; it could be a
  record:

  ```csharp
  public record LogLine(string Text, DateTime Timestamp);
  ```

  That removes about 50 lines for zero loss of behavior.

I left `LogLine` alone because the active tests don't depend on its shape
and rewriting it would have meant either touching every call site or
preserving a no-op compatibility surface — both wider than the README's
scope.

## Other small wins

- **Injectable writer factory** so demand #4's recovery behavior is under
  test. Today it's verified only by inspection because `StreamWriter` is
  created internally. A `Func<string, StreamWriter>` constructor parameter
  (path → writer) would let a test pass in a writer that throws on
  `Write()`, proving the consumer survives.
- **`Application/Program.cs` naming.** Methods `flush` / `withoutFlush`
  should be PascalCase (`Flush` / `WithoutFlush`); locals
  `logger_flush` / `logger_to_stop_without_flush` should be camelCase.
  Inherited from the original, harmless but non-idiomatic.
- **`LogInterface` XML docs** carry over typos from the original code
  (`outstadning`, `all all logs`). Quick edit.
- **File-scoped namespaces + usings at file top** in `AsyncLogInterface`
  and `LogLine`. Modern-C# nit; would save indentation and match the
  test project's style. `namespace LogTest;` instead of
  `namespace LogTest { … }`.

## Operational hardening

- **Bounded queue / backpressure policy.** `BlockingCollection<LogLine>`
  is unbounded today, and `WriteLog` is fire-and-forget — if the disk
  stalls or a sink jams, the queue grows without limit and the process
  eats memory until it OOMs. `BlockingCollection`'s constructor takes a
  `boundedCapacity`, which pushes the policy choice (block the producer,
  drop-oldest, drop-newest) into a single decision at construction time
  and doesn't require touching the `LogInterface` contract. A sensible
  default plus an overload to override it would be enough.
- **Health / liveness signals on the logger.** Callers have no way to
  ask whether the consumer thread is still alive, whether writes are
  still landing, or whether errors have occurred — the only current
  signal of trouble is a stderr line. Adding `bool IsRunning`,
  `Exception? LastError`, and an
  `event EventHandler<LogFailedEventArgs> WriteFailed` would let hosts
  surface degraded-logger state in their own health checks and react
  programmatically (alert, switch to a fallback sink) instead of
  finding out after the fact.
- **Hidden `Console.Error` coupling.** `TryProcessLogLine`'s demand-#4
  recovery path writes to `Console.Error.WriteLine(...)`. That's a
  process-wide static dependency: it couples the library to whoever
  owns stderr, can't be redirected for tests, and is invisible in hosts
  that don't surface stderr at all (Windows Service, Azure Function,
  containerised workloads with stderr discarded). The fix dovetails with
  the bullet above — once failures are exposed as an event or callback,
  the library no longer needs to touch `Console` itself, and the host
  decides where the diagnostic lands.

## Continuous integration

- **No CI pipeline wired up.** The test suite is only run locally today —
  every push to `main` and every pull request should trigger the full
  build + test run automatically, so a regression can't slip in unnoticed
  if a contributor forgets to run `dotnet test`. A minimal **GitHub
  Actions** workflow at `.github/workflows/ci.yml` running
  `dotnet test CodeTest.sln` on `ubuntu-latest` against the .NET 10 SDK
  is ~20 lines. Add a CI-status badge to `README.md` so the state of
  `main` is visible at a glance, and gate PR merges on the workflow
  passing.
