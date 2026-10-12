# Design decisions

## 1. Plan restores from LSNs, not from file names or timestamps

Every backup's `first_lsn`, `last_lsn`, `checkpoint_lsn` and `database_backup_lsn` is read from `msdb.dbo.backupset`
right after it completes and stored in the agent's catalog. The restore planner uses them to:

- pick the newest full backup that finished before the target time;
- pick the newest differential whose `database_backup_lsn` equals that full's `checkpoint_lsn`. A differential belongs to
  one specific full, and a later full (or a copy-only full) does not change that;
- chain log backups where each one starts at or before the previous one's end (`first_lsn <= previous.last_lsn < last_lsn`).

A gap is reported with the missing LSN instead of producing a script that fails on the third `RESTORE LOG`.
Timestamps alone can't detect a missing log backup, and two backups can finish in the same second
(the integration tests hit exactly that), so ties are broken by LSN.

## 2. Retention that keeps the restore window intact

"Delete files older than 14 days" can delete the full backup that today's differentials depend on. The rule here:
for each database, find the newest full backup that finished **before** the window start (the *anchor*). Everything newer
than the anchor is kept, because together it can restore any point in the window. Only backups older than the anchor are
deleted. A log backup that finished during the anchor full but extends past it is also kept, and the newest `KeepLastFull`
fulls are always kept whatever their age.

## 3. Prove it with restore drills

A backup is only proven by restoring it. A `RestoreDrill` job restores each database's latest chain into
`<db>_drill_<timestamp>`, runs `DBCC CHECKDB`, reports, and always drops the scratch copy (even on failure).
It exercises the whole path: catalog → planner → fetching files from destinations → `MOVE` relocation → recovery.

## 4. Verify every copy

- `BACKUP ... WITH CHECKSUM` plus `RESTORE VERIFYONLY ... WITH CHECKSUM` on the staged file.
- SHA-256 computed once, and every destination copy checked against it. Folder copies are written as `.partial` and renamed,
  so a crash never leaves a truncated file that looks valid. Azure uploads store the hash as blob metadata and check it on download.

## 5. Staging paths seen from two places

`BACKUP TO DISK` paths are interpreted by the SQL Server service, not by the agent. When SQL Server runs in a container,
`/backups` inside it might be `/srv/sql-backups` on the host. `Staging:ServerPath` and `Staging:AgentPath` keep both views
explicit, and the CI integration tests run in exactly that configuration (SQL Server container + bind mount).

## 6. Safe T-SQL generation

`BACKUP`/`RESTORE` can't take a database name or file path as a parameter, so names are escaped the way `QUOTENAME`
does (`]` → `]]`) and paths as `N'...'` literals (`'` → `''`). Database names come from `sys.databases`, not from
user input, and that is also covered by tests.

## 7. Configuration that fails loudly

Jobs must list `Databases` and `Destinations` explicitly. An empty list is a startup error, never "all databases".
This is deliberate: the .NET configuration binder **appends** configured list items to a list that already has default
values, so an early version that defaulted `Databases` to `["*"]` turned a configured `["Sales"]` into `["*", "Sales"]`.
Defaults on collections are gone, a regression test pins the behaviour, and `validate` prints exactly which databases
each job will touch.

## 8. Own catalog instead of relying on msdb

msdb history is routinely purged (`sp_delete_backuphistory`), lives on the server you may be trying to recover, and
doesn't know where copies were sent. The agent keeps its own SQLite catalog: backups, LSNs, hashes, storage locations,
runs and drill results. LSNs are `numeric(25,0)`, beyond SQLite's REAL precision, so they're stored as text.

## 9. One process, two hats

The same executable is a long-running service (scheduler + status page) and a CLI (`run`, `plan`, `restore`, `validate`),
sharing configuration and code paths. `run` returns a non-zero exit code on failure, so it can also be driven by SQL Agent,
Task Scheduler or a CI pipeline.
