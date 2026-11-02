# Shared Lobby2 contracts

Current dependency: `Lobby2.Contracts.1.0.0.nupkg`.

Source commit: `198549254344b4e9d7ebcc097315d6c588aade6d`.
SHA-256: `0ba0bb15fba2cc6abcb36e29ada03279604a16fdfe9fbbcf7d749b404afa3b82`.

Source: https://github.com/burn0ut7/Lobby2/tree/198549254344b4e9d7ebcc097315d6c588aade6d/integrations/Contracts

This expands the shared package to statistics, session events and mission history, preserving the existing telemetry codec. Build with `dotnet pack integrations/Contracts -c Release` from the committed revision. Consumer dependency and locks must move together. Never overwrite a released version. No player captures are packaged.

The previous telemetry-only package below is retained as an immutable historical artifact and is no longer referenced by the website.

# Shared Lobby2 contract package

`Lobby2.TelemetryContracts.1.0.1.nupkg` is built from the canonical source in
Lobby2 `integrations/TelemetryContracts` at commit
`227388702bda2b0280f68b0cf72ebf5130032143`.

SHA-256: `03be0fb57e9f3533dffe863a52b48e1090f602c94acbe7665193f49469f03d58`

Source: https://github.com/burn0ut7/Lobby2/tree/227388702bda2b0280f68b0cf72ebf5130032143/integrations/TelemetryContracts

This small immutable package keeps website builds independent of a local game
checkout. NuGet source mapping reserves this package ID for this folder; all
other dependencies come from NuGet.org. The importer references the canonical
source project directly. Do not fork the codec or validator in the website.

For updates, change and test the canonical source, increment its package version,
commit, then `dotnet pack integrations/TelemetryContracts -c Release -o <output>`.
Update this provenance record and consumer dependency/locks together. Never
replace an existing released package version. No capture data is included here.
