using System.Text.Json;
using System.Text.RegularExpressions;
using CsIndex.Core.Analysis;
using CsIndex.Core.Model;
using CsIndex.Cli;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

[Collection(CSharpSymbolPathAcceptanceCollection.Name)]
public sealed class TypedSearchAcceptanceTests(CSharpSymbolPathAcceptanceFixture fixture)
{
    private static readonly string[] NamespaceConditions =
    [
        "namespace", "namespace-literal", "namespace-regex", "namespace-case",
    ];

    private static readonly string[] TypeConditions =
    [
        "type", "type-literal", "type-regex", "type-case",
    ];

    private static readonly string[] MethodConditions =
    [
        "method", "method-literal", "method-regex", "method-case",
    ];

    private static readonly string[] FileConditions =
    [
        "file", "file-literal", "file-regex", "file-case",
    ];

    private static readonly string[] SourceConditions =
    [
        "include", "include-literal", "include-regex",
        "exclude", "exclude-literal", "exclude-regex", "source-case",
    ];

    private static readonly string[] RootConditions =
    [.. NamespaceConditions, .. TypeConditions, .. MethodConditions, .. FileConditions];

    private static readonly string[] AllConditions =
    [.. RootConditions, .. SourceConditions];

    private static readonly string[] QueryOptionFamilies =
    [
        .. AllConditions,
        "kind", "async-status",
        "symbol-path-style", "short-names", "base-dir", "path-style",
    ];

    private static readonly CommandScope[] CommandScopes =
    [
        new(
            "global",
            [],
            ["db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose"]),
        new(
            "symbol find",
            ["symbol", "find"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "require-single", "include-overrides", "show-source", "source-layout",
            ]),
        new(
            "symbol list",
            ["symbol", "list"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "async-involved",
            ]),
        new(
            "source search",
            ["source", "search"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "source-layout",
            ]),
        new(
            "source show",
            ["source", "show"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "source-layout",
            ]),
        new(
            "definition query",
            ["definition"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "at", "require-single", "include-overrides",
            ]),
        new(
            "references",
            ["references"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "require-single", "include-overrides", "exclude-generated", "only-generated",
            ]),
        new(
            "callers",
            ["callers"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "require-single", "include-overrides", "exclude-generated", "only-generated",
                "dispatch", "caller-scope",
            ]),
        new(
            "callees",
            ["callees"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "require-single", "include-overrides", "exclude-generated", "only-generated",
                "exclude-lambda-calls",
            ]),
        new(
            "overrides",
            ["overrides"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. AllConditions, "kind", "async-status", "symbol-path-style", "short-names", "base-dir", "path-style",
                "require-single",
            ]),
        new(
            "async tree",
            ["async", "tree"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "max-nodes",
            ]),
        new(
            "callers tree",
            ["callers", "tree"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "depth", "max-nodes",
            ]),
        new(
            "definition --at",
            ["definition", "--at", "Source.cs:1:1"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose", "at",
                "symbol-path-style", "short-names", "base-dir", "path-style",
            ]),
        new(
            "conditions",
            ["conditions"],
            ["db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose", "base-dir", "path-style"]),
        new(
            "index",
            ["index"],
            [
                "db", "help", "mode", "solution", "configuration", "framework", "target-framework", "runtime",
                "profile-name", "define", "undefine", "define-file", "reference", "unity-editor", "exclude",
                "generated-source", "rebuild", "verbose", "diagnostics", "help-verbose",
            ]),
    ];

    private static readonly string[] AllKnownOptions = CommandScopes
        .SelectMany(scope => scope.Allowed)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(option => option, StringComparer.Ordinal)
        .ToArray();

    private static readonly string[] AlwaysForbiddenOptions =
    [
        "all-profiles", "regex", "ignore-case",
    ];

    private static readonly IReadOnlySet<string> FlagOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "help", "help-verbose", "verbose", "rebuild", "diagnostics", "exclude-generated", "only-generated",
        "all-profiles",
        "require-single", "async-involved", "short-names", "exclude-lambda-calls", "include-overrides", "show-source",
    };

    private sealed record CommandScope(string Name, string[] Prefix, string[] Allowed);

    private readonly CSharpSymbolPathAcceptanceFixture _fixture = fixture;

    [Fact]
    public async Task TM01_GlobLiteralAndRegexOccurrencesCoexistInOneRealCommand()
    {
        var rootOne = await _fixture.GetSymbolAsync(
            "Acceptance.Corpus.LocalOwners::RootOne()",
            cancellationToken: Token);
        var partial = await _fixture.GetSymbolAsync(
            "Acceptance.Partials.PartialHost::PairedPartial()",
            cancellationToken: Token);
        var rootWithSecondary = await _fixture.GetSymbolAsync(
            "Acceptance.Graph.GraphHost::RootWithSecondary()",
            cancellationToken: Token);
        var arguments = new[]
        {
            "symbol", "find",
            "--namespace", "Acceptance.Corp*",
            "--namespace-literal", "Acceptance.Partials",
            "--namespace-regex", @"\AAcceptance\.Graph\z",
            "--type", "Local*",
            "--type-literal", "PartialHost",
            "--type-regex", @"\AGraphHost\z",
            "--method", "RootO*",
            "--method-literal", "PairedPartial()",
            "--method-regex", @"\ARootWithSecondary\(\)\z",
            "--file", "src/*/Ambiguity.cs",
            "--file-literal", "src/Corpus/Partials.Implementation.cs",
            "--file-regex", @"\Asrc/Corpus/Graph\.cs\z",
            "--include", "pub*",
            "--include-literal", "void",
            "--include-regex", @"\bpublic\b",
            "--output-format", "json",
        };

        var parsed = CliArguments.Parse(arguments[2..]);
        Assert.Equal(
            [
                new CliOptionOccurrence("namespace", "Acceptance.Corp*"),
                new CliOptionOccurrence("namespace-literal", "Acceptance.Partials"),
                new CliOptionOccurrence("namespace-regex", @"\AAcceptance\.Graph\z"),
            ],
            parsed.GetOccurrences("namespace", "namespace-literal", "namespace-regex"));
        Assert.Equal(
            [
                new CliOptionOccurrence("type", "Local*"),
                new CliOptionOccurrence("type-literal", "PartialHost"),
                new CliOptionOccurrence("type-regex", @"\AGraphHost\z"),
            ],
            parsed.GetOccurrences("type", "type-literal", "type-regex"));
        Assert.Equal(
            [
                new CliOptionOccurrence("method", "RootO*"),
                new CliOptionOccurrence("method-literal", "PairedPartial()"),
                new CliOptionOccurrence("method-regex", @"\ARootWithSecondary\(\)\z"),
            ],
            parsed.GetOccurrences("method", "method-literal", "method-regex"));
        Assert.Equal(
            [
                new CliOptionOccurrence("file", "src/*/Ambiguity.cs"),
                new CliOptionOccurrence("file-literal", "src/Corpus/Partials.Implementation.cs"),
                new CliOptionOccurrence("file-regex", @"\Asrc/Corpus/Graph\.cs\z"),
            ],
            parsed.GetOccurrences("file", "file-literal", "file-regex"));
        Assert.Equal(
            [
                new CliOptionOccurrence("include", "pub*"),
                new CliOptionOccurrence("include-literal", "void"),
                new CliOptionOccurrence("include-regex", @"\bpublic\b"),
            ],
            parsed.GetOccurrences("include", "include-literal", "include-regex"));

        var result = await _fixture.RunStandardCliAsync(arguments);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(
            [rootOne.Id, rootWithSecondary.Id, partial.Id],
            MatchedIds(result));
        Assert.Equal(
            [
                _fixture.FormatPath(rootOne, _fixture.FullCsharp),
                _fixture.FormatPath(rootWithSecondary, _fixture.FullCsharp),
                _fixture.FormatPath(partial, _fixture.FullCsharp),
            ],
            MatchedNames(result));

        var sourceArguments = new[]
        {
            "source", "search",
            "--namespace", "Acceptance.Corp*",
            "--namespace-literal", "Acceptance.Partials",
            "--namespace-regex", @"\AAcceptance\.Graph\z",
            "--type", "Local*",
            "--type-literal", "PartialHost",
            "--type-regex", @"\AGraphHost\z",
            "--method", "RootO*",
            "--method-literal", "PairedPartial()",
            "--method-regex", @"\ARootWithSecondary\(\)\z",
            "--file", "src/*/Ambiguity.cs",
            "--file-literal", "src/Corpus/Partials.Implementation.cs",
            "--file-regex", @"\Asrc/Corpus/Graph\.cs\z",
            "--include", "pub*",
            "--include-literal", "void",
            "--include-regex", @"\bpublic\b",
            "--exclude", "NOT_PRESENT*",
            "--exclude-literal", "ROOT-FILTER-MARKER",
            "--exclude-regex", "PARTIAL-DEFINITION-MARKER",
            "--output-format", "json",
        };
        var parsedSource = CliArguments.Parse(sourceArguments[2..]);
        Assert.Equal(
            [
                new CliOptionOccurrence("namespace", "Acceptance.Corp*"),
                new CliOptionOccurrence("namespace-literal", "Acceptance.Partials"),
                new CliOptionOccurrence("namespace-regex", @"\AAcceptance\.Graph\z"),
            ],
            parsedSource.GetOccurrences("namespace", "namespace-literal", "namespace-regex"));
        Assert.Equal(
            [
                new CliOptionOccurrence("type", "Local*"),
                new CliOptionOccurrence("type-literal", "PartialHost"),
                new CliOptionOccurrence("type-regex", @"\AGraphHost\z"),
            ],
            parsedSource.GetOccurrences("type", "type-literal", "type-regex"));
        Assert.Equal(
            [
                new CliOptionOccurrence("method", "RootO*"),
                new CliOptionOccurrence("method-literal", "PairedPartial()"),
                new CliOptionOccurrence("method-regex", @"\ARootWithSecondary\(\)\z"),
            ],
            parsedSource.GetOccurrences("method", "method-literal", "method-regex"));
        Assert.Equal(
            [
                new CliOptionOccurrence("file", "src/*/Ambiguity.cs"),
                new CliOptionOccurrence("file-literal", "src/Corpus/Partials.Implementation.cs"),
                new CliOptionOccurrence("file-regex", @"\Asrc/Corpus/Graph\.cs\z"),
            ],
            parsedSource.GetOccurrences("file", "file-literal", "file-regex"));
        Assert.Equal(
            [
                new CliOptionOccurrence("include", "pub*"),
                new CliOptionOccurrence("include-literal", "void"),
                new CliOptionOccurrence("include-regex", @"\bpublic\b"),
            ],
            parsedSource.GetOccurrences("include", "include-literal", "include-regex"));
        Assert.Equal(
            [
                new CliOptionOccurrence("exclude", "NOT_PRESENT*"),
                new CliOptionOccurrence("exclude-literal", "ROOT-FILTER-MARKER"),
                new CliOptionOccurrence("exclude-regex", "PARTIAL-DEFINITION-MARKER"),
            ],
            parsedSource.GetOccurrences("exclude", "exclude-literal", "exclude-regex"));

        var sourceAlternatives = await _fixture.RunStandardCliAsync(sourceArguments);
        var graphSource = await _fixture.GetDeclarationsAsync(rootWithSecondary, cancellationToken: Token);
        Assert.Contains(
            "ROOT-FILTER-MARKER",
            Assert.Single(graphSource).NormalizedSource,
            StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, sourceAlternatives.ExitCode);
        Assert.DoesNotContain(rootWithSecondary.Id, MatchedIds(sourceAlternatives));
        Assert.Equal(
            [rootOne.Id, partial.Id],
            MatchedIds(sourceAlternatives));
        Assert.Equal(["ordinary", "partial-implementation"], MatchedRoles(sourceAlternatives));
        Assert.Equal(
            [
                _fixture.FormatPath(rootOne, _fixture.FullCsharp),
                _fixture.FormatPath(partial, _fixture.FullCsharp),
            ],
            MatchedNames(sourceAlternatives));
    }

    [Theory]
    [InlineData("TM02", "namespace", "--namespace", "acceptance.corpus")]
    [InlineData("TM02", "namespace", "--namespace-literal", "acceptance.corpus")]
    [InlineData("TM02", "namespace", "--namespace-regex", "^acceptance\\.corpus$")]
    [InlineData("TM02", "type", "--type", "localowners")]
    [InlineData("TM02", "type", "--type-literal", "localowners")]
    [InlineData("TM02", "type", "--type-regex", "^localowners$")]
    [InlineData("TM02", "method", "--method", "rootone()")]
    [InlineData("TM02", "method", "--method-literal", "rootone()")]
    [InlineData("TM02", "method", "--method-regex", "^rootone\\(\\)$")]
    [InlineData("TM02", "file", "--file", "src/corpus/ambiguity.cs")]
    [InlineData("TM02", "file", "--file-literal", "src/corpus/ambiguity.cs")]
    [InlineData("TM02", "file", "--file-regex", "src/corpus/ambiguity\\.cs")]
    [InlineData("TM02", "source", "--include", "rootone")]
    [InlineData("TM02", "source", "--include-literal", "rootone")]
    [InlineData("TM02", "source", "--include-regex", "rootone")]
    public async Task TM02_EveryCaseCategoryIsIndependentAndDefaultsStrict(
        string rowId,
        string category,
        string option,
        string value)
    {
        Assert.Equal("TM02", rowId);
        var expected = await _fixture.GetSymbolAsync(
            "Acceptance.Corpus.LocalOwners::RootOne()",
            cancellationToken: Token);

        var strictArguments = BuildCaseArguments(category, option, value, caseMode: null);
        var strict = await _fixture.RunStandardCliAsync(strictArguments);
        Assert.Equal(ExitCodes.Success, strict.ExitCode);
        Assert.Empty(MatchedIds(strict));

        var ignoredArguments = BuildCaseArguments(category, option, value, caseMode: "ignore");
        var ignored = await _fixture.RunStandardCliAsync(ignoredArguments);
        Assert.Equal(ExitCodes.Success, ignored.ExitCode);
        Assert.Equal([expected.Id], MatchedIds(ignored));

        var allMisCased = BuildAllMisCasedArguments(category);
        var categoryOnlyIgnored = await _fixture.RunStandardCliAsync(allMisCased);
        Assert.Equal(ExitCodes.Success, categoryOnlyIgnored.ExitCode);
        Assert.Empty(MatchedIds(categoryOnlyIgnored));
    }

    [Fact]
    public async Task TM03_CsharpSuffixCaseRoutingUsesCandidateNamespaceAndTypePositions()
    {
        var narrow = await _fixture.GetSymbolAsync(
            "Namespace1.Namespace2.Class1.Class2::Method()",
            cancellationToken: Token);
        var wider = await _fixture.GetSymbolAsync(
            "Wider.Namespace1.Namespace2.Class1.Class2::Method()",
            cancellationToken: Token);
        var expectedIds = new[] { narrow.Id, wider.Id };

        var namespaceIgnore = await _fixture.RunStandardCliAsync(
            "symbol", "find", "namespace1.namespace2.Class1.Class2::Method()",
            "--namespace-case", "ignore", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, namespaceIgnore.ExitCode);
        Assert.Equal(expectedIds, MatchedIds(namespaceIgnore));

        var namespaceStrict = await _fixture.RunStandardCliAsync(
            "symbol", "find", "namespace1.namespace2.Class1.Class2::Method()",
            "--namespace-case", "strict", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, namespaceStrict.ExitCode);
        Assert.Empty(MatchedIds(namespaceStrict));

        var typeIgnore = await _fixture.RunStandardCliAsync(
            "symbol", "find", "Namespace1.Namespace2.class1.class2::Method()",
            "--type-case", "ignore", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, typeIgnore.ExitCode);
        Assert.Equal(expectedIds, MatchedIds(typeIgnore));

        var typeStrict = await _fixture.RunStandardCliAsync(
            "symbol", "find", "Namespace1.Namespace2.class1.class2::Method()",
            "--type-case", "strict", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, typeStrict.ExitCode);
        Assert.Empty(MatchedIds(typeStrict));
    }

    [Theory]
    [InlineData("TM04", "namespace")]
    [InlineData("TM04", "type")]
    [InlineData("TM04", "executable")]
    [InlineData("TM04", "file")]
    public async Task TM04_StarAndDoubleStarFollowEveryHierarchyDomain(string rowId, string domain)
    {
        Assert.Equal("TM04", rowId);
        switch (domain)
        {
            case "namespace":
                await AssertNamespaceGlobDomainAsync();
                break;
            case "type":
                await AssertTypeGlobDomainAsync();
                break;
            case "executable":
                await AssertExecutableGlobDomainAsync();
                break;
            case "file":
                await AssertFileGlobDomainAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(domain), domain, "Unknown TM04 domain.");
        }
    }

    private async Task AssertNamespaceGlobDomainAsync()
    {
        var one = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace", "Namespace1.*", "--method-literal", "Method()",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, one.ExitCode);
        Assert.Equal(
            ["Namespace1.Namespace2.Class1.Class2::Method()"],
            MatchedNames(one));

        var recursive = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace", "Namespace1.**", "--method-literal", "Method()",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, recursive.ExitCode);
        Assert.Equal(
            [
                "Namespace1.Namespace2.Class1.Class2::Method()",
                "Namespace1.Namespace2.Namespace3.Class2::Method()",
            ],
            MatchedNames(recursive));

        Assert.True(StructuralGlobMatcher.MatchHierarchy(
            ["Namespace1", "**"], ["Namespace1"], StringComparison.Ordinal, Token));
        Assert.True(StructuralGlobMatcher.MatchHierarchy(
            ["Namespace1", "**"], ["Namespace1", "Namespace2"], StringComparison.Ordinal, Token));
        Assert.True(StructuralGlobMatcher.MatchHierarchy(
            ["Namespace1", "**"], ["Namespace1", "Namespace2", "Namespace3"],
            StringComparison.Ordinal, Token));
        Assert.True(StructuralGlobMatcher.MatchHierarchy(
            ["Namespace1", "Na**espace2"], ["Namespace1", "Namespace2"],
            StringComparison.Ordinal, Token));
        Assert.False(StructuralGlobMatcher.MatchHierarchy(
            ["Namespace1", "Na**espace2"], ["Namespace1", "Namespace2", "Namespace3"],
            StringComparison.Ordinal, Token));
    }

    private async Task AssertTypeGlobDomainAsync()
    {
        var one = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace-literal", "Namespace1.Namespace2",
            "--type", "Class1.*", "--method-literal", "Method()", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, one.ExitCode);
        Assert.Equal(["Namespace1.Namespace2.Class1.Class2::Method()"], MatchedNames(one));

        var recursive = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace-literal", "Namespace1.Namespace2",
            "--type", "Class1.**", "--method-literal", "Method()", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, recursive.ExitCode);
        Assert.Equal(MatchedNames(one), MatchedNames(recursive));

        Assert.True(StructuralGlobMatcher.MatchHierarchy(
            ["Outer", "**"], ["Outer"], StringComparison.Ordinal, Token));
        Assert.True(StructuralGlobMatcher.MatchHierarchy(
            ["Outer", "**"], ["Outer", "Inner"], StringComparison.Ordinal, Token));
        Assert.True(StructuralGlobMatcher.MatchHierarchy(
            ["Outer", "**"], ["Outer", "Inner", "Deep"], StringComparison.Ordinal, Token));
        Assert.True(StructuralGlobMatcher.MatchHierarchy(
            ["Out**"], ["Outer"], StringComparison.Ordinal, Token));
        Assert.False(StructuralGlobMatcher.MatchHierarchy(
            ["Out**"], ["Outer", "Inner"], StringComparison.Ordinal, Token));
    }

    private async Task AssertExecutableGlobDomainAsync()
    {
        var one = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace-literal", "Acceptance.Corpus",
            "--type-literal", "LocalOwners", "--method", "RootOne().*", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, one.ExitCode);
        Assert.Equal(
            [
                "Acceptance.Corpus.LocalOwners::RootOne().NestedOwner()",
                "Acceptance.Corpus.LocalOwners::RootOne().SameLocal()",
            ],
            MatchedNames(one));

        var recursive = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace-literal", "Acceptance.Corpus",
            "--type-literal", "LocalOwners", "--method", "RootOne().**", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, recursive.ExitCode);
        var recursiveNames = MatchedNames(recursive);
        Assert.Contains("Acceptance.Corpus.LocalOwners::RootOne()", recursiveNames);
        Assert.Contains("Acceptance.Corpus.LocalOwners::RootOne().NestedOwner()", recursiveNames);
        Assert.Contains("Acceptance.Corpus.LocalOwners::RootOne().NestedOwner().DeepOnly()", recursiveNames);

        var embeddedOne = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace-literal", "Acceptance.Corpus",
            "--type-literal", "LocalOwners", "--method", "Root*", "--output-format", "json");
        var embeddedDouble = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace-literal", "Acceptance.Corpus",
            "--type-literal", "LocalOwners", "--method", "Root**", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, embeddedOne.ExitCode);
        Assert.Equal(ExitCodes.Success, embeddedDouble.ExitCode);
        Assert.Equal(
            [
                "Acceptance.Corpus.LocalOwners::RootOne()",
                "Acceptance.Corpus.LocalOwners::RootTwo()",
            ],
            MatchedNames(embeddedOne));
        Assert.Equal(MatchedNames(embeddedOne), MatchedNames(embeddedDouble));
    }

    private async Task AssertFileGlobDomainAsync()
    {
        var one = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace-literal", "Acceptance.Corpus",
            "--type-literal", "LocalOwners", "--method-literal", "RootOne()",
            "--file", "src\\*\\Ambiguity.cs", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, one.ExitCode);
        Assert.Equal(["Acceptance.Corpus.LocalOwners::RootOne()"], MatchedNames(one));

        var recursive = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace-literal", "Acceptance.Corpus",
            "--type-literal", "LocalOwners", "--method-literal", "RootOne()",
            "--file", "src\\**\\Ambiguity.cs", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, recursive.ExitCode);
        Assert.Equal(MatchedNames(one), MatchedNames(recursive));

        Assert.True(StructuralGlobMatcher.MatchFile(
            "src\\**\\Ambiguity.cs", "src/Ambiguity.cs", StringComparison.Ordinal, Token));
        Assert.True(StructuralGlobMatcher.MatchFile(
            "src\\**\\Ambiguity.cs", "src/one/Ambiguity.cs", StringComparison.Ordinal, Token));
        Assert.True(StructuralGlobMatcher.MatchFile(
            "src\\**\\Ambiguity.cs", "src/one/two/Ambiguity.cs", StringComparison.Ordinal, Token));
        Assert.False(StructuralGlobMatcher.MatchFile(
            "src\\*\\Ambiguity.cs", "src/one/two/Ambiguity.cs", StringComparison.Ordinal, Token));
        Assert.Equal(
            StructuralGlobMatcher.MatchFile(
                "src/C**/Ambiguity.cs", "src/Corpus/Ambiguity.cs", StringComparison.Ordinal, Token),
            StructuralGlobMatcher.MatchFile(
                "src/C*/Ambiguity.cs", "src/Corpus/Ambiguity.cs", StringComparison.Ordinal, Token));
    }

    [Theory]
    [InlineData("TM05", "NestedGeneric(System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int? []>>)", "Acceptance.Signatures.Outer<T>.Inner<U>::NestedGeneric(System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int? []>>)")]
    [InlineData("TM05", "Tuple((int,string))", "Acceptance.Signatures.Outer<T>.Inner<U>::Tuple((int, string))")]
    [InlineData("TM05", "RankTwo(int[,])", "Acceptance.Signatures.Outer<T>.Inner<U>::RankTwo(int[, ])")]
    [InlineData("TM05", "RootOne().NestedOwner().<lambda#1>", "Acceptance.Corpus.LocalOwners::RootOne().NestedOwner().<lambda#1>")]
    [InlineData("TM05", "[operator:+](Acceptance.Special.SpecialHost,Acceptance.Special.SpecialHost)", "Acceptance.Special.SpecialHost::[operator:+](Acceptance.Special.SpecialHost,Acceptance.Special.SpecialHost)")]
    [InlineData("TM05", "FunctionPointer(delegate*<int, void>)", "Acceptance.Signatures.Outer<T>.Inner<U>::FunctionPointer(delegate*<int, void>)")]
    public async Task TM05_MethodGlobKeepsBalancedSignaturePunctuationInsideOneSegment(
        string rowId,
        string methodPattern,
        string expectedPath)
    {
        Assert.Equal("TM05", rowId);
        var result = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--method", methodPattern, "--output-format", "json");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var expected = await _fixture.GetSymbolAsync(expectedPath, cancellationToken: Token);
        Assert.Equal([expected.Id], MatchedIds(result));
        Assert.Equal([expectedPath], MatchedNames(result));
    }

    [Fact]
    public async Task TM06_MethodLiteralRetainsPerSegmentParameterOmission()
    {
        var cases = new[]
        {
            (Pattern: "Bare", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::Bare()",
                "Acceptance.Signatures.Outer<T>.Inner<U>::Bare(int)",
            }),
            (Pattern: "Generic<V>", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::Generic<V>()",
                "Acceptance.Signatures.Outer<T>.Inner<U>::Generic<V>(V)",
            }),
            (Pattern: "Generic<V>()", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::Generic<V>()",
            }),
            (Pattern: "Bare(int)", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::Bare(int)",
            }),
            (Pattern: "LocalStates.First<V>", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates().First<V>()",
                "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates(int).First<V>(V)",
            }),
            (Pattern: "LocalStates().First<V>", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates().First<V>()",
            }),
            (Pattern: "LocalStates(int).First<V>", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates(int).First<V>(V)",
            }),
            (Pattern: "LocalStates().First<V>()", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates().First<V>()",
            }),
            (Pattern: "LocalStates(int).First<V>(V)", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates(int).First<V>(V)",
            }),
        };

        foreach (var (pattern, expectedPaths) in cases)
        {
            var result = await _fixture.RunStandardCliAsync(
                "symbol", "find", "--method-literal", pattern, "--output-format", "json");
            Assert.Equal(ExitCodes.Success, result.ExitCode);
            var actualPaths = MatchedNames(result);
            Assert.True(
                expectedPaths.SequenceEqual(actualPaths, StringComparer.Ordinal),
                $"Pattern '{pattern}' expected [{string.Join(", ", expectedPaths)}], " +
                $"actual [{string.Join(", ", actualPaths)}].");
        }

        var literalWildcard = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--method-literal", "Bare*");
        Assert.Equal(ExitCodes.InvalidArguments, literalWildcard.ExitCode);
        Assert.Contains(
            "wildcards require glob mode",
            literalWildcard.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(literalWildcard.StandardOutput);
    }

    [Fact]
    public async Task TM07_MethodRegexMatchesWholeCanonicalExecutableTextOnly()
    {
        var nested = await _fixture.GetSymbolAsync(
            "Acceptance.Signatures.Outer<T>.Inner<U>::NestedGeneric(System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int? []>>)",
            cancellationToken: Token);
        var canonical = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--method-regex",
            @"\ANestedGeneric\(System\.Collections\.Generic\.Dictionary<string, System\.Collections\.Generic\.List<int\? \[\]>>\)\z",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, canonical.ExitCode);
        Assert.Equal([nested.Id], MatchedIds(canonical));

        var unanchored = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--method-regex", "NestedGeneric", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, unanchored.ExitCode);
        Assert.Empty(MatchedIds(unanchored));

        var omission = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--method-regex", @"\ABare\z", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, omission.ExitCode);
        Assert.Empty(MatchedIds(omission));

        var aliases = await _fixture.GetSymbolAsync(
            "Acceptance.Signatures.Outer<T>.Inner<U>::FunctionPointer(delegate*<int, void>)",
            cancellationToken: Token);
        var aliasRegex = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--method-regex", @"\AFunctionPointer\(delegate\*<int, void>\)\z",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, aliasRegex.ExitCode);
        Assert.Equal([aliases.Id], MatchedIds(aliasRegex));

        var otherCaseOptionsCannotRewriteMethodRegex = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--method-regex", @"\Arootone\(\)\z",
            "--namespace-literal", "acceptance.corpus", "--namespace-case", "ignore",
            "--type-literal", "localowners", "--type-case", "ignore",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, otherCaseOptionsCannotRewriteMethodRegex.ExitCode);
        Assert.Empty(MatchedIds(otherCaseOptionsCannotRewriteMethodRegex));

        var caseInsensitive = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--method-regex", @"\Arootone\(\)\z", "--method-case", "ignore",
            "--namespace-literal", "Acceptance.Corpus", "--type-literal", "LocalOwners",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, caseInsensitive.ExitCode);
        Assert.Equal(
            [(await _fixture.GetSymbolAsync("Acceptance.Corpus.LocalOwners::RootOne()", cancellationToken: Token)).Id],
            MatchedIds(caseInsensitive));
    }

    [Theory]
    [InlineData("TM08", "--include", "glob", false)]
    [InlineData("TM08", "--include-literal", "literal", false)]
    [InlineData("TM08", "--include-regex", "regex", false)]
    [InlineData("TM08", "--exclude", "glob", true)]
    [InlineData("TM08", "--exclude-literal", "literal", true)]
    [InlineData("TM08", "--exclude-regex", "regex", true)]
    public async Task TM08_SourceModesAreUnanchoredAndCrossEverySupportedNewline(
        string rowId,
        string option,
        string syntax,
        bool excludes)
    {
        Assert.Equal("TM08", rowId);
        var symbol = await _fixture.GetSymbolAsync(
            "Acceptance.Newlines.NewlineHost::NewlineMarkers()",
            cancellationToken: Token);
        var declarations = await _fixture.GetDeclarationsAsync(symbol, cancellationToken: Token);
        var source = Assert.IsType<string>(Assert.Single(declarations).NormalizedSource);
        var boundaries = new[]
        {
            (Left: "TAB-MARKER", Separator: "\t", Right: "CR-MARKER"),
            (Left: "CR-MARKER", Separator: "\r", Right: "LF-MARKER"),
            (Left: "LF-MARKER", Separator: "\n", Right: "CRLF-MARKER"),
            (Left: "CRLF-MARKER", Separator: "\r\n", Right: "NEL-MARKER"),
            (Left: "NEL-MARKER", Separator: "\u0085", Right: "LS-MARKER"),
            (Left: "LS-MARKER", Separator: "\u2028", Right: "PS-MARKER"),
            (Left: "PS-MARKER", Separator: "\u2029", Right: "END-MARKER"),
        };

        for (var index = 0; index < boundaries.Length; index++)
        {
            var (left, separator, right) = boundaries[index];
            Assert.Contains(left + separator + right, source, StringComparison.Ordinal);
            var value = syntax switch
            {
                "glob" => left + (index % 2 == 0 ? "*" : "**") + right,
                "literal" => left + separator + right,
                "regex" => Regex.Escape(left + separator + right),
                _ => throw new ArgumentOutOfRangeException(nameof(syntax), syntax, "Unknown TM08 syntax."),
            };
            var result = await _fixture.RunStandardCliAsync(
                "source", "search", "--method-literal", "NewlineMarkers()",
                option, value, "--output-format", "json");
            Assert.Equal(ExitCodes.Success, result.ExitCode);
            if (excludes)
            {
                Assert.Empty(MatchedIds(result));
            }
            else
            {
                Assert.Equal([symbol.Id], MatchedIds(result));
                Assert.Equal(
                    ["Acceptance.Newlines.NewlineHost::NewlineMarkers()"],
                    MatchedNames(result));
            }
        }

        if (excludes)
        {
            var nonMatching = await _fixture.RunStandardCliAsync(
                "source", "search", "--method-literal", "NewlineMarkers()",
                option, "NOT-PRESENT", "--output-format", "json");
            Assert.Equal(ExitCodes.Success, nonMatching.ExitCode);
            Assert.Equal([symbol.Id], MatchedIds(nonMatching));
        }

        if (option.Equals("--include-literal", StringComparison.Ordinal))
        {
            var tempParent = Path.Combine(Path.GetTempPath(), $"csindex-tm08-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempParent);
            try
            {
                var relocated = _fixture.CopyContainerTo(tempParent);
                var relocatedWorkspace = Path.Combine(relocated, "workspace");
                foreach (var file in Directory.EnumerateFiles(
                             Path.Combine(relocatedWorkspace, "src"),
                             "*.cs",
                             SearchOption.AllDirectories))
                {
                    File.Delete(file);
                }

                File.Delete(Path.Combine(relocated, "Shared", "Linked.cs"));
                var relocatedQuery = _fixture.CreateQueryService(
                    Path.Combine(relocatedWorkspace, ".csindex", "index.sqlite"));
                var relocatedRows = await relocatedQuery.SelectSourceRowsAsync(
                    new SymbolSelectionRequest(
                        Selector: null,
                        Conditions:
                        [
                            new TypedCondition(
                                ConditionCategory.Include,
                                ConditionSyntax.Literal,
                                "NEL-MARKER\u0085LS-MARKER"),
                        ],
                        Case: new SymbolCaseOptions(),
                        FunctionFilter: new FunctionTargetFilter(null, AsyncStatusFilter.All),
                        KindSpecified: false,
                        AsyncStatusSpecified: false),
                    profileName: null,
                    cancellationToken: Token);
                var relocatedMatch = Assert.Single(relocatedRows.Matches);
                Assert.Equal(symbol.Id, relocatedMatch.Symbol.Id);
                Assert.Contains(
                    "NEL-MARKER\u0085LS-MARKER",
                    relocatedMatch.Declaration.NormalizedSource,
                    StringComparison.Ordinal);
            }
            finally
            {
                if (Directory.Exists(tempParent))
                {
                    Directory.Delete(tempParent, recursive: true);
                }
            }
        }
    }

    [Fact]
    public async Task TM09_InvalidRegexAndDeterministicTimeoutCommitNoPartialPayload()
    {
        var matcherTimeout = Assert.Throws<SymbolQueryParseException>(() =>
            SourceTextFilter.IsMatch(
                new string('a', 50_000) + "!",
                [new TypedCondition(ConditionCategory.Include, ConditionSyntax.Regex, "(a+)+$")],
                [],
                CaseMode.Strict,
                Token,
                TimeSpan.FromMilliseconds(1)));
        Assert.Contains("timed out", matcherTimeout.Message, StringComparison.OrdinalIgnoreCase);

        var outputPath = Path.Combine(_fixture.ContainerPath, "tm09-sentinel.txt");
        File.WriteAllText(outputPath, "output sentinel");
        var invalidCounters = new DependencyCounters();
        var invalid = await RunWithDependenciesAsync(
            [
                "symbol", "find", "--method-regex", "[", "--output-format", "json",
                "--output-file", outputPath, "--db", _fixture.StandardDatabasePath,
            ],
            invalidCounters,
            throwOnDependency: true);
        Assert.Equal(ExitCodes.InvalidArguments, invalid.ExitCode);
        Assert.Contains("Invalid regular expression condition", invalid.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, invalidCounters.QueryFactoryCalls);
        Assert.Equal(0, invalidCounters.OutputFactoryCalls);
        Assert.Equal(string.Empty, invalid.StandardOutput);
        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Empty(Directory.GetFiles(_fixture.ContainerPath, ".*.tmp", SearchOption.TopDirectoryOnly));

        var timeoutCounters = new DependencyCounters();
        var timeout = await RunWithDependenciesAsync(
            [
                "symbol", "find", "Acceptance.Corpus.LocalOwners::RootOne()",
                "--output-format", "json", "--output-file", outputPath,
                "--db", _fixture.StandardDatabasePath,
            ],
            timeoutCounters,
            throwOnDependency: true,
            forceRegexTimeout: true);
        Assert.Equal(ExitCodes.AnalysisFailure, timeout.ExitCode);
        Assert.Equal(1, timeoutCounters.QueryFactoryCalls);
        Assert.Equal(0, timeoutCounters.OutputFactoryCalls);
        Assert.Equal(string.Empty, timeout.StandardOutput);
        Assert.Contains("Forced regex timeout", timeout.StandardError, StringComparison.Ordinal);
        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Empty(Directory.GetFiles(_fixture.ContainerPath, ".*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task TM10_TypedConditionCompositionMatchesTheNormativeTruthTable()
    {
        var rootOne = await _fixture.GetSymbolAsync(
            "Acceptance.Corpus.LocalOwners::RootOne()", cancellationToken: Token);
        var partial = await _fixture.GetSymbolAsync(
            "Acceptance.Partials.PartialHost::PairedPartial()", cancellationToken: Token);

        var alternatives = await _fixture.RunStandardCliAsync(
            "symbol", "find",
            "--namespace", "Acceptance.Corp*",
            "--namespace-regex", @"\AAcceptance\.Partials\z",
            "--type-literal", "LocalOwners",
            "--type-regex", @"\APartialHost\z",
            "--method", "RootOne()",
            "--method-literal", "PairedPartial()",
            "--file", "src/*/Ambiguity.cs",
            "--file-regex", @"src/Corpus/Partials\.Definition\.cs",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, alternatives.ExitCode);
        Assert.Equal([rootOne.Id, partial.Id], MatchedIds(alternatives));
        var alternativeLocations = MatchedLocations(alternatives);
        Assert.EndsWith("Ambiguity.cs", alternativeLocations[0], StringComparison.Ordinal);
        Assert.EndsWith("Partials.Implementation.cs", alternativeLocations[1], StringComparison.Ordinal);

        var categoriesAnd = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace-literal", "Acceptance.Corpus",
            "--type-literal", "PartialHost", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, categoriesAnd.ExitCode);
        Assert.Empty(MatchedIds(categoriesAnd));

        var includes = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace-literal", "Acceptance.Corpus",
            "--type-literal", "LocalOwners", "--method-literal", "RootOne()",
            "--include-literal", "public void RootOne",
            "--include-literal", "RootOne()", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, includes.ExitCode);
        Assert.Equal([rootOne.Id], MatchedIds(includes));

        var missingInclude = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace-literal", "Acceptance.Corpus",
            "--type-literal", "LocalOwners", "--method-literal", "RootOne()",
            "--include-literal", "RootOne", "--include-regex", "MISSING-INCLUDE",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, missingInclude.ExitCode);
        Assert.Empty(MatchedIds(missingInclude));

        var excludes = await _fixture.RunStandardCliAsync(
            "symbol", "find",
            "--namespace-literal", "Acceptance.Corpus",
            "--namespace-literal", "Acceptance.Partials",
            "--type-literal", "LocalOwners",
            "--type-literal", "PartialHost",
            "--method-literal", "RootOne()",
            "--method-literal", "PairedPartial()",
            "--file-literal", "src/Corpus/Ambiguity.cs",
            "--file-literal", "src/Corpus/Partials.Definition.cs",
            "--exclude-literal", "PARTIAL-DEFINITION-MARKER",
            "--exclude-literal", "PARTIAL-IMPLEMENTATION-MARKER",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, excludes.ExitCode);
        Assert.Equal([rootOne.Id], MatchedIds(excludes));
        Assert.EndsWith(
            "Ambiguity.cs",
            Assert.Single(MatchedLocations(excludes)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TM11_DeclarationScopedPredicatesMustMatchOnePhysicalRow()
    {
        var split = await _fixture.RunStandardCliAsync(
            "source", "search", "--file-literal", "src/Corpus/Partials.Definition.cs",
            "--include-literal", "PARTIAL-IMPLEMENTATION-MARKER", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, split.ExitCode);
        Assert.Empty(MatchedIds(split));

        var sameRow = await _fixture.RunStandardCliAsync(
            "source", "search", "--file-literal", "src/Corpus/Partials.Definition.cs",
            "--include-literal", "PARTIAL-DEFINITION-MARKER", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, sameRow.ExitCode);
        Assert.Single(MatchedIds(sameRow));
        Assert.Equal(["partial-definition"], MatchedRoles(sameRow));
        Assert.EndsWith("Partials.Definition.cs", Assert.Single(MatchedLocations(sameRow)), StringComparison.Ordinal);

        var logicalSplit = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace-literal", "Acceptance.Partials",
            "--type-literal", "PartialHost", "--method-literal", "PairedPartial()",
            "--file-literal", "src/Corpus/Partials.Definition.cs",
            "--include-literal", "PARTIAL-IMPLEMENTATION-MARKER", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, logicalSplit.ExitCode);
        Assert.Empty(MatchedIds(logicalSplit));
    }

    [Fact]
    public async Task TM12_PassingPartialRowsProjectAccordingToCommandOrientation()
    {
        var partial = await _fixture.GetSymbolAsync(
            "Acceptance.Partials.PartialHost::PairedPartial()", cancellationToken: Token);
        var symbol = await _fixture.RunStandardCliAsync(
            "symbol", "find", "--namespace-literal", "Acceptance.Partials",
            "--type-literal", "PartialHost", "--method-literal", "PairedPartial()",
            "--include-literal", "PARTIAL", "--require-single", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, symbol.ExitCode);
        Assert.Equal([partial.Id], MatchedIds(symbol));

        var definition = await _fixture.RunStandardCliAsync(
            "definition", "Acceptance.Partials.PartialHost::PairedPartial()",
            "--include-literal", "PARTIAL", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, definition.ExitCode);
        Assert.Equal([partial.Id], MatchedIds(definition));
        Assert.Equal(["partial-definition", "partial-implementation"], DefinitionRoles(definition));

        var source = await _fixture.RunStandardCliAsync(
            "source", "search", "--namespace-literal", "Acceptance.Partials",
            "--type-literal", "PartialHost", "--method-literal", "PairedPartial()",
            "--include-literal", "PARTIAL", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, source.ExitCode);
        Assert.Equal(2, MatchedIds(source).Length);
        Assert.Equal(["partial-definition", "partial-implementation"], MatchedRoles(source));

        var relation = await _fixture.RunStandardCliAsync(
            "callees", "Acceptance.Partials.PartialHost::PairedPartial()",
            "--include-literal", "PARTIAL", "--require-single", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, relation.ExitCode);
        Assert.Contains("Acceptance.Partials.PartialHost::PairedPartial()", relation.StandardOutput, StringComparison.Ordinal);

        var graph = await _fixture.RunStandardCliAsync(
            "callers", "tree", "Acceptance.Partials.PartialHost::PairedPartial()",
            "--include-literal", "PARTIAL", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, graph.ExitCode);
        var graphNodeIds = PropertyIds(graph, "nodes");
        Assert.Equal(1, graphNodeIds.Count(id => id == partial.Id));
        Assert.Equal(graphNodeIds.Length, graphNodeIds.Distinct().Count());
    }

    [Fact]
    public async Task CM01_EveryAllowedOptionIsAcceptedOnEveryDocumentedCommandForm()
    {
        foreach (var scope in CommandScopes)
        {
            var help = await _fixture.RunCliAsync([.. scope.Prefix, "--help"]);
            Assert.Equal(ExitCodes.Success, help.ExitCode);
            Assert.Equal(
                scope.Allowed.OrderBy(value => value, StringComparer.Ordinal),
                ReadAcceptedOptions(help.StandardOutput));

            foreach (var option in scope.Allowed)
            {
                var separated = await RunHelpOptionAsync(scope, option, useEquals: false);
                Assert.Equal(
                    ExitCodes.Success,
                    separated.ExitCode
                    );

                if (IsValueOption(option))
                {
                    var equals = await RunHelpOptionAsync(scope, option, useEquals: true);
                    Assert.Equal(
                        ExitCodes.Success,
                        equals.ExitCode
                        );
                }
                else
                {
                    var equals = await RunHelpOptionAsync(scope, option, useEquals: true);
                    Assert.True(
                        equals.ExitCode == ExitCodes.InvalidArguments,
                        $"{scope.Name} --{option}=true returned {equals.ExitCode}: {equals.StandardError}");
                    Assert.Contains(option, equals.StandardError, StringComparison.Ordinal);
                }
            }
        }

        var root = await _fixture.GetSymbolAsync(
            "Acceptance.Partials.PartialHost::PairedPartial()",
            cancellationToken: Token);
        var symbolFind = await _fixture.RunStandardCliAsync(
            "symbol", "find",
            "--namespace-literal", "Acceptance.Partials",
            "--type-literal", "PartialHost",
            "--method-literal", "PairedPartial()",
            "--file-literal", "src/Corpus/Partials.Implementation.cs",
            "--include-literal", "PARTIAL-IMPLEMENTATION-MARKER",
            "--exclude-literal", "NOT_PRESENT",
            "--namespace-case", "strict", "--type-case", "strict", "--method-case", "strict",
            "--file-case", "strict", "--source-case", "strict",
            "--kind", "method", "--async-status", "sync",
            "--symbol-path-style", "explicit", "--short-names", "--base-dir", _fixture.WorkspacePath,
            "--path-style", "relative", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, symbolFind.ExitCode);
        Assert.Equal([root.Id], MatchedIds(symbolFind));
        Assert.Equal(
            "**::PartialHost::PairedPartial()",
            Assert.Single(MatchedNames(symbolFind)));

        var symbolList = await _fixture.RunStandardCliAsync(
            "symbol", "list", "--namespace-literal", "Acceptance.Partials",
            "--type-literal", "PartialHost", "--method-literal", "PairedPartial()",
            "--include-literal", "PARTIAL", "--kind", "method", "--async-status", "sync",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, symbolList.ExitCode);
        Assert.Equal([root.Id], PropertyIds(symbolList, "symbols"));

        var sourceSearch = await _fixture.RunStandardCliAsync(
            "source", "search", "--namespace-literal", "Acceptance.Partials",
            "--type-literal", "PartialHost", "--method-literal", "PairedPartial()",
            "--file-literal", "src/Corpus/Partials.Definition.cs",
            "--include-literal", "PARTIAL-DEFINITION-MARKER", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, sourceSearch.ExitCode);
        Assert.Single(PropertyIds(sourceSearch, "matched"));
        Assert.Equal(["partial-definition"], MatchedRoles(sourceSearch));

        var sourceShow = await _fixture.RunStandardCliAsync(
            "source", "show", "Acceptance.Partials.PartialHost::PairedPartial()",
            "--method-literal", "PairedPartial()", "--include-literal", "PARTIAL",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, sourceShow.ExitCode);
        Assert.Equal([root.Id], MatchedIds(sourceShow));

        var definition = await _fixture.RunStandardCliAsync(
            "definition", "Acceptance.Partials.PartialHost::PairedPartial()",
            "--method-literal", "PairedPartial()", "--include-literal", "PARTIAL",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, definition.ExitCode);
        Assert.Equal([root.Id], MatchedIds(definition));
        Assert.Equal(["partial-definition", "partial-implementation"], DefinitionRoles(definition));

        foreach (var command in new[] { "references", "callers", "callees" })
        {
            var relation = await _fixture.RunStandardCliAsync(
                command, "Acceptance.Partials.PartialHost::PairedPartial()",
                "--method-literal", "PairedPartial()", "--include-literal", "PARTIAL",
                "--output-format", "json");
            Assert.Equal(ExitCodes.Success, relation.ExitCode);
            Assert.Equal([root.Id], MatchedIds(relation));
            Assert.Contains("calls", relation.StandardOutput, StringComparison.Ordinal);
        }

        var overrides = await _fixture.RunStandardCliAsync(
            "overrides", "Acceptance.Partials.PartialBase::PairedPartial()",
            "--method-literal", "PairedPartial()", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, overrides.ExitCode);
        Assert.Contains("Acceptance.Partials.PartialHost::PairedPartial()", overrides.StandardOutput, StringComparison.Ordinal);

        var asyncTree = await _fixture.RunStandardCliAsync(
            "async", "tree", "Acceptance.Special.AsyncAndIteratorHost::AsyncMember()",
            "--method-literal", "AsyncMember()", "--async-status", "async",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, asyncTree.ExitCode);
        Assert.NotEmpty(PropertyIds(asyncTree, "nodes"));

        var callerTree = await _fixture.RunStandardCliAsync(
            "callers", "tree", "Acceptance.Graph.GraphHost::CallerTarget()",
            "--method-literal", "CallerTarget()", "--depth", "1", "--max-nodes", "20",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, callerTree.ExitCode);
        Assert.NotEmpty(PropertyIds(callerTree, "nodes"));

        var conditions = await _fixture.RunStandardCliAsync(
            "conditions", "--path-style", "relative", "--base-dir", _fixture.WorkspacePath,
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, conditions.ExitCode);
        using var conditionDocument = JsonDocument.Parse(conditions.StandardOutput);
        Assert.True(conditionDocument.RootElement.TryGetProperty("conditionalSymbols", out _));
        Assert.Equal(ExitCodes.Success, _fixture.StandardIndexResult.ExitCode);
        Assert.Equal(ExitCodes.Success, _fixture.CustomIndexResult.ExitCode);
    }

    [Fact]
    public async Task CM02_EveryForbiddenOptionIsUnknownOnEveryOtherCommandForm()
    {
        var outputPath = Path.Combine(_fixture.ContainerPath, "cm-option-result.txt");
        Assert.False(File.Exists(outputPath));
        foreach (var scope in CommandScopes)
        {
            var forbiddenOptions = AllKnownOptions
                .Where(value => !scope.Allowed.Contains(value, StringComparer.Ordinal))
                .Concat(AlwaysForbiddenOptions)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal);
            foreach (var option in forbiddenOptions)
            {
                var counters = new DependencyCounters();
                var result = await RunWithDependenciesAsync(
                    [.. scope.Prefix, "--help", .. OptionTokens(option)],
                    counters,
                    throwOnDependency: true);
                Assert.True(
                    result.ExitCode == ExitCodes.InvalidArguments,
                    $"{scope.Name} should reject --{option}, but exited {result.ExitCode}: " +
                    $"{result.StandardError}{result.StandardOutput}");
                Assert.Empty(result.StandardOutput);
                Assert.True(
                    string.Equals(UnknownOptionError(option), result.StandardError, StringComparison.Ordinal),
                    $"{scope.Name} --{option} returned the wrong usage error: {result.StandardError}");
                Assert.Equal(0, counters.QueryFactoryCalls);
                Assert.Equal(0, counters.OutputFactoryCalls);
                Assert.Equal(0, counters.AnalysisFactoryCalls);
                Assert.Equal(0, counters.SqliteFactoryCalls);
            }
        }

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task CM03_SelectorAndConditionMinimumsAreExact()
    {
        await AssertRejectedBeforeDependenciesAsync(
            ["symbol", "find"],
            UsageError("symbol find requires a selector or at least one explicit selection condition."));
        await AssertRejectedBeforeDependenciesAsync(
            ["source", "search"],
            UsageError("source search requires a selector or at least one explicit selection condition."));

        var list = await _fixture.RunStandardCliAsync("symbol", "list", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, list.ExitCode);
        using (var listDocument = JsonDocument.Parse(list.StandardOutput))
        {
            Assert.NotEmpty(listDocument.RootElement.GetProperty("symbols").EnumerateArray());
        }
        await AssertRejectedBeforeDependenciesAsync(
            ["symbol", "list", "Acceptance.Corpus.LocalOwners::RootOne()"],
            UsageError("symbol list does not accept positional arguments."));

        var rootOne = await _fixture.GetSymbolAsync(
            "Acceptance.Corpus.LocalOwners::RootOne()", cancellationToken: Token);
        var selector = await _fixture.RunStandardCliAsync(
            "symbol", "find", "Acceptance.Corpus.LocalOwners::RootOne()", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, selector.ExitCode);
        Assert.Equal([rootOne.Id], MatchedIds(selector));

        var typedMinimums = new[]
        {
            (Option: "--namespace", Value: "Acceptance.Corp*"),
            (Option: "--namespace-literal", Value: "Acceptance.Corpus"),
            (Option: "--namespace-regex", Value: @"Acceptance\.Corpus"),
            (Option: "--type", Value: "Local*"),
            (Option: "--type-literal", Value: "LocalOwners"),
            (Option: "--type-regex", Value: "LocalOwners"),
            (Option: "--method", Value: "RootO*"),
            (Option: "--method-literal", Value: "RootOne()"),
            (Option: "--method-regex", Value: @"RootOne\(\)"),
            (Option: "--file", Value: "src/*/Ambiguity.cs"),
            (Option: "--file-literal", Value: "src/Corpus/Ambiguity.cs"),
            (Option: "--file-regex", Value: @"src/Corpus/Ambiguity\.cs"),
            (Option: "--include", Value: "*RootOne*"),
            (Option: "--include-literal", Value: "RootOne"),
            (Option: "--include-regex", Value: "RootOne"),
            (Option: "--exclude", Value: "ABSENT*"),
            (Option: "--exclude-literal", Value: "ABSENT"),
            (Option: "--exclude-regex", Value: "ABSENT"),
        };
        foreach (var condition in typedMinimums)
        {
            var find = await _fixture.RunStandardCliAsync(
                "symbol", "find", condition.Option, condition.Value, "--output-format", "json");
            Assert.True(
                find.ExitCode == ExitCodes.Success,
                $"symbol find minimum {condition.Option} failed: {find.StandardError}");
            Assert.Contains(rootOne.Id, MatchedIds(find));

            var search = await _fixture.RunStandardCliAsync(
                "source", "search", condition.Option, condition.Value, "--output-format", "json");
            Assert.True(
                search.ExitCode == ExitCodes.Success,
                $"source search minimum {condition.Option} failed: {search.StandardError}");
            Assert.Contains(rootOne.Id, MatchedIds(search));

            var listFiltered = await _fixture.RunStandardCliAsync(
                "symbol", "list", condition.Option, condition.Value, "--output-format", "json");
            Assert.True(
                listFiltered.ExitCode == ExitCodes.Success,
                $"symbol list condition {condition.Option} failed: {listFiltered.StandardError}");
            Assert.Contains(rootOne.Id, PropertyIds(listFiltered, "symbols"));
        }

        var explicitPredicateMinimums = new[]
        {
            (Option: "--kind", Value: "method", Expected: rootOne),
            (Option: "--kind", Value: "lambda", Expected: await _fixture.GetSymbolAsync(
                "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#1>", cancellationToken: Token)),
            (Option: "--kind", Value: "all", Expected: rootOne),
            (Option: "--async-status", Value: "sync", Expected: rootOne),
            (Option: "--async-status", Value: "async", Expected: await _fixture.GetSymbolAsync(
                "Acceptance.Special.AsyncAndIteratorHost::AsyncMember()", cancellationToken: Token)),
            (Option: "--async-status", Value: "all", Expected: rootOne),
        };
        foreach (var predicate in explicitPredicateMinimums)
        {
            var find = await _fixture.RunStandardCliAsync(
                "symbol", "find", predicate.Option, predicate.Value, "--output-format", "json");
            Assert.Equal(ExitCodes.Success, find.ExitCode);
            Assert.Contains(predicate.Expected.Id, MatchedIds(find));

            var search = await _fixture.RunStandardCliAsync(
                "source", "search", predicate.Option, predicate.Value, "--output-format", "json");
            Assert.Equal(ExitCodes.Success, search.ExitCode);
            Assert.Contains(predicate.Expected.Id, MatchedIds(search));

            var listFiltered = await _fixture.RunStandardCliAsync(
                "symbol", "list", predicate.Option, predicate.Value, "--output-format", "json");
            Assert.Equal(ExitCodes.Success, listFiltered.ExitCode);
            Assert.Contains(predicate.Expected.Id, PropertyIds(listFiltered, "symbols"));
        }

        var outputPath = Path.Combine(_fixture.ContainerPath, "cm03-minimum-output.txt");
        File.WriteAllText(outputPath, "minimum sentinel");
        string[][] nonSelectionOptions =
        [
            ["--namespace-case", "strict"],
            ["--type-case", "strict"],
            ["--method-case", "strict"],
            ["--file-case", "strict"],
            ["--source-case", "strict"],
            ["--symbol-path-style", "csharp"],
            ["--short-names"],
            ["--base-dir", _fixture.WorkspacePath],
            ["--path-style", "relative"],
            ["--output-format", "json"],
            ["--output-file", outputPath],
            ["--profile", "secondary"],
            ["--db", _fixture.StandardDatabasePath],
        ];
        foreach (var optionTokens in nonSelectionOptions)
        {
            await AssertRejectedBeforeDependenciesAsync(
                ["symbol", "find", .. optionTokens],
                UsageError("symbol find requires a selector or at least one explicit selection condition."));
            await AssertRejectedBeforeDependenciesAsync(
                ["source", "search", .. optionTokens],
                UsageError("source search requires a selector or at least one explicit selection condition."));
        }
        foreach (var optionTokens in nonSelectionOptions.Take(10))
        {
            var arguments = new List<string> { "symbol", "list" };
            arguments.AddRange(optionTokens);
            if (!optionTokens.Contains("--output-format", StringComparer.Ordinal))
            {
                arguments.AddRange(["--output-format", "json"]);
            }

            var listWithNonSelection = await _fixture.RunStandardCliAsync([.. arguments]);
            Assert.True(
                listWithNonSelection.ExitCode == ExitCodes.Success,
                $"symbol list non-selection {string.Join(' ', optionTokens)} failed: " +
                listWithNonSelection.StandardError);
            Assert.NotEmpty(PropertyIds(listWithNonSelection, "symbols"));
        }
        Assert.Equal("minimum sentinel", File.ReadAllText(outputPath));

        var requiredRootCommands = new[]
        {
            (Prefix: new[] { "source", "show" },
                Selector: "Acceptance.Partials.PartialHost::PairedPartial()"),
            (Prefix: new[] { "definition" },
                Selector: "Acceptance.Partials.PartialHost::PairedPartial()"),
            (Prefix: new[] { "references" },
                Selector: "Acceptance.Partials.PartialHost::PairedPartial()"),
            (Prefix: new[] { "callers" },
                Selector: "Acceptance.Partials.PartialHost::PairedPartial()"),
            (Prefix: new[] { "callees" },
                Selector: "Acceptance.Partials.PartialHost::PairedPartial()"),
            (Prefix: new[] { "overrides" },
                Selector: "Acceptance.Partials.PartialBase::PairedPartial()"),
            (Prefix: new[] { "async", "tree" },
                Selector: "Acceptance.Special.AsyncAndIteratorHost::AsyncMember()"),
            (Prefix: new[] { "callers", "tree" },
                Selector: "Acceptance.Graph.GraphHost::CallerTarget()"),
        };
        var requiredRootSelectionMinimums = typedMinimums
            .Select(condition => new[] { condition.Option, condition.Value })
            .Concat(
            [
                new[] { "--kind", "method" },
                new[] { "--kind", "lambda" },
                new[] { "--async-status", "sync" },
                new[] { "--async-status", "async" },
            ])
            .ToArray();
        foreach (var command in requiredRootCommands)
        {
            await AssertRejectedBeforeDependenciesAsync(
                command.Prefix,
                UsageError("This command requires exactly one symbol query."));
            foreach (var explicitAll in new[]
                     {
                         new[] { "--kind", "all" },
                         new[] { "--async-status", "all" },
                     })
            {
                await AssertRejectedBeforeDependenciesAsync(
                    [.. command.Prefix, .. explicitAll],
                    UsageError("This command requires exactly one symbol query."));
            }

            foreach (var explicitSelection in requiredRootSelectionMinimums)
            {
                await AssertRejectedBeforeDependenciesAsync(
                    [.. command.Prefix, .. explicitSelection],
                    UsageError("This command requires exactly one symbol query."));
            }

            foreach (var nonSelection in nonSelectionOptions)
            {
                await AssertRejectedBeforeDependenciesAsync(
                    [.. command.Prefix, .. nonSelection],
                    UsageError("This command requires exactly one symbol query."));
            }

            var selected = await _fixture.RunStandardCliAsync(
                [.. command.Prefix, command.Selector, "--output-format", "json"]);
            Assert.True(
                selected.ExitCode == ExitCodes.Success,
                $"{string.Join(' ', command.Prefix)} selector minimum failed: {selected.StandardError}");
            var expected = await _fixture.GetSymbolAsync(command.Selector, cancellationToken: Token);
            var selectedIds = command.Prefix[^1] == "tree"
                ? [RootId(selected)]
                : MatchedIds(selected);
            Assert.Equal([expected.Id], selectedIds);
        }

        var atTarget = await _fixture.GetSymbolAsync(
            "Acceptance.Graph.GraphHost::SecondaryWithoutRootMarker()", cancellationToken: Token);
        var atLocation = _fixture.GetLocation("Graph.cs", "SecondaryWithoutRootMarker()");
        foreach (var condition in typedMinimums)
        {
            await AssertRejectedBeforeDependenciesAsync(
                ["definition", "--at", atLocation, condition.Option, condition.Value],
                UnknownOptionError(condition.Option[2..]));
        }
        foreach (var predicate in new[]
                 {
                     new[] { "--kind", "method" },
                     new[] { "--async-status", "sync" },
                 })
        {
            await AssertRejectedBeforeDependenciesAsync(
                ["definition", "--at", atLocation, .. predicate],
                UnknownOptionError(predicate[0][2..]));
        }
        foreach (var caseOption in nonSelectionOptions.Take(5))
        {
            await AssertRejectedBeforeDependenciesAsync(
                ["definition", "--at", atLocation, .. caseOption],
                UnknownOptionError(caseOption[0][2..]));
        }

        var at = await _fixture.RunStandardCliAsync(
            "definition", "--at", atLocation,
            "--symbol-path-style", "explicit", "--short-names",
            "--base-dir", _fixture.WorkspacePath, "--path-style", "relative",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, at.ExitCode);
        Assert.Equal([atTarget.Id], MatchedIds(at));
    }

    [Fact]
    public async Task CM04_DefinitionAtRejectsEverySelectorAndRootFilterVariant()
    {
        var absoluteLocation = _fixture.GetLocation("Graph.cs", "SecondaryWithoutRootMarker()");
        var graphPath = Path.Combine(_fixture.CorpusPath, "Graph.cs");
        var relativeLocation = Path.GetRelativePath(_fixture.WorkspacePath, graphPath) +
            absoluteLocation[graphPath.Length..];
        var expected = await _fixture.GetSymbolAsync(
            "Acceptance.Graph.GraphHost::SecondaryWithoutRootMarker()", cancellationToken: Token);
        var expectedDeclaration = Assert.Single(
            await _fixture.GetDeclarationsAsync(expected, cancellationToken: Token));
        var outputPath = Path.Combine(_fixture.ContainerPath, "cm04-definition-at-output.json");
        File.WriteAllText(outputPath, "definition-at sentinel");

        string[][] forbiddenOptions =
        {
            new[] { "--namespace", "global" },
            new[] { "--namespace-literal", "global" },
            new[] { "--namespace-regex", "global" },
            new[] { "--type", "Global*" },
            new[] { "--type-literal", "GlobalOwner" },
            new[] { "--type-regex", "GlobalOwner" },
            new[] { "--method", "Global*" },
            new[] { "--method-literal", "GlobalMethod()" },
            new[] { "--method-regex", "GlobalMethod" },
            new[] { "--file", "**/Ambiguity.cs" },
            new[] { "--file-literal", "src/Corpus/Ambiguity.cs" },
            new[] { "--file-regex", "Ambiguity\\.cs" },
            new[] { "--include", "GlobalMethod" },
            new[] { "--include-literal", "GlobalMethod" },
            new[] { "--include-regex", "GlobalMethod" },
            new[] { "--exclude", "NOT_PRESENT" },
            new[] { "--exclude-literal", "NOT_PRESENT" },
            new[] { "--exclude-regex", "NOT_PRESENT" },
            new[] { "--namespace-case", "ignore" },
            new[] { "--type-case", "ignore" },
            new[] { "--method-case", "ignore" },
            new[] { "--file-case", "ignore" },
            new[] { "--source-case", "ignore" },
            new[] { "--kind", "method" },
            new[] { "--async-status", "sync" },
            new[] { "--require-single" },
            new[] { "--include-overrides" },
            new[] { "--exclude-generated" },
            new[] { "--only-generated" },
            new[] { "--async-involved" },
        };

        foreach (var location in new[] { absoluteLocation, relativeLocation })
        {
            await AssertRejectedBeforeDependenciesAsync(
                [
                    "definition", "--at", location, "GlobalOwner::GlobalMethod()",
                    "--output-file", outputPath, "--db", _fixture.StandardDatabasePath,
                ],
                UsageError("definition accepts either a query or --at, not both."));

            foreach (var extra in forbiddenOptions)
            {
                var option = extra[0][2..];
                await AssertRejectedBeforeDependenciesAsync(
                    [
                        "definition", "--at", location, .. extra,
                        "--output-file", outputPath, "--db", _fixture.StandardDatabasePath,
                    ],
                    UnknownOptionError(option));
            }
        }
        Assert.Equal("definition-at sentinel", File.ReadAllText(outputPath));

        (string Path, int Offset)[]? firstDefinitionLocations = null;
        foreach (var location in new[] { absoluteLocation, relativeLocation })
        {
            var valid = await _fixture.RunStandardCliAsync(
                "definition", "--at", location, "--path-style", "relative", "--output-format", "json");
            Assert.True(
                valid.ExitCode == ExitCodes.Success,
                $"definition --at {location} failed: {valid.StandardError}");
            Assert.Equal([expected.Id], MatchedIds(valid));
            Assert.Equal([expected.Id], DefinitionIds(valid));
            Assert.Equal(["ordinary"], DefinitionRoles(valid));
            Assert.Equal(
                [(expectedDeclaration.DocumentPath, expectedDeclaration.SourceStart)],
                DefinitionLocations(valid));
            firstDefinitionLocations ??= DefinitionLocations(valid);
            Assert.Equal(firstDefinitionLocations, DefinitionLocations(valid));
        }
    }

    [Fact]
    public async Task CM05_RootFiltersRunBeforeCardinalityAndTraversal()
    {
        var repository = _fixture.CreateRepository();
        var traversalEvents = 0;
        repository.TraversalObserver = _ => traversalEvents++;
        var query = new SemanticQueryService(repository);
        var outputPath = Path.Combine(_fixture.ContainerPath, "cm05-sentinel.txt");
        File.WriteAllText(outputPath, "cm05 sentinel");

        async Task<CSharpSymbolPathCliResult> RunBeforeTraversalAsync(string[] arguments)
        {
            traversalEvents = 0;
            var counters = new DependencyCounters();
            var result = await RunWithDependenciesAsync(
                [
                    .. arguments,
                    "--output-format", "json", "--output-file", outputPath,
                    "--db", _fixture.StandardDatabasePath,
                ],
                counters,
                throwOnDependency: true,
                injectedQueryService: query);
            Assert.Empty(result.StandardOutput);
            Assert.Equal(1, counters.QueryFactoryCalls);
            Assert.Equal(0, counters.OutputFactoryCalls);
            Assert.Equal(0, counters.AnalysisFactoryCalls);
            Assert.Equal(0, counters.SqliteFactoryCalls);
            Assert.Equal(0, traversalEvents);
            Assert.Equal("cm05 sentinel", File.ReadAllText(outputPath));
            return result;
        }

        async Task<CSharpSymbolPathCliResult> RunAfterFilteringAsync(string[] arguments)
        {
            traversalEvents = 0;
            var counters = new DependencyCounters();
            var result = await RunWithDependenciesAsync(
                [.. arguments, "--output-format", "json", "--db", _fixture.StandardDatabasePath],
                counters,
                throwOnDependency: false,
                injectedQueryService: query);
            Assert.True(
                result.ExitCode == ExitCodes.Success,
                $"{string.Join(' ', arguments)} returned {result.ExitCode}: " +
                $"{result.StandardError}\n{result.StandardOutput}");
            Assert.Equal(1, counters.QueryFactoryCalls);
            Assert.Equal(1, counters.OutputFactoryCalls);
            Assert.Equal(0, counters.AnalysisFactoryCalls);
            Assert.Equal(0, counters.SqliteFactoryCalls);
            Assert.True(traversalEvents > 0, $"{string.Join(' ', arguments)} performed no traversal.");
            return result;
        }

        var multi = await RunBeforeTraversalAsync(
            ["callees", "Acceptance.Duplicates.Twin::Same()", "--require-single"]);
        Assert.Equal(ExitCodes.RequireSingleFailure, multi.ExitCode);
        Assert.Equal(
            $"--require-single expected one symbol but matched 2.{Environment.NewLine}",
            multi.StandardError);

        var zero = await RunBeforeTraversalAsync(
            [
                "callees", "Acceptance.Duplicates.Twin::Same()",
                "--file-literal", "src/Missing/Twin.cs", "--require-single",
            ]);
        Assert.Equal(ExitCodes.RequireSingleFailure, zero.ExitCode);
        Assert.Equal(
            $"--require-single expected one symbol but matched 0.{Environment.NewLine}",
            zero.StandardError);

        var typed = await RunAfterFilteringAsync(
            [
                "callees", "Acceptance.Duplicates.Twin::Same()",
                "--file-literal", "src/DuplicateA/Twin.cs", "--require-single",
            ]);
        Assert.Single(MatchedIds(typed));
        var typedPath = Assert.Single(MatchedLocations(typed)).Replace('\\', '/');
        Assert.EndsWith("src/DuplicateA/Twin.cs", typedPath, StringComparison.OrdinalIgnoreCase);

        var kindMulti = await RunBeforeTraversalAsync(
            [
                "callees", "Acceptance.Special.SpecialHost::MixedAnonymous().**",
                "--require-single",
            ]);
        Assert.Equal(ExitCodes.RequireSingleFailure, kindMulti.ExitCode);
        Assert.Matches(
            @"\A--require-single expected one symbol but matched (?:[2-9]|[1-9]\d+)\.\r?\n\z",
            kindMulti.StandardError);

        var mixedAnonymous = await _fixture.GetSymbolAsync(
            "Acceptance.Special.SpecialHost::MixedAnonymous()", cancellationToken: Token);
        var kind = await RunAfterFilteringAsync(
            [
                "callees", "Acceptance.Special.SpecialHost::MixedAnonymous().**",
                "--kind", "method", "--require-single",
            ]);
        Assert.Equal([mixedAnonymous.Id], MatchedIds(kind));

        var asyncMulti = await RunBeforeTraversalAsync(
            [
                "callees", "Acceptance.Graph.GraphHost::SyncOwnerWithAsyncChild().**",
                "--require-single",
            ]);
        Assert.Equal(ExitCodes.RequireSingleFailure, asyncMulti.ExitCode);
        Assert.Matches(
            @"\A--require-single expected one symbol but matched (?:[2-9]|[1-9]\d+)\.\r?\n\z",
            asyncMulti.StandardError);

        var asyncChild = await _fixture.GetSymbolAsync(
            "Acceptance.Graph.GraphHost::SyncOwnerWithAsyncChild().<lambda#1>",
            cancellationToken: Token);
        var asyncRoot = await RunAfterFilteringAsync(
            [
                "callees", "Acceptance.Graph.GraphHost::SyncOwnerWithAsyncChild().**",
                "--async-status", "async", "--require-single",
            ]);
        Assert.Equal([asyncChild.Id], MatchedIds(asyncRoot));

        var generatedMulti = await RunBeforeTraversalAsync(
            ["references", "Acceptance.**::**::**", "--require-single"]);
        Assert.Equal(ExitCodes.RequireSingleFailure, generatedMulti.ExitCode);
        Assert.Matches(
            @"\A--require-single expected one symbol but matched (?:[2-9]|[1-9]\d+)\.\r?\n\z",
            generatedMulti.StandardError);

        var generatedRoot = await _fixture.GetSymbolAsync(
            "Acceptance.Generated.GeneratedHost::GeneratedRoot()", cancellationToken: Token);
        var generated = await RunAfterFilteringAsync(
            ["references", "Acceptance.**::**::**", "--only-generated", "--require-single"]);
        Assert.Equal([generatedRoot.Id], MatchedIds(generated));

        var graphAmbiguous = await RunBeforeTraversalAsync(
            ["callers", "tree", "Acceptance.Duplicates.Twin::Same()"]);
        Assert.Equal(ExitCodes.InvalidArguments, graphAmbiguous.ExitCode);
        Assert.StartsWith(
            "Query error: Graph query is ambiguous for 'Acceptance.Duplicates.Twin::Same()'. Candidates: ",
            graphAmbiguous.StandardError,
            StringComparison.Ordinal);
        var normalizedAmbiguity = graphAmbiguous.StandardError.Replace('\\', '/');
        Assert.Contains("src/DuplicateA/Twin.cs", normalizedAmbiguity, StringComparison.Ordinal);
        Assert.Contains("src/DuplicateB/Twin.cs", normalizedAmbiguity, StringComparison.Ordinal);

        var graphZero = await RunBeforeTraversalAsync(
            [
                "callers", "tree", "Acceptance.Duplicates.Twin::Same()",
                "--file-literal", "src/Missing/Twin.cs",
            ]);
        Assert.Equal(ExitCodes.InvalidArguments, graphZero.ExitCode);
        Assert.Equal(
            "Query error: No source-backed executable matches graph query: " +
            $"Acceptance.Duplicates.Twin::Same(){Environment.NewLine}",
            graphZero.StandardError);

        var graphFiltered = await RunAfterFilteringAsync(
            [
                "callers", "tree", "Acceptance.Duplicates.Twin::Same()",
                "--file-literal", "src/DuplicateA/Twin.cs",
            ]);
        Assert.Single(PropertyIds(graphFiltered, "nodes"));
        Assert.Equal("cm05 sentinel", File.ReadAllText(outputPath));
    }

    [Fact]
    public async Task CM06_RootFiltersNeverRemoveSecondaryRelationOrGraphResults()
    {
        var root = await _fixture.GetSymbolAsync(
            "Acceptance.Graph.GraphHost::RootWithSecondary()", cancellationToken: Token);
        var secondary = await _fixture.GetSymbolAsync(
            "Acceptance.Graph.GraphHost::SecondaryWithoutRootMarker()", cancellationToken: Token);
        var callerTarget = await _fixture.GetSymbolAsync(
            "Acceptance.Graph.GraphHost::CallerTarget()", cancellationToken: Token);
        var aCaller = await _fixture.GetSymbolAsync(
            "Acceptance.Graph.GraphHost::ACaller()", cancellationToken: Token);
        var zCaller = await _fixture.GetSymbolAsync(
            "Acceptance.Graph.GraphHost::ZCaller()", cancellationToken: Token);
        var graphBase = await _fixture.GetSymbolAsync(
            "Acceptance.Graph.GraphBase::OverrideMe()", cancellationToken: Token);
        var graphDerived = await _fixture.GetSymbolAsync(
            "Acceptance.Graph.GraphDerived::OverrideMe()", cancellationToken: Token);
        var callsLinked = await _fixture.GetSymbolAsync(
            "Acceptance.Corpus.LocalOwners::CallsLinked()", cancellationToken: Token);
        var linked = await _fixture.GetSymbolAsync(
            "Acceptance.Shared.Linked::LinkedMarker()", cancellationToken: Token);

        var callees = await _fixture.RunStandardCliAsync(
            "callees", "Acceptance.Graph.GraphHost::RootWithSecondary()",
            "--type-literal", "GraphHost", "--method-literal", "RootWithSecondary()",
            "--include-literal", "ROOT-FILTER-MARKER", "--require-single",
            "--output-format", "json");

        Assert.True(
            callees.ExitCode == ExitCodes.Success,
            $"CM06 callees returned {callees.ExitCode}: {callees.StandardError}\n{callees.StandardOutput}");
        Assert.Equal([root.Id], MatchedIds(callees));
        Assert.Equal(
            [
                "Acceptance.Graph.GraphHost::RootWithSecondary()->" +
                "Acceptance.Graph.GraphHost::SecondaryWithoutRootMarker()",
            ],
            CallEndpoints(callees));

        var referenceArguments = new[]
        {
            "--type-literal", "GraphHost", "--method-literal", "CallerTarget()",
            "--include-literal", "public void CallerTarget", "--require-single",
            "--output-format", "json",
        };
        var expectedCallerEndpoints = new[]
        {
            "Acceptance.Graph.GraphHost::ACaller()->Acceptance.Graph.GraphHost::CallerTarget()",
            "Acceptance.Graph.GraphHost::ZCaller()->Acceptance.Graph.GraphHost::CallerTarget()",
        };
        var expectedCallerNames = new[]
        {
            "Acceptance.Graph.GraphHost::ACaller()",
            "Acceptance.Graph.GraphHost::ZCaller()",
        };

        var references = await _fixture.RunStandardCliAsync(
            ["references", "Acceptance.Graph.GraphHost::CallerTarget()", .. referenceArguments]);
        Assert.True(
            references.ExitCode == ExitCodes.Success,
            $"CM06 references returned {references.ExitCode}: " +
            $"{references.StandardError}\n{references.StandardOutput}");
        Assert.Equal([callerTarget.Id], MatchedIds(references));
        Assert.Equal(expectedCallerEndpoints, CallEndpoints(references));

        var callers = await _fixture.RunStandardCliAsync(
            ["callers", "Acceptance.Graph.GraphHost::CallerTarget()", .. referenceArguments]);
        Assert.True(
            callers.ExitCode == ExitCodes.Success,
            $"CM06 callers returned {callers.ExitCode}: {callers.StandardError}\n{callers.StandardOutput}");
        Assert.Equal([callerTarget.Id], MatchedIds(callers));
        Assert.Equal(expectedCallerEndpoints, CallEndpoints(callers));
        Assert.Equal(expectedCallerNames, CallerNames(callers));

        var overrides = await _fixture.RunStandardCliAsync(
            "overrides", "Acceptance.Graph.GraphBase::OverrideMe()",
            "--type-literal", "GraphBase", "--method-literal", "OverrideMe()",
            "--include-literal", "virtual void OverrideMe()", "--require-single",
            "--output-format", "json");
        Assert.True(
            overrides.ExitCode == ExitCodes.Success,
            $"CM06 overrides returned {overrides.ExitCode}: " +
            $"{overrides.StandardError}\n{overrides.StandardOutput}");
        Assert.Equal([graphBase.Id], MatchedIds(overrides));
        Assert.Equal(
            ["Acceptance.Graph.GraphDerived::OverrideMe()->Acceptance.Graph.GraphBase::OverrideMe()"],
            RelationEndpoints(overrides));

        var callerTree = await _fixture.RunStandardCliAsync(
            "callers", "tree", "Acceptance.Graph.GraphHost::CallerTarget()",
            "--type-literal", "GraphHost", "--method-literal", "CallerTarget()",
            "--include-literal", "public void CallerTarget", "--depth", "1", "--max-nodes", "20",
            "--output-format", "json");
        Assert.True(
            callerTree.ExitCode == ExitCodes.Success,
            $"CM06 callers tree returned {callerTree.ExitCode}: " +
            $"{callerTree.StandardError}\n{callerTree.StandardOutput}");
        Assert.Equal(callerTarget.Id, RootId(callerTree));
        Assert.Equal(
            new[] { callerTarget.Id, aCaller.Id, zCaller.Id }.Order(),
            PropertyIds(callerTree, "nodes").Order());
        Assert.Equal(
            new[]
            {
                $"{aCaller.Id}->{callerTarget.Id}",
                $"{zCaller.Id}->{callerTarget.Id}",
            }.Order(StringComparer.Ordinal),
            CallerTreeEdges(callerTree).Order(StringComparer.Ordinal));

        var show = await _fixture.RunStandardCliAsync(
            "source", "show", "Acceptance.Graph.GraphHost::RootWithSecondary()",
            "--include-literal", "ROOT-FILTER-MARKER", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, show.ExitCode);
        Assert.Equal([root.Id], MatchedIds(show));
        using var showDocument = JsonDocument.Parse(show.StandardOutput);
        var sourceText = showDocument.RootElement.GetProperty("matched").EnumerateArray()
            .Single().GetProperty("normalizedSource").GetString();
        Assert.Contains("ROOT-FILTER-MARKER", sourceText, StringComparison.Ordinal);

        Assert.DoesNotContain("ROOT-FILTER-MARKER", secondary.NormalizedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("public void CallerTarget", aCaller.NormalizedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("public void CallerTarget", zCaller.NormalizedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("virtual void OverrideMe()", graphDerived.NormalizedSource, StringComparison.Ordinal);

        Assert.Equal("src/Corpus/Ambiguity.cs", callsLinked.PreferredDocumentPath);
        Assert.Equal("../Shared/Linked.cs", linked.PreferredDocumentPath);
        var fileFilteredCallees = await _fixture.RunStandardCliAsync(
            "callees", "Acceptance.Corpus.LocalOwners::CallsLinked()",
            "--file-literal", "src/Corpus/Ambiguity.cs", "--require-single",
            "--output-format", "json");
        Assert.True(
            fileFilteredCallees.ExitCode == ExitCodes.Success,
            $"CM06 file-filtered callees failed: {fileFilteredCallees.StandardError}");
        Assert.Equal([callsLinked.Id], MatchedIds(fileFilteredCallees));
        Assert.Equal(
            ["Acceptance.Corpus.LocalOwners::CallsLinked()->Acceptance.Shared.Linked::LinkedMarker()"],
            CallEndpoints(fileFilteredCallees));

        var fileFilteredCallerTree = await _fixture.RunStandardCliAsync(
            "callers", "tree", "Acceptance.Shared.Linked::LinkedMarker()",
            "--file-literal", "../Shared/Linked.cs", "--depth", "1", "--max-nodes", "20",
            "--output-format", "json");
        Assert.True(
            fileFilteredCallerTree.ExitCode == ExitCodes.Success,
            $"CM06 file-filtered caller tree failed: {fileFilteredCallerTree.StandardError}");
        Assert.Equal(linked.Id, RootId(fileFilteredCallerTree));
        Assert.Equal(
            new[] { linked.Id, callsLinked.Id }.Order(),
            PropertyIds(fileFilteredCallerTree, "nodes").Order());
    }

    [Fact]
    public async Task CM07_KindAndDirectAsyncStatusCoverAllExecutableKindsWithoutOwnerLeakage()
    {
        var methodPaths = new[]
        {
            "Acceptance.Special.SpecialHost::MixedAnonymous()",
            "Acceptance.Corpus.LocalOwners::RootOne().SameLocal()",
            "Acceptance.Special.SpecialHost::[constructor](int)",
            "Acceptance.Special.SpecialHost::[static-constructor]()",
            "Acceptance.Special.SpecialHost::[destructor]()",
            "Acceptance.Special.SpecialHost::[operator:+](Acceptance.Special.SpecialHost,Acceptance.Special.SpecialHost)",
            "Acceptance.Special.SpecialHost::[checked-operator:+](Acceptance.Special.SpecialHost,Acceptance.Special.SpecialHost)",
            "Acceptance.Special.SpecialHost::[conversion:implicit:int](Acceptance.Special.SpecialHost)",
            "Acceptance.Special.SpecialHost::[conversion:explicit:string](Acceptance.Special.SpecialHost)",
            "Acceptance.Special.SpecialHost::[checked-conversion:explicit:double](Acceptance.Special.SpecialHost)",
            "Acceptance.Special.SpecialHost::[get:Auto]()",
            "Acceptance.Special.SpecialHost::[set:Auto](int)",
            "Acceptance.Special.SpecialHost::[init:InitOnly](string)",
            "Acceptance.Special.SpecialHost::[add:Custom](System.EventHandler?)",
            "Acceptance.Special.SpecialHost::[remove:Custom](System.EventHandler?)",
            "Acceptance.Special.SpecialHost::[explicit:Acceptance.Special.ISpecial.Run]()",
        };
        foreach (var path in methodPaths)
        {
            var method = await _fixture.RunStandardCliAsync(
                "symbol", "find", path, "--kind", "method", "--output-format", "json");
            Assert.True(
                method.ExitCode == ExitCodes.Success,
                $"method category {path} returned {method.ExitCode}: " +
                $"{method.StandardError}\n{method.StandardOutput}");
            Assert.Equal([path], MatchedNames(method));
            Assert.Equal(["method"], MatchedKinds(method));
        }

        foreach (var path in new[]
                 {
                     "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#1>",
                     "Acceptance.Special.SpecialHost::MixedAnonymous().<anonymous-method#2>",
                 })
        {
            var lambda = await _fixture.RunStandardCliAsync(
                "symbol", "find", path, "--kind", "lambda", "--output-format", "json");
            Assert.Equal(ExitCodes.Success, lambda.ExitCode);
            Assert.Equal([path], MatchedNames(lambda));
            Assert.Equal(["lambda"], MatchedKinds(lambda));
        }

        var directCases = new[]
        {
            (Path: "Acceptance.Special.SpecialHost::MixedAnonymous()", Kind: "method", Status: "sync", Opposite: "async"),
            (Path: "Acceptance.Special.AsyncAndIteratorHost::AsyncMember()", Kind: "method", Status: "async", Opposite: "sync"),
            (Path: "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#1>", Kind: "lambda", Status: "sync", Opposite: "async"),
            (Path: "Acceptance.Graph.GraphHost::SyncOwnerWithAsyncChild().<lambda#1>", Kind: "lambda", Status: "async", Opposite: "sync"),
            (Path: "Acceptance.Special.InitializerHost::<initializer:@field>", Kind: "initializer", Status: "sync", Opposite: "async"),
            (Path: "Program::<top-level-statements>", Kind: "toplevelstatements", Status: "async", Opposite: "sync"),
        };
        Assert.Equal(
            ["initializer", "lambda", "method", "toplevelstatements"],
            directCases.Select(value => value.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        foreach (var directCase in directCases)
        {
            var expected = await _fixture.GetSymbolAsync(directCase.Path, cancellationToken: Token);
            var direct = await _fixture.RunStandardCliAsync(
                "symbol", "find", directCase.Path, "--output-format", "json");
            var explicitAll = await _fixture.RunStandardCliAsync(
                "symbol", "find", directCase.Path, "--kind", "all",
                "--async-status", "all", "--output-format", "json");
            var matchingStatus = await _fixture.RunStandardCliAsync(
                "symbol", "find", directCase.Path, "--async-status", directCase.Status,
                "--output-format", "json");
            var oppositeStatus = await _fixture.RunStandardCliAsync(
                "symbol", "find", directCase.Path, "--async-status", directCase.Opposite,
                "--output-format", "json");

            Assert.Equal(ExitCodes.Success, direct.ExitCode);
            Assert.Equal(ExitCodes.Success, explicitAll.ExitCode);
            Assert.Equal(ExitCodes.Success, matchingStatus.ExitCode);
            Assert.Equal(ExitCodes.Success, oppositeStatus.ExitCode);
            Assert.Equal([expected.Id], MatchedIds(direct));
            Assert.Equal([expected.Id], MatchedIds(explicitAll));
            Assert.Equal([expected.Id], MatchedIds(matchingStatus));
            Assert.Empty(MatchedIds(oppositeStatus));
            Assert.Equal([directCase.Kind], MatchedKinds(direct));
        }

        var ownerAndChildArguments = new[]
        {
            "symbol", "find", "--namespace-literal", "Acceptance.Graph",
            "--type-literal", "GraphHost", "--method", "SyncOwnerWithAsyncChild().**",
        };
        var ownerAndChildAll = await _fixture.RunStandardCliAsync(
            [.. ownerAndChildArguments, "--async-status", "all", "--output-format", "json"]);
        var ownerOnly = await _fixture.RunStandardCliAsync(
            [.. ownerAndChildArguments, "--async-status", "sync", "--output-format", "json"]);
        var childOnly = await _fixture.RunStandardCliAsync(
            [.. ownerAndChildArguments, "--async-status", "async", "--output-format", "json"]);
        Assert.Equal(ExitCodes.Success, ownerAndChildAll.ExitCode);
        Assert.Equal(ExitCodes.Success, ownerOnly.ExitCode);
        Assert.Equal(ExitCodes.Success, childOnly.ExitCode);
        Assert.Equal(
            [
                "Acceptance.Graph.GraphHost::SyncOwnerWithAsyncChild()",
                "Acceptance.Graph.GraphHost::SyncOwnerWithAsyncChild().<lambda#1>",
            ],
            MatchedNames(ownerAndChildAll));
        Assert.Equal(
            ["Acceptance.Graph.GraphHost::SyncOwnerWithAsyncChild()"],
            MatchedNames(ownerOnly));
        Assert.Equal(
            ["Acceptance.Graph.GraphHost::SyncOwnerWithAsyncChild().<lambda#1>"],
            MatchedNames(childOnly));
    }

    [Fact]
    public async Task CM08_InitializerAndTopLevelAreReachableThroughAllAndDirectPathsOnly()
    {
        var directPaths = new[]
        {
            (Path: "Acceptance.Special.InitializerHost::<initializer:Changed>", Kind: "initializer"),
            (Path: "Acceptance.Special.InitializerHost::<initializer:Changed>.<lambda#1>", Kind: "lambda"),
            (Path: "Program::<top-level-statements>", Kind: "toplevelstatements"),
            (Path: "Program::<top-level-statements>.TopLevelLocal()", Kind: "method"),
            (Path: "Program::<top-level-statements>.TopLevelLocal().<lambda#1>", Kind: "lambda"),
        };
        foreach (var directPath in directPaths)
        {
            var expected = await _fixture.GetSymbolAsync(directPath.Path, cancellationToken: Token);
            var direct = await _fixture.RunStandardCliAsync(
                "symbol", "find", directPath.Path, "--output-format", "json");
            var explicitAll = await _fixture.RunStandardCliAsync(
                "symbol", "find", directPath.Path, "--kind", "all", "--output-format", "json");
            Assert.Equal(ExitCodes.Success, direct.ExitCode);
            Assert.Equal(ExitCodes.Success, explicitAll.ExitCode);
            Assert.Equal([expected.Id], MatchedIds(direct));
            Assert.Equal([expected.Id], MatchedIds(explicitAll));
            Assert.Equal([directPath.Kind], MatchedKinds(direct));
        }

        foreach (var invalidKind in new[] { "initializer", "top-level" })
        {
            await AssertRejectedBeforeDependenciesAsync(
                ["symbol", "find", "Program::<top-level-statements>", "--kind", invalidKind],
                UsageError($"Unknown symbol kind: {invalidKind}. Use all, method, or lambda."));
        }

        var initializerArguments = new[]
        {
            "symbol", "find", "--namespace-literal", "Acceptance.Special",
            "--type-literal", "InitializerHost", "--method", "<initializer:Changed>.**",
        };
        var initializerAll = await _fixture.RunStandardCliAsync(
            [.. initializerArguments, "--kind", "all", "--output-format", "json"]);
        var initializerMethods = await _fixture.RunStandardCliAsync(
            [.. initializerArguments, "--kind", "method", "--output-format", "json"]);
        var initializerLambdas = await _fixture.RunStandardCliAsync(
            [.. initializerArguments, "--kind", "lambda", "--output-format", "json"]);
        Assert.Equal(ExitCodes.Success, initializerAll.ExitCode);
        Assert.Equal(ExitCodes.Success, initializerMethods.ExitCode);
        Assert.Equal(ExitCodes.Success, initializerLambdas.ExitCode);
        Assert.Equal(
            new[]
            {
                "Acceptance.Special.InitializerHost::<initializer:Changed>",
                "Acceptance.Special.InitializerHost::<initializer:Changed>.<lambda#1>",
            }.Order(StringComparer.Ordinal),
            MatchedNames(initializerAll).Order(StringComparer.Ordinal));
        Assert.Empty(MatchedIds(initializerMethods));
        Assert.Equal(
            ["Acceptance.Special.InitializerHost::<initializer:Changed>.<lambda#1>"],
            MatchedNames(initializerLambdas));

        var topLevelArguments = new[]
        {
            "symbol", "find", "--type-literal", "Program", "--method", "<top-level-statements>.**",
        };
        var topLevelAll = await _fixture.RunStandardCliAsync(
            [.. topLevelArguments, "--kind", "all", "--output-format", "json"]);
        var topLevelMethods = await _fixture.RunStandardCliAsync(
            [.. topLevelArguments, "--kind", "method", "--output-format", "json"]);
        var topLevelLambdas = await _fixture.RunStandardCliAsync(
            [.. topLevelArguments, "--kind", "lambda", "--output-format", "json"]);
        Assert.Equal(ExitCodes.Success, topLevelAll.ExitCode);
        Assert.Equal(ExitCodes.Success, topLevelMethods.ExitCode);
        Assert.Equal(ExitCodes.Success, topLevelLambdas.ExitCode);
        Assert.Equal(
            new[]
            {
                "Program::<top-level-statements>",
                "Program::<top-level-statements>.TopLevelLocal()",
                "Program::<top-level-statements>.TopLevelLocal().<lambda#1>",
            }.Order(StringComparer.Ordinal),
            MatchedNames(topLevelAll).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["Program::<top-level-statements>.TopLevelLocal()"],
            MatchedNames(topLevelMethods));
        Assert.Equal(
            ["Program::<top-level-statements>.TopLevelLocal().<lambda#1>"],
            MatchedNames(topLevelLambdas));

        foreach (var owner in new[]
                 {
                     "Acceptance.Special.InitializerHost::<initializer:Changed>",
                     "Program::<top-level-statements>",
                 })
        {
            foreach (var leakingKind in new[] { "method", "lambda" })
            {
                var leakedOwner = await _fixture.RunStandardCliAsync(
                    "symbol", "find", owner, "--kind", leakingKind, "--output-format", "json");
                Assert.Equal(ExitCodes.Success, leakedOwner.ExitCode);
                Assert.Empty(MatchedIds(leakedOwner));
            }
        }
    }

    [Fact]
    public async Task CM09_OverridesRejectsLambdaOnlyRootsBeforeTraversal()
    {
        var repository = _fixture.CreateRepository();
        var traversalEvents = 0;
        repository.TraversalObserver = _ => traversalEvents++;
        var query = new SemanticQueryService(repository);
        var outputPath = Path.Combine(_fixture.ContainerPath, "cm09-sentinel.txt");
        File.WriteAllText(outputPath, "cm09 sentinel");

        foreach (var selector in new[]
                 {
                     "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#1>",
                     "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#*>",
                 })
        {
            var counters = new DependencyCounters();
            var result = await RunWithDependenciesAsync(
                [
                    "overrides", selector, "--output-format", "json",
                    "--output-file", outputPath, "--db", _fixture.StandardDatabasePath,
                ],
                counters,
                throwOnDependency: true,
                injectedQueryService: query);
            Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
            Assert.Equal(
                $"Query error: --kind lambda is not applicable to overrides.{Environment.NewLine}",
                result.StandardError);
            Assert.Empty(result.StandardOutput);
            Assert.Equal(1, counters.QueryFactoryCalls);
            Assert.Equal(0, counters.OutputFactoryCalls);
            Assert.Equal(0, counters.AnalysisFactoryCalls);
            Assert.Equal(0, counters.SqliteFactoryCalls);
            Assert.Equal(0, traversalEvents);
            Assert.Equal("cm09 sentinel", File.ReadAllText(outputPath));
        }

        traversalEvents = 0;
        var validCounters = new DependencyCounters();
        var valid = await RunWithDependenciesAsync(
            [
                "overrides", "Acceptance.Graph.GraphBase::OverrideMe()",
                "--method-literal", "OverrideMe()", "--output-format", "json",
                "--db", _fixture.StandardDatabasePath,
            ],
            validCounters,
            throwOnDependency: false,
            injectedQueryService: query);
        Assert.Equal(ExitCodes.Success, valid.ExitCode);
        Assert.Equal(1, validCounters.QueryFactoryCalls);
        Assert.Equal(1, validCounters.OutputFactoryCalls);
        Assert.Equal(0, validCounters.AnalysisFactoryCalls);
        Assert.Equal(0, validCounters.SqliteFactoryCalls);
        Assert.True(traversalEvents > 0);
        Assert.Equal(
            ["Acceptance.Graph.GraphDerived::OverrideMe()->Acceptance.Graph.GraphBase::OverrideMe()"],
            RelationEndpoints(valid));
        Assert.Equal("cm09 sentinel", File.ReadAllText(outputPath));
    }

    [Fact]
    public async Task CM10_PartialDefinitionAndImplementationNeverDuplicateCardinality()
    {
        const string selector = "Acceptance.Partials.PartialHost::PairedPartial()";
        var partial = await _fixture.GetSymbolAsync(selector, cancellationToken: Token);
        var invokePaired = await _fixture.GetSymbolAsync(
            "Acceptance.Partials.PartialHost::InvokePaired()", cancellationToken: Token);
        var graphTarget = await _fixture.GetSymbolAsync(
            "Acceptance.Partials.PartialHost::GraphTarget()", cancellationToken: Token);
        var partialBase = await _fixture.GetSymbolAsync(
            "Acceptance.Partials.PartialBase::PairedPartial()", cancellationToken: Token);

        var find = await _fixture.RunStandardCliAsync(
            "symbol", "find", selector, "--require-single", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, find.ExitCode);
        Assert.Equal([partial.Id], MatchedIds(find));

        var show = await _fixture.RunStandardCliAsync(
            "source", "show", selector, "--output-format", "json");
        Assert.Equal(ExitCodes.Success, show.ExitCode);
        Assert.Equal([partial.Id], MatchedIds(show));
        using (var showDocument = JsonDocument.Parse(show.StandardOutput))
        {
            var source = Assert.Single(showDocument.RootElement.GetProperty("matched").EnumerateArray())
                .GetProperty("normalizedSource").GetString();
            Assert.Contains("PARTIAL-IMPLEMENTATION-MARKER", source, StringComparison.Ordinal);
            Assert.DoesNotContain("PARTIAL-DEFINITION-MARKER", source, StringComparison.Ordinal);
        }

        var definition = await _fixture.RunStandardCliAsync(
            "definition", selector, "--require-single", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, definition.ExitCode);
        Assert.Equal([partial.Id], MatchedIds(definition));
        Assert.Equal(["partial-definition", "partial-implementation"], DefinitionRoles(definition));
        Assert.Equal(2, DefinitionLocations(definition).Distinct().Count());

        var expectedInbound = new[]
        {
            "Acceptance.Partials.PartialHost::InvokePaired()->" +
            "Acceptance.Partials.PartialHost::PairedPartial()",
        };
        var references = await _fixture.RunStandardCliAsync(
            "references", selector, "--require-single", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, references.ExitCode);
        Assert.Equal([partial.Id], MatchedIds(references));
        Assert.Equal(expectedInbound, CallEndpoints(references));

        var callers = await _fixture.RunStandardCliAsync(
            "callers", selector, "--require-single", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, callers.ExitCode);
        Assert.Equal([partial.Id], MatchedIds(callers));
        Assert.Equal(expectedInbound, CallEndpoints(callers));
        Assert.Equal(["Acceptance.Partials.PartialHost::InvokePaired()"], CallerNames(callers));

        var callees = await _fixture.RunStandardCliAsync(
            "callees", selector, "--require-single", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, callees.ExitCode);
        Assert.Equal([partial.Id], MatchedIds(callees));
        Assert.Equal(
            [
                "Acceptance.Partials.PartialHost::PairedPartial()->" +
                "Acceptance.Partials.PartialHost::GraphTarget()",
            ],
            CallEndpoints(callees));

        var overrides = await _fixture.RunStandardCliAsync(
            "overrides", "Acceptance.Partials.PartialBase::PairedPartial()",
            "--require-single", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, overrides.ExitCode);
        Assert.Equal([partialBase.Id], MatchedIds(overrides));
        Assert.Equal(
            [
                "Acceptance.Partials.PartialHost::PairedPartial()->" +
                "Acceptance.Partials.PartialBase::PairedPartial()",
            ],
            RelationEndpoints(overrides));

        var asyncTree = await _fixture.RunStandardCliAsync(
            "async", "tree", selector, "--max-nodes", "20", "--output-format", "json");
        Assert.Equal(ExitCodes.Success, asyncTree.ExitCode);
        Assert.Equal(partial.Id, RootId(asyncTree));
        using (var asyncDocument = JsonDocument.Parse(asyncTree.StandardOutput))
        {
            Assert.False(asyncDocument.RootElement.GetProperty("found").GetBoolean());
            Assert.False(asyncDocument.RootElement.GetProperty("truncated").GetBoolean());
            Assert.Empty(asyncDocument.RootElement.GetProperty("nodes").EnumerateArray());
        }

        var callerTree = await _fixture.RunStandardCliAsync(
            "callers", "tree", selector, "--depth", "2", "--max-nodes", "20",
            "--output-format", "json");
        Assert.Equal(ExitCodes.Success, callerTree.ExitCode);
        Assert.Equal(partial.Id, RootId(callerTree));
        Assert.Equal(
            new[] { partial.Id, invokePaired.Id }.Order(),
            PropertyIds(callerTree, "nodes").Order());
        Assert.Equal(
            [$"{invokePaired.Id}->{partial.Id}"],
            CallerTreeEdges(callerTree));

        Assert.NotEqual(partial.Id, graphTarget.Id);
        Assert.Equal(1, PropertyIds(callerTree, "nodes").Count(id => id == partial.Id));
    }

    private async Task<CSharpSymbolPathCliResult> RunHelpOptionAsync(
        CommandScope scope,
        string option,
        bool useEquals)
    {
        var optionTokens = OptionTokens(scope, option);
        if (useEquals && optionTokens.Length == 2)
        {
            optionTokens = [$"{optionTokens[0]}={optionTokens[1]}"];
        }
        else if (useEquals && optionTokens.Length == 1)
        {
            optionTokens = [$"{optionTokens[0]}=true"];
        }

        return await _fixture.RunCliAsync([.. scope.Prefix, "--help", .. optionTokens]);
    }

    private static bool IsValueOption(string option) => !FlagOptions.Contains(option);

    private static string UnknownOptionError(string option) =>
        $"Argument error: Unknown option(s): --{option}{Environment.NewLine}" +
        $"Run 'csindex --help' for usage.{Environment.NewLine}";

    private static string UsageError(string message) =>
        $"Argument error: {message}{Environment.NewLine}" +
        $"Run 'csindex --help' for usage.{Environment.NewLine}";

    private async Task AssertRejectedBeforeDependenciesAsync(
        string[] arguments,
        string expectedStandardError)
    {
        var counters = new DependencyCounters();
        var result = await RunWithDependenciesAsync(arguments, counters, throwOnDependency: true);
        Assert.True(
            result.ExitCode == ExitCodes.InvalidArguments,
            $"{string.Join(' ', arguments)} exited {result.ExitCode}: " +
            $"{result.StandardError}{result.StandardOutput}");
        Assert.Empty(result.StandardOutput);
        Assert.Equal(expectedStandardError, result.StandardError);
        Assert.Equal(0, counters.QueryFactoryCalls);
        Assert.Equal(0, counters.OutputFactoryCalls);
        Assert.Equal(0, counters.AnalysisFactoryCalls);
        Assert.Equal(0, counters.SqliteFactoryCalls);
    }

    private string ValueFor(string option) => option switch
    {
        "at" => "Source.cs:1:1",
        "db" => _fixture.StandardDatabasePath,
        "profile" or "profile-name" => "default",
        "output-format" => "json",
        "output-file" => Path.Combine(_fixture.ContainerPath, "cm-option-result.txt"),
        "mode" => "directory",
        "solution" or "configuration" or "framework" or "target-framework" or "runtime" => "value",
        "define" or "undefine" => "SYMBOL",
        "define-file" or "reference" or "unity-editor" => "value",
        "generated-source" => "physical",
        "exclude" => "obj/**",
        "source-layout" => "single-line",
        "dispatch" => "static",
        "caller-scope" => "direct",
        "depth" or "max-nodes" => "1",
        "namespace-case" or "type-case" or "method-case" or "file-case" or "source-case" => "strict",
        "kind" => "all",
        "async-status" => "all",
        "symbol-path-style" => "csharp",
        "path-style" => "absolute",
        _ => option is "include" or "include-literal" or "include-regex" or
            "exclude" or "exclude-literal" or "exclude-regex"
                ? "Play"
                : option is "file" or "file-literal" or "file-regex"
                    ? "Main.cs"
                    : option is "namespace" or "namespace-literal" or "namespace-regex"
                        ? "Alpha"
                        : option is "type" or "type-literal" or "type-regex"
                            ? "AClass"
                            : option is "method" or "method-literal" or "method-regex"
                                ? "Play"
                                : ".",
    };

    private string[] OptionTokens(string option) =>
        IsValueOption(option)
            ? [$"--{option}", ValueFor(option)]
            : [$"--{option}"];

    private string[] OptionTokens(CommandScope scope, string option) =>
        option == "at" && scope.Prefix.Contains("--at", StringComparer.Ordinal)
            ? []
            : OptionTokens(option);

    private static IReadOnlyList<string> ReadAcceptedOptions(string help)
    {
        var lines = help.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var heading = Array.IndexOf(lines, "Accepted options:");
        Assert.True(heading >= 0, help);
        return lines
            .Skip(heading + 1)
            .TakeWhile(line => line.StartsWith("  --", StringComparison.Ordinal))
            .Select(line => line[4..])
            .OrderBy(option => option, StringComparer.Ordinal)
            .ToArray();
    }

    private static long[] PropertyIds(CSharpSymbolPathCliResult result, string property)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty(property)
            .EnumerateArray()
            .Select(value => value.TryGetProperty("id", out var id)
                ? id.GetInt64()
                : value.GetProperty("symbol").GetProperty("id").GetInt64())
            .ToArray();
    }

    private static long RootId(CSharpSymbolPathCliResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("root").GetProperty("id").GetInt64();
    }

    private static string[] RelationEndpoints(CSharpSymbolPathCliResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("relations")
            .EnumerateArray()
            .Select(value => $"{value.GetProperty("source").GetString()}->{value.GetProperty("target").GetString()}")
            .ToArray();
    }

    private static string[] CallEndpoints(CSharpSymbolPathCliResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("calls")
            .EnumerateArray()
            .Select(value => $"{value.GetProperty("caller").GetString()}->{value.GetProperty("callee").GetString()}")
            .ToArray();
    }

    private static string[] CallerNames(CSharpSymbolPathCliResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("callers")
            .EnumerateArray()
            .Select(value => value.GetProperty("displayName").GetString()!)
            .ToArray();
    }

    private static string[] CallerTreeEdges(CSharpSymbolPathCliResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("edges")
            .EnumerateArray()
            .Select(value =>
                $"{value.GetProperty("callerSymbolId").GetInt64()}->" +
                $"{value.GetProperty("calleeSymbolId").GetInt64()}")
            .ToArray();
    }

    private static string[] BuildCaseArguments(
        string category,
        string selectedOption,
        string selectedValue,
        string? caseMode)
    {
        var arguments = new List<string> { "symbol", "find" };
        AddSelectedOrDefault(
            arguments, category, "namespace", selectedOption, selectedValue,
            "--namespace-literal", "Acceptance.Corpus");
        AddSelectedOrDefault(
            arguments, category, "type", selectedOption, selectedValue,
            "--type-literal", "LocalOwners");
        AddSelectedOrDefault(
            arguments, category, "method", selectedOption, selectedValue,
            "--method-literal", "RootOne()");
        AddSelectedOrDefault(
            arguments, category, "file", selectedOption, selectedValue,
            "--file-literal", "src/Corpus/Ambiguity.cs");
        AddSelectedOrDefault(
            arguments, category, "source", selectedOption, selectedValue,
            "--include-literal", "RootOne");
        if (caseMode is not null)
        {
            AddCaseOptions(arguments, category, caseMode);
        }
        arguments.AddRange(["--output-format", "json"]);
        return arguments.ToArray();
    }

    private static string[] BuildAllMisCasedArguments(string ignoredCategory)
    {
        var arguments = new List<string>
        {
            "symbol", "find",
            "--namespace-literal", "acceptance.corpus",
            "--type-literal", "localowners",
            "--method-literal", "rootone()",
            "--file-literal", "src/corpus/ambiguity.cs",
            "--include-literal", "rootone",
        };
        AddCaseOptions(arguments, ignoredCategory, "ignore");
        arguments.AddRange(["--output-format", "json"]);
        return arguments.ToArray();
    }

    private static void AddSelectedOrDefault(
        List<string> arguments,
        string selectedCategory,
        string category,
        string selectedOption,
        string selectedValue,
        string defaultOption,
        string defaultValue)
    {
        if (selectedCategory.Equals(category, StringComparison.Ordinal))
        {
            arguments.Add(selectedOption);
            arguments.Add(selectedValue);
            return;
        }

        arguments.Add(defaultOption);
        arguments.Add(defaultValue);
    }

    private static void AddCaseOptions(List<string> arguments, string selectedCategory, string selectedMode)
    {
        foreach (var (category, option) in new[]
                 {
                     ("namespace", "--namespace-case"),
                     ("type", "--type-case"),
                     ("method", "--method-case"),
                     ("file", "--file-case"),
                     ("source", "--source-case"),
                 })
        {
            arguments.Add(option);
            arguments.Add(selectedCategory.Equals(category, StringComparison.Ordinal) ? selectedMode : "strict");
        }
    }

    private static long[] MatchedIds(CSharpSymbolPathCliResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("matched")
            .EnumerateArray()
            .Select(value => value.GetProperty("id").GetInt64())
            .ToArray();
    }

    private static string[] MatchedNames(CSharpSymbolPathCliResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("matched")
            .EnumerateArray()
            .Select(value => value.GetProperty("displayName").GetString()!)
            .ToArray();
    }

    private static string[] MatchedKinds(CSharpSymbolPathCliResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("matched")
            .EnumerateArray()
            .Select(value => value.GetProperty("kind").GetString()!)
            .ToArray();
    }

    private static string[] MatchedRoles(CSharpSymbolPathCliResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("matched")
            .EnumerateArray()
            .Select(value => value.GetProperty("declarationRole").GetString()!)
            .ToArray();
    }

    private static string[] MatchedLocations(CSharpSymbolPathCliResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("matched")
            .EnumerateArray()
            .Select(value => value.GetProperty("location").GetProperty("path").GetString()!)
            .ToArray();
    }

    private static long[] DefinitionIds(CSharpSymbolPathCliResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("definitions")
            .EnumerateArray()
            .Select(value => value.GetProperty("id").GetInt64())
            .ToArray();
    }

    private static string[] DefinitionRoles(CSharpSymbolPathCliResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("definitions")
            .EnumerateArray()
            .Select(value => value.GetProperty("declarationRole").GetString()!)
            .ToArray();
    }

    private static (string Path, int Offset)[] DefinitionLocations(CSharpSymbolPathCliResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("definitions")
            .EnumerateArray()
            .Select(value => (
                Path: value.GetProperty("location").GetProperty("path").GetString()!,
                Offset: value.GetProperty("location").GetProperty("offset").GetInt32()))
            .ToArray();
    }

    private async Task<CSharpSymbolPathCliResult> RunWithDependenciesAsync(
        string[] arguments,
        DependencyCounters counters,
        bool throwOnDependency,
        bool forceRegexTimeout = false,
        SemanticQueryService? injectedQueryService = null)
    {
        var dependencies = new ProgramDependencies(
            (outputPath, databasePath) =>
            {
                counters.OutputFactoryCalls++;
                if (throwOnDependency)
                {
                    throw new InvalidOperationException("Output destination must remain unopened.");
                }

                return OutputDestination.Create(outputPath, databasePath);
            },
            (databasePath, baseDirectory) =>
            {
                counters.QueryFactoryCalls++;
                if (forceRegexTimeout)
                {
                    throw new RegexMatchTimeoutException("Forced regex timeout at the query boundary.");
                }

                if (injectedQueryService is not null)
                {
                    return injectedQueryService;
                }

                if (throwOnDependency)
                {
                    throw new InvalidOperationException("Query service must remain unopened.");
                }

                return _fixture.CreateQueryService(databasePath, baseDirectory);
            },
            () =>
            {
                counters.AnalysisFactoryCalls++;
                throw new InvalidOperationException("Analysis coordinator must remain unused.");
            },
            databasePath =>
            {
                counters.SqliteFactoryCalls++;
                throw new InvalidOperationException("SQLite factory must remain unused.");
            });

        await ConsoleGate.WaitAsync(Token);
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await Program.RunAsync(arguments, Token, dependencies);
            return new CSharpSymbolPathCliResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            ConsoleGate.Release();
        }
    }

    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    private sealed class DependencyCounters
    {
        public int OutputFactoryCalls { get; set; }
        public int QueryFactoryCalls { get; set; }
        public int AnalysisFactoryCalls { get; set; }
        public int SqliteFactoryCalls { get; set; }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
