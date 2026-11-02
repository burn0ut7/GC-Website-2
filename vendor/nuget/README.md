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
