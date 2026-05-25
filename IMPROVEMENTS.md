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
- **Structured error reporting** for demand #4 (event / callback /
  `LastError` property) so callers can react to write failures programmatically rather than just by scraping `Console.Error`.
  See [`NOTES.md`](./NOTES.md) → *"What was deliberately not done"* for
  why this was left as-is.
