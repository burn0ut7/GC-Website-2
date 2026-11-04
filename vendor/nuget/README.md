# Shared Lobby2 contracts

Current dependency: `Lobby2.Contracts.1.1.2.nupkg`.

Source content: Lobby2 commit `04652ca538e1870832cf434f7cf5ac97f5790088`,
`integrations/Contracts`. Package was built and tested before committing that
unchanged source content.
SHA-256: `148b563842ddb99c763c54b67fc340dd5b794d79ebddb17b27afe76c82812090`.
Adds validated local/radio voice start, refresh and stop cold events, retaining
L2TB v1 and earlier recording support. The importer references the same source.

Previous dependency: `Lobby2.Contracts.1.1.1.nupkg`.

Source commit: `bbc8edc9f16bc0bc4d4d793de084a72761bd1208`.
SHA-256: `94907494173936550e08310cf5ecba68854598974d022f4464298b13607e561e`.

Source: https://github.com/burn0ut7/Lobby2/tree/bbc8edc9f16bc0bc4d4d793de084a72761bd1208/integrations/Contracts

This adds validated faction map snapshots and preserved military marker metadata, retaining the existing telemetry codec and statistics/session/history contracts. Build with `dotnet pack integrations/Contracts -c Release` from the committed revision. Consumer dependency and locks must move together. Never overwrite a released version. No player captures are packaged.

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

