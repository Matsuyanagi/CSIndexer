# C#-Aligned Symbol Paths, Typed Matching, and Portable Index Design

Date: 2026-08-16

Status: Design approved in conversation. Written-spec review is pending.
Implementation and implementation planning are intentionally deferred until
that review is complete.

## 1. Normative language

The words **MUST**, **MUST NOT**, **SHOULD**, **SHOULD NOT**, and **MAY** are
normative. Examples illustrate the rules but do not override them.

This document is deliberately explicit. An implementation agent MUST NOT add
aliases, fallback parsing, migration behavior, matching shortcuts, or new
syntax that is not specified here. If an implementation detail would change
observable behavior and this document does not determine it, the agent must
return the question to the primary agent instead of choosing independently.

## 2. Decision summary

CSIndexer will make a breaking replacement of its current symbol-query
spelling, global matcher switches, and absolute-path persistence.

The revision has five connected parts:

1. A canonical symbol-path model for nested namespaces, nested types,
   methods, local functions, anonymous functions, special callables,
   initializers, and top-level statements.
2. Two interoperable input/output spellings:
   `csharp` (default) and `explicit`.
3. Typed, independently configurable namespace, type, executable, file, and
   source conditions whose default matcher is glob.
4. Schema version 5, with one logical callable identity, role-tagged source
   declarations, partial implementation normalization, and only relative
   persisted filesystem paths.
5. Query-time path reconstruction with `--base-dir` and display selection with
   `--path-style`.

There is no backward-compatibility mode. A single invocation never switches
between old and new grammar based on whether the new grammar found a result.

## 3. Goals

- Make every concrete emitted symbol path valid search input.
- Represent nested types and the complete lexical executable chain without
  guessing which dotted components are namespaces or types.
- Let users copy a displayed result, edit it, and search again.
- Preserve a C#-like owner spelling for source-oriented output.
- Offer an explicit namespace/type boundary when human inspection or exact
  namespace selection is more important.
- Make parameter spelling familiar C#, including keyword aliases.
- Support exact overload selection and intentional overload expansion.
- Give each condition its own literal, glob, or regex mode and each semantic
  category its own strict or case-insensitive comparison.
- Let lightweight glob patterns express recursive namespace, nested-type,
  executable, and project-relative file searches.
- Store logical callable identity only once when C# has a partial declaration
  and implementation.
- Keep every persisted source-derived path portable across project moves.
- Keep formatting choices separate from symbol identity, result ordering, and
  query semantics.
- Preserve the current atomic output-file guarantee.

## 4. Non-goals

- Supporting the old `[namespace.]type::method[(parameter-types)]` grammar.
- Supporting `::` between a method and a local function or anonymous child.
- Making a complete CSIndexer path a compilable C# expression. The type and
  parameter spellings are C#-like; the top-level `::` boundaries and special
  markers are CSIndexer notation.
- Querying constructed generic instantiations such as calls to `M<int>`.
  Paths identify source definitions.
- Inferring namespace/type boundaries in an `explicit` selector.
- Inferring runtime delegate targets, reflection, callbacks, or dynamic
  dispatch from path text.
- Persisting compiler-generated state-machine, closure, backing-field, or
  record-synthesized callable names.
- Adding a multi-root/root-ID storage model for different volumes or UNC
  shares.
- Migrating a schema-4-or-older database.
- Adding a source-location suffix to symbol paths.
- Encoding partial declaration/implementation roles in symbol paths.

## 5. Terms

**Namespace path**
: A sequence of C# namespace identifiers separated by `.`. The exact global
  namespace token is `global` in an explicit selector.

**Type path**
: An outer source type followed by zero or more nested source types, separated
  by `.`. Generic arity is part of each type component.

**Executable path**
: A source member or synthetic owner followed by zero or more immediate
  lexical executable children, separated by `.`.

**Executable segment**
: One named callable, bracketed special callable, anonymous-function marker,
  or synthetic-owner marker in an executable path.

**Concrete path**
: A path emitted for one indexed result. It contains concrete identifiers,
  concrete generic arity, concrete parameter lists, and concrete anonymous
  ordinals. It contains no search wildcard, except that `--short-names` with
  `explicit` style deliberately emits `**` for the omitted namespace.

**Structured selector**
: A positional symbol selector parsed using the grammar in this document. Its
  identifier components use glob semantics, but its C# signature syntax is
  parsed semantically.

**Logical callable**
: One project/profile-scoped callable identity used by calls, relations,
  graphs, and symbol enumeration.

**Declaration row**
: One source location belonging to a logical callable. A partial definition
  and partial implementation are two declaration rows for one logical
  callable.

**Preferred declaration**
: The partial implementation when one exists; otherwise the partial
  definition; otherwise the ordinary declaration.

**Storage root**
: The root established by `index` from the resolved input. In the standard
  layout it is the directory containing `.csindex`.

**Index-root anchor**
: The relative path from the database directory to the storage root.

**Root selection**
: Positional selector and typed-condition evaluation performed before a
  relation or graph traversal.

## 6. Canonical path grammar

### 6.1 Top-level forms

Both forms are accepted as input at all structured-selector call sites.

```text
# csharp style (default output)
dotted-type-selector :: executable-selector

# explicit style
namespace-selector :: type-selector :: executable-selector
```

Concrete examples:

```text
# csharp
Game.Core.Player.Inventory::Load(int).Validate(string).<lambda#1>

# csharp, namespace omitted (suffix search)
Class1.Class2::Method1(int).Method2(string).<lambda#1>

# explicit
Game.Core::Player.Inventory::Load(int).Validate(string).<lambda#1>

# explicit, exact namespace/type boundary
Namespace1.Namespace2::Class1.Class2::Method1(int).Method2(string).<lambda#1>
```

The parser determines the form from top-level `::` separators only:

- exactly one top-level `::` means `csharp`;
- exactly two top-level `::` means `explicit`;
- zero, three, or more top-level `::` separators are malformed.

`::` text inside balanced generic arguments, parameter lists, square-bracket
special segments, or angle-bracket anonymous/synthetic markers is not a
top-level separator. For example, `global::System.String` inside a parameter
type and `::` inside a bracket payload do not change the selector form.

`A::B::C()` is always parsed as explicit namespace `A`, type `B`, executable
`C()`. It is never reinterpreted as a csharp selector after a no-match.

### 6.2 Executable ownership separator

Every child in an executable path is separated by `.`:

```text
Method()
Method().Local()
Method().Local().<lambda#1>
Method().<lambda#1>.NestedLocal()
Method().<anonymous-method#2>
```

The old child spelling is invalid:

```text
Method()::Local()              # invalid
Method()::<lambda#1>           # invalid
```

The first executable segment belongs directly to the selected type. Every
later segment MUST be an immediate stored lexical child of the preceding
segment. A textual prefix alone is never sufficient proof of ownership.

### 6.3 Informative grammar

The following EBNF is informative about structure. The lexical scanner rules
below are normative and take precedence where balanced C# syntax is involved.

```text
symbol-selector       = explicit-selector | csharp-selector ;

explicit-selector     = namespace-selector, "::",
                        type-selector, "::",
                        executable-selector ;

csharp-selector       = dotted-type-selector, "::",
                        executable-selector ;

namespace-selector    = global-namespace | hierarchy-pattern ;
global-namespace      = "global" ;

type-selector         = hierarchy-pattern ;
dotted-type-selector  = hierarchy-pattern ;

executable-selector   = executable-segment,
                        { ".", executable-segment } ;

executable-segment    = named-callable
                      | special-callable
                      | lambda-marker
                      | anonymous-method-marker
                      | initializer-marker
                      | top-level-marker ;

named-callable        = identifier,
                        [ generic-parameter-list ],
                        [ parameter-list ] ;

special-callable      = "[", special-tag, "]",
                        [ generic-parameter-list ],
                        [ parameter-list ] ;

lambda-marker         = "<lambda#", positive-integer-or-star, ">" ;
anonymous-method-marker
                      = "<anonymous-method#",
                        positive-integer-or-star, ">" ;
initializer-marker    = "<initializer:", identifier, ">" ;
top-level-marker      = "<top-level-statements>" ;
```

`identifier` means a C# identifier, including an escaped identifier such as
`@class`. Concrete output MUST escape C# keywords when they are identifiers.
Grammar keywords and marker/tag names are lowercase ASCII and are not affected
by any case-insensitive search option.

The leading `@` is a C# lexical escape and is not part of semantic identifier
identity. An input `Name` and `@Name` denote the same identifier when both are
lexically valid; concrete output uses `@` only where C# escaping is required.
The explicit-namespace token `global` remains the documented exception:
`global` selects the global namespace and `@global` selects a namespace whose
identifier text is `global`.

### 6.4 Balanced lexical scanning

The parser MUST scan the selector once while tracking balanced constructs. It
MUST NOT use `Split('.')`, `Split("::")`, a last-dot heuristic, or a regex as
the structural parser.

The scanner must distinguish separators from punctuation inside:

- `<...>` generic parameter/type argument syntax;
- `(...)` parameter and tuple syntax;
- `[...]` special segments and array-rank syntax;
- nullable `?` and pointer `*` type syntax;
- function-pointer syntax;
- escaped identifiers;
- `global::` type qualifiers.

Dots in all of the following are atomic with respect to executable ownership:

```text
M(System.Collections.Generic.List<string>)
[explicit:System.IDisposable.Dispose]()
[get:System.Collections.Generic.IReadOnlyList<int>.Item](int)
```

The parser MUST report an unmatched or misordered delimiter as a usage/query
parse error. It MUST NOT attempt a best-effort recovery.

## 7. Namespace and type selectors

### 7.1 Explicit form

The explicit form fixes the namespace/type boundary syntactically:

```text
Game.Core::Player.Inventory::Load()
```

With no wildcard, namespace and type constraints are exact after semantic
identifier, generic-arity, and configured case comparison. A no-match is a
no-match; the resolver MUST NOT reinterpret the input as csharp style.

The exact global namespace spelling is:

```text
global::Program::<top-level-statements>
global::Player::Run()
```

If a source namespace identifier is literally named `global`, concrete output
uses the escaped identifier `@global`; unescaped `global` in the explicit
namespace field means the global namespace.

An explicit namespace wildcard uses `**`, not `*`:

```text
**::Player.Inventory::Load()
```

`**` includes the global namespace because it may match zero namespace
components.

### 7.2 Csharp form and suffix resolution

The csharp type field is never pre-split into a guessed namespace prefix and
type suffix. The resolver compares it against each candidate's indexed
complete dotted type path.

```text
Game.Core.Player.Inventory::Load()
```

Even when the text appears fully qualified, csharp style is a suffix search.
For example, that selector may also match a candidate whose complete dotted
type path is:

```text
Company.Game.Core.Player.Inventory
```

This behavior is intentional and applies equally to copied csharp output.
Users who require an exact namespace use explicit style.

Namespace omission is therefore natural:

```text
Player.Inventory::Load()
```

It matches every indexed complete dotted type path ending in
`Player.Inventory`. This is semantically equivalent to:

```text
**::Player.Inventory::Load()
```

All matches are returned in canonical order. Identical semantic paths in
different indexed projects/profiles remain distinct logical candidates; path
syntax does not erase the existing project/profile scope.

### 7.3 Nested and generic types

Nested type components are separated by `.` in both styles:

```text
Game.Core::Outer<T>.Inner<U>::Run(T,U)
Game.Core.Outer<T>.Inner<U>::Run(T,U)
```

Generic arity is part of a type component. Omitting a type generic-parameter
list does not mean every arity:

```text
Repository             # exact non-generic type component
Repository<T>          # exact arity 1
Repository<T,U>        # exact arity 2
```

To search multiple type arities, use a type glob deliberately, for example
`--type 'Repository*'`. CSIndexer indexes source definitions, not constructed
type instantiations. A generic list in a type selector contains placeholder
identifiers, not concrete type arguments.

Generic placeholder names are normalized by ordinal. `Outer<T>` and
`Outer<U>` express the same arity-one definition pattern.

## 8. Executable paths and overload selection

### 8.1 Immediate containment

The first segment may be:

- a named member;
- a bracketed special callable;
- an initializer synthetic owner; or
- the top-level-statements synthetic owner.

Later segments may resolve only through persisted immediate containment. A
later named segment is normally a local function. A later anonymous marker is
an anonymous function owned directly by the previous segment. The resolver
MUST NOT search all descendants and accept a same-named grandchild without
matching the intervening segments.

Examples:

```text
N::T::Outer().Local().<lambda#1>
N::T::Outer().<lambda#1>.InsideLambda()
N::T::<initializer:Factory>.<lambda#1>
global::Program::<top-level-statements>.Local()
```

### 8.2 Parameter-list omission

Concrete output always includes a parameter list for every named or bracketed
callable. Query input may omit it independently at each callable segment.

```text
Outer.Local                   # every Outer overload and every matching Local
Outer(int).Local              # exact Outer(int), every matching Local overload
Outer.Local(string)           # every Outer overload, exact Local(string)
Outer(int).Local(string)      # exact at both segments
```

An empty `()` means exactly zero parameters. It never means “any overload.”

### 8.3 Generic callable arity

Callable generic syntax has a deliberate three-state rule:

```text
Method          # any parameter signature and any generic arity
Method<T>       # generic arity 1, any parameter signature
Method()        # non-generic, exactly zero parameters
Method<T>()     # generic arity 1, exactly zero parameters
Method(int)     # non-generic, exactly one int parameter
Method<T>(T)    # arity 1, parameter is placeholder ordinal 0
```

Once a parameter list is present, omission of `<...>` means generic arity
zero. This ensures emitted `Method()` is an exact non-generic query while bare
`Method` remains the intentionally broad “all overloads” shorthand.

Generic callable list entries are definition placeholders. Each entry must be
a C# identifier. Their names are normalized by ordinal, so these are
equivalent:

```text
Method<T>(T)
Method<U>(U)
```

Constructed generic-call notation is outside scope. `Method<System.String>`
does not mean “the construction of Method<T> with string”; it is invalid as a
definition-placeholder list.

### 8.4 C# parameter spelling and semantic identity

Concrete output uses source-oriented C# spelling with these rules:

- Prefer C# predefined/native aliases where available, such as `bool`, `int`,
  `nint`, `string`, and `object`.
- Fully qualify every other named type with its namespace and nested-type
  path. Do not add a leading `global::` to normal concrete output.
- Preserve generic structure, array rank, pointer syntax, tuple shape,
  function-pointer syntax, and nullable spelling.
- Omit parameter names.
- Omit return type from the symbol path.
- Omit `params`, `scoped`, extension-method `this`, optional markers, and
  default values.
- Preserve the actual `ref`, `out`, `in`, or `ref readonly` mode and require
  that mode to match.
- Omit tuple element names from identity and concrete path output.

Input accepts a C# alias or the corresponding framework type spelling. For
example, `int`, `System.Int32`, and `global::System.Int32` resolve to the same
semantic type identity.

Named, non-alias input types MUST be fully qualified. An unqualified non-alias
named parameter/conversion type is a query error; the resolver MUST NOT guess
between simple type names. Generic placeholder references are the exception
because they bind by ordinal within the path segment.

Additional identity rules:

- `dynamic` and `object` are the same overload identity. Concrete output may
  retain `dynamic` when the source declaration used it.
- Nullable-reference `?` is retained in concrete output but ignored for
  overload identity.
- Nullable-value types remain distinct: `int` and `int?` do not match.
- Generic placeholder names are ignored but their ordinal use is significant.
- A conversion target type is part of the special-callable identity even
  though return types are otherwise omitted.
- No wildcard is interpreted inside parameter type syntax. `int*` is a
  pointer type. To match every parameter signature, omit the parameter list.

## 9. Special, anonymous, and synthetic segments

### 9.1 Segment classes

The notation deliberately separates three classes:

```text
Name(...)        named member or local function
[...](...)       source-backed special callable
<...>            anonymous or synthetic owner marker
```

Square-bracket contents are one atomic segment. Dots and `::` within the
brackets never split the executable path.

### 9.2 Normative special-callable catalog

The initial implementation MUST support exactly these tags:

```text
# Constructors and finalizer
[constructor](int,string)
[static-constructor]()
[destructor]()

# User-defined operators
[operator:+](T,T)
[checked-operator:+](T,T)

# User-defined conversions
[conversion:implicit:int](T)
[conversion:explicit:string](T)
[checked-conversion:explicit:int](T)

# Property and indexer accessors
[get:Name]()
[set:Name](string)
[init:Name](string)
[get:Item](int)
[set:Item](int,string)

# Event accessors
[add:Changed](System.EventHandler)
[remove:Changed](System.EventHandler)

# Explicit interface method implementation
[explicit:System.IDisposable.Dispose]()
```

The operator token after `operator:` or `checked-operator:` is emitted using
the C# operator token. Because it is inside brackets, `*` in `[operator:*]` is
literal operator syntax, not a glob wildcard.

The conversion target type after the final colon is mandatory and follows the
same canonical type-spelling and semantic-identity rules as parameter types.

For a default indexer, the accessor member name is its semantic metadata name
`Item`. If source metadata changes the indexer name, emit that semantic name.
Indexer parameters precede the setter value parameter.

An explicit interface accessor uses the accessor tag with the fully qualified
interface member as its payload:

```text
[get:Game.Contracts.IPlayer.Name]()
[set:Game.Contracts.IPlayer.Name](string)
[add:Game.Contracts.IEvents.Changed](System.EventHandler)
```

An explicit interface generic method puts the generic placeholder list after
the bracket:

```text
[explicit:Game.Contracts.IMapper.Map]<T>(T)
```

Special callables follow the same parameter-list omission and generic-arity
rules as named callables where that C# construct permits generics.

### 9.3 Initializers and top-level statements

Initializer and top-level owners are explicit synthetic index nodes:

```text
N::T::<initializer:Field>
N::T::<initializer:Property>
N::T::<initializer:Changed>
global::Program::<top-level-statements>
```

An initializer marker names the declared field, property, or event whose
initializer expression it owns. It has no parameter list. Lambdas within the
initializer are children of that marker.

Top-level statements use the compiler's global `Program` owner and the single
marker `<top-level-statements>`. Local functions and anonymous functions in
top-level code are its lexical children.

### 9.4 Lambdas and anonymous methods

Lambda expressions and `delegate` anonymous methods have distinct concrete
markers:

```text
<lambda#1>
<anonymous-method#2>
```

Within one immediate lexical owner, both syntax kinds share one source-order
ordinal sequence. The counter starts at 1 for each owner. Nested anonymous
functions belong to their immediate anonymous owner and use that owner's
sequence.

Example:

```csharp
void Run()
{
    Action a = () => Work();
    Action b = delegate { Work(); };
    Action c = () => Work();
}
```

Concrete paths:

```text
Run().<lambda#1>
Run().<anonymous-method#2>
Run().<lambda#3>
```

A concrete ordinal is a positive integer. Pattern input may use `*` in the
ordinal position:

```text
Run().<lambda#*>
Run().<anonymous-method#*>
```

Both kinds belong to CLI `--kind lambda`. Their distinct marker allows users
to search either C# syntax without adding a new kind value.

Ordinal stability is guaranteed only within one indexed snapshot. Resolution
always validates the stored containment edge; it never trusts a marker text
under a different owner.

## 10. Source callable coverage

The inclusion principle is: index a callable when the user can point to a
corresponding source declaration or to one of the two explicitly designed
synthetic source owners.

### 10.1 Included

- ordinary methods;
- local functions;
- instance, static, and primary constructors;
- destructors;
- user-defined operators and conversions;
- explicit interface implementations;
- interface, abstract, extern, and partial method declarations even when they
  have no executable body;
- explicitly written auto-property `get;`, `set;`, and `init;` accessors;
- body-bearing property, indexer, and custom-event accessors;
- expression-bodied property/indexer getters;
- simple and parenthesized lambda expressions;
- `delegate` anonymous methods;
- field, property, and event initializer synthetic owners;
- the top-level-statements synthetic owner.

Primary constructors on classes, structs, and records are source-declared and
use `[constructor](...)`.

### 10.2 Excluded

- an implicit default constructor with no source declaration;
- compiler-generated backing fields;
- implicit add/remove methods for field-like events;
- async/iterator state-machine methods such as generated `MoveNext`;
- closure/display classes and their generated methods;
- record-synthesized equality, hash, print, clone, and similar members;
- accessors synthesized for record positional properties when there is no
  direct accessor source syntax;
- any other callable that exists only as a compiler artifact.

Initializer and top-level nodes are the only exceptions to the “direct
callable declaration” rule, because they are deliberate source-ownership
nodes defined by this design.

## 11. Kind and async-status semantics

No new `--kind` values are introduced.

```text
--kind method
```

includes named members, local functions, constructors, destructors,
operators, conversions, accessors, and explicit interface implementations.

```text
--kind lambda
```

includes lambda expressions and `delegate` anonymous methods.

```text
--kind all
```

includes method, lambda, initializer, and top-level executable kinds. It is
the default and is semantically identical to omitting `--kind`.

Initializer and top-level nodes are selected directly with their path markers
when a caller needs those exact kinds; there is no `initializer` or
`top-level` kind option in this revision.

`--async-status async|sync|all` applies directly to every executable kind:

- `async`: stored direct `AsyncRole != None`;
- `sync`: stored direct `AsyncRole == None`;
- `all`: no direct async-role predicate.

`all` is the default and is identical to omission. A synchronous owner does
not become `async` merely because a child lambda/local function is async. A
field initializer containing an async lambda remains sync while the lambda is
async. Top-level code with direct await is async. This rule forbids owner
leakage.

## 12. Typed matcher conditions

### 12.1 Option families

The concise option in each family uses glob:

```text
--namespace <glob>
--type <glob>
--method <glob>
--file <glob>
--include <glob>
--exclude <glob>
```

Each family also has explicit literal and regex forms:

```text
--namespace-literal <text>    --namespace-regex <expression>
--type-literal <text>         --type-regex <expression>
--method-literal <text>       --method-regex <expression>
--file-literal <text>         --file-regex <expression>
--include-literal <text>      --include-regex <expression>
--exclude-literal <text>      --exclude-regex <expression>
```

Punctuation never implicitly switches a condition to regex. The legacy query
options `--regex` and `--ignore-case` are removed and are not aliases.

The existing `index --exclude` remains an index-input exclusion option. It is
command-local and is not the query/source-text `--exclude` family described
here. `index` rejects the other new typed query conditions.

### 12.2 Independent case options

```text
--namespace-case strict|ignore
--type-case strict|ignore
--method-case strict|ignore
--file-case strict|ignore
--source-case strict|ignore
```

All default to `strict`. Each applies to glob, literal, and regex occurrences
in its category and to the corresponding semantic category in a positional
structured selector.

`strict` uses ordinal comparison. `ignore` uses ordinal case-insensitive
comparison. Regex adds `CultureInvariant` and, for `ignore`, `IgnoreCase`.
Each case option may occur at most once; any duplicate is a usage error rather
than “first wins” or “last wins.”

C# grammar keywords, predefined type keywords, special-tag names, and marker
names remain lowercase and case-sensitive. Case modes apply to identifiers,
not grammar tokens.

For a csharp dotted selector, the resolver aligns the pattern to each complete
candidate path before applying case rules. Candidate namespace components use
`namespace-case`; candidate type components use `type-case`. The input is not
pre-split using capitalization or indexed simple-name guesses.

Within an explicit-interface special segment, interface namespace/type
identifiers use namespace/type case rules and the member identifier uses the
method case rule. Named types inside parameter/conversion types are resolved
with namespace/type case rules before semantic identity comparison.

That semantic subcategory rule applies to positional selectors and structured
glob/literal method conditions. `--method-regex` is intentionally raw: its one
regex is evaluated against the complete canonical executable text, and
`--method-case` controls the case behavior of that entire regex. Namespace/type
case options do not rewrite subranges of a method regex.

### 12.3 Match domains

| Category | Value matched | Anchoring |
| --- | --- | --- |
| namespace | complete semantic namespace path | whole value |
| type | complete outer-to-inner type path | whole value |
| method | complete member-to-leaf executable path | whole value |
| file | stored storage-root-relative forward-slash path | whole value |
| include | stored normalized source text | substring/search |
| exclude | stored normalized source text | substring/search |

Name/file regex is wrapped as `\A(?:<user-expression>)\z` for a whole-value
match. Source regex uses
`Regex.IsMatch` semantics and is unanchored unless the user supplies anchors.
Every production regex uses the existing two-second timeout and
`CultureInvariant`. A timeout aborts the query; it is not treated as a
non-match.

Source conditions evaluate the normalized source stored in the index. They do
not reopen source files at query time. Consequently, `--base-dir` affects path
reconstruction and source-reading commands but does not change source-filter
text.

### 12.4 Structural glob semantics

Only `*` and `**` are glob metacharacters. Regex metacharacters have no special
meaning in glob mode. To search a literal `*` in a source value, use the
corresponding `-literal` option.

For namespace, type, and method hierarchy patterns:

- `*` inside one parsed component/segment matches zero or more characters but
  does not cross its hierarchy boundary;
- a whole component/segment equal to `*` matches exactly one hierarchy level;
- a whole component/segment equal to `**` matches zero or more hierarchy
  levels;
- `**` embedded inside a component is equivalent to `*`, not recursive;
- balanced C# punctuation within a method segment is not a hierarchy boundary.

Examples:

```text
--namespace 'Game.*'              # exactly one child below Game
--namespace 'Game.**'             # Game and every descendant
--type 'Outer.*'                  # one nested-type level
--type '**.Controller'            # Controller at any nested depth
--method 'Method'                 # root member Method, all overloads
--method '**.Method'              # Method at any executable depth
--method 'Outer.*'                # one immediate executable child
--method 'Outer.**.<lambda#1>'     # lambda#1 at any depth under Outer

# Complete structured-selector examples
Namespace1.Namespace1_2::*::**.Method2.<lambda#1>
**::Class1::Method2.<lambda#1>
```

The `--method` glob and literal forms parse the same executable-path structure
as a positional selector. Parameter-list omission and generic-arity rules
therefore still apply. “Literal” disables identifier wildcards; it does not
turn the condition into unparsed raw-string equality.

`--method-regex` is different: it evaluates one regex against the complete
canonical executable-path text, using canonical C# aliases and fully
qualified non-alias types. It does not perform signature omission expansion.

For storage-relative file paths:

- input `\` is normalized to `/`;
- `*` does not cross `/`;
- a whole path component `**` spans zero or more directory components;
- an embedded `**` is equivalent to `*`.

```text
src/*/Player.cs       # one directory level
src/**/Player.cs      # any depth, including none
```

Source glob is intentionally non-structural and unanchored:

- `--include await` means “contains `await`”;
- `*` and `**` are equivalent and match zero or more characters;
- the match may cross CR, LF, CRLF, NEL, U+2028, and U+2029;
- literal mode is exact substring search;
- regex mode is unanchored `Regex.IsMatch`.

### 12.5 Condition composition

Repeated namespace, type, method, or file conditions are ORed within their
category, including mixtures of glob, literal, and regex.

Different categories are ANDed.

Repeated source includes are ANDed. Repeated source excludes are ORed and
reject a declaration if any one matches. Excludes may be evaluated before
includes as an optimization, but the observable boolean result must be:

```text
(namespace alternatives)
AND (type alternatives)
AND (method alternatives)
AND (file alternatives)
AND include1
AND include2
AND NOT (exclude1 OR exclude2)
AND kind
AND async-status
```

For logical-symbol commands, namespace/type/method/kind/async predicates apply
to the logical symbol. File/include/exclude predicates are declaration-scoped:
all declaration-scoped predicates must be satisfied by the same associated
declaration row. If at least one declaration row passes, the logical symbol is
selected exactly once. Conditions MUST NOT combine an include found only in a
partial definition with another include found only in its implementation.

`source search` is location-oriented and returns every declaration row that
passes. Other symbol, relation, and graph commands project passing declaration
rows to their logical symbol and de-duplicate before cardinality checks.

## 13. Command option and root-selection contract

### 13.1 Selector/filter matrix

| Command form | Positional selector | Typed root conditions | Cardinality |
| --- | --- | --- | --- |
| `symbol find` | optional | all | selector or at least one selection condition required; `--require-single` optional |
| `symbol list` | none | all | zero or more logical symbols |
| `source search` | none | all | at least one selection condition required; returns matching declaration rows |
| `source show` | required | all, AND refinement | exactly one logical root |
| `definition <query>` | required | all, AND refinement | multiple roots allowed unless `--require-single` |
| `references <query>` | required | all, AND refinement | multiple roots allowed unless `--require-single` |
| `callers <query>` | required | all, AND refinement | multiple roots allowed unless `--require-single` |
| `callees <query>` | required | all, AND refinement | multiple roots allowed unless `--require-single` |
| `overrides <query>` | required | all applicable method conditions | multiple roots allowed unless `--require-single` |
| `async tree <query>` | required | all, AND refinement | exactly one logical root |
| `callers tree <query>` | required | all, AND refinement | exactly one logical root |
| `definition --at <location>` | forbidden | forbidden | source position determines context |
| `conditions` | none | forbidden | command-specific behavior only |
| `index` | input path, not selector | new query conditions forbidden | command-specific behavior only |

“All” typed root conditions means namespace, type, method, file, include,
exclude, their literal/regex variants, their case modes, kind, and
async-status, subject to a command's semantic applicability.

A `symbol find` or `source search` selection condition is any explicit
namespace/type/method/file/include/exclude condition or an explicit
kind/async-status predicate. Output-formatting, database, profile, path-style,
and help options do not count as selection conditions.

`overrides` rejects a lambda-only root because anonymous functions do not
participate in C# override relations. Existing command-specific options such
as override expansion, dispatch, caller scope, or lambda-call inclusion retain
their current semantic role and are applied after the new root selection.

### 13.2 Existing command-specific options

This design does not remove unrelated established command behavior. Unless a
normative rule above explicitly replaces it, existing options such as
`--include-overrides`, `--dispatch`, `--caller-scope`,
`--exclude-lambda-calls`, `--async-involved`, generated-source filters,
`--show-source`, `--source-layout`, output formats, diagnostics, profiles, and
atomic output files retain their documented command scope and semantics.

Root predicates provided by those options, including `--async-involved` and
generated-source filters, are ANDed with the new typed root conditions and run
before `--require-single`. Expansion/traversal controls such as
`--include-overrides`, dispatch, caller scope, and lambda-call inclusion run
only after root selection. This paragraph does not make an option valid on a
new command; its existing command matrix remains authoritative unless this
document explicitly changes that row.

### 13.3 Root-only filtering

For definition, reference, caller, callee, override, and graph commands, all
typed conditions select starting logical roots before traversal. They MUST NOT
filter secondary definitions, calls, callers, callees, override descendants,
or graph nodes/edges.

Examples:

- A caller's file need not match the root's `--file` condition.
- A graph descendant's source need not contain the root's `--include` text.
- `definition` returns all declaration rows for a selected logical root even
  when one declaration row was the row that satisfied a file/source filter.

Filters, kind, and async-status run before `--require-single` or mandatory
single-root validation. Partial definition and implementation never count as
two roots.

### 13.4 Option scope for presentation and paths

`--symbol-path-style csharp|explicit` and `--short-names` are accepted by every
command/result format that emits a human-facing symbol name. They are rejected
where a command has no symbol-bearing payload.

`--base-dir` and `--path-style absolute|relative` are accepted by commands that
open an existing database and may read or display source-derived paths,
including `conditions`. `index` rejects `--base-dir`; it establishes the
storage root. `index` also rejects query-only path presentation options.

All command help MUST publish the exact command-specific matrix. An option
accepted by one command MUST remain an unknown option on a command for which
this specification marks it forbidden.

## 14. Logical callable and declaration data model

### 14.1 Required conceptual entities

Schema v5 MUST distinguish logical identity from physical source declaration.
The exact SQL names may follow repository conventions, but the model must be
equivalent to:

```text
logical callable symbol
  id
  project/profile scope
  semantic stable key
  namespace path
  type path
  executable path components
  callable kind and method kind
  semantic signature data
  direct async role
  preferred declaration id (nullable during construction)

callable declaration
  id
  logical symbol id
  document id
  declaration role
  source start and length
  normalized source and hash
  generated-source flag
```

The declaration-role domain is exactly:

```text
ordinary
partial-definition
partial-implementation
```

The role MUST be stored as typed data, not inferred later from source text,
path order, body presence, or a display-name suffix.

### 14.2 Invariants

- Every ordinary source callable has one logical symbol and an ordinary
  declaration row.
- A partial definition and its implementation share one logical symbol.
- The partial implementation is the preferred declaration when present.
- A partial definition without an implementation remains a valid logical
  symbol and is its own preferred declaration.
- Logical callable properties derived from executable source, including direct
  async role, normalized source, generated flag, and normal display location,
  come from the preferred declaration. Definition/source commands may still
  expose per-declaration values.
- A partial pair's logical stable key is independent of which part supplies
  the preferred declaration, absolute path, display style, and partial role.
- When semantic identity alone cannot distinguish source callables, such as
  same-path local declarations or anonymous/synthetic nodes, a logical or
  declaration key may include only stored relative path, span, and ordinal
  discriminators; it MUST never include a machine-specific absolute path.
- Calls, call bindings, interface bindings, overrides, async graph edges, and
  containment edges reference logical symbol IDs.
- Partial pairing is represented by the shared logical ID and declaration-role
  rows. It MUST NOT be represented as a logical self-edge between a definition
  and implementation that are already the same logical symbol.
- Extraction MUST normalize Roslyn partial-definition/implementation symbols
  to the same logical identity before persisting calls or relations.
- Local functions and anonymous functions in a partial method body belong to
  the logical method through the implementation declaration.
- A bodyless partial definition contributes no executable-body call edges.

The indexer may use a two-pass insertion/update to establish preferred
declaration IDs. It MUST NOT temporarily expose a committed database in which
partial rows are separate logical functions.

### 14.3 Query projection

- `symbol find` and `symbol list` return one logical symbol. Its normal
  location is the preferred declaration.
- `source show` displays the preferred implementation source; if absent, it
  displays the definition source.
- `definition` returns every associated declaration location and exposes its
  declaration role in table/text and JSON output.
- `source search` may return both physical declaration locations because its
  result unit is a source match, not a logical-function count.
- Callers, callees, references, overrides, and graphs operate only on logical
  IDs and therefore never duplicate a partial callable.

Any JSON object whose result unit is a declaration row MUST contain:

```text
declarationRole: "ordinary" | "partial-definition" | "partial-implementation"
```

Definition text/table records MUST expose the same value outside the symbol
path, using a dedicated declaration-role field/column rather than altering the
path. Source-search records MUST expose it when they represent a callable
declaration. Logical-symbol rows do not acquire a partial-role suffix; their
normal location continues to be the preferred declaration.

Definition declaration rows sort `partial-definition` before
`partial-implementation`, then by stored path and source position. This order
does not alter which declaration is preferred for execution-oriented commands.
Source-search rows sort by the logical canonical key and then declaration path,
source position, and declaration role.

## 15. Portable filesystem persistence

### 15.1 Standard layout

`index` MUST use the existing input resolver's resolved `RootPath` as the
storage root. A custom `--db` location changes only the database location and
the relative anchor; it MUST NOT redefine the storage root.

Given:

```text
database:     D:/Work/Game/.csindex/index.sqlite
storage root: D:/Work/Game
document:     D:/Work/Game/src/play.cs
```

persist:

```text
index-root anchor: ..
document path:     src/play.cs
```

Every persisted filesystem-derived value uses a forward-slash path relative
to the storage root or, for the root anchor, relative to the database
directory. This includes:

- document paths;
- project paths;
- persisted input-root representation;
- source-derived path components in stable/declaration keys; and
- the index-root anchor.

No row may persist a machine-specific absolute source, project, or input-root
path.

### 15.2 Custom database anchor

For a custom database, `index` stores the route from the database directory to
the storage root:

```text
database:     D:/Indexes/Game/index.sqlite
storage root: D:/Work/Game
anchor:       ../../Work/Game
document:     src/play.cs
```

If the database and source tree move while preserving their relative layout,
the anchor resolves to the new storage root automatically.

### 15.3 Linked sources and volume/share constraint

A source or project outside the storage root may use leading `../` components:

```text
../Shared/Generated/Bindings.cs
```

This is supported only when the database directory, storage root, projects,
and all persisted source paths are on the same filesystem volume or the same
UNC server/share. If any required relative path crosses a drive volume or UNC
share boundary, `index` fails before modifying the database and reports the
offending paths and the same-volume/share requirement.

The initial schema has no root-ID or absolute-path escape hatch. An agent MUST
NOT silently store an absolute path for an out-of-volume document.

Path normalization is lexical and platform-aware. It resolves `.`/`..`, drive
roots, UNC roots, and accepted separator styles but does not require resolving
filesystem symlinks or junction targets. Stored casing is preserved.

### 15.4 Query-time reconstruction

Without an override:

```text
effective base = FullPath(database directory + stored anchor)
absolute path  = FullPath(effective base + stored relative path)
```

With an override:

```text
effective base = FullPath(--base-dir)
absolute path  = FullPath(effective base + stored relative path)
```

`--base-dir`:

- is query/read time only;
- does not update the database, anchor, stable keys, declaration keys, cache
  identity, or `--db` location;
- accepts either slash style and is normalized to an absolute path;
- need not exist for an operation that only formats a path;
- produces the command's normal missing-file error when a command must open a
  source file and the reconstructed file is absent;
- does not impose a containment check after combining the path, because a
  stored leading `../` linked source is valid.

### 15.5 Path display and source-location input

```text
--path-style absolute    # default
--path-style relative
```

Absolute style uses the effective base, including `--base-dir`. Relative style
emits the stored forward-slash path and is unaffected by `--base-dir`.

The selected style applies consistently to:

- table/text location fields;
- JSON `location.path` fields;
- graph locations;
- source headers; and
- diagnostics that expose a persisted source path.

Inputs naming an indexed source location, including `definition --at`, accept
an absolute path or an effective-base-relative path. Relative input is joined
to the effective base. Absolute input is converted to the same stored-relative
form before comparison. If it cannot be relativized because it is on a
different volume/share, the command reports a path/query error rather than
falling back to absolute string comparison.

File conditions always match the stored relative form, never the reconstructed
absolute display form.

## 16. Display formatting and deterministic ordering

### 16.1 Symbol path style

```text
--symbol-path-style csharp    # default
--symbol-path-style explicit
```

The option affects presentation only. Both input forms are accepted regardless
of the selected output style.

```text
# csharp
Game.Core.Player.Inventory::Load(int).Validate()

# explicit
Game.Core::Player.Inventory::Load(int).Validate()
```

One shared formatter MUST be used by table/text rows, JSON `displayName` and
other human-facing name fields, graph labels, source headers, diagnostics, and
ambiguity candidate lists. An output family MUST NOT keep formatting an old
stored `display_name` string independently.

Canonical semantic/storage fields in JSON remain semantic data. The display
option changes only documented presentation fields; it does not rewrite IDs,
stable keys, namespace fields, parameter semantic data, or stored rows.

### 16.2 Short names

`--short-names` omits only the owner namespace:

```text
# csharp + short
Player.Inventory::Load(System.Guid)

# explicit + short
**::Player.Inventory::Load(System.Guid)
```

The explicit short form deliberately contains a namespace wildcard and may
resolve to multiple namespaces when copied back as input. `--short-names` does
not shorten fully qualified non-alias parameter types, conversion target
types, or explicit-interface payload types.

### 16.3 Canonical order

Every unordered logical-symbol result uses this ordinal, case-sensitive sort
key:

1. complete semantic namespace path;
2. complete outer-to-inner type path with generic arity;
3. complete executable path and semantic signature;
4. preferred declaration's stored relative path;
5. preferred declaration's source start;
6. logical stable key.

This order MUST NOT change with:

- csharp versus explicit output;
- short versus full names;
- absolute versus relative paths;
- `--base-dir`;
- strict versus ignore search mode; or
- table, JSON, tree, line, or Mermaid output format.

Trees are parent-first. Siblings use the canonical symbol key. Graph node and
edge collections use deterministic semantic keys so equivalent output formats
represent the same ordered logical result.

## 17. Help contract

The canonical verbose-help spelling is:

```text
--help --verbose
```

The shorthand is:

```text
--help-verbose
```

For the same recognized global or command scope, both produce identical
content. Help flags are order-independent.

Examples:

```text
csindex --help --verbose
csindex symbol find --help-verbose
csindex source search --help --verbose
```

Verbose help MUST include:

- csharp and explicit grammar;
- the suffix-search warning for copied csharp paths;
- namespace omission and exact global namespace;
- `*` and `**` hierarchy behavior;
- overload and generic-arity omission rules;
- the complete bracketed special-callable catalog;
- lambda, anonymous-method, initializer, and top-level markers;
- kind and direct async-status behavior;
- literal/glob/regex option families and case categories;
- condition composition;
- command-specific option scope and root-only filtering;
- partial logical-symbol behavior;
- root-relative persistence, `--base-dir`, and `--path-style`;
- several copyable valid examples and representative invalid examples.

For a recognized command path, a help request is terminal before required
positional validation, database opening, source opening, or output-destination
creation. CLI tokenization and the recognized command's allowed-option set are
still validated first: an unknown option remains a usage error, while a known
operational option such as `--output-file` is ignored after help dispatch and
never opens its destination. Help writes to stdout and exits successfully. An
unknown command path still reports a usage error.

`index --verbose` without help retains its existing runtime progress meaning.
With `--help`, it requests verbose help. Query command `--verbose` without help
remains unknown unless that command independently documents a runtime verbose
option.

## 18. Schema compatibility and index behavior

Schema version 5 is required for all behavior in this document.

- There is no schema migration.
- A query against schema 4 or older fails without modifying the database.
- `index` opening an existing incompatible database also fails without
  deleting, truncating, rebuilding, or otherwise modifying it.
- The error instructs the user to delete or rename the old database, or choose
  a new `--db` path, and then explicitly run `csindex index`.
- `--rebuild` does not authorize an implicit incompatible-schema migration or
  deletion unless a later separately approved design changes that rule.
- A new schema-5 index is committed transactionally only after path-volume
  validation and logical/declaration consistency checks succeed.

The analysis/cache version must change independently of the schema constant as
needed so a schema-5 index cannot reuse analysis artifacts containing old
display paths, absolute persisted paths, old anonymous markers, or split
partial identities.

## 19. Error and exit behavior

The implementation preserves the current exit-code categories:

```text
0  success
2  invalid arguments or query
3  input, analysis, cancellation, or output failure
4  SQLite or schema failure
5  --require-single failure
```

### 19.1 Usage/query errors

Exit 2 includes:

- unknown or command-inapplicable options;
- removed legacy matcher options;
- old or malformed path grammar;
- zero or too many top-level separators;
- empty namespace/type/executable fields;
- empty executable segments;
- unmatched or misordered balanced delimiters;
- invalid generic placeholder lists;
- invalid anonymous ordinals;
- invalid glob hierarchy syntax;
- invalid regex syntax;
- duplicate case-mode options;
- forbidden selector/filter combinations such as `definition --at` plus name
  filters;
- `--base-dir` on `index`.

A syntactically valid selector with no indexed candidate is not a parse error.
List/search commands return an empty successful payload. A command that
requires an actual root reports its normal no-match query failure.

### 19.2 Ambiguity

Mandatory-single-root commands report an ambiguity query error when more than
one logical root remains. Commands with `--require-single` return exit 5 when
the post-filter logical root count is not exactly one.

Candidate lists use canonical order and show enough path/location information
to distinguish logical candidates. Partial definition and implementation are
never listed as separate candidates.

### 19.3 Regex, cancellation, source, and output failures

- Regex timeout aborts the query with no partial payload.
- Cancellation checks occur during parsing of large candidate sets, matching,
  traversal, ordering, formatting, flush, and immediately before atomic
  commit.
- A reconstructed missing source file is reported only by a command that must
  read that file.
- Invalid/missing `--base-dir` content is a path/input error as appropriate;
  the database is never rewritten.
- Output-file creation remains lazy.
- Query, formatting, flush, cancellation, replace, or commit failure preserves
  an existing destination and removes owned temporary files where possible.
- Help never creates an output destination.

## 20. Component boundaries

The implementation plan must preserve these responsibilities. Names may adapt
to repository conventions, but responsibilities MUST NOT be collapsed into
ad-hoc string handling in `Program` or `OutputFormatter`.

### 20.1 `SymbolPathParser`

- Performs balanced lexical scanning.
- Determines csharp versus explicit form.
- Produces an immutable structured AST.
- Represents omitted versus present parameter/generic lists distinctly.
- Does not access the database or decide candidate matches.

### 20.2 `SymbolSignatureCanonicalizer`

- Produces canonical type/signature identity from Roslyn symbols.
- Formats concrete C# parameter text.
- Normalizes aliases, generic placeholder ordinals, nullability identity,
  ref-kind, arrays, pointers, tuples, and function pointers.
- Is shared by indexing, resolver comparison, and formatting.

### 20.3 `TypedConditionCompiler`

- Validates option-specific literal/glob/regex input.
- Compiles category-aware matchers and case behavior.
- Owns regex timeout behavior.
- Does not traverse relations or format output.

### 20.4 `SymbolPathResolver`

- Retrieves candidate logical symbols for a profile.
- Performs explicit exact-boundary or csharp suffix matching.
- Applies candidate-aware namespace/type case rules.
- Validates every executable child through stored immediate containment.
- Projects passing declaration rows to logical symbols.
- Normalizes partial roots to the logical implementation-backed identity.

### 20.5 `LogicalSymbolRepository`

- Persists logical callable and declaration rows.
- Enforces declaration roles and preferred-declaration invariants.
- Ensures calls/relations reference logical IDs.
- Exposes declaration rows to definition/source operations without creating
  duplicate logical callables.

### 20.6 `SymbolPathFormatter`

- Formats a logical path in csharp or explicit style.
- Applies only the documented short-name transformation.
- Uses the signature canonicalizer rather than stored legacy display text.
- Is the only formatter for human-facing symbol names.

### 20.7 `IndexPathResolver`

- Owns storage-root and database-directory normalization.
- Validates same-volume/share relative paths.
- Creates and resolves the index-root anchor.
- Normalizes stored separators.
- Applies query-time base override and display style.
- Converts absolute/relative source-location input to stored identity.

### 20.8 Query orchestration

Every command follows this order unless its help path exits earlier:

```text
parse CLI
→ validate command option scope
→ open/check schema and profile
→ parse selector and compile typed conditions
→ select declaration rows and logical roots
→ apply kind/async and de-duplicate logical roots
→ apply mandatory/optional cardinality check
→ execute command-specific traversal
→ canonical sort
→ format through shared formatters/path resolver
→ flush and atomically commit output
```

No relation or graph traversal may start before root filters and logical
partial normalization finish.

## 21. Performance and safety constraints

- Name-only searches SHOULD use indexed semantic namespace/type/executable
  fields and MUST NOT scan stored source blobs unnecessarily.
- Declaration source is loaded/evaluated only when include/exclude conditions
  or source payloads require it.
- Regex evaluation always uses the bounded production timeout.
- Candidate enumeration and output use cancellation-aware loops.
- The structured parser and glob compiler must avoid unbounded backtracking.
- Path reconstruction never mutates stored values.
- A path-volume/share validation failure occurs before an index transaction can
  replace valid persisted data.
- Display style and short-name selection are pure formatting operations.
- Canonical sorting uses stored relative paths so relocating a project does not
  reorder otherwise identical results.

## 22. Required verification matrix

The later implementation plan must assign every requirement below to a focused
test before implementation code for that requirement.

### 22.1 Parser and formatter

- csharp and explicit concrete paths parse to equivalent semantic constraints;
- `A::B::C()` is unconditionally explicit;
- one versus two top-level separators are detected while nested `global::` is
  ignored structurally;
- executable children use `.` and old `::` child syntax is rejected;
- nested generics, tuples, arrays, pointers, function pointers, nullable types,
  and escaped identifiers do not split paths incorrectly;
- every concrete special segment parses and round-trips;
- csharp, explicit, csharp-short, and explicit-short formatting is exact;
- every concrete emitted path can be parsed again;
- malformed delimiters and empty fields fail with exact error categories.

### 22.2 Resolution and containment

- namespace-like and nested-type-like component names are not guessed;
- csharp full dotted input is still suffix matching;
- explicit input fixes the namespace/type boundary;
- namespace omission returns every namespace match;
- global namespace and a literal `@global` namespace remain distinct;
- each local/anonymous segment requires an immediate containment edge;
- same-named locals under another owner do not leak into results;
- concrete and wildcard anonymous ordinals resolve correctly;
- result paths copied from each output style are accepted as input;
- multiple project/profile candidates remain deterministic.

### 22.3 Signature identity

- bare method, generic-only method, `()`, generic `()`, and typed parameter
  forms implement the three-state arity rules;
- omission is tested independently at every local-function segment;
- aliases equal their framework spellings;
- all non-alias output types are fully qualified;
- generic placeholder names normalize by ordinal;
- constructed generic argument syntax is rejected/not misinterpreted;
- ref modes match exactly;
- nullable-reference annotation is display-only identity metadata;
- nullable-value, array rank, pointer, tuple shape, and function-pointer types
  remain distinct;
- conversion target type participates in identity.

### 22.4 Callable extraction and partial identity

- every included callable category has a source fixture and canonical path;
- every excluded compiler-generated category is absent;
- expression-bodied getter and auto accessor behavior is explicit;
- primary constructor and record-synthesized exclusions are distinguished;
- lambda and anonymous-method markers share source-order ordinals;
- nested ordinal counters reset per immediate owner;
- one partial definition/implementation pair creates one logical symbol and
  two role-tagged declaration rows;
- preferred declaration is the implementation;
- a definition-only partial callable remains valid;
- calls, callers, callees, overrides, interface bindings, and graph roots use
  the logical implementation identity once;
- `definition` returns both roles while `symbol list` returns one callable.

### 22.5 Typed matchers

- default glob, explicit literal, and explicit regex coexist in one command;
- each case category is independent and defaults to strict;
- candidate-aware csharp case mapping is verified across a suffix that spans
  namespace and type components;
- `*` and `**` semantics are covered for namespace, type, executable, and file
  hierarchies;
- method glob respects balanced signature punctuation;
- method literal retains parameter-omission semantics;
- method regex uses whole canonical executable text;
- source literal/glob/regex is unanchored and crosses every supported newline;
- invalid regex and a deterministic timeout abort without partial results;
- repeated same-category alternatives OR, categories AND, includes AND, and
  excludes OR;
- declaration-scoped conditions must all match one declaration row;
- passing partial declaration rows project to one logical result.

### 22.6 Command matrix and traversal

- every allowed option is accepted on every documented command form;
- every forbidden option is rejected on every other command form;
- selector/condition minimum requirements are exact;
- `definition --at` rejects selector filters;
- filters run before `--require-single` and graph single-root validation;
- root filters do not remove secondary relation/graph results;
- kind and direct async-status cover every executable category with no owner
  leakage;
- initializer/top-level are available through `all` and direct paths but no
  new kind option;
- `overrides` rejects lambda-only roots;
- no partial definition/implementation duplicate appears in cardinality.

### 22.7 Portable paths and schema

- standard `.csindex/index.sqlite` stores anchor `..` and root-relative paths;
- custom database stores the correct relative anchor;
- relocating database/source with the same relative layout reconstructs the
  new absolute paths;
- `--base-dir` overrides reconstruction without mutating any DB value;
- absolute and relative style is exact in table, JSON, graph, source header,
  and diagnostics;
- absolute and relative `--at` inputs resolve to the same stored document;
- linked leading-`../` source paths round-trip;
- different drive/UNC share indexing fails before DB mutation;
- no persisted path field contains an absolute machine path;
- schema 4 query/index attempts fail and preserve the old file;
- the error tells the user how to rebuild explicitly.

### 22.8 Ordering, help, and output safety

- canonical order is identical across style, short, path, base-dir, case, and
  output-format choices;
- tree parents precede canonically ordered children;
- partial declaration outputs have the specified role order;
- global and every command's normal/verbose help are synchronized;
- `--help --verbose` and `--help-verbose` are identical;
- help with missing required positional input opens no DB/source/output file;
- removed old grammar/options have explicit rejection tests;
- cancellation at final-record/flush/pre-commit boundaries preserves an
  existing output file;
- formatting, regex, path, and database failure emit no partial committed
  payload.

### 22.9 Final gates

Before completion, run fresh:

- focused unit/integration suites for every implementation task;
- the full solution test suite with zero failures and no feature-owned skips;
- Release build with zero errors and warnings;
- formatting verification;
- CLI help and representative positive/negative executable probes;
- schema/path inspection proving no absolute persisted paths;
- diff, scope, and temporary-artifact checks.

## 23. Implementation staging constraints

This document is one coherent schema-5 product design, but the later plan must
stage it in dependency order:

1. semantic path/signature data structures and source callable extraction;
2. logical/declaration storage and portable path schema;
3. parser, formatter, typed matcher, and resolver;
4. query-service and command-matrix integration;
5. output/help/documentation integration and acceptance closure.

Each implementation task must use RED-GREEN-REFACTOR and must state which files
and interfaces it owns. A task may rely only on interfaces completed by an
earlier task. It must not invent a temporary public grammar or compatibility
layer that a later task is expected to remove.

Because implementation subagents may have less reasoning capacity than the
primary agent, each later task brief must quote or point to the exact normative
sections it implements, list its positive and negative cases, name its command
matrix rows, and state all out-of-scope behavior. Any apparent contradiction
must be escalated to the primary agent before code changes.

## 24. Acceptance summary

The design is complete when an implementation satisfying it can demonstrate:

- one unambiguous structured model behind both csharp and explicit paths;
- exact explicit namespace resolution and documented csharp suffix behavior;
- editable/copyable concrete names for every supported source callable;
- independently typed, cased, and composed search conditions;
- root-only filtering and deterministic results on every command;
- one logical partial callable used by every call/relation/graph operation;
- role-preserving declaration lookup;
- zero machine-specific persisted source paths;
- movable standard/custom database layouts with an explicit query-time base
  override;
- fully synchronized help and output formats;
- preservation of atomic output and incompatible-database safety; and
- no old grammar, matcher switch, or schema fallback.
