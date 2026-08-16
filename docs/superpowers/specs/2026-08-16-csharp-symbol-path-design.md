# C#-Aligned Symbol Path Query and Display Design

Date: 2026-08-16

Status: Approved design. Implementation is intentionally deferred.

## Decision

CSIndexer will replace its current exact symbol-query spelling with a
round-trippable symbol-path grammar. The same path can be accepted as search
input and emitted as a result display name. The formatter supports two
namespace/type boundary styles:

- csharp (the default): a dotted, C#-style type path;
- explicit: an explicit namespace/type boundary using double colon.

Both spellings are accepted as input. They resolve to the same semantic symbol
path and differ only in presentation of the namespace/type boundary.

## Goals

- Represent nested types, member methods, local functions, and nested lambdas
  in one readable path.
- Make every emitted path valid input for a later search.
- Preserve C# spelling where it helps source navigation: dotted type paths,
  C# keyword aliases such as int and string, nullable modifiers, generic
  syntax, and parameter lists.
- Allow omission of a namespace as a wildcard over every namespace.
- Avoid guessing whether a dotted identifier is a namespace segment or a
  nested-type segment.
- Support a human-oriented explicit rendering for users who want the
  namespace/type boundary visible.
- Let namespace, type, method, file, and source conditions independently
  choose strict or case-insensitive comparison.
- Let each condition use a simple glob by default, while providing separately
  named literal and regular-expression conditions where their extra precision
  is needed.
- Store all persisted filesystem locations as portable paths relative to the
  indexed source root, rather than repeating machine-specific absolute paths.
- Resolve stored paths to absolute paths at query time, with an explicit
  query-only base-directory override for relocated projects.

## Non-goals

- Backward compatibility with the old
  [namespace.]type::method[(parameter-types)] grammar.
- Making an entire display path a compilable C# expression. Only its type
  spelling is C#-style; double-colon executable ownership is an index
  notation.
- Inferring runtime dispatch, delegate invocation, reflection, or callback
  flow from the path grammar.
- Changing stable keys, symbol identity, or database identity solely because a
  display style changes.

## Terms

A namespace path is a dot-separated sequence of C# namespace identifiers.

A type path is a dot-separated sequence comprising an outer type followed by
zero or more nested types. It is stored and resolved as a full type display
path, not as a parser guess about where its namespace begins.

An executable path begins at a member method and follows lexical ownership
through local functions and lambdas. Every child segment is an immediate
lexical child of its preceding executable segment.

A storage root is the resolved input root used during indexing. It is the
directory that contains .csindex for the default database layout.

An index-root anchor is the portable relative path from the database directory
to the storage root. It is saved once for an index run so a custom database
location can still find the root without persisting an absolute path.

## Public syntax

The canonical structure is:

~~~
type-selector :: member-signature (:: executable-child)*
~~~

The two accepted type-selector forms are:

~~~
# Namespace/type boundary explicitly written
namespace-path :: type-path

# C#-style full dotted type path
dotted-type-path
~~~

Examples:

~~~
# Explicit style
Namespace1.Namespace2::Class1.Class2::Method1(int)::Method2(string)::<lambda#1>

# C# style
Namespace1.Namespace2.Class1.Class2::Method1(int)::Method2(string)::<lambda#1>

# Namespace omitted: wildcard namespace scope
Class1.Class2::Method1(int)::Method2(string)
~~~

The first callable segment after the type selector is a member method. Later
callable segments are local functions or lambda markers, and must be immediate
lexical children of the prior segment:

~~~
member-signature  = identifier generic-arguments? ( parameter-list? )
executable-child  = member-signature | <lambda#positive-integer> | <lambda#*>
~~~

The asterisk lambda form is a pattern form for all lambdas at that path level;
it is not emitted as the display name of a concrete lambda.

Generic type and method syntax follows C# token balancing. For example:

~~~
Company.Collections::Map<TKey,TValue>.Entry::TryGet<T>(TKey,out TValue)
~~~

The parser must scan balanced angle brackets, parentheses, arrays, nullable
modifiers, tuples, and global namespace qualifiers in parameter type syntax;
it must never split those nested constructs on a path separator.

## Namespace omission and dotted resolution

For a C#-style selector such as:

~~~
Namespace1.Namespace2.Class1.Class2::Method1()
~~~

the parser treats Namespace1.Namespace2.Class1.Class2 as one dotted type path.
It does not decide that Class1 is either a namespace or a type. Resolution
matches the indexed complete type display path. If more than one indexed
symbol legitimately has that full path in its analysis scope, every matching
symbol is returned.

For a namespace-omitted selector such as:

~~~
Class1.Class2::Method1()
~~~

the type path is matched as an exact suffix of an indexed complete type path.
This is semantically equivalent to:

~~~
*::Class1.Class2::Method1()
~~~

All matching namespaces are returned in deterministic order. Result paths
always include their actual namespace so users can copy one result and perform
an exact fully qualified search.

The explicit form provides a syntactic exact namespace constraint:

~~~
Namespace1.Namespace2::Class1.Class2::Method1()
~~~

Here the first double colon is the namespace/type boundary. The namespace and
type are therefore known without consulting symbol names.

## Flexible matcher conditions

The default matcher for every user-supplied name, file, and source condition
is glob. This is a deliberate breaking replacement for the current global
regex and ignore-case switches. A plain name with no wildcard characters still
behaves as an exact name match, so every emitted display path remains directly
searchable.

The shared conditions and their default glob options are:

~~~
--namespace <glob>
--type <glob>
--method <glob>
--file <glob>
--include <glob>
--exclude <glob>
~~~

Each has corresponding explicit literal and regular-expression forms:

~~~
--namespace-literal <text>    --namespace-regex <expression>
--type-literal <text>         --type-regex <expression>
--method-literal <text>       --method-regex <expression>
--file-literal <text>         --file-regex <expression>
--include-literal <text>      --include-regex <expression>
--exclude-literal <text>      --exclude-regex <expression>
~~~

The base options are intentionally concise for routine searches. The literal
forms make a character such as an asterisk searchable as text, particularly
in source conditions. The regex forms are never inferred from punctuation;
they are explicit so a user can safely choose glob for one condition and regex
for another in the same command.

Each category has independent case behavior:

~~~
--namespace-case strict|ignore
--type-case strict|ignore
--method-case strict|ignore
--file-case strict|ignore
--source-case strict|ignore
~~~

Every category defaults to strict. A category case option applies to all of
that category's literal, glob, and regex conditions, without affecting any
other category. For example, a user can ignore case for a method name while
requiring exact spelling for its namespace and source text.

The legacy global --regex and --ignore-case options are removed, not retained
as aliases.

### Glob semantics

Glob syntax has no regular-expression metacharacter behavior.

- An asterisk matches zero or more characters within one structural component.
- A double asterisk recursively spans structural hierarchy components.
- Neither wildcard crosses a double-colon executable ownership boundary.

For namespace and type paths, a structural component is a dot-separated
identifier. Thus Game.* matches one immediate child component, and Game.**
matches descendant namespace or nested-type paths at every depth. An
unqualified wildcard over every namespace or type depth uses double asterisk.
For a method name, source text, or another non-hierarchical value, double
asterisk is accepted as an equivalent spelling of asterisk.

For storage-root-relative file paths, paths are normalized to forward slashes
before matching. Asterisk does not cross a slash; double asterisk spans zero
or more directory components:

~~~
src/*/Player.cs       # exactly one directory level
src/**/Player.cs      # any directory depth, including none
~~~

The positional structured selector also uses glob for its name components:

~~~
**::**::Get*()
~~~

This selects every namespace, every type path, and every method whose name
starts with Get. The three structural components are required; a form such as
::*:: is malformed rather than treated as an implicit empty wildcard.

Wildcard matching does not reinterpret C# parameter type syntax. Parameter
lists remain C# type syntax so that a signature such as Method(int*) denotes a
pointer parameter rather than a wildcard. To search every overload, omit the
parameter list. Regular-expression method-name matching is available only
through the explicit method-regex condition.

### Composition and command scope

Conditions in different categories are ANDed. Repeated namespace, type,
method, or file alternatives are ORed within their own category. Source
includes remain ANDed, while source excludes remain ORed and reject a candidate
before includes are evaluated. Each repeated source condition may use its own
base, literal, or regex option.

Storage-root-relative file conditions evaluate the stored source document path
of a candidate symbol. Name, file, and source conditions select roots before
definition, reference, caller, callee, override, or graph traversal begins;
they do not silently filter later edge or graph results. Commands whose
contract requires exactly one root report ambiguity when the selector returns
multiple candidates. List and search commands return every selected candidate
in deterministic order.

## Portable path storage and presentation

Every persisted filesystem location uses a forward-slash path relative to the
storage root. The standard layout is:

~~~
database:     D:/Work/Game/.csindex/index.sqlite
storage root: D:/Work/Game
document:     D:/Work/Game/src/play.cs

stored document path: src/play.cs
stored root anchor:   ..
~~~

The database directory is D:/Work/Game/.csindex. The root anchor therefore
resolves its parent as the storage root. A custom database uses the same rule:
the index run stores the relative route from that database's directory to the
storage root. For example, a database at D:/Indexes/Game.sqlite that indexes
D:/Work/Game stores ../Work/Game as its root anchor and src/play.cs as the
document path.

At query time, without an override, the effective base directory is:

~~~
effective base = FullPath(database directory + stored root anchor)
absolute path  = FullPath(effective base + stored relative path)
~~~

If the database and source root move while retaining their relative layout,
the same saved anchor resolves from the database's new location. In the
default layout, moving a project with its .csindex directory keeps the anchor
as .. and automatically resolves its new source-root location.

Documents outside the storage root, such as linked shared source files, use
an allowed leading ../ path. The resolver normalizes that path only after
combining it with the effective base, so linked source remains addressable.

This rule applies to every persisted filesystem-derived value: document path,
project path, input-root representation, and any source-derived path component
in a stable key. The index-root anchor itself is also relative; no persisted
path field contains a machine-specific absolute path.

Schema version 5 replaces the schema-4 absolute-path representation. Version
4 and older databases are rejected and rebuilt; there is no data migration.

### Base directory override

All query and source commands that open an existing database accept:

~~~
--base-dir <path>
~~~

The option is query-time only. It replaces the effective base directory for
that invocation:

~~~
effective base = FullPath(--base-dir)
absolute path  = FullPath(effective base + stored relative path)
~~~

For example, a database containing src/play.cs can be queried after its source
tree has independently moved:

~~~
csindex source show "..." --db D:/Indexes/Game.sqlite --base-dir E:/Moved/Game
~~~

The result location and source-file read resolve to E:/Moved/Game/src/play.cs.
The override does not rewrite database rows, stable keys, cache identity,
stored anchors, or the --db location. It is invalid for index because index
always establishes a new storage root from its resolved input.

Base-dir input accepts either slash style and is normalized to an absolute path
for the process. It need not exist merely to format stored locations; commands
that must open a source file report their normal missing-file error if it is
absent.

### Path display and path inputs

Path-bearing query output accepts:

~~~
--path-style absolute    # default
--path-style relative
~~~

Absolute output uses the effective base directory, including a --base-dir
override. Relative output emits the root-relative stored path exactly, such as
src/play.cs, and does not change when --base-dir is present. This selection
applies consistently to table location fields, JSON location.path values,
source headers, graph locations, and diagnostics that expose a source path.

File filters match the root-relative stored form. Inputs that name a source
location, including definition --at, accept either a root-relative path or an
absolute path. A relative input is resolved from the effective base; an
absolute input is converted to a root-relative form before it is compared to
the stored value. Both slash styles are accepted at input, while stored and
relative output paths use forward slashes.

## Method signatures and type spelling

Emitted member and local-function signatures always include parentheses. An
empty parameter list means an exact parameterless signature. A query that
omits the parameter list is a name-pattern shorthand that can match every
overload with that name.

Result paths prefer C# aliases and syntax:

~~~
Method(int, string?, List<string>, ref Guid, out Result)
~~~

Input accepts either an alias or its framework spelling, such as int or
System.Int32. The resolver normalizes both to the internal canonical type
identity before comparing a signature. Return type is not part of a method
path because C# does not overload on return type.

Ref-kind remains part of overload identity when applicable. Generic
arity/arguments, nullable annotations, arrays, pointers, tuples, and escaped
identifiers use their C# spelling and are parsed with balanced-token rules.

## Lexical children

A local function is shown as a callable child of its owning method, local
function, or lambda:

~~~
Namespace.Type::Outer(int)::Local(string)
Namespace.Type::Outer()::<lambda#1>::Local()
~~~

A lambda is shown as an ordinal child:

~~~
Namespace.Type::Outer()::<lambda#1>
Namespace.Type::Outer()::<lambda#1>::<lambda#2>
~~~

Lambda ordinals are stable within the immediate lexical owner for one indexed
snapshot. Resolution validates containment through stored parent/child symbol
relationships, rather than trusting a textual prefix. This prevents a
same-named method or local function elsewhere from satisfying a child path.

If a language-legal source layout contains more than one local declaration
with the same visible path and signature under an otherwise unnamed block
scope, the unadorned path returns all of them. Each result retains its source
location and stable key for disambiguation; adding a source-location or
ordinal selector is deliberately outside this first design.

## Output formatting

All commands and all result formats that expose a display name use one
formatter with this option:

~~~
--symbol-path-style csharp    # default
--symbol-path-style explicit
~~~

The csharp style emits:

~~~
Namespace1.Namespace2.Class1.Class2::Method1(int)::Method2(string)
~~~

The explicit style emits:

~~~
Namespace1.Namespace2::Class1.Class2::Method1(int)::Method2(string)
~~~

The option changes presentation only. It does not alter stored stable keys,
database rows, ordering, or resolver semantics. JSON display-name fields,
table cells, text output, graph labels, and source headers use the selected
style so every human-facing result remains copyable as a query.

## Error behavior

The parser rejects malformed separators, unmatched generic or parameter
delimiters, an empty type selector, an empty callable segment, and a child
whose form is not a local-function signature or lambda marker.

It also rejects a malformed glob hierarchy and an invalid regular expression.
Regular-expression compilation and evaluation are culture-invariant and
timeout-bounded. A failure is a query error with no partial result. The removed
global --regex and --ignore-case options are unknown options, not compatibility
aliases.

An empty base-dir value, or base-dir passed to index, is a usage error. A
missing source file after path resolution is reported by the command that
requires that file; it does not change the stored portable path.

The resolver reports no match, rather than a parse error, when a syntactically
valid full or suffix type path has no indexed candidates. A dotted spelling
never fails merely because its namespace/type boundary is not lexically
obvious; matching against indexed full type paths is the intended resolution
mechanism.

An explicit namespace/type form cannot be silently reinterpreted as a dotted
form. It must satisfy its stated namespace and type constraints exactly.

## Implementation boundaries for the later plan

The future implementation will replace the current last-dot split in
SymbolQueryParser with a structured path parser. Exact method resolution must
query full type paths and suffix paths rather than namespace plus simple type
only. Display-name construction must walk stored lexical containment for local
functions and lambdas. The existing formatting path must expose the selected
style consistently across text, JSON, graph, and source output.

The current single matcher mode will be replaced by typed condition records
that carry category, literal/glob/regex kind, and strict/ignore case mode.
File paths will be normalized to storage-root-relative forward-slash paths
before their matcher runs. A single path resolver will own persistence,
anchor-based reconstruction, base-dir overrides, display style, source-file
reads, and source-location input conversion. Query routing must apply
conditions only to roots for relationship and graph commands, as specified
above.

Focused tests will cover:

- both styles parsing to equivalent semantic paths;
- nested type versus namespace-like segment names;
- namespace omission returning all matching namespaces;
- a displayed csharp or explicit path being accepted again as input;
- nested local-function and lambda paths;
- aliases versus framework type spellings;
- generic and ref-kind signatures;
- malformed balanced syntax and invalid child containment;
- deterministic ordering and unchanged stable identity across styles.
- independent strict/ignore modes for namespace, type, method, file, and
  source conditions;
- default glob, explicit literal, and explicit regex conditions in one query;
- single- and recursive-component glob behavior for namespace/type and files;
- source literals containing an asterisk, invalid regexes, timeout behavior,
  and removal of the legacy global matcher options;
- root-only filtering, multiple-root ambiguity, and deterministic result
  ordering.
- root-relative persistence for documents, projects, input roots, and
  source-derived stable keys;
- standard .csindex anchor reconstruction, custom-database anchors, and
  relocation that preserves their relative layout;
- base-dir override without DB mutation, including linked ../ source paths;
- absolute and relative table, JSON, graph, source-header, diagnostic, and
  --at path behavior.

## Approved product choices

- The default output style is csharp.
- The explicit output style remains available for visual namespace/type
  separation.
- Both styles are accepted as input.
- Namespace omission is a wildcard and returns all matches.
- Output uses C# aliases where available.
- The prior grammar is intentionally replaced rather than supported as a
  compatibility mode.
- All unqualified name, file, and source conditions use glob by default.
- Literal and regex conditions use the dedicated --<field>-literal and
  --<field>-regex options.
- Case sensitivity is independently configured per namespace, type, method,
  file, and source category, and defaults to strict.
- Persisted filesystem paths are storage-root-relative with forward slashes;
  absolute paths are reconstructed only at query time.
- The default display style for locations is absolute; --path-style relative
  shows the stored root-relative path.
- --base-dir overrides path reconstruction for query/source commands only and
  never changes the database.
- Schema version 5 requires a rebuild of older databases.
- No implementation work begins until a separate implementation plan is
  approved.
