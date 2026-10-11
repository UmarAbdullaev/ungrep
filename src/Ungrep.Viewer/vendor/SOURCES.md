# Vendored scripts

Inlined into the page `ug view` writes (SPEC-viewer, Boundaries). Each file is the package's
published file, byte for byte (`.gitattributes` keeps git from touching line endings here); the
page drops a `sourceMappingURL` comment when it inlines them, so the browser asks for nothing.
Fetched 2026-10-11 with `npm pack`.

| File | Package | npm integrity | SHA-256 of the file |
| --- | --- | --- | --- |
| `cytoscape.min.js` | cytoscape 3.34.3, `dist/cytoscape.min.js` | `sha512-yfYGhRcGAntq6YBD583j4n0Eg3jIxvWmZtz/5uz9UYkeIStSlMxuUja+ec5j3iBD8nv1rwaOAYMW09tBdkSeaQ==` | `5f3b5b529546d5af1fc5628590af033b74511a5b6f789f5f4682845863228b91` |
| `cytoscape-dagre.min.js` | cytoscape-dagre 4.0.1, `dist/cytoscape-dagre.min.js` (bundles @dagrejs/dagre 3 and @dagrejs/graphlib 4) | `sha512-hyNdh8Vp1nwzBcjHnM4c71G81bFhPmQYLXD2HM9AZIvl+8TrMzWEOPW45hF/XOBQiEguPcDQzi6VrXR1XBcXOA==` | `ec671a41d2bfcac580352e2997797793949e3489cecaf347fc48670d4bd456b0` |
| `layout-base.js` | layout-base 2.0.1 | `sha512-dp3s92+uNI1hWIpPGH3jK2kxE2lMjdXdr+DH8ynZHpd6PUlH6x6cbuXnoMmiNumznqaNO31xu9e79F0uuZ0JFg==` | `ec15ab5df9af3f20708f4faab994accf91cda71848cd5bb10a23432cc50b6745` |
| `cose-base.js` | cose-base 2.2.0 | `sha512-AzlgcsCbUMymkADOJtQm3wO9S3ltPfYOFD5033keQn9NJzIbtnZj+UdBJe7DYml/8TdbtHJW3j58SOnKhWY/5g==` | `7cae9509bd36235a63a85e71c8d9fa2cd0bc1d0c1ecc5b5a737976f39d040ddf` |
| `cytoscape-fcose.js` | cytoscape-fcose 2.2.0 | `sha512-ki1/VuRIHFCzxWNrsshHYPs6L7TvLu3DL+TyIGEsRcvVERmxokbf5Gdk7mFxZnTdiGtnA4cfSmjZJMviqSuZrQ==` | `4b1cab218d74996aa59cd8473f9239cc6398b8c1774d84d7e59ad9a68959cb57` |

Licences, all MIT, next to the scripts as `<name>.LICENSE`: cytoscape, cytoscape-dagre, dagre
(@dagrejs/dagre 3.1.1's file), graphlib (@dagrejs/graphlib 4.0.5's file), layout-base, cose-base,
cytoscape-fcose. layout-base's SVD routine is from JamaJS and carries its Apache-2.0 notice inline
in `layout-base.js`.

To update: `npm pack <package>@<version>` into an empty folder, copy the files above, update this
table and the versions in `ViewPage.cs` and SPEC-viewer.
