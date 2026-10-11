# Third-party notices

unitygraph is MIT licensed (`LICENSE`). The `ug` build in `dist/ug` ships these libraries
unchanged, under their own licences.

| Component | Version | Licence | Source |
| --- | --- | --- | --- |
| Microsoft.CodeAnalysis.CSharp (Roslyn), with Microsoft.CodeAnalysis | 4.14.0 | MIT | https://github.com/dotnet/roslyn |
| Microsoft.Data.Sqlite | 10.0.12 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw (core, provider, batteries, e_sqlite3 natives) | 2.1.12 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite | bundled in e_sqlite3 | Public domain | https://sqlite.org |
| System.CommandLine | 2.0.12 | MIT | https://github.com/dotnet/command-line-api |
| System.Collections.Immutable, System.Reflection.Metadata | with Roslyn | MIT | https://github.com/dotnet/runtime |

`ug view` pages inline these scripts, kept byte for byte in `src/UnityGraph.Viewer/vendor/` with
the npm integrity hashes in `SOURCES.md` there. Their licence files (all MIT) ship in `licenses/`:

| Script | Version | Licence |
| --- | --- | --- |
| cytoscape | 3.34.3 | MIT |
| cytoscape-dagre, bundling @dagrejs/dagre and @dagrejs/graphlib | 4.0.1 | MIT |
| cytoscape-fcose | 2.2.0 | MIT |
| cose-base | 2.2.0 | MIT |
| layout-base (its SVD routine is from JAMA, with its Apache-2.0 notice inline) | 2.0.1 | MIT |
