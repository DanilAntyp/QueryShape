# QS_OVERFLOW — Scope stopped recording

**Severity:** Warning · **Reported when:** a scope saw more than `QueryShapeOptions.MaxCommandsPerScope` commands (default 10 000).

## What it looks like

```
QS_OVERFLOW WARNING  Scope recorded 10000 commands and stopped capturing; 2413 more ran
```

## Why it happens

QueryShape keeps a bounded list of commands per scope so that it can never grow memory without limit inside a running application
(CLAUDE.md section 9). Once the list is full, later commands are counted (`QueryShapeScope.DroppedCommands`) but not recorded, so the rules see
only the first `MaxCommandsPerScope` commands: an N+1 that starts after the limit is invisible, and query counts in snapshots and reports are truncated.

A scope with ten thousand commands is usually itself the finding: look at the QS001 and QS009 diagnoses that were produced from the recorded part first.

## Fix

- Raise `QueryShapeOptions.MaxCommandsPerScope` when the scope legitimately runs that many commands (a batch job), or
- split the work into smaller scopes (one per batch, one per item) so each stays complete.

## Notes

- Nested scopes have their own limits: an inner scope that overflows does not stop the enclosing scope from recording.
- The diagnosis is excluded from snapshot files (it describes the scope, not the code under test).
