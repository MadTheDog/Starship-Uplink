# Package format v1

A native package is a ZIP containing a root `sevo.mod.json` and a single declared plugin payload directory:

```text
sevo.mod.json
payload/MyMod.dll
payload/assets.bundle
README.md
LICENSE
```

```json
{
  "schemaVersion": 1,
  "id": "example.my-mod",
  "name": "My Mod",
  "version": "1.0.0",
  "payloadRoot": "payload",
  "repository": "publisher/repository",
  "dependencies": { "example.library": ">=1.0.0" },
  "conflicts": [],
  "fixedBrickIds": [],
  "fixedShapeIds": []
}
```

IDs use lowercase letters, digits, dots, underscores and hyphens, at most100 characters, without consecutive dots. Prefer stable publisher-qualified IDs. Versions have major.minor.patch, optional prerelease/build suffixes. Dependencies support `*`, exact versions, a single comparison (`>=`, `<=`, `>`, `<`, `=`), caret or tilde. Compound npm-style range expressions are not supported. Required dependencies must already be installed and enabled at a satisfying version; the manager does not guess or download unrelated packages automatically.

The payload retains its internal relative paths and installs below `BepInEx/plugins/SevoManaged/<id>`. It must include a DLL. Only supported ordinary plugin/asset file extensions are accepted. Configuration, loader replacements, scripts and executables cannot be installed through a normal package. Documentation remains in the retained archive rather than being deployed to the game.

`repository` is optional for local-only packages. GitHub updates use a public owner/repository and a user-selected published ZIP asset. A GitHub source archive is not a compiled mod package. Prereleases/drafts are not automatically selected. Version pins prevent replacement until explicitly removed. Package ID+version is immutable: different bytes cannot replace an already cached version.

The current legacy adapters recognize `BepInEx/plugins/GraphicalTweaks`, `HullFiller` and `InfiniteRotator` layouts; this describes formats, not whether a release is published or compatible. Ambiguous version/identity requires explicit import metadata through the CLI. DLLs are never loaded to inspect or install packages.

`fixedBrickIds` and `fixedShapeIds` declare numeric claims for preflight. They do not relocate content or prove what arbitrary plugin code will do. The framework's namespaced dynamic registrations are a separate API. Configuration-dependent legacy IDs require a reviewed adapter reading actual profile configuration; do not mistake defaults for fixed claims.

The store retains archive SHA256, payload file inventories and source metadata. Digests detect changed bytes; they are not a sandbox or a guarantee of an author's trustworthiness. Downloaded mod code runs inside the game when the user launches it.
