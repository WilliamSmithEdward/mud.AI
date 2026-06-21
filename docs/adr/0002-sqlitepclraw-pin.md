# 0002 - Pin SQLitePCLRaw to 3.0.3

Status: Accepted

## Context

`Microsoft.Data.Sqlite` 10.0.x pulls in the transitive native bundle
`SQLitePCLRaw.bundle_e_sqlite3` 2.1.11, which carries a known high-severity advisory
(GHSA-2m69-gcr7-jv3q). The build surfaced this as NU1903. No 2.1.12 patch exists; the next
published version is 3.0.x.

## Decision

Add an explicit `PackageReference` to `SQLitePCLRaw.bundle_e_sqlite3` version `3.0.3` in
`MudAI.Core`, promoting the transitive dependency to a patched version.

## Alternatives considered

- Leave 2.1.11 and suppress NU1903: keeps a known-vulnerable native library; rejected.
- Wait for `Microsoft.Data.Sqlite` to bump the bundle: unbounded timeline; rejected.

## Consequences

- The build is warning-clean again, which lets CI run `-warnaserror`.
- Verified at runtime: the DB still initializes and the schema is created with the 3.0.3 provider.
- Re-check this pin when upgrading `Microsoft.Data.Sqlite`, and drop it once the framework
  references a patched bundle itself.
