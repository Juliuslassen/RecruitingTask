# Approach & Decision Log

A short record of the choices made in this solution and why. Bugs and cleanup
were pulled in only when an active test exposed them, so each change stays
explainable on its own.

For things I'd improve with more time on the clock — libraries to reach for,
legacy code to clean up — see [`IMPROVEMENTS.md`](./IMPROVEMENTS.md).

## How we worked: test, then fix

Every README requirement was approached the same way: write the failing test
first, watch what it forces in the code, do the smallest change that makes it
pass. This kept each commit reviewable on its own and pushed back against
"while I'm here" refactors that would muddy the diff.

The corollary is **deferring fixes when a later test will drive them**. After
the `BlockingCollection` refactor, `Stop_With_Flush` was still non-blocking;
that gap was left in place with a `NOTE` comment so requirement #3's failing
test would pin it down — which it did (`Expected 1000, Actual 118`). Doing the
fix earlier would have meant rewriting the same lines twice.

## Summary so far

- **Test #1 (write-through-to-file)** drove the minimum setup: a
  `ProjectReference` to the component, an isolated temp directory per run,
  and bumping all projects to `net10.0`.
- **Test #2 (midnight rollover)** drove injecting `TimeProvider` and
  surfaced the latent `.Days != 0` bug (see below).
- **Demo app NRE** observed while running the `Application` project pushed
  the `List<LogLine>` → `BlockingCollection<LogLine>` refactor. A regression
  test (8 × 250 concurrent writes) locks that fix in.
- **Test #3 (stop behavior)** drove adding `Thread.Join()` to both `Stop_*`
  methods after their respective signals. The contract is now symmetric:
  the call returns once the consumer thread has fully exited — the
  difference between the two methods is *what causes it to exit* (drain
  vs. cancel), not whether the caller waits.

## Architecture decisions

- **`BlockingCollection<LogLine>` over `ConcurrentQueue<LogLine>`.** Both are
  thread-safe, but `BlockingCollection` also gives producer/consumer
  signaling out of the box: `GetConsumingEnumerable(token)` blocks until an
  item arrives, and `CompleteAdding` cleanly ends the consumer loop. With
  `ConcurrentQueue` we'd have to rebuild that signaling with sleeps or a
  `ManualResetEvent` — exactly the pattern the original buggy code had.
- **`CancellationTokenSource` for `Stop_Without_Flush`.** Cancelling the
  token makes `GetConsumingEnumerable` throw, the consumer unwinds, and
  outstanding items are discarded — which directly satisfies the README's
  "discard outstanding logs" contract instead of relying on a polled flag.
- **`TimeProvider` injected via constructor (defaults to
  `TimeProvider.System`).** Required to test the midnight rollover in
  milliseconds with `FakeTimeProvider` instead of waiting for a real day
  boundary. The parameter is optional, so production callers are unaffected.
- **Log directory injected via constructor.** A library shouldn't depend on
  cwd. Tests use a temp directory per run, which keeps them parallel-safe
  and self-cleaning.
- **`IDisposable` with the flush-on-dispose pattern.** Originally omitted on
  the grounds that `Stop_*` covered shutdown; an architecture review surfaced
  that `_cts` and `_lines` both implement `IDisposable` and were leaking their
  internal handles per instance. Once `Stop_*` had been made idempotent
  (`Interlocked` guard), the original rationale no longer held — `Dispose`
  could safely call `StopAndFlush()` first (idempotent, joins the consumer),
  then release the queue and cancellation source. Callers can now use `using`
  blocks; explicit `Stop_*` still works exactly as before.

## Bugs identified in the inherited code

| Bug | Status |
|-----|--------|
| `List<LogLine>` accessed from two threads → NRE on the consumer when `Add` resized mid-`foreach` | Fixed via `BlockingCollection`. |
| Consumer busy-waited when the list was empty (`Thread.Sleep` was inside the "has items" branch only) | Fixed; `GetConsumingEnumerable` blocks instead of polling. |
| Artificial 5-line-per-iteration throttle (`if (f > 5) continue;`) | Fixed; no longer present. |
| `MainLoop` opened a new `StreamWriter` on midnight rollover without disposing the old one | Fixed. |
| Log files written to `./LogTest` relative to cwd — unpredictable across launch methods | Fixed; directory is injected. |
| Midnight detection used `(DateTime.Now - _curDate).Days != 0`, which returns `0` for any pair of times less than 24 h apart — so 23:59 → 00:01 silently skipped the rollover | Fixed; check is now `now.Date != _curDate.Date`. Test #2 made the bug observable. |
| `Stop_With_Flush` returned immediately rather than blocking until drain (call returned with the queue still being consumed) | Fixed; `Stop_With_Flush` now calls `_runThread.Join()` after `CompleteAdding`, so the call returns only once the consumer has drained the queue and exited. Test #3 (`Expected 1000, Actual 118` before the fix) made the bug observable. |
| `Stop_Without_Flush` returned before the consumer's `finally` had disposed the writer — caller could observe the file mid-shutdown | Fixed; `Stop_Without_Flush` now `Cancel`s the token *and* Joins the worker, so the file is settled when the call returns. The two `Stop_*` methods are now symmetric in shape: signal, then wait. |
| Demand #4 (error recovery): an exception during a write — disk full, file handle revoked, midnight rollover failing to open a new file — bubbled out of `MainLoop` and killed the consumer thread silently. `WriteLog` kept enqueueing afterwards, so the caller had no signal that logs had stopped reaching disk. | Fixed. The per-line work is now wrapped in `TryProcessLogLine`, which catches and reports failures to `Console.Error` and continues with the next item. `RotateWriterIfDateChanged` was rewritten to open the new writer *before* disposing the old one, so a failed rollover leaves us with the previous working writer rather than a disposed one. |

## What was deliberately *not* done

- **No structured logging, levels, sinks, or async write API.** The interface
  is what the README defines; expanding it would conflict with the stated
  contract.
- **No structured error reporting to the caller.** Demand #4 is satisfied
  in the minimal sense — the consumer thread now survives per-line write
  failures (see bug table) — but the only signal a failure produces is a
  line on `Console.Error`. A more complete answer would expose failures via
  an event, callback, or `LastError`-style property so the calling
  application can react (alert, switch to a fallback sink, etc.). Left out
  to keep the public `LogInterface` shape unchanged from the README.
