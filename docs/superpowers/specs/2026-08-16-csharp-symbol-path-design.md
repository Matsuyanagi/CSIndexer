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

## Approved product choices

- The default output style is csharp.
- The explicit output style remains available for visual namespace/type
  separation.
- Both styles are accepted as input.
- Namespace omission is a wildcard and returns all matches.
- Output uses C# aliases where available.
- The prior grammar is intentionally replaced rather than supported as a
  compatibility mode.
- No implementation work begins until a separate implementation plan is
  approved.
