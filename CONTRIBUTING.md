# Contributing to Swivel

Thanks for helping. A few things keep Swivel trustworthy:

1. **Never touch an original.** Engines write only to `Job.TempPath`; the queue renames the result
   into place. Every engine test hashes its input before and after (`Fixtures.Hash`).
2. **Errors are plain and kind.** Throw `JobException` with a reason a person can act on
   ("tickets.pdf is password-protected."). The toast adds "Your original is safe."
3. **No new network calls.** Swivel works offline. Optional packs download only after the user says yes,
   and every download is pinned by SHA-256 in `build/engines.lock.json` and `EnginePacks`.
4. **Licences stay separate.** GPL tools run as child processes, never linked. Check a package's
   licence before adding it (PDFsharp is pinned to 6.2.4, its last MIT release).

## Adding a wedge

1. Add an `Operation` to `OperationCatalog.BuiltIn` (id, mode, label, hint, input kinds, output).
2. Handle it in the engine named by `Operation.Engine`.
3. Add a golden test in `tests/Swivel.Engines.Tests` that converts a generated fixture and checks
   the output's format, the original's hash, and that no `.swivel-tmp` files remain.
4. Give it a line in `Feedback/Voice.cs`.

## Running things

```powershell
dotnet test Swivel.sln --filter "Category!=Perf"     # fast suite
dotnet test tests/Swivel.Engines.Tests -c Release --filter "Category=Perf"
dotnet test tests/Swivel.UiTests                     # drives the real app; leave the mouse alone
```

Commits end with a short summary of *why*. Keep the code's voice: small classes, comments that
explain intent, no dead code.
