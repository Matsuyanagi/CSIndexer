# Cross-volume generated compilation documents design

Status: approved in conversation on 2026-09-07; implementation pending.

## Context

`csindex index .` selects the repository solution and loads it through
`MSBuildWorkspace`. Build tooling may add physical C# files to `Compile` from
outside the selected storage root. For example, xUnit v3 adds
`DefaultRunnerReporters.cs` from the global NuGet package cache.

The portable index stores source locations relative to one storage root. A
document on another Windows drive or UNC share cannot be represented by that
model, so the current preflight rejects it. That behavior is correct for a
user-authored linked source, but it makes tool-generated compilation
infrastructure prevent otherwise valid projects and solutions from being
indexed.

The fix must not hard-code xUnit, a package name, the default NuGet cache path,
or a particular MSBuild target.

## Goals

- Let solution and project indexing continue when build tooling contributes a
  positively identified generated C# document from another volume or share.
- Keep such a document in the Roslyn compilation so that it can still
  contribute types, members, attributes, and other compilation semantics.
- Exclude that document's path, source, declarations, and body-derived facts
  from the portable database and from source-root selection.
- Preserve the existing error for an ordinary cross-volume linked source.
- Preserve existing indexing of same-volume physical generated sources.
- Keep fingerprints deterministic and independent of absolute machine paths.
- Report the exclusion through both warnings and the existing excluded-document
  count.

## Non-goals

- Multi-root or absolute-path persistence.
- Schema changes or migration.
- Disabling package-specific MSBuild targets or setting vendor-specific build
  properties.
- Treating every file under a NuGet cache as generated.
- Changing `--generated-source` modes or query-side generated filters.
- Deploying or replacing an installed executable; deployment is a separate
  post-verification operation.

## Document disposition

Preparation assigns every physical MSBuild document one of three dispositions
before portable paths are materialized.

### Indexed

A document whose absolute path shares the storage root's drive or UNC
server/share remains indexed. Existing relative-path conversion, generated-code
detection, extraction, and persistence remain unchanged. This includes a
same-volume linked source with leading `../` and a same-volume physical
generated source.

### Compilation-only generated

When a document is on another drive or UNC share, preparation reads its text and
uses the existing `GeneratedCodeDetector` with the original file path. It is
compilation-only only when that detector positively recognizes it, including
the established generated filename, generated-directory, assembly-attributes,
or auto-generated-header rules.

The original Roslyn `Project` and `Compilation` retain the document. The
document is excluded from:

- `AnalysisPathMappings` document, syntax-tree, and runtime-path maps;
- `DocumentData` and `symbol_declarations` persistence;
- declaration/body extraction and source queries;
- root selection, because it has no preferred source declaration.

No synthetic filesystem path and no absolute path is persisted. If indexed
source refers to a named symbol declared only in a compilation-only document,
that symbol may be materialized as a declaration-less dependency endpoint, as
metadata symbols already are. The excluded document cannot itself become a
source/query root, and its body does not produce calls or relations.

### Rejected

A cross-volume document that is not positively recognized as generated remains
an input error. A text-read or generated-detection failure must not silently
convert it to compilation-only. The existing same-volume/share diagnostic and
pre-database-mutation ordering are preserved.

## Components and data flow

Introduce one internal immutable document-selection value created during
`AnalysisCoordinator.PrepareAsync`, after `WorkspaceLoader` returns and before
`AnalysisPathMappings` validates paths. It records the indexable document IDs,
the compilation-only document IDs, deterministic compilation-only fingerprint
items, warnings, and exclusion count.

`AnalysisPathMappings` validates and maps only indexable documents. Project
paths are always validated as before. `PreparedAnalysis` owns both the selection
and mappings so all downstream consumers use one decision rather than
reclassifying documents independently.

`SemanticExtractor` obtains the full compilation from the unchanged project but
iterates only indexable documents for source extraction. Existing exclusions
for missing paths, missing files, non-C# files, and `obj` documents still apply
after this new selection.

`ProjectFingerprintBuilder` fingerprints mapped documents by stored relative
path as before. For each compilation-only document it appends a fixed
`compilation-only-generated` marker, document name, generation kind, and content
hash. It must not append the original absolute path. Sorting must be ordinal and
deterministic so relocation and a different NuGet cache root do not change the
fingerprint when document contents are identical.

The global input fingerprint remains scoped to selected input files and explicit
reference/define files; this change does not redesign cache invalidation outside
the prepared-project fingerprint.

## Diagnostics

Each compilation-only document contributes a warning that states it was kept in
the compilation but excluded from the portable index because it is generated
and located on another volume/share. The runtime warning may show the original
absolute path; that path is never stored in SQLite.

The snapshot's `DocumentsExcluded` value includes compilation-only documents
exactly once. Existing downstream exclusions continue to increment the same
counter without double-counting the new disposition.

## Error and cancellation behavior

- Cancellation is checked before and after document text access and during all
  classification and mapping loops.
- Any ordinary cross-volume document still fails preparation before the SQLite
  index is opened or mutated.
- A compilation-only document must not cause a missing document/source-tree path
  lookup merely because it remains in the Roslyn compilation.
- Disposal behavior on preparation failure remains unchanged.

## Tests

Follow RED-GREEN-REFACTOR with focused tests covering:

1. A synthetic cross-volume document with an auto-generated header is retained
   in the compilation, excluded from path mappings and persisted documents, and
   counted and warned exactly once.
2. Indexed source can resolve a named symbol from that compilation-only
   document, while the generated document has no source declaration or query
   root and its body is not extracted.
3. Generated detection by established filename/category rules has the same
   compilation-only behavior.
4. A cross-volume non-generated linked source continues to fail with the
   same-volume/share message before the continuation/database-open sentinel.
5. A same-volume generated linked source remains indexed.
6. Compilation-only fingerprints contain no absolute path, are deterministic
   across relocated roots, and change when content or generation kind changes.
7. A real MSBuild/xUnit project regression proves
   `DefaultRunnerReporters.cs` no longer prevents indexing without setting
   xUnit-specific properties.
8. Existing portable-path, MSBuild workspace, fingerprint, extraction, and CLI
   integration suites remain green.

## Documentation impact

Update the active specification, limitations, implementation status, and test
plan to distinguish persisted linked sources from compilation-only generated
inputs. The single-root limitation remains; only generated compilation inputs
that are deliberately not persisted bypass cross-volume rejection.

