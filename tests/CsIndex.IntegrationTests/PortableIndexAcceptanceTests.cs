using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Cli;
using CsIndex.Core.Analysis;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

[Collection(CSharpSymbolPathAcceptanceCollection.Name)]
public sealed class PortableIndexAcceptanceTests(CSharpSymbolPathAcceptanceFixture fixture)
{
    private readonly CSharpSymbolPathAcceptanceFixture _fixture = fixture;

    private static readonly ImmutableArray<string> TypedConditionOptions =
    [
        "namespace", "namespace-literal", "namespace-regex", "namespace-case",
        "type", "type-literal", "type-regex", "type-case",
        "method", "method-literal", "method-regex", "method-case",
        "file", "file-literal", "file-regex", "file-case",
        "include", "include-literal", "include-regex",
        "exclude", "exclude-literal", "exclude-regex", "source-case",
    ];

    private static readonly ImmutableArray<string> QueryPresentationOptions =
    [
        "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
        "symbol-path-style", "short-names", "base-dir", "path-style",
    ];

    private static readonly ImmutableArray<string> QuerySelectionOptions =
    [.. TypedConditionOptions, "kind", "async-status"];

    private static readonly ImmutableArray<string> QueryOptions =
    [.. QueryPresentationOptions, .. QuerySelectionOptions];

    private static readonly ImmutableArray<HelpScope> ExpectedHelpScopes =
    [
        new("global", [],
            ["db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose"]),
        new("index", ["index"],
            [
                "db", "mode", "solution", "configuration", "framework", "target-framework", "runtime",
                "profile-name", "define", "undefine", "define-file", "reference", "unity-editor", "exclude",
                "generated-source", "rebuild", "verbose", "diagnostics", "help", "help-verbose",
            ]),
        new("symbol find", ["symbol", "find"],
            [.. QueryOptions, "require-single", "include-overrides", "show-source", "source-layout"]),
        new("symbol list", ["symbol", "list"],
            [.. QueryOptions, "async-involved"]),
        new("source show", ["source", "show"],
            [.. QueryOptions, "source-layout"]),
        new("source search", ["source", "search"],
            [.. QueryOptions, "source-layout"]),
        new("definition query", ["definition"],
            [.. QueryOptions, "at", "require-single", "include-overrides"]),
        new("definition at", ["definition", "--at", "Source.cs:1:1"],
            [.. QueryPresentationOptions, "at"]),
        new("references", ["references"],
            [.. QueryOptions, "exclude-generated", "only-generated", "require-single", "include-overrides"]),
        new("callers", ["callers"],
            [
                .. QueryOptions, "exclude-generated", "only-generated", "require-single", "include-overrides",
                "dispatch", "caller-scope",
            ]),
        new("callees", ["callees"],
            [
                .. QueryOptions, "exclude-generated", "only-generated", "require-single", "include-overrides",
                "exclude-lambda-calls",
            ]),
        new("overrides", ["overrides"],
            [.. TypedConditionOptions, "kind", "async-status", .. QueryPresentationOptions, "require-single"]),
        new("async tree", ["async", "tree"],
            [.. QueryOptions, "max-nodes"]),
        new("callers tree", ["callers", "tree"],
            [.. QueryOptions, "depth", "max-nodes"]),
        new("conditions", ["conditions"],
            ["db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose", "base-dir", "path-style"]),
    ];

    private static readonly ImmutableArray<PresentationCase> PresentationCases =
    [
        new("csharp-full-default-json", SymbolPathStyle.CSharp, false, PathDisplayStyle.Absolute, false, "strict", "json"),
        new("explicit-full-default-json", SymbolPathStyle.Explicit, false, PathDisplayStyle.Relative, false, "ignore", "json"),
        new("csharp-short-alternate-json", SymbolPathStyle.CSharp, true, PathDisplayStyle.Absolute, true, "ignore", "json"),
        new("explicit-short-alternate-json", SymbolPathStyle.Explicit, true, PathDisplayStyle.Relative, true, "strict", "json"),
        new("csharp-full-default-table", SymbolPathStyle.CSharp, false, PathDisplayStyle.Relative, false, "strict", "table"),
        new("explicit-full-default-table", SymbolPathStyle.Explicit, false, PathDisplayStyle.Absolute, false, "ignore", "table"),
        new("csharp-short-alternate-table", SymbolPathStyle.CSharp, true, PathDisplayStyle.Relative, true, "ignore", "table"),
        new("explicit-short-alternate-table", SymbolPathStyle.Explicit, true, PathDisplayStyle.Absolute, true, "strict", "table"),
    ];

    public static TheoryData<string, string, string[]> SchemaFourAttempts => new()
    {
        { "PP10", "symbol-list", ["symbol", "list"] },
        { "PP10", "symbol-find", ["symbol", "find", "Acceptance.Shared.Linked::LinkedMarker()"] },
        { "PP10", "async-tree", ["async", "tree", "Acceptance.Graph.GraphHost::AsyncRoot()"] },
        { "PP10", "caller-tree", ["callers", "tree", "Acceptance.Graph.GraphHost::TreeRoot()"] },
        { "PP10", "source-show", ["source", "show", "Acceptance.Shared.Linked::LinkedMarker()"] },
        { "PP10", "source-search", ["source", "search", "--include-literal", "LinkedMarker"] },
        { "PP10", "definition", ["definition", "Acceptance.Shared.Linked::LinkedMarker()"] },
        { "PP10", "references", ["references", "Acceptance.Shared.Linked::LinkedMarker()"] },
        { "PP10", "callers", ["callers", "Acceptance.Shared.Linked::LinkedMarker()"] },
        { "PP10", "callees", ["callees", "Acceptance.Corpus.LocalOwners::CallsLinked()"] },
        { "PP10", "overrides", ["overrides", "Acceptance.Partials.PartialBase::PairedPartial()"] },
        { "PP10", "conditions", ["conditions"] },
        { "PP10", "index", ["index", "<solution>"] },
        { "PP10", "index-rebuild", ["index", "<solution>", "--rebuild"] },
    };

    public static TheoryData<string, string, string[]> SchemaFourGuidanceAttempts => new()
    {
        { "PP11", "query", ["conditions"] },
        { "PP11", "index", ["index", "<solution>"] },
        { "PP11", "index-rebuild", ["index", "<solution>", "--rebuild"] },
    };

    [Fact]
    public async Task PP01_StandardDatabaseStoresParentAnchorAndRootRelativePaths()
    {
        var values = await _fixture.ReadAllTextColumnsAsync(cancellationToken: Token);
        var profile = await _fixture.GetProfileAsync(cancellationToken: Token);
        var resolver = _fixture.CreatePathResolver();

        Assert.Equal("..", profile.IndexRootAnchor);
        Assert.Equal(["."], values["index_runs.input_root"].Distinct(StringComparer.Ordinal));
        Assert.Equal([".."], values["index_runs.index_root_anchor"].Distinct(StringComparer.Ordinal));

        foreach (var key in new[]
        {
            "projects.project_path",
            "documents.normalized_path",
            "symbols.stable_key",
            "symbol_declarations.declaration_key",
        })
        {
            Assert.NotEmpty(values[key]);
            Assert.All(values[key].Where(value => value is not null), value =>
            {
                Assert.DoesNotContain('\\', value!);
                Assert.DoesNotContain(_fixture.ContainerPath, value!, StringComparison.OrdinalIgnoreCase);
                Assert.False(Path.IsPathRooted(value!), value);
            });
        }

        var documentPaths = values["documents.normalized_path"]
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("src/Corpus/Ambiguity.cs", documentPaths);
        Assert.Contains("../Shared/Linked.cs", documentPaths);
        foreach (var storedPath in documentPaths)
        {
            var absolutePath = resolver.ToAbsolutePath(storedPath);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(_fixture.WorkspacePath, storedPath.Replace('/', Path.DirectorySeparatorChar))),
                absolutePath);
            Assert.True(File.Exists(absolutePath), absolutePath);
        }
    }

    [Fact]
    public async Task PP02_CustomDatabaseStoresTheCalculatedRelativeSourceAnchor()
    {
        var standardValues = await _fixture.ReadAllTextColumnsAsync(
            _fixture.StandardDatabasePath,
            Token);
        var customValues = await _fixture.ReadAllTextColumnsAsync(
            _fixture.CustomDatabasePath,
            Token);
        var customProfile = await _fixture.GetProfileAsync(
            _fixture.CustomDatabasePath,
            Token);
        var customPaths = IndexPathResolver.CreateForIndex(
            _fixture.CustomDatabasePath,
            _fixture.WorkspacePath);
        var databaseDirectory = Path.GetDirectoryName(
            Path.GetFullPath(_fixture.CustomDatabasePath))!;
        var expectedAnchor = Path.GetRelativePath(
                databaseDirectory,
                Path.GetFullPath(_fixture.WorkspacePath))
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        Assert.Equal(expectedAnchor, customPaths.IndexRootAnchor);
        Assert.Equal(expectedAnchor, customProfile.IndexRootAnchor);
        Assert.NotEqual("..", customProfile.IndexRootAnchor);
        Assert.Equal(
            [expectedAnchor],
            customValues["index_runs.index_root_anchor"].Distinct(StringComparer.Ordinal));

        foreach (var key in new[]
        {
            "index_runs.input_root",
            "projects.project_path",
            "documents.normalized_path",
            "symbols.stable_key",
            "symbol_declarations.declaration_key",
        })
        {
            Assert.Equal(standardValues[key], customValues[key]);
        }

        Assert.Contains(
            "../Shared/Linked.cs",
            customValues["documents.normalized_path"].OfType<string>());
    }

    [Fact]
    public async Task PP03_StandardAndCustomLayoutsFollowRelativeRelocation()
    {
        var destinationParent = Path.Combine(
            Path.GetTempPath(),
            $"csindex-pp03-relocation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destinationParent);
        try
        {
            var relocatedContainer = _fixture.CopyContainerTo(destinationParent);
            var relocatedWorkspace = Path.Combine(relocatedContainer, "workspace");
            var relocatedStandardDatabase = Path.Combine(
                relocatedWorkspace, ".csindex", "index.sqlite");
            var relocatedCustomDatabase = Path.Combine(
                relocatedContainer, "indexes", "custom", "index.sqlite");

            Assert.Equal(
                await _fixture.ReadDatabaseSnapshotAsync(
                    _fixture.StandardDatabasePath, Token),
                await _fixture.ReadDatabaseSnapshotAsync(
                    relocatedStandardDatabase, Token));
            Assert.Equal(
                await _fixture.ReadDatabaseSnapshotAsync(
                    _fixture.CustomDatabasePath, Token),
                await _fixture.ReadDatabaseSnapshotAsync(
                    relocatedCustomDatabase, Token));

            var original = await RunSymbolListAsync(
                _fixture.StandardDatabasePath,
                PathDisplayStyle.Relative);
            var relocatedStandard = await RunSymbolListAsync(
                relocatedStandardDatabase,
                PathDisplayStyle.Absolute);
            var relocatedCustom = await RunSymbolListAsync(
                relocatedCustomDatabase,
                PathDisplayStyle.Absolute);

            Assert.Equal(SymbolIdentities(original), SymbolIdentities(relocatedStandard));
            Assert.Equal(SymbolIdentities(original), SymbolIdentities(relocatedCustom));
            AssertRelocatedLocationPaths(relocatedStandard, relocatedContainer);
            AssertRelocatedLocationPaths(relocatedCustom, relocatedContainer);
        }
        finally
        {
            Directory.Delete(destinationParent, recursive: true);
        }
    }

    [Fact]
    public async Task PP04_BaseDirectoryOverrideIsReadTimeOnly()
    {
        var destinationParent = Path.Combine(
            Path.GetTempPath(),
            $"csindex-pp04-base-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destinationParent);
        try
        {
            var relocatedContainer = _fixture.CopyContainerTo(destinationParent);
            var relocatedWorkspace = Path.Combine(relocatedContainer, "workspace");
            var relocatedDatabase = Path.Combine(
                relocatedWorkspace, ".csindex", "index.sqlite");
            var relocatedLinkedSource = Path.Combine(relocatedContainer, "Shared", "Linked.cs");
            var beforeBytes = await _fixture.ReadDatabaseBytesAsync(relocatedDatabase, Token);
            var beforeSnapshot = await _fixture.ReadDatabaseSnapshotAsync(relocatedDatabase, Token);

            var withoutOverride = await RunSourceShowAsync(relocatedDatabase);
            var withOverride = await RunSourceShowAsync(
                relocatedDatabase,
                _fixture.WorkspacePath);
            var relativeWithOverride = await RunSourceShowAsync(
                relocatedDatabase,
                _fixture.WorkspacePath,
                PathDisplayStyle.Relative);

            Assert.Equal(
                [relocatedLinkedSource],
                LocationPaths(withoutOverride));
            Assert.Equal(
                [_fixture.LinkedSourcePath],
                LocationPaths(withOverride));
            Assert.Equal(
                ["../Shared/Linked.cs"],
                LocationPaths(relativeWithOverride));
            Assert.Equal(
                NormalizeLocationPaths(withoutOverride),
                NormalizeLocationPaths(withOverride));
            Assert.Equal(
                NormalizeLocationPaths(withoutOverride),
                NormalizeLocationPaths(relativeWithOverride));

            Assert.Equal(
                beforeBytes,
                await _fixture.ReadDatabaseBytesAsync(relocatedDatabase, Token));
            Assert.Equal(
                beforeSnapshot,
                await _fixture.ReadDatabaseSnapshotAsync(relocatedDatabase, Token));
        }
        finally
        {
            Directory.Delete(destinationParent, recursive: true);
        }
    }

    [Fact]
    public async Task PP05_EveryPathConsumerHonorsExactAbsoluteAndRelativeStyles()
    {
        var destinationParent = Path.Combine(
            Path.GetTempPath(),
            $"csindex-pp05-path-consumers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destinationParent);
        try
        {
            var relocatedContainer = _fixture.CopyContainerTo(destinationParent);
            var overrideBase = Path.Combine(relocatedContainer, "workspace");
            var defaultResolver = _fixture.CreatePathResolver();
            var overrideResolver = _fixture.CreatePathResolver(baseDirectory: overrideBase);
            var textValues = await _fixture.ReadAllTextColumnsAsync(cancellationToken: Token);
            var storedDocumentPaths = textValues["documents.normalized_path"]
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(path => path.Length)
                .ToArray();

            PathConsumerCase[] jsonConsumers =
            [
                new("symbol-find-json", ["symbol", "find", "Acceptance.Shared.Linked::LinkedMarker()", "--output-format", "json"]),
                new("references-json", ["references", "Acceptance.Shared.Linked::LinkedMarker()", "--output-format", "json"]),
                new("callers-json", ["callers", "Acceptance.Graph.GraphHost::CallerTarget()", "--output-format", "json"]),
                new("callees-json", ["callees", "Acceptance.Graph.GraphHost::RootWithSecondary()", "--output-format", "json"]),
                new("overrides-json", ["overrides", "Acceptance.Graph.GraphBase::OverrideMe()", "--output-format", "json"]),
                new("show-source-json", ["symbol", "find", "Acceptance.Shared.Linked::LinkedMarker()", "--show-source", "--output-format", "json"]),
                new("async-tree-json", ["async", "tree", "Acceptance.Graph.GraphHost::AsyncRoot()", "--output-format", "json"]),
                new("caller-tree-json", ["callers", "tree", "Acceptance.Graph.GraphHost::TreeRoot()", "--output-format", "json"]),
            ];

            foreach (var consumer in jsonConsumers)
            {
                var absolute = await RunPathVariantAsync(consumer.Arguments, baseDirectory: null, "absolute");
                var relative = await RunPathVariantAsync(consumer.Arguments, baseDirectory: null, "relative");
                var overridden = await RunPathVariantAsync(consumer.Arguments, overrideBase, "absolute");
                AssertPathConsumerSuccess(consumer.Name, absolute, relative, overridden);

                var relativePaths = AllLocationPaths(relative.StandardOutput);
                Assert.NotEmpty(relativePaths);
                Assert.Equal(
                    relativePaths.Select(defaultResolver.ToAbsolutePath),
                    AllLocationPaths(absolute.StandardOutput));
                Assert.Equal(
                    relativePaths.Select(overrideResolver.ToAbsolutePath),
                    AllLocationPaths(overridden.StandardOutput));
                Assert.Equal(
                    NormalizeLocationPaths(relative.StandardOutput),
                    NormalizeLocationPaths(absolute.StandardOutput));
                Assert.Equal(
                    NormalizeLocationPaths(relative.StandardOutput),
                    NormalizeLocationPaths(overridden.StandardOutput));
            }

            PathConsumerCase[] textConsumers =
            [
                new("symbol-find-table", ["symbol", "find", "Acceptance.Shared.Linked::LinkedMarker()"]),
                new("references-table", ["references", "Acceptance.Shared.Linked::LinkedMarker()"]),
                new("source-header", ["source", "show", "Acceptance.Shared.Linked::LinkedMarker()", "--source-layout", "multi-line"]),
                new("show-source-table", ["symbol", "find", "Acceptance.Shared.Linked::LinkedMarker()", "--show-source"]),
            ];

            foreach (var consumer in textConsumers)
            {
                var absolute = await RunPathVariantAsync(consumer.Arguments, baseDirectory: null, "absolute");
                var relative = await RunPathVariantAsync(consumer.Arguments, baseDirectory: null, "relative");
                var overridden = await RunPathVariantAsync(consumer.Arguments, overrideBase, "absolute");
                AssertPathConsumerSuccess(consumer.Name, absolute, relative, overridden);

                var emittedStoredPaths = storedDocumentPaths
                    .Where(path => relative.StandardOutput.Contains(path, StringComparison.Ordinal))
                    .ToArray();
                Assert.True(
                    emittedStoredPaths.Length > 0,
                    $"{consumer.Name} emitted no stored path:{Environment.NewLine}{relative.StandardOutput}");
                Assert.All(emittedStoredPaths, storedPath =>
                {
                    Assert.Contains(
                        defaultResolver.ToAbsolutePath(storedPath),
                        absolute.StandardOutput,
                        StringComparison.OrdinalIgnoreCase);
                    Assert.Contains(
                        overrideResolver.ToAbsolutePath(storedPath),
                        overridden.StandardOutput,
                        StringComparison.OrdinalIgnoreCase);
                });
                Assert.DoesNotContain(_fixture.ContainerPath, relative.StandardOutput, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(relocatedContainer, relative.StandardOutput, StringComparison.OrdinalIgnoreCase);
            }

            PathConsumerCase[] pathlessConsumers =
            [
                new("symbol-list-table", ["symbol", "list", "--file-literal", "../Shared/Linked.cs"]),
                new("conditions-json", ["conditions", "--output-format", "json"]),
                new("caller-mermaid", ["callers", "tree", "Acceptance.Graph.GraphHost::TreeRoot()", "--output-format", "mermaid"]),
            ];

            foreach (var consumer in pathlessConsumers)
            {
                var absolute = await RunPathVariantAsync(consumer.Arguments, baseDirectory: null, "absolute");
                var relative = await RunPathVariantAsync(consumer.Arguments, baseDirectory: null, "relative");
                var overridden = await RunPathVariantAsync(consumer.Arguments, overrideBase, "absolute");
                AssertPathConsumerSuccess(consumer.Name, absolute, relative, overridden);
                Assert.Equal(absolute.StandardOutput, relative.StandardOutput);
                Assert.Equal(absolute.StandardOutput, overridden.StandardOutput);
                Assert.DoesNotContain(_fixture.ContainerPath, absolute.StandardOutput, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(relocatedContainer, overridden.StandardOutput, StringComparison.OrdinalIgnoreCase);
                if (consumer.Name.Equals("conditions-json", StringComparison.Ordinal))
                {
                    Assert.Empty(AllLocationPaths(absolute.StandardOutput));
                }
            }

            var ambiguityArguments = new[] { "source", "show", "Class2::Method" };
            var absoluteAmbiguity = await RunPathVariantAsync(
                ambiguityArguments, baseDirectory: null, "absolute");
            var relativeAmbiguity = await RunPathVariantAsync(
                ambiguityArguments, baseDirectory: null, "relative");
            var overriddenAmbiguity = await RunPathVariantAsync(
                ambiguityArguments, overrideBase, "absolute");
            Assert.All(
                new[] { absoluteAmbiguity, relativeAmbiguity, overriddenAmbiguity },
                result => Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode));
            Assert.Contains("src/Corpus/Ambiguity.cs", relativeAmbiguity.StandardError, StringComparison.Ordinal);
            Assert.Contains(
                defaultResolver.ToAbsolutePath("src/Corpus/Ambiguity.cs"),
                absoluteAmbiguity.StandardError,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                overrideResolver.ToAbsolutePath("src/Corpus/Ambiguity.cs"),
                overriddenAmbiguity.StandardError,
                StringComparison.OrdinalIgnoreCase);

            var missingBase = Path.Combine(destinationParent, "missing-layout", "workspace");
            Directory.CreateDirectory(missingBase);
            var missingAbsolute = await RunPathVariantAsync(
                ["symbol", "find", "Acceptance.Shared.Linked::LinkedMarker()"],
                missingBase,
                "absolute");
            var missingRelative = await RunPathVariantAsync(
                ["symbol", "find", "Acceptance.Shared.Linked::LinkedMarker()"],
                missingBase,
                "relative");
            Assert.Equal(ExitCodes.AnalysisFailure, missingAbsolute.ExitCode);
            Assert.Equal(ExitCodes.AnalysisFailure, missingRelative.ExitCode);
            var missingResolver = _fixture.CreatePathResolver(baseDirectory: missingBase);
            Assert.Contains(
                missingResolver.ToAbsolutePath("../Shared/Linked.cs"),
                missingAbsolute.StandardError,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("../Shared/Linked.cs", missingRelative.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain(missingBase, missingRelative.StandardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(destinationParent, recursive: true);
        }
    }

    [Fact]
    public async Task PP06_DefinitionAtAbsoluteAndRelativeInputsSelectTheSameStoredDocument()
    {
        var callSitePath = Path.Combine(
            _fixture.CorpusPath,
            "Partials.Definition.cs");
        var callSiteSource = File.ReadAllText(callSitePath);
        var callOffset = callSiteSource.LastIndexOf("PairedPartial()", StringComparison.Ordinal);
        Assert.True(callOffset >= 0);
        var callPoint = CsIndex.Query.SourcePositionResolver.ResolveOffset(callSitePath, callOffset);
        var absoluteLocation = $"{callSitePath}:{callPoint.Line}:{callPoint.Column}";
        var relativeLocation = "src/Corpus/Partials.Definition.cs" +
                               absoluteLocation[callSitePath.Length..];

        var query = _fixture.CreateQueryService();
        var absoluteSelection = await query.FindDefinitionAtAsync(
            absoluteLocation,
            cancellationToken: Token);
        var relativeSelection = await query.FindDefinitionAtAsync(
            relativeLocation,
            cancellationToken: Token);
        Assert.True(
            absoluteSelection.Definitions.Count > 0,
            $"Absolute --at selected no declaration: {absoluteLocation}");
        Assert.True(
            relativeSelection.Definitions.Count > 0,
            $"Relative --at selected no declaration: {relativeLocation}");
        Assert.Equal(
            DefinitionIdentity(absoluteSelection),
            DefinitionIdentity(relativeSelection));

        var absoluteCli = await _fixture.RunStandardCliAsync(
            "definition", "--at", absoluteLocation,
            "--output-format", "json",
            "--path-style", "absolute");
        var relativeCli = await _fixture.RunStandardCliAsync(
            "definition", "--at", relativeLocation,
            "--output-format", "json",
            "--base-dir", _fixture.WorkspacePath,
            "--path-style", "relative");
        AssertPathConsumerSuccess("definition-at", absoluteCli, relativeCli);
        Assert.True(
            AllLocationPaths(relativeCli.StandardOutput).Count > 0,
            $"Relative definition JSON had no locations:{Environment.NewLine}{relativeCli.StandardOutput}");
        Assert.Equal(
            DefinitionJsonIdentity(absoluteCli.StandardOutput),
            DefinitionJsonIdentity(relativeCli.StandardOutput));
        Assert.Equal(
            NormalizeLocationPaths(absoluteCli.StandardOutput),
            NormalizeLocationPaths(relativeCli.StandardOutput));
        Assert.All(
            AllLocationPaths(absoluteCli.StandardOutput),
            path => Assert.True(Path.IsPathFullyQualified(path), path));
        Assert.Equal(
            [
                "src/Corpus/Partials.Definition.cs",
                "src/Corpus/Partials.Implementation.cs",
            ],
            AllLocationPaths(relativeCli.StandardOutput)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PP07_LinkedParentSourcePathRoundTripsThroughRealIndexAndCli()
    {
        const string storedLinkedPath = "../Shared/Linked.cs";
        var standardValues = await _fixture.ReadAllTextColumnsAsync(
            _fixture.StandardDatabasePath,
            Token);
        var customValues = await _fixture.ReadAllTextColumnsAsync(
            _fixture.CustomDatabasePath,
            Token);
        Assert.Contains(storedLinkedPath, standardValues["documents.normalized_path"].OfType<string>());
        Assert.Contains(storedLinkedPath, customValues["documents.normalized_path"].OfType<string>());
        Assert.DoesNotContain(
            standardValues["documents.normalized_path"].OfType<string>(),
            path => Path.IsPathRooted(path));
        Assert.DoesNotContain(
            customValues["documents.normalized_path"].OfType<string>(),
            path => Path.IsPathRooted(path));

        var destinationParent = Path.Combine(
            Path.GetTempPath(),
            $"csindex-pp07-linked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destinationParent);
        try
        {
            var relocatedContainer = _fixture.CopyContainerTo(destinationParent);
            var relocatedWorkspace = Path.Combine(relocatedContainer, "workspace");
            var relocatedLinked = Path.Combine(relocatedContainer, "Shared", "Linked.cs");
            var relocatedStandardDatabase = Path.Combine(
                relocatedWorkspace, ".csindex", "index.sqlite");
            var relocatedCustomDatabase = Path.Combine(
                relocatedContainer, "indexes", "custom", "index.sqlite");

            Assert.Equal(
                _fixture.LinkedSourcePath,
                _fixture.CreatePathResolver().ToAbsolutePath(storedLinkedPath));
            Assert.Equal(
                _fixture.LinkedSourcePath,
                _fixture.CreatePathResolver(_fixture.CustomDatabasePath).ToAbsolutePath(storedLinkedPath));
            Assert.Equal(
                relocatedLinked,
                _fixture.CreatePathResolver(relocatedStandardDatabase).ToAbsolutePath(storedLinkedPath));
            Assert.Equal(
                relocatedLinked,
                _fixture.CreatePathResolver(relocatedCustomDatabase).ToAbsolutePath(storedLinkedPath));
            Assert.Equal(
                relocatedLinked,
                _fixture.CreatePathResolver(baseDirectory: relocatedWorkspace).ToAbsolutePath(storedLinkedPath));

            string[][] queryFamilies =
            [
                ["symbol", "find", "Acceptance.Shared.Linked::LinkedMarker()", "--output-format", "json"],
                ["symbol", "list", "--file-literal", storedLinkedPath, "--output-format", "json"],
                ["source", "search", "--file-literal", storedLinkedPath, "--include-literal", "LinkedMarker", "--output-format", "json"],
                ["definition", "Acceptance.Shared.Linked::LinkedMarker()", "--output-format", "json"],
            ];
            foreach (var arguments in queryFamilies)
            {
                var absolute = await RunPathVariantAsync(
                    arguments, baseDirectory: null, "absolute");
                var relative = await RunPathVariantAsync(
                    arguments, baseDirectory: null, "relative");
                AssertPathConsumerSuccess("linked-query", absolute, relative);
                Assert.NotEmpty(AllLocationPaths(absolute.StandardOutput));
                Assert.All(
                    AllLocationPaths(absolute.StandardOutput),
                    path => Assert.Equal(_fixture.LinkedSourcePath, path, ignoreCase: true));
                Assert.All(
                    AllLocationPaths(relative.StandardOutput),
                    path => Assert.Equal(storedLinkedPath, path));
                Assert.Equal(
                    NormalizeLocationPaths(absolute.StandardOutput),
                    NormalizeLocationPaths(relative.StandardOutput));
            }

            var relocatedStandard = await RunPathVariantAsync(
                ["definition", "Acceptance.Shared.Linked::LinkedMarker()", "--output-format", "json"],
                baseDirectory: null,
                pathStyle: "absolute",
                databasePath: relocatedStandardDatabase);
            var relocatedCustom = await RunPathVariantAsync(
                ["definition", "Acceptance.Shared.Linked::LinkedMarker()", "--output-format", "json"],
                baseDirectory: null,
                pathStyle: "absolute",
                databasePath: relocatedCustomDatabase);
            var overridden = await RunPathVariantAsync(
                ["definition", "Acceptance.Shared.Linked::LinkedMarker()", "--output-format", "json"],
                relocatedWorkspace,
                "absolute");
            AssertPathConsumerSuccess(
                "linked-layouts",
                relocatedStandard,
                relocatedCustom,
                overridden);
            Assert.All(
                new[] { relocatedStandard, relocatedCustom, overridden },
                result => Assert.All(
                    AllLocationPaths(result.StandardOutput),
                    path => Assert.Equal(relocatedLinked, path, ignoreCase: true)));
        }
        finally
        {
            Directory.Delete(destinationParent, recursive: true);
        }
    }

    [Fact]
    public async Task PP08_CrossDriveAndUncShareFailuresOccurBeforeDatabaseMutation()
    {
        var attemptDirectory = Path.Combine(
            _fixture.ContainerPath,
            $"pp08-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(attemptDirectory);
        var databasePath = Path.Combine(attemptDirectory, "sentinel.sqlite");
        await CSharpSymbolPathAcceptanceFixture.CreateLegacyDatabaseAsync(
            databasePath,
            version: 4,
            Token);
        var beforeBytes = await _fixture.ReadDatabaseBytesAsync(databasePath, Token);
        var beforeSnapshot = await _fixture.ReadDatabaseSnapshotAsync(databasePath, Token);
        var beforeFiles = DirectoryFileNames(attemptDirectory);

        var root = Path.GetPathRoot(Path.GetFullPath(databasePath))!;
        Assert.True(root.Length >= 3 && root[1] == ':', root);
        var currentDrive = char.ToUpperInvariant(root[0]);
        var differentDrive = currentDrive == 'Z' ? 'Y' : 'Z';
        var databaseTail = Path.GetFullPath(databasePath)[root.Length..];
        var directoryTail = Path.GetFullPath(attemptDirectory)[root.Length..];
        var deviceDatabasePath = $@"\\?\{char.ToLowerInvariant(currentDrive)}:\{databaseTail}";
        var deviceDirectoryPath = $@"\\.\{currentDrive}:\{directoryTail}";

        var acceptedDriveAliases = IndexPathResolver.CreateForIndex(
            deviceDatabasePath,
            deviceDirectoryPath);
        Assert.Equal(".", acceptedDriveAliases.IndexRootAnchor);
        var acceptedUncAliases = IndexPathResolver.CreateForIndex(
            @"\\?\UNC\Server\Share\Indexes\index.sqlite",
            @"\\.\UNC\server\SHARE\Work");
        Assert.Equal("../Work", acceptedUncAliases.IndexRootAnchor);

        string[] syntheticInputRoots =
        [
            $@"{differentDrive}:\Synthetic\Work",
            @"\\Server-One\Share-One\Synthetic\Work",
            @"\\.\PIPE\csindex\Synthetic\Work",
        ];
        var observations = new List<(string Input, string ExpectedError, CSharpSymbolPathCliResult Result)>();
        foreach (var syntheticInputRoot in syntheticInputRoots)
        {
            var expected = Assert.Throws<InputResolutionException>(() =>
                IndexPathResolver.CreateForIndex(databasePath, syntheticInputRoot));
            Assert.True(
                expected.Message.Contains("same-volume/share", StringComparison.OrdinalIgnoreCase) ||
                expected.Message.Contains("unsupported Windows device namespace", StringComparison.OrdinalIgnoreCase),
                expected.Message);

            var resolvedInput = new ResolvedInput(
                InputMode.Directory,
                syntheticInputRoot,
                syntheticInputRoot,
                [syntheticInputRoot]);
            var indexFactoryCount = 0;
            var dependencies = new ProgramDependencies(
                (_, _) => throw new InvalidOperationException("Output factory must not run during index preflight."),
                (_, _) => throw new InvalidOperationException("Query factory must not run during index preflight."),
                () => AnalysisCoordinator.CreateForTesting(_ => resolvedInput),
                path =>
                {
                    indexFactoryCount++;
                    return new SqliteIndex(path);
                });
            var result = await _fixture.RunCliAsync(
                dependencies,
                "index", syntheticInputRoot,
                "--mode", "directory",
                "--db", databasePath);

            Assert.Equal(0, indexFactoryCount);
            Assert.Equal(beforeBytes, await _fixture.ReadDatabaseBytesAsync(databasePath, Token));
            Assert.Equal(beforeSnapshot, await _fixture.ReadDatabaseSnapshotAsync(databasePath, Token));
            Assert.Equal(beforeFiles, DirectoryFileNames(attemptDirectory));
            observations.Add((syntheticInputRoot, expected.Message, result));
        }

        Assert.All(observations, observation =>
        {
            Assert.Equal(ExitCodes.AnalysisFailure, observation.Result.ExitCode);
            Assert.Equal(
                $"Input error: {observation.ExpectedError}{Environment.NewLine}",
                observation.Result.StandardError);
            Assert.Contains(observation.Input, observation.Result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                observation.Result.StandardError.Contains("same-volume/share", StringComparison.OrdinalIgnoreCase) ||
                observation.Result.StandardError.Contains("unsupported Windows device namespace", StringComparison.OrdinalIgnoreCase),
                observation.Result.StandardError);
        });
    }

    [Fact]
    public async Task PP09_NoPersistedTextFieldContainsAnAbsoluteMachinePath()
    {
        string[] requiredTextColumns =
        [
            "index_runs.input_root",
            "index_runs.index_root_anchor",
            "projects.project_path",
            "documents.normalized_path",
            "symbols.stable_key",
            "symbols.path_segment_identity",
            "symbols.type_identity_path",
            "symbols.executable_identity_path",
            "symbols.return_type_key",
            "symbols.conversion_type_key",
            "method_parameters.type_key",
            "method_parameters.type_display",
            "symbol_declarations.declaration_key",
            "symbol_declarations.normalized_source",
            "calls.unresolved_name",
            "calls.receiver_type_key",
        ];

        foreach (var databasePath in new[]
                 {
                     _fixture.StandardDatabasePath,
                     _fixture.CustomDatabasePath,
                 })
        {
            var columns = await _fixture.ReadAllTextColumnsAsync(databasePath, Token);
            Assert.All(requiredTextColumns, column => Assert.True(
                columns.ContainsKey(column),
                $"Expected TEXT column was not inspected: {column}"));
            foreach (var (column, values) in columns)
            {
                foreach (var value in values.OfType<string>())
                {
                    Assert.DoesNotContain(
                        _fixture.ContainerPath,
                        value,
                        StringComparison.OrdinalIgnoreCase);
                    Assert.False(
                        ContainsAbsoluteMachinePath(value),
                        $"Persisted absolute/device path in {column}: {value}");
                }
            }

            var documentPaths = columns["documents.normalized_path"].OfType<string>().ToArray();
            Assert.Contains("../Shared/Linked.cs", documentPaths);
            Assert.All(documentPaths, path => Assert.False(Path.IsPathRooted(path), path));

            var databaseBytes = await _fixture.ReadDatabaseBytesAsync(databasePath, Token);
            foreach (var machinePath in new[]
                     {
                         _fixture.ContainerPath,
                         _fixture.ContainerPath.Replace('\\', '/'),
                         _fixture.WorkspacePath,
                         _fixture.WorkspacePath.Replace('\\', '/'),
                     })
            {
                Assert.False(
                    ContainsBytes(databaseBytes, System.Text.Encoding.UTF8.GetBytes(machinePath)),
                    $"Database bytes contain UTF-8 machine path: {machinePath}");
                Assert.False(
                    ContainsBytes(databaseBytes, System.Text.Encoding.Unicode.GetBytes(machinePath)),
                    $"Database bytes contain UTF-16 machine path: {machinePath}");
            }

            var signatureSymbols = OrderByIndependentCanonicalFields(
                    await _fixture.GetExecutableSymbolsAsync(cancellationToken: Token))
                .Where(symbol => symbol.NamespaceName.Equals("Acceptance.Signatures", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(signatureSymbols);
            var signaturePresentation = PresentationCases[0];
            var signatures = await RunPresentedCliAsync(
                [
                    "symbol", "list",
                    "--namespace-literal", "Acceptance.Signatures",
                    "--namespace-case", signaturePresentation.NamespaceCase,
                ],
                signaturePresentation,
                _fixture.StandardDatabasePath,
                baseDirectory: null);
            AssertSuccessful("csharp-signatures-order", signatures);
            Assert.Equal(
                signatureSymbols.Select(symbol => symbol.StableKey),
                ExtractSymbolStableKeys(signatures.StandardOutput, "symbols"));
        }
    }

    [Theory]
    [MemberData(nameof(SchemaFourAttempts))]
    public async Task PP10_SchemaFourIndexAndQueryAttemptsPreserveTheOldFile(
        string rowId,
        string caseName,
        string[] command)
    {
        Assert.Equal("PP10", rowId);
        var attemptDirectory = Path.Combine(
            _fixture.ContainerPath,
            $"pp10-{caseName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(attemptDirectory);
        var templatePath = Path.Combine(attemptDirectory, "schema-four-template.sqlite");
        var databasePath = Path.Combine(attemptDirectory, "schema-four-attempt.sqlite");
        var outputPath = Path.Combine(attemptDirectory, "result.txt");
        await CSharpSymbolPathAcceptanceFixture.CreateLegacyDatabaseAsync(
            templatePath,
            version: 4,
            Token);
        File.Copy(templatePath, databasePath);
        await File.WriteAllTextAsync(outputPath, "output-sentinel", Token);
        var beforeBytes = await _fixture.ReadDatabaseBytesAsync(databasePath, Token);
        var beforeSnapshot = await _fixture.ReadDatabaseSnapshotAsync(databasePath, Token);
        var beforeFiles = DirectoryFileNames(attemptDirectory);

        var arguments = BuildSchemaFourArguments(command, databasePath, outputPath);
        var result = await _fixture.RunCliAsync(arguments);

        Assert.Equal(ExitCodes.DatabaseFailure, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(beforeBytes, await _fixture.ReadDatabaseBytesAsync(databasePath, Token));
        Assert.Equal(beforeSnapshot, await _fixture.ReadDatabaseSnapshotAsync(databasePath, Token));
        Assert.Equal("output-sentinel", await File.ReadAllTextAsync(outputPath, Token));
        Assert.Equal(beforeFiles, DirectoryFileNames(attemptDirectory));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(attemptDirectory),
            path => path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("-journal", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(SchemaFourGuidanceAttempts))]
    public async Task PP11_IncompatibleSchemaErrorExplainsExplicitRecreation(
        string rowId,
        string caseName,
        string[] command)
    {
        Assert.Equal("PP11", rowId);
        var attemptDirectory = Path.Combine(
            _fixture.ContainerPath,
            $"pp11-{caseName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(attemptDirectory);
        var databasePath = Path.Combine(attemptDirectory, "schema-four.sqlite");
        var outputPath = Path.Combine(attemptDirectory, "result.txt");
        await CSharpSymbolPathAcceptanceFixture.CreateLegacyDatabaseAsync(
            databasePath,
            version: 4,
            Token);
        await File.WriteAllTextAsync(outputPath, "output-sentinel", Token);
        var beforeSnapshot = await _fixture.ReadDatabaseSnapshotAsync(databasePath, Token);

        var result = await _fixture.RunCliAsync(
            BuildSchemaFourArguments(command, databasePath, outputPath));

        const string expectedGuidance =
            "Database error: Unsupported database schema version 4; this build supports version 5. " +
            "The database was not modified. Delete or rename the old database or choose a new --db path, " +
            "then run csindex index explicitly.";
        Assert.Equal(ExitCodes.DatabaseFailure, result.ExitCode);
        var databaseErrors = result.StandardError
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("Database error: ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal([expectedGuidance], databaseErrors);
        Assert.DoesNotContain("automatic", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("migrat", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("automatically rebuild", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeSnapshot, await _fixture.ReadDatabaseSnapshotAsync(databasePath, Token));
        Assert.Equal("output-sentinel", await File.ReadAllTextAsync(outputPath, Token));
    }

    [Fact]
    public async Task OH04_NormalAndVerboseHelpShareTheExactCommandOptionMatrix()
    {
        foreach (var scope in ExpectedHelpScopes)
        {
            var normal = await _fixture.RunCliAsync([.. scope.Prefix, "--help"]);
            var verbose = await _fixture.RunCliAsync([.. scope.Prefix, "--help", "--verbose"]);
            var expectedOptions = scope.Options.OrderBy(option => option, StringComparer.Ordinal).ToArray();

            Assert.Equal(ExitCodes.Success, normal.ExitCode);
            Assert.Equal(ExitCodes.Success, verbose.ExitCode);
            Assert.Equal(string.Empty, normal.StandardError);
            Assert.Equal(string.Empty, verbose.StandardError);
            Assert.Equal(expectedOptions, ExtractAcceptedOptions(normal.StandardOutput));
            Assert.Equal(expectedOptions, ExtractAcceptedOptions(verbose.StandardOutput));
            Assert.DoesNotContain("Canonical symbol path examples:", normal.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Canonical symbol path examples:", verbose.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Command option scopes:", verbose.StandardOutput, StringComparison.Ordinal);
            Assert.Contains(
                "Game.Core.Player.Inventory::Load(int).Validate()",
                verbose.StandardOutput,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OH05_BothVerboseHelpSpellingsAreByteIdenticalEverywhere()
    {
        foreach (var scope in ExpectedHelpScopes)
        {
            var canonical = await _fixture.RunCliAsync([.. scope.Prefix, "--help", "--verbose"]);
            var shorthand = await _fixture.RunCliAsync([.. scope.Prefix, "--help-verbose"]);
            var normal = await _fixture.RunCliAsync([.. scope.Prefix, "--help"]);

            Assert.Equal(ExitCodes.Success, canonical.ExitCode);
            Assert.Equal(ExitCodes.Success, shorthand.ExitCode);
            Assert.Equal(ExitCodes.Success, normal.ExitCode);
            Assert.Equal(
                Encoding.UTF8.GetBytes(canonical.StandardOutput),
                Encoding.UTF8.GetBytes(shorthand.StandardOutput));
            Assert.Equal(
                Encoding.UTF8.GetBytes(canonical.StandardError),
                Encoding.UTF8.GetBytes(shorthand.StandardError));
            Assert.Contains("Canonical symbol path examples:", canonical.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("Canonical symbol path examples:", normal.StandardOutput, StringComparison.Ordinal);
            Assert.True(normal.StandardOutput.Length < canonical.StandardOutput.Length, scope.Name);
        }
    }

    [Fact]
    public async Task OH06_TerminalHelpAndMissingRequiredInputOpenNoDependencies()
    {
        foreach (var scope in ExpectedHelpScopes)
        {
            var directory = Path.Combine(_fixture.ContainerPath, $"oh06-help-{Guid.NewGuid():N}");
            var databasePath = Path.Combine(directory, "missing.sqlite");
            var outputPath = Path.Combine(directory, "result.txt");
            var invocations = new List<string>();
            var arguments = scope.Prefix
                .AddRange(OperationalOptions(scope.Name, databasePath, outputPath))
                .Add("--help")
                .ToArray();

            var result = await _fixture.RunCliAsync(
                CreateThrowingDependencies(invocations),
                arguments);

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.NotEmpty(result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);
            Assert.Empty(invocations);
            Assert.False(File.Exists(outputPath), scope.Name);
            Assert.False(Directory.Exists(directory), scope.Name);
        }

        foreach (var missing in MissingRequiredInputCases(_fixture.ContainerPath))
        {
            var invocations = new List<string>();
            var result = await _fixture.RunCliAsync(
                CreateThrowingDependencies(invocations),
                missing.Arguments);

            Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.NotEmpty(result.StandardError);
            Assert.Empty(invocations);
            Assert.False(File.Exists(missing.OutputPath), missing.Name);
            Assert.False(Directory.Exists(Path.GetDirectoryName(missing.OutputPath)!), missing.Name);
        }

        foreach (var valid in EmptySelectorIsValidCases(_fixture.ContainerPath))
        {
            var invocations = new List<string>();
            var result = await _fixture.RunCliAsync(
                CreateThrowingDependencies(invocations),
                valid.Arguments);

            Assert.Equal(ExitCodes.AnalysisFailure, result.ExitCode);
            Assert.Equal(["query"], invocations);
            Assert.Empty(result.StandardOutput);
            Assert.False(File.Exists(valid.OutputPath), valid.Name);
            Assert.False(Directory.Exists(Path.GetDirectoryName(valid.OutputPath)!), valid.Name);
        }
    }

    [Fact]
    public async Task OH07_EveryRemovedGrammarOptionAndFallbackIsExplicitlyRejected()
    {
        var rejectionDirectory = Path.Combine(_fixture.ContainerPath, $"oh07-rejection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rejectionDirectory);
        var outputPath = Path.Combine(rejectionDirectory, "result.txt");
        var beforeBytes = await _fixture.ReadDatabaseBytesAsync(cancellationToken: Token);
        var beforeSnapshot = await _fixture.ReadDatabaseSnapshotAsync(cancellationToken: Token);
        foreach (var rejected in new[]
                 {
                     new[] { "Old.Type::Method()::Local()" },
                     new[] { "Old.Type::Method()::<lambda#1>" },
                     new[] { "Acceptance.Graph.GraphHost::TreeRoot()", "--regex", "obsolete" },
                     new[] { "Acceptance.Graph.GraphHost::TreeRoot()", "--ignore-case", "true" },
                 })
        {
            await File.WriteAllTextAsync(outputPath, "output-sentinel", Token);
            var result = await _fixture.RunStandardCliAsync(
                ["symbol", "find", .. rejected, "--output-file", outputPath]);

            Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.NotEmpty(result.StandardError);
            Assert.Equal("output-sentinel", await File.ReadAllTextAsync(outputPath, Token));
            Assert.Equal(beforeBytes, await _fixture.ReadDatabaseBytesAsync(cancellationToken: Token));
            Assert.Equal(beforeSnapshot, await _fixture.ReadDatabaseSnapshotAsync(cancellationToken: Token));
            Assert.Empty(FindOutputTemporaryFiles(rejectionDirectory));
        }

        var schemaDirectory = Path.Combine(rejectionDirectory, "schema-four");
        var schemaDatabasePath = Path.Combine(schemaDirectory, "schema-four.sqlite");
        var schemaOutputPath = Path.Combine(schemaDirectory, "schema-result.txt");
        await CSharpSymbolPathAcceptanceFixture.CreateLegacyDatabaseAsync(schemaDatabasePath, version: 4, Token);
        await File.WriteAllTextAsync(schemaOutputPath, "schema-output-sentinel", Token);
        var schemaBytes = await _fixture.ReadDatabaseBytesAsync(schemaDatabasePath, Token);
        var schemaSnapshot = await _fixture.ReadDatabaseSnapshotAsync(schemaDatabasePath, Token);
        var schemaResult = await _fixture.RunCliAsync(
            "conditions", "--db", schemaDatabasePath, "--output-file", schemaOutputPath);

        Assert.Equal(ExitCodes.DatabaseFailure, schemaResult.ExitCode);
        Assert.Empty(schemaResult.StandardOutput);
        Assert.Contains("Unsupported database schema version 4", schemaResult.StandardError, StringComparison.Ordinal);
        Assert.Equal(schemaBytes, await _fixture.ReadDatabaseBytesAsync(schemaDatabasePath, Token));
        Assert.Equal(schemaSnapshot, await _fixture.ReadDatabaseSnapshotAsync(schemaDatabasePath, Token));
        Assert.Equal("schema-output-sentinel", await File.ReadAllTextAsync(schemaOutputPath, Token));
        Assert.Empty(FindOutputTemporaryFiles(schemaDirectory));

        var fallbackDatabasePath = Path.Combine(rejectionDirectory, "absolute-fallback.sqlite");
        await CSharpSymbolPathAcceptanceFixture.CreateLegacyDatabaseAsync(fallbackDatabasePath, version: 4, Token);
        var fallbackBytes = await _fixture.ReadDatabaseBytesAsync(fallbackDatabasePath, Token);
        var fallbackSnapshot = await _fixture.ReadDatabaseSnapshotAsync(fallbackDatabasePath, Token);
        var root = Path.GetPathRoot(Path.GetFullPath(fallbackDatabasePath))!;
        var currentDrive = char.ToUpperInvariant(root[0]);
        var differentDrive = currentDrive == 'Z' ? 'Y' : 'Z';
        var attemptedAbsoluteFallback = $@"{differentDrive}:\csindex\absolute-fallback";
        var resolvedInput = new ResolvedInput(
            InputMode.Directory,
            attemptedAbsoluteFallback,
            attemptedAbsoluteFallback,
            [attemptedAbsoluteFallback]);
        var sqliteFactoryCalls = 0;
        var dependencies = new ProgramDependencies(
            (_, _) => throw new InvalidOperationException("Output must not be created for index preflight rejection."),
            (_, _) => throw new InvalidOperationException("Query must not be created for index preflight rejection."),
            () => AnalysisCoordinator.CreateForTesting(_ => resolvedInput),
            path =>
            {
                sqliteFactoryCalls++;
                return new SqliteIndex(path);
            });
        var fallbackResult = await _fixture.RunCliAsync(
            dependencies,
            "index", attemptedAbsoluteFallback, "--mode", "directory", "--db", fallbackDatabasePath);

        Assert.Equal(ExitCodes.AnalysisFailure, fallbackResult.ExitCode);
        Assert.Empty(fallbackResult.StandardOutput);
        Assert.Contains("same-volume/share", fallbackResult.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, sqliteFactoryCalls);
        Assert.Equal(fallbackBytes, await _fixture.ReadDatabaseBytesAsync(fallbackDatabasePath, Token));
        Assert.Equal(fallbackSnapshot, await _fixture.ReadDatabaseSnapshotAsync(fallbackDatabasePath, Token));
        Assert.Empty(Directory.EnumerateFiles(rejectionDirectory, ".*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task OH08_CancellationAtFinalRecordFlushAndPreCommitPreservesSentinel()
    {
        var asyncPath = await _fixture.CreateQueryService().FindAsyncPathAsync(
            "Acceptance.Graph.GraphHost::AsyncTreeRoot()",
            maxNodes: 32,
            cancellationToken: Token);
        Assert.True(asyncPath.Found);
        Assert.True(asyncPath.Nodes.Count >= 3, "The graph cancellation corpus must be multi-depth.");

        OutputPayloadCase[] payloads =
        [
            new(
                "text",
                ["symbol", "find", "Acceptance.Graph.GraphHost::TreeRoot()"],
                FinalRecordWriteLines: 1),
            new(
                "json",
                [
                    "symbol", "find", "Acceptance.Graph.GraphHost::TreeRoot()",
                    "--output-format", "json",
                ],
                FinalRecordWriteLines: 1),
            new(
                "graph",
                [
                    "async", "tree", "Acceptance.Graph.GraphHost::AsyncTreeRoot()",
                    "--output-format", "tree", "--max-nodes", "32",
                ],
                FinalRecordWriteLines: asyncPath.Nodes.Count),
            new(
                "source",
                [
                    "source", "search",
                    "--namespace-literal", "Acceptance.Partials",
                    "--type-literal", "PartialHost",
                    "--method-literal", "PairedPartial()",
                    "--include-literal", "PARTIAL-",
                ],
                FinalRecordWriteLines: 2),
        ];

        foreach (var payload in payloads)
        {
            foreach (var point in new[]
                     {
                         OutputCancellationPoint.FinalRecord,
                         OutputCancellationPoint.Flush,
                         OutputCancellationPoint.PreCommit,
                     })
            {
                var directory = Path.Combine(
                    _fixture.ContainerPath,
                    $"oh08-{payload.Name}-{point}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(directory);
                var outputPath = Path.Combine(directory, "result.txt");
                var sentinel = Encoding.UTF8.GetBytes($"oh08-sentinel-{payload.Name}-{point}");
                await File.WriteAllBytesAsync(outputPath, sentinel, Token);
                using var cancellation = new CancellationTokenSource();
                CancellationPointTextWriter? writer = null;
                var dependencies = CreateRealQueryDependencies(
                    (requestedOutputPath, databasePath) => OutputDestination.Create(
                        requestedOutputPath,
                        databasePath,
                        stream => writer = new CancellationPointTextWriter(
                            stream,
                            cancellation,
                            point,
                            payload.FinalRecordWriteLines)));

                var result = await _fixture.RunCliAsync(
                    dependencies,
                    cancellation.Token,
                    [.. payload.Arguments, "--output-file", outputPath, "--db", _fixture.StandardDatabasePath]);

                Assert.NotNull(writer);
                Assert.True(
                    writer.CancellationRaised,
                    $"{payload.Name}/{point} did not reach its deterministic output boundary.");
                Assert.Equal(
                    payload.FinalRecordWriteLines,
                    writer.WriteLineCount);
                Assert.Equal(ExitCodes.AnalysisFailure, result.ExitCode);
                Assert.Equal(string.Empty, result.StandardOutput);
                Assert.Equal("Operation was cancelled.", ErrorLines(result.StandardError).Last());
                Assert.Equal(sentinel, await File.ReadAllBytesAsync(outputPath, Token));
                Assert.Empty(FindOutputTemporaryFiles(directory));
            }
        }
    }

    [Fact]
    public async Task OH09_FormattingRegexPathAndDatabaseFailuresCommitNoPartialPayload()
    {
        var writeDirectory = Path.Combine(_fixture.ContainerPath, $"oh09-write-{Guid.NewGuid():N}");
        Directory.CreateDirectory(writeDirectory);
        var writeOutputPath = Path.Combine(writeDirectory, "result.txt");
        var writeSentinel = Encoding.UTF8.GetBytes("oh09-write-sentinel");
        await File.WriteAllBytesAsync(writeOutputPath, writeSentinel, Token);
        FailAfterWriteAndDisposeTextWriter? writeWriter = null;
        var writeResult = await _fixture.RunCliAsync(
            CreateRealQueryDependencies(
                (requestedOutputPath, databasePath) => OutputDestination.Create(
                    requestedOutputPath,
                    databasePath,
                    stream => writeWriter = new FailAfterWriteAndDisposeTextWriter(stream))),
            "symbol", "find", "Acceptance.Graph.GraphHost::TreeRoot()",
            "--output-file", writeOutputPath, "--db", _fixture.StandardDatabasePath);

        Assert.NotNull(writeWriter);
        Assert.Equal(1, writeWriter.WriteLineCount);
        Assert.True(writeWriter.DisposeAttempted);
        Assert.Equal(ExitCodes.AnalysisFailure, writeResult.ExitCode);
        Assert.Contains("Output error: Could not write output file", writeResult.StandardError, StringComparison.Ordinal);
        Assert.Contains("OH09 primary write failure", writeResult.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("OH09 cleanup disposal failure", writeResult.StandardError, StringComparison.Ordinal);
        await AssertUncommittedFileOutputAsync(writeResult, writeOutputPath, writeSentinel, writeDirectory);

        var flushDirectory = Path.Combine(_fixture.ContainerPath, $"oh09-flush-{Guid.NewGuid():N}");
        Directory.CreateDirectory(flushDirectory);
        var flushOutputPath = Path.Combine(flushDirectory, "result.txt");
        var flushSentinel = Encoding.UTF8.GetBytes("oh09-flush-sentinel");
        await File.WriteAllBytesAsync(flushOutputPath, flushSentinel, Token);
        ThrowOnFlushTextWriter? flushWriter = null;
        var flushResult = await _fixture.RunCliAsync(
            CreateRealQueryDependencies(
                (requestedOutputPath, databasePath) => OutputDestination.Create(
                    requestedOutputPath,
                    databasePath,
                    stream => flushWriter = new ThrowOnFlushTextWriter(stream))),
            "symbol", "find", "Acceptance.Graph.GraphHost::TreeRoot()",
            "--output-file", flushOutputPath, "--db", _fixture.StandardDatabasePath);

        Assert.NotNull(flushWriter);
        Assert.Equal(1, flushWriter.WriteLineCount);
        Assert.True(flushWriter.FlushAttempted);
        Assert.Equal(ExitCodes.AnalysisFailure, flushResult.ExitCode);
        Assert.Contains("Output error: Could not commit output file", flushResult.StandardError, StringComparison.Ordinal);
        Assert.Contains("OH09 flush failure", flushResult.StandardError, StringComparison.Ordinal);
        await AssertUncommittedFileOutputAsync(flushResult, flushOutputPath, flushSentinel, flushDirectory);

        var replaceDirectory = Path.Combine(_fixture.ContainerPath, $"oh09-replace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(replaceDirectory);
        var replaceOutputPath = Path.Combine(replaceDirectory, "result.txt");
        var replaceSentinel = Encoding.UTF8.GetBytes("oh09-replace-sentinel");
        await File.WriteAllBytesAsync(replaceOutputPath, replaceSentinel, Token);
        CSharpSymbolPathCliResult replaceResult;
        using (var destinationLock = new FileStream(
                   replaceOutputPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            replaceResult = await _fixture.RunCliAsync(
                CreateRealQueryDependencies(OutputDestination.Create),
                "symbol", "find", "Acceptance.Graph.GraphHost::TreeRoot()",
                "--output-file", replaceOutputPath, "--db", _fixture.StandardDatabasePath);
        }

        Assert.Equal(ExitCodes.AnalysisFailure, replaceResult.ExitCode);
        Assert.Contains("Output error: Could not commit output file", replaceResult.StandardError, StringComparison.Ordinal);
        await AssertUncommittedFileOutputAsync(replaceResult, replaceOutputPath, replaceSentinel, replaceDirectory);

        var invalidRegexDirectory = Path.Combine(_fixture.ContainerPath, $"oh09-invalid-regex-{Guid.NewGuid():N}");
        Directory.CreateDirectory(invalidRegexDirectory);
        var invalidRegexOutputPath = Path.Combine(invalidRegexDirectory, "result.txt");
        var invalidRegexSentinel = Encoding.UTF8.GetBytes("oh09-invalid-regex-sentinel");
        await File.WriteAllBytesAsync(invalidRegexOutputPath, invalidRegexSentinel, Token);
        var invalidRegexOutputFactoryCalls = 0;
        var invalidRegexResult = await _fixture.RunCliAsync(
            CreateRealQueryDependencies(
                (requestedOutputPath, databasePath) =>
                {
                    invalidRegexOutputFactoryCalls++;
                    return OutputDestination.Create(requestedOutputPath, databasePath);
                }),
            "symbol", "find", "--method-regex", "[",
            "--output-format", "json", "--output-file", invalidRegexOutputPath,
            "--db", _fixture.StandardDatabasePath);

        Assert.Equal(ExitCodes.InvalidArguments, invalidRegexResult.ExitCode);
        Assert.Equal(0, invalidRegexOutputFactoryCalls);
        Assert.Contains("Invalid regular expression condition", invalidRegexResult.StandardError, StringComparison.Ordinal);
        await AssertUncommittedFileOutputAsync(
            invalidRegexResult,
            invalidRegexOutputPath,
            invalidRegexSentinel,
            invalidRegexDirectory);

        var stdoutInvalidRegexOutputFactoryCalls = 0;
        var stdoutInvalidRegexResult = await _fixture.RunCliAsync(
            CreateRealQueryDependencies(
                (requestedOutputPath, databasePath) =>
                {
                    stdoutInvalidRegexOutputFactoryCalls++;
                    return OutputDestination.Create(requestedOutputPath, databasePath);
                }),
            "symbol", "find", "--method-regex", "[",
            "--output-format", "json", "--db", _fixture.StandardDatabasePath);
        Assert.Equal(ExitCodes.InvalidArguments, stdoutInvalidRegexResult.ExitCode);
        Assert.Equal(0, stdoutInvalidRegexOutputFactoryCalls);
        Assert.Empty(stdoutInvalidRegexResult.StandardOutput);
        Assert.Contains(
            "Invalid regular expression condition",
            stdoutInvalidRegexResult.StandardError,
            StringComparison.Ordinal);

        var timeoutDirectory = Path.Combine(_fixture.ContainerPath, $"oh09-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(timeoutDirectory);
        var timeoutOutputPath = Path.Combine(timeoutDirectory, "result.txt");
        var timeoutSentinel = Encoding.UTF8.GetBytes("oh09-timeout-sentinel");
        await File.WriteAllBytesAsync(timeoutOutputPath, timeoutSentinel, Token);
        var timeoutOutputFactoryCalls = 0;
        var timeoutQueryFactoryCalls = 0;
        var timeoutDependencies = new ProgramDependencies(
            (requestedOutputPath, databasePath) =>
            {
                timeoutOutputFactoryCalls++;
                return OutputDestination.Create(requestedOutputPath, databasePath);
            },
            (_, _) =>
            {
                timeoutQueryFactoryCalls++;
                throw new RegexMatchTimeoutException("OH09 forced regex timeout at the query boundary.");
            },
            () => throw new InvalidOperationException("OH09 timeout must not open analysis."),
            _ => throw new InvalidOperationException("OH09 timeout must not open SQLite directly."));
        var timeoutResult = await _fixture.RunCliAsync(
            timeoutDependencies,
            "symbol", "find", "Acceptance.Graph.GraphHost::TreeRoot()",
            "--output-file", timeoutOutputPath, "--db", _fixture.StandardDatabasePath);

        Assert.Equal(ExitCodes.AnalysisFailure, timeoutResult.ExitCode);
        Assert.Equal(1, timeoutQueryFactoryCalls);
        Assert.Equal(0, timeoutOutputFactoryCalls);
        Assert.Contains("OH09 forced regex timeout", timeoutResult.StandardError, StringComparison.Ordinal);
        await AssertUncommittedFileOutputAsync(timeoutResult, timeoutOutputPath, timeoutSentinel, timeoutDirectory);

        var stdoutTimeoutResult = await _fixture.RunCliAsync(
            timeoutDependencies,
            "symbol", "find", "Acceptance.Graph.GraphHost::TreeRoot()",
            "--db", _fixture.StandardDatabasePath);
        Assert.Equal(ExitCodes.AnalysisFailure, stdoutTimeoutResult.ExitCode);
        Assert.Equal(2, timeoutQueryFactoryCalls);
        Assert.Equal(0, timeoutOutputFactoryCalls);
        Assert.Empty(stdoutTimeoutResult.StandardOutput);
        Assert.Contains("OH09 forced regex timeout", stdoutTimeoutResult.StandardError, StringComparison.Ordinal);

        var missingPathDirectory = Path.Combine(_fixture.ContainerPath, $"oh09-missing-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(missingPathDirectory);
        var missingPathOutputPath = Path.Combine(missingPathDirectory, "result.txt");
        var missingPathSentinel = Encoding.UTF8.GetBytes("oh09-missing-path-sentinel");
        await File.WriteAllBytesAsync(missingPathOutputPath, missingPathSentinel, Token);
        var missingPathOutputFactoryCalls = 0;
        var missingPathBase = Path.Combine(missingPathDirectory, "missing-reconstructed-root");
        var missingPathResult = await _fixture.RunCliAsync(
            CreateRealQueryDependencies(
                (requestedOutputPath, databasePath) =>
                {
                    missingPathOutputFactoryCalls++;
                    return OutputDestination.Create(requestedOutputPath, databasePath);
                }),
            "definition", "--at", "src/Corpus/Ambiguity.cs:1:1",
            "--base-dir", missingPathBase,
            "--output-file", missingPathOutputPath, "--db", _fixture.StandardDatabasePath);

        Assert.Equal(ExitCodes.AnalysisFailure, missingPathResult.ExitCode);
        Assert.Equal(0, missingPathOutputFactoryCalls);
        Assert.Contains("Fatal error:", missingPathResult.StandardError, StringComparison.Ordinal);
        await AssertUncommittedFileOutputAsync(
            missingPathResult,
            missingPathOutputPath,
            missingPathSentinel,
            missingPathDirectory);

        var invalidPathDirectory = Path.Combine(_fixture.ContainerPath, $"oh09-invalid-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(invalidPathDirectory);
        var invalidPathOutputPath = Path.Combine(invalidPathDirectory, "result.txt");
        var invalidPathSentinel = Encoding.UTF8.GetBytes("oh09-invalid-path-sentinel");
        await File.WriteAllBytesAsync(invalidPathOutputPath, invalidPathSentinel, Token);
        var invalidPathOutputFactoryCalls = 0;
        var invalidPathResult = await _fixture.RunCliAsync(
            CreateRealQueryDependencies(
                (requestedOutputPath, databasePath) =>
                {
                    invalidPathOutputFactoryCalls++;
                    return OutputDestination.Create(requestedOutputPath, databasePath);
                }),
            "definition", "--at", "src/Corpus/Ambiguity.cs:1:1",
            "--base-dir", @"\\?\oh09-invalid-device",
            "--output-file", invalidPathOutputPath, "--db", _fixture.StandardDatabasePath);

        Assert.Equal(ExitCodes.AnalysisFailure, invalidPathResult.ExitCode);
        Assert.Equal(0, invalidPathOutputFactoryCalls);
        Assert.Contains("Input error: Unsupported Windows device namespace", invalidPathResult.StandardError, StringComparison.Ordinal);
        await AssertUncommittedFileOutputAsync(
            invalidPathResult,
            invalidPathOutputPath,
            invalidPathSentinel,
            invalidPathDirectory);

        var schemaDirectory = Path.Combine(_fixture.ContainerPath, $"oh09-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(schemaDirectory);
        var schemaDatabasePath = Path.Combine(schemaDirectory, "schema-four.sqlite");
        var schemaOutputPath = Path.Combine(schemaDirectory, "result.txt");
        await CSharpSymbolPathAcceptanceFixture.CreateLegacyDatabaseAsync(schemaDatabasePath, version: 4, Token);
        var schemaBytes = await File.ReadAllBytesAsync(schemaDatabasePath, Token);
        var schemaSentinel = Encoding.UTF8.GetBytes("oh09-schema-sentinel");
        await File.WriteAllBytesAsync(schemaOutputPath, schemaSentinel, Token);
        var schemaResult = await _fixture.RunCliAsync(
            "conditions", "--db", schemaDatabasePath, "--output-file", schemaOutputPath);

        Assert.Equal(ExitCodes.DatabaseFailure, schemaResult.ExitCode);
        Assert.Contains("Unsupported database schema version 4", schemaResult.StandardError, StringComparison.Ordinal);
        Assert.Equal(schemaBytes, await File.ReadAllBytesAsync(schemaDatabasePath, Token));
        await AssertUncommittedFileOutputAsync(schemaResult, schemaOutputPath, schemaSentinel, schemaDirectory);

        var stdoutSchemaResult = await _fixture.RunCliAsync("conditions", "--db", schemaDatabasePath);
        Assert.Equal(ExitCodes.DatabaseFailure, stdoutSchemaResult.ExitCode);
        Assert.Empty(stdoutSchemaResult.StandardOutput);
        Assert.Contains(
            "Unsupported database schema version 4",
            stdoutSchemaResult.StandardError,
            StringComparison.Ordinal);
        Assert.Equal(schemaBytes, await File.ReadAllBytesAsync(schemaDatabasePath, Token));
    }

    [Fact]
    public async Task OH01_CanonicalLogicalOrderIsInvariantAcrossAllPresentationChoices()
    {
        var expectedLogicalSymbols = OrderByIndependentCanonicalFields(
                await _fixture.GetExecutableSymbolsAsync(cancellationToken: Token))
            .Where(symbol => symbol.NamespaceName.Equals("Acceptance.Graph", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(expectedLogicalSymbols);
        var expectedLogicalKeys = expectedLogicalSymbols.Select(symbol => symbol.StableKey).ToArray();

        var displayOrderProbeSymbols = OrderByIndependentCanonicalFields(
                await _fixture.GetExecutableSymbolsAsync(cancellationToken: Token))
            .Where(symbol => symbol.NamespaceName.Equals("Acceptance.Signatures", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(displayOrderProbeSymbols);
        var expectedDisplayOrderProbeKeys = displayOrderProbeSymbols
            .Select(symbol => symbol.StableKey)
            .ToArray();
        var forbiddenDisplayOrderProbeKeys = displayOrderProbeSymbols
            .OrderBy(symbol => SemanticPath(symbol).NamespacePath, StringComparer.Ordinal)
            .ThenBy(symbol => SemanticPath(symbol).TypeDisplayPath, StringComparer.Ordinal)
            .ThenBy(symbol => SemanticPath(symbol).ExecutableDisplayPath, StringComparer.Ordinal)
            .ThenBy(symbol => string.IsNullOrEmpty(symbol.PreferredDocumentPath) ? 1 : 0)
            .ThenBy(symbol => symbol.PreferredDocumentPath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.PreferredSourceStart is null ? 1 : 0)
            .ThenBy(symbol => symbol.PreferredSourceStart ?? int.MaxValue)
            .ThenBy(symbol => symbol.StableKey, StringComparer.Ordinal)
            .Select(symbol => symbol.StableKey)
            .ToArray();
        Assert.False(
            expectedDisplayOrderProbeKeys.SequenceEqual(
                forbiddenDisplayOrderProbeKeys,
                StringComparer.Ordinal),
            "The OH01 indexed corpus must distinguish semantic identity order from formatted display order.");

        var partial = await _fixture.GetSymbolAsync(
            "Acceptance.Partials.PartialHost::PairedPartial()",
            cancellationToken: Token);
        SemanticDeclaration[] expectedDeclarationSequence =
        [
            new(partial.StableKey, "partial-definition"),
            new(partial.StableKey, "partial-implementation"),
        ];

        var relocationParent = Path.Combine(
            Path.GetTempPath(),
            $"csindex-oh01-presentation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(relocationParent);
        try
        {
            var relocatedContainer = _fixture.CopyContainerTo(relocationParent);
            var relocatedDatabasePath = Path.Combine(
                relocatedContainer,
                "workspace",
                ".csindex",
                "index.sqlite");
            SemanticEdge[]? expectedEdgeSequence = null;

            foreach (var presentation in PresentationCases)
            {
                var databasePath = presentation.UseAlternateBase
                    ? relocatedDatabasePath
                    : _fixture.StandardDatabasePath;
                var baseDirectory = presentation.UseAlternateBase
                    ? _fixture.WorkspacePath
                    : null;
                var logical = await RunPresentedCliAsync(
                    [
                        "symbol", "list",
                        "--namespace-literal", "Acceptance.Graph",
                        "--namespace-case", presentation.NamespaceCase,
                    ],
                    presentation,
                    databasePath,
                    baseDirectory);
                AssertSuccessful(presentation.Name, logical);

                if (presentation.OutputFormat.Equals("json", StringComparison.Ordinal))
                {
                    Assert.Equal(expectedLogicalKeys, ExtractSymbolStableKeys(logical.StandardOutput, "symbols"));
                }
                else
                {
                    AssertDisplaysAppearInOrder(
                        logical.StandardOutput,
                        expectedLogicalSymbols.Select(symbol => _fixture.FormatPath(symbol, ToSymbolPathOptions(presentation))));
                }

                var displayOrderProbe = await RunPresentedCliAsync(
                    [
                        "symbol", "list",
                        "--namespace-literal", "Acceptance.Signatures",
                        "--namespace-case", presentation.NamespaceCase,
                    ],
                    presentation,
                    databasePath,
                    baseDirectory);
                AssertSuccessful($"{presentation.Name}-display-order-probe", displayOrderProbe);
                if (presentation.OutputFormat.Equals("json", StringComparison.Ordinal))
                {
                    Assert.Equal(
                        expectedDisplayOrderProbeKeys,
                        ExtractSymbolStableKeys(displayOrderProbe.StandardOutput, "symbols"));
                }
                else
                {
                    AssertDisplaysAppearInOrder(
                        displayOrderProbe.StandardOutput,
                        displayOrderProbeSymbols.Select(
                            symbol => _fixture.FormatPath(symbol, ToSymbolPathOptions(presentation))));
                }

                var definition = await RunPresentedCliAsync(
                    [
                        "definition", "Acceptance.Partials.PartialHost::PairedPartial()",
                        "--namespace-case", presentation.NamespaceCase,
                    ],
                    presentation,
                    databasePath,
                    baseDirectory);
                AssertSuccessful($"{presentation.Name}-definition", definition);
                if (presentation.OutputFormat.Equals("json", StringComparison.Ordinal))
                {
                    Assert.Equal(expectedDeclarationSequence, ExtractDefinitionSequence(definition.StandardOutput, "definitions"));
                }
                else
                {
                    Assert.Equal(
                        expectedDeclarationSequence.Select(row => row.Role),
                        ExtractTableDeclarationRows(definition.StandardOutput).Select(row => row.Role));
                }

                if (!presentation.OutputFormat.Equals("json", StringComparison.Ordinal))
                {
                    continue;
                }

                var callerTree = await RunPresentedCliAsync(
                    [
                        "callers", "tree", "Acceptance.Graph.GraphHost::TreeRoot()",
                        "--depth", "2", "--max-nodes", "32",
                    ],
                    presentation,
                    databasePath,
                    baseDirectory);
                AssertSuccessful($"{presentation.Name}-caller-tree", callerTree);
                var currentEdges = ExtractCallerTreeEdgeSequence(callerTree.StandardOutput);
                Assert.NotEmpty(currentEdges);
                expectedEdgeSequence ??= currentEdges.ToArray();
                Assert.Equal(expectedEdgeSequence, currentEdges);
            }
        }
        finally
        {
            Directory.Delete(relocationParent, recursive: true);
        }
    }

    [Fact]
    public async Task OH02_TreeParentsPrecedeCanonicallyOrderedChildren()
    {
        var query = _fixture.CreateQueryService();
        var callerTree = await query.FindCallerTreeAsync(
            "Acceptance.Graph.GraphHost::TreeRoot()",
            depth: 2,
            maxNodes: 32,
            cancellationToken: Token);
        var expectedCallerNodes = BuildIndependentCallerTreeOrder(callerTree);
        Assert.True(expectedCallerNodes.Count >= 5, "The caller corpus must include sibling and depth-two nodes.");

        PresentationCase[] callerPresentations =
        [
            new("caller-json", SymbolPathStyle.CSharp, false, PathDisplayStyle.Absolute, false, "strict", "json"),
            new("caller-tree", SymbolPathStyle.Explicit, true, PathDisplayStyle.Relative, false, "ignore", "tree"),
            new("caller-mermaid", SymbolPathStyle.CSharp, true, PathDisplayStyle.Absolute, false, "strict", "mermaid"),
        ];
        foreach (var presentation in callerPresentations)
        {
            var result = await RunPresentedCliAsync(
                [
                    "callers", "tree", "Acceptance.Graph.GraphHost::TreeRoot()",
                    "--depth", "2", "--max-nodes", "32",
                ],
                presentation,
                _fixture.StandardDatabasePath,
                baseDirectory: null);
            AssertSuccessful(presentation.Name, result);
            if (presentation.OutputFormat.Equals("json", StringComparison.Ordinal))
            {
                AssertCallerTreeJsonUsesIndependentParentFirstOrder(result.StandardOutput, callerTree);
            }
            else
            {
                AssertDisplaysAppearInOrder(
                    result.StandardOutput,
                    expectedCallerNodes.Select(symbol => _fixture.FormatPath(symbol, ToSymbolPathOptions(presentation))));
            }
        }

        var asyncPath = await query.FindAsyncPathAsync(
            "Acceptance.Graph.GraphHost::AsyncTreeRoot()",
            maxNodes: 32,
            cancellationToken: Token);
        Assert.True(
            asyncPath.Found,
            $"The async corpus must reach a declared async callable. rootDepth={asyncPath.Root.AsyncInvolvementDepth}; " +
            $"rootNext={asyncPath.Root.AsyncNextSymbolId}; rootRole={asyncPath.Root.AsyncRole}; nodes={asyncPath.Nodes.Count}.");
        Assert.True(asyncPath.Nodes.Count >= 3, "The async corpus must include a multi-depth path.");
        var expectedAsyncKeys = asyncPath.Nodes.Select(symbol => symbol.StableKey).ToArray();
        PresentationCase[] asyncPresentations =
        [
            new("async-json", SymbolPathStyle.CSharp, false, PathDisplayStyle.Absolute, false, "strict", "json"),
            new("async-tree", SymbolPathStyle.Explicit, true, PathDisplayStyle.Relative, false, "ignore", "tree"),
            new("async-line", SymbolPathStyle.CSharp, true, PathDisplayStyle.Absolute, false, "strict", "line"),
        ];
        foreach (var presentation in asyncPresentations)
        {
            var result = await RunPresentedCliAsync(
                ["async", "tree", "Acceptance.Graph.GraphHost::AsyncTreeRoot()", "--max-nodes", "32"],
                presentation,
                _fixture.StandardDatabasePath,
                baseDirectory: null);
            AssertSuccessful(presentation.Name, result);
            if (presentation.OutputFormat.Equals("json", StringComparison.Ordinal))
            {
                Assert.Equal(expectedAsyncKeys, ExtractAsyncPathStableKeys(result.StandardOutput));
            }
            else
            {
                AssertDisplaysAppearInOrder(
                    result.StandardOutput,
                    asyncPath.Nodes.Select(symbol => _fixture.FormatPath(symbol, ToSymbolPathOptions(presentation))));
            }
        }
    }

    [Fact]
    public async Task OH03_PartialDeclarationOutputUsesStableRoleOrder()
    {
        var partial = await _fixture.GetSymbolAsync(
            "Acceptance.Partials.PartialHost::PairedPartial()",
            cancellationToken: Token);
        SemanticDeclaration[] expectedSequence =
        [
            new(partial.StableKey, "partial-definition"),
            new(partial.StableKey, "partial-implementation"),
        ];

        var relocationParent = Path.Combine(
            Path.GetTempPath(),
            $"csindex-oh03-partials-{Guid.NewGuid():N}");
        Directory.CreateDirectory(relocationParent);
        try
        {
            var relocatedContainer = _fixture.CopyContainerTo(relocationParent);
            var relocatedDatabasePath = Path.Combine(
                relocatedContainer,
                "workspace",
                ".csindex",
                "index.sqlite");
            foreach (var presentation in PresentationCases)
            {
                var databasePath = presentation.UseAlternateBase
                    ? relocatedDatabasePath
                    : _fixture.StandardDatabasePath;
                var baseDirectory = presentation.UseAlternateBase
                    ? _fixture.WorkspacePath
                    : null;
                var expectedDefinitionPath = presentation.PathStyle == PathDisplayStyle.Relative
                    ? "src/Corpus/Partials.Definition.cs"
                    : Path.Combine(_fixture.CorpusPath, "Partials.Definition.cs");
                var expectedImplementationPath = presentation.PathStyle == PathDisplayStyle.Relative
                    ? "src/Corpus/Partials.Implementation.cs"
                    : Path.Combine(_fixture.CorpusPath, "Partials.Implementation.cs");
                var definition = await RunPresentedCliAsync(
                    [
                        "definition", "Acceptance.Partials.PartialHost::PairedPartial()",
                        "--namespace-case", presentation.NamespaceCase,
                    ],
                    presentation,
                    databasePath,
                    baseDirectory);
                AssertSuccessful($"{presentation.Name}-definition", definition);
                AssertPartialDeclarationPresentation(
                    definition.StandardOutput,
                    "definitions",
                    presentation,
                    expectedSequence,
                    expectedDefinitionPath,
                    expectedImplementationPath,
                    _fixture.FormatPath(partial, ToSymbolPathOptions(presentation)));

                var sourceSearch = await RunPresentedCliAsync(
                    [
                        "source", "search",
                        "--namespace-literal", "Acceptance.Partials",
                        "--namespace-case", presentation.NamespaceCase,
                        "--type-literal", "PartialHost",
                        "--method-literal", "PairedPartial()",
                        "--include-literal", "PARTIAL-",
                    ],
                    presentation,
                    databasePath,
                    baseDirectory);
                AssertSuccessful($"{presentation.Name}-source-search", sourceSearch);
                AssertPartialDeclarationPresentation(
                    sourceSearch.StandardOutput,
                    "matched",
                    presentation,
                    expectedSequence,
                    expectedDefinitionPath,
                    expectedImplementationPath,
                    _fixture.FormatPath(partial, ToSymbolPathOptions(presentation)));
            }
        }
        finally
        {
            Directory.Delete(relocationParent, recursive: true);
        }
    }

    private async Task<CSharpSymbolPathCliResult> RunPresentedCliAsync(
        string[] command,
        PresentationCase presentation,
        string databasePath,
        string? baseDirectory)
    {
        var arguments = new List<string>(command)
        {
            "--symbol-path-style",
            presentation.SymbolPathStyle == SymbolPathStyle.CSharp ? "csharp" : "explicit",
            "--path-style",
            presentation.PathStyle == PathDisplayStyle.Absolute ? "absolute" : "relative",
            "--output-format",
            presentation.OutputFormat,
        };
        if (presentation.ShortNames)
        {
            arguments.Add("--short-names");
        }

        if (baseDirectory is not null)
        {
            arguments.AddRange(["--base-dir", baseDirectory]);
        }

        arguments.AddRange(["--db", databasePath]);
        return await _fixture.RunCliAsync(arguments.ToArray());
    }

    private static SymbolPathFormatOptions ToSymbolPathOptions(PresentationCase presentation) =>
        new(presentation.SymbolPathStyle, presentation.ShortNames);

    private static void AssertSuccessful(string caseName, CSharpSymbolPathCliResult result)
    {
        Assert.True(
            result.ExitCode == ExitCodes.Success,
            $"{caseName} failed:{Environment.NewLine}{result.StandardError}");
    }

    private static IReadOnlyList<StoredSymbol> OrderByIndependentCanonicalFields(
        IEnumerable<StoredSymbol> symbols) =>
        symbols
            .OrderBy(symbol => SemanticPath(symbol).NamespacePath, StringComparer.Ordinal)
            .ThenBy(symbol => SemanticPath(symbol).TypeIdentityPath, StringComparer.Ordinal)
            .ThenBy(symbol => SemanticPath(symbol).ExecutableIdentityPath, StringComparer.Ordinal)
            .ThenBy(symbol => string.IsNullOrEmpty(symbol.PreferredDocumentPath) ? 1 : 0)
            .ThenBy(symbol => symbol.PreferredDocumentPath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.PreferredSourceStart is null ? 1 : 0)
            .ThenBy(symbol => symbol.PreferredSourceStart ?? int.MaxValue)
            .ThenBy(symbol => symbol.StableKey, StringComparer.Ordinal)
            .ToArray();

    private static SymbolPathData SemanticPath(StoredSymbol symbol) =>
        symbol.Path ?? throw new InvalidOperationException(
            $"Expected semantic path data for stable key '{symbol.StableKey}'.");

    private static IReadOnlyList<string> ExtractSymbolStableKeys(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName)
            .EnumerateArray()
            .Select(symbol => symbol.GetProperty("stableKey").GetString()!)
            .ToArray();
    }

    private static IReadOnlyList<SemanticDeclaration> ExtractDefinitionSequence(
        string json,
        string propertyName) =>
        ExtractJsonDeclarationRows(json, propertyName)
            .Select(row => new SemanticDeclaration(row.StableKey, row.Role))
            .ToArray();

    private static IReadOnlyList<JsonDeclarationRow> ExtractJsonDeclarationRows(
        string json,
        string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName)
            .EnumerateArray()
            .Select(row => new JsonDeclarationRow(
                row.GetProperty("stableKey").GetString()!,
                row.GetProperty("declarationRole").GetString()!,
                row.GetProperty("location").GetProperty("path").GetString()!,
                row.GetProperty("displayName").GetString()!))
            .ToArray();
    }

    private static IReadOnlyList<TableDeclarationRow> ExtractTableDeclarationRows(string output) =>
        output.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(cells => cells.Length >= 3)
            .Select(cells => new TableDeclarationRow(cells[0], cells[1], cells[2]))
            .ToArray();

    private static void AssertPartialDeclarationPresentation(
        string output,
        string jsonProperty,
        PresentationCase presentation,
        IReadOnlyList<SemanticDeclaration> expectedSequence,
        string expectedDefinitionPath,
        string expectedImplementationPath,
        string expectedDisplayName)
    {
        if (presentation.OutputFormat.Equals("json", StringComparison.Ordinal))
        {
            var rows = ExtractJsonDeclarationRows(output, jsonProperty);
            Assert.Equal(expectedSequence, rows.Select(row => new SemanticDeclaration(row.StableKey, row.Role)));
            Assert.Equal(
                [expectedDefinitionPath, expectedImplementationPath],
                rows.Select(row => row.Path));
            Assert.All(rows, row => Assert.Equal(expectedDisplayName, row.DisplayName));
            return;
        }

        var tableRows = ExtractTableDeclarationRows(output);
        Assert.Equal(expectedSequence.Count, tableRows.Count);
        Assert.Equal(expectedSequence.Select(row => row.Role), tableRows.Select(row => row.Role));
        Assert.All(tableRows, row => Assert.Contains(expectedDisplayName, row.Signature, StringComparison.Ordinal));
        Assert.Contains(expectedDefinitionPath, tableRows[0].Location, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedImplementationPath, tableRows[1].Location, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertDisplaysAppearInOrder(string output, IEnumerable<string> displays)
    {
        var priorIndex = -1;
        foreach (var display in displays)
        {
            var currentIndex = output.IndexOf(display, priorIndex + 1, StringComparison.Ordinal);
            Assert.True(
                currentIndex > priorIndex,
                $"Expected semantic display '{display}' after offset {priorIndex}:{Environment.NewLine}{output}");
            priorIndex = currentIndex;
        }
    }

    private static IReadOnlyList<SemanticEdge> ExtractCallerTreeEdgeSequence(string json)
    {
        using var document = JsonDocument.Parse(json);
        var stableKeysById = document.RootElement.GetProperty("nodes")
            .EnumerateArray()
            .ToDictionary(
                node => node.GetProperty("symbol").GetProperty("id").GetInt64(),
                node => node.GetProperty("symbol").GetProperty("stableKey").GetString()!);
        return document.RootElement.GetProperty("edges")
            .EnumerateArray()
            .Select(edge => new SemanticEdge(
                stableKeysById[edge.GetProperty("callerSymbolId").GetInt64()],
                stableKeysById[edge.GetProperty("calleeSymbolId").GetInt64()]))
            .ToArray();
    }

    private static IReadOnlyList<StoredSymbol> BuildIndependentCallerTreeOrder(CallerTreeResult result)
    {
        var symbolsById = result.Nodes
            .Select(node => node.Symbol)
            .Append(result.Root)
            .GroupBy(symbol => symbol.Id)
            .ToDictionary(group => group.Key, SelectConsistentSymbol);
        var childrenByParent = result.Edges
            .GroupBy(edge => edge.CalleeSymbolId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(edge => symbolsById[edge.CallerSymbolId])
                    .ToArray());
        var ordered = new List<StoredSymbol>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void AppendDepthFirst(StoredSymbol parent)
        {
            if (!visited.Add(parent.StableKey))
            {
                return;
            }

            ordered.Add(parent);
            if (!childrenByParent.TryGetValue(parent.Id, out var children))
            {
                return;
            }

            foreach (var child in OrderByIndependentCanonicalFields(children))
            {
                AppendDepthFirst(child);
            }
        }

        AppendDepthFirst(result.Root);
        return ordered;
    }

    private static void AssertCallerTreeJsonUsesIndependentParentFirstOrder(
        string json,
        CallerTreeResult expectedTree)
    {
        var payload = ExtractCallerTreePayload(json);
        var expectedNodes = BuildIndependentCallerTreeOrder(expectedTree)
            .Select(symbol => symbol.StableKey)
            .ToArray();
        Assert.Equal(expectedNodes, payload.Nodes.Select(node => node.StableKey));
        Assert.True(payload.Nodes.Any(node => node.Depth >= 2), "Caller tree must include depth-two descendants.");

        var nodeIndex = payload.Nodes
            .Select((node, index) => (node.StableKey, index))
            .ToDictionary(value => value.StableKey, value => value.index, StringComparer.Ordinal);
        Assert.All(payload.Edges, edge =>
        {
            Assert.True(
                nodeIndex[edge.CalleeStableKey] < nodeIndex[edge.CallerStableKey],
                $"Parent '{edge.CalleeStableKey}' followed child '{edge.CallerStableKey}'.");
        });

        var symbolsByStableKey = expectedTree.Nodes
            .Select(node => node.Symbol)
            .Append(expectedTree.Root)
            .GroupBy(symbol => symbol.StableKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, SelectConsistentSymbol, StringComparer.Ordinal);
        var siblingGroups = payload.Edges
            .GroupBy(edge => edge.CalleeStableKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToArray();
        Assert.NotEmpty(siblingGroups);
        foreach (var siblings in siblingGroups)
        {
            var expected = OrderByIndependentCanonicalFields(
                    siblings.Select(edge => symbolsByStableKey[edge.CallerStableKey]))
                .Select(symbol => symbol.StableKey)
                .ToArray();
            Assert.Equal(expected, siblings.Select(edge => edge.CallerStableKey));
            Assert.Equal(
                expected,
                payload.Nodes
                    .Select(node => node.StableKey)
                    .Where(stableKey => expected.Contains(stableKey, StringComparer.Ordinal)));
        }
    }

    private static CallerTreeJsonPayload ExtractCallerTreePayload(string json)
    {
        using var document = JsonDocument.Parse(json);
        var nodes = document.RootElement.GetProperty("nodes")
            .EnumerateArray()
            .Select(node => new CallerTreeJsonNode(
                node.GetProperty("symbol").GetProperty("id").GetInt64(),
                node.GetProperty("symbol").GetProperty("stableKey").GetString()!,
                node.GetProperty("depth").GetInt32()))
            .ToArray();
        var stableKeysById = nodes.ToDictionary(node => node.Id, node => node.StableKey);
        var edges = document.RootElement.GetProperty("edges")
            .EnumerateArray()
            .Select(edge => new SemanticEdge(
                stableKeysById[edge.GetProperty("callerSymbolId").GetInt64()],
                stableKeysById[edge.GetProperty("calleeSymbolId").GetInt64()]))
            .ToArray();
        return new CallerTreeJsonPayload(nodes, edges);
    }

    private static StoredSymbol SelectConsistentSymbol(IGrouping<long, StoredSymbol> symbols)
    {
        var first = symbols.First();
        Assert.All(symbols, symbol => Assert.Equal(first.StableKey, symbol.StableKey));
        return first;
    }

    private static StoredSymbol SelectConsistentSymbol(IGrouping<string, StoredSymbol> symbols)
    {
        var first = symbols.First();
        Assert.All(symbols, symbol => Assert.Equal(first.StableKey, symbol.StableKey));
        return first;
    }

    private static IReadOnlyList<string> ExtractAsyncPathStableKeys(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("nodes")
            .EnumerateArray()
            .Select(node => node.GetProperty("stableKey").GetString()!)
            .ToArray();
    }

    private async Task<string> RunSymbolListAsync(string databasePath, PathDisplayStyle pathStyle)
    {
        var result = await _fixture.RunCliAsync(
            "symbol", "list",
            "--output-format", "json",
            "--path-style", pathStyle == PathDisplayStyle.Absolute ? "absolute" : "relative",
            "--db", databasePath);
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.True(string.IsNullOrEmpty(result.StandardError), result.StandardError);
        return result.StandardOutput;
    }

    private async Task<CSharpSymbolPathCliResult> RunPathVariantAsync(
        string[] arguments,
        string? baseDirectory,
        string pathStyle,
        string? databasePath = null)
    {
        var completeArguments = new List<string>(arguments);
        completeArguments.AddRange(["--path-style", pathStyle]);
        if (baseDirectory is not null)
        {
            completeArguments.AddRange(["--base-dir", baseDirectory]);
        }

        completeArguments.AddRange(["--db", databasePath ?? _fixture.StandardDatabasePath]);
        return await _fixture.RunCliAsync(completeArguments.ToArray());
    }

    private static void AssertPathConsumerSuccess(
        string caseName,
        params CSharpSymbolPathCliResult[] results) =>
        Assert.All(results, result => Assert.True(
            result.ExitCode == ExitCodes.Success,
            $"{caseName} failed:{Environment.NewLine}{result.StandardError}"));

    private string[] BuildSchemaFourArguments(
        string[] command,
        string databasePath,
        string outputPath)
    {
        var arguments = command
            .Select(argument => argument.Equals("<solution>", StringComparison.Ordinal)
                ? _fixture.SolutionPath
                : argument)
            .ToList();
        if (!arguments[0].Equals("index", StringComparison.Ordinal))
        {
            arguments.AddRange(["--output-file", outputPath]);
        }

        arguments.AddRange(["--db", databasePath]);
        return arguments.ToArray();
    }

    private static string[] DirectoryFileNames(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path) ?? throw new InvalidOperationException(
                $"File path had no name: {path}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] FindOutputTemporaryFiles(string directory) =>
        Directory.EnumerateFiles(directory, ".*.tmp", SearchOption.TopDirectoryOnly).ToArray();

    private static ImmutableArray<string> OperationalOptions(
        string scopeName,
        string databasePath,
        string outputPath) =>
        scopeName switch
        {
            "global" => ["--db", databasePath, "--output-file", outputPath],
            "index" => ["--db", databasePath, "--mode", "directory", "--solution", "missing.sln"],
            "symbol find" =>
                ["--db", databasePath, "--output-file", outputPath, "--kind", "all"],
            "symbol list" =>
                ["--db", databasePath, "--output-file", outputPath, "--namespace-literal", "Acceptance"],
            "source show" =>
                ["--db", databasePath, "--output-file", outputPath, "--namespace-literal", "Acceptance"],
            "source search" =>
                ["--db", databasePath, "--output-file", outputPath, "--include-literal", "MARKER"],
            "definition query" =>
                ["--db", databasePath, "--output-file", outputPath, "--namespace-literal", "Acceptance"],
            "definition at" => ["--db", databasePath, "--output-file", outputPath],
            "references" =>
                ["--db", databasePath, "--output-file", outputPath, "--namespace-literal", "Acceptance"],
            "callers" =>
                ["--db", databasePath, "--output-file", outputPath, "--namespace-literal", "Acceptance"],
            "callees" =>
                ["--db", databasePath, "--output-file", outputPath, "--namespace-literal", "Acceptance"],
            "overrides" =>
                ["--db", databasePath, "--output-file", outputPath, "--namespace-literal", "Acceptance"],
            "async tree" =>
                ["--db", databasePath, "--output-file", outputPath, "--max-nodes", "1"],
            "callers tree" =>
                ["--db", databasePath, "--output-file", outputPath, "--depth", "1"],
            "conditions" => ["--db", databasePath, "--output-file", outputPath, "--base-dir", "portable-root"],
            _ => throw new ArgumentOutOfRangeException(nameof(scopeName), scopeName, "Unknown help scope."),
        };

    private static IEnumerable<DependencyCase> MissingRequiredInputCases(string containerPath)
    {
        foreach (var name in new[]
                 {
                     "index", "source-show", "definition-query", "definition-at", "references", "callers",
                     "callees", "overrides", "async-tree", "callers-tree",
                 })
        {
            var directory = Path.Combine(containerPath, $"oh06-missing-{name}-{Guid.NewGuid():N}");
            var databasePath = Path.Combine(directory, "missing.sqlite");
            var outputPath = Path.Combine(directory, "result.txt");
            var common = new[] { "--db", databasePath, "--output-file", outputPath };
            string[] arguments = name switch
            {
                "index" => ["index", "--mode", "directory", "--db", databasePath],
                "source-show" => ["source", "show", "--namespace-literal", "Acceptance", .. common],
                "definition-query" => ["definition", "--namespace-literal", "Acceptance", .. common],
                "definition-at" => ["definition", "--at=", .. common],
                "references" => ["references", "--namespace-literal", "Acceptance", .. common],
                "callers" => ["callers", "--namespace-literal", "Acceptance", .. common],
                "callees" => ["callees", "--namespace-literal", "Acceptance", .. common],
                "overrides" => ["overrides", "--namespace-literal", "Acceptance", .. common],
                "async-tree" => ["async", "tree", "--max-nodes", "1", .. common],
                "callers-tree" => ["callers", "tree", "--depth", "1", .. common],
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown missing-input case."),
            };

            yield return new DependencyCase(name, arguments, outputPath);
        }
    }

    private static IEnumerable<DependencyCase> EmptySelectorIsValidCases(string containerPath)
    {
        foreach (var name in new[] { "symbol-find", "symbol-list", "source-search", "conditions" })
        {
            var directory = Path.Combine(containerPath, $"oh06-empty-{name}-{Guid.NewGuid():N}");
            var databasePath = Path.Combine(directory, "missing.sqlite");
            var outputPath = Path.Combine(directory, "result.txt");
            string[] arguments = name switch
            {
                "symbol-find" =>
                    ["symbol", "find", "--kind", "all", "--db", databasePath, "--output-file", outputPath],
                "symbol-list" =>
                    ["symbol", "list", "--db", databasePath, "--output-file", outputPath],
                "source-search" =>
                    ["source", "search", "--include-literal", "MARKER", "--db", databasePath, "--output-file", outputPath],
                "conditions" =>
                    ["conditions", "--db", databasePath, "--output-file", outputPath],
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown empty-selector case."),
            };

            yield return new DependencyCase(name, arguments, outputPath);
        }
    }

    private static ProgramDependencies CreateThrowingDependencies(ICollection<string> invocations) =>
        new(
            (_, _) => ThrowDependency<OutputDestination>("output", invocations),
            (_, _) => ThrowDependency<SemanticQueryService>("query", invocations),
            () => ThrowDependency<AnalysisCoordinator>("analysis", invocations),
            _ => ThrowDependency<SqliteIndex>("sqlite", invocations));

    private static ProgramDependencies CreateRealQueryDependencies(
        Func<string?, string, OutputDestination> outputDestinationFactory) =>
        new(
            outputDestinationFactory,
            (databasePath, baseDirectory) => new SemanticQueryService(
                new SqliteIndex(databasePath).CreateQueryRepository(),
                baseDirectory),
            () => AnalysisCoordinator.CreateDefault(),
            databasePath => new SqliteIndex(databasePath));

    private static T ThrowDependency<T>(string name, ICollection<string> invocations)
    {
        invocations.Add(name);
        throw new InvalidOperationException($"OH06 dependency '{name}' must not be opened.");
    }

    private static IReadOnlyList<string> ExtractAcceptedOptions(string help)
    {
        var lines = help.ReplaceLineEndings("\n").Split('\n');
        var headerIndex = Array.FindIndex(lines, line => line.Equals("Accepted options:", StringComparison.Ordinal));
        if (headerIndex < 0)
        {
            throw new InvalidOperationException($"Help did not contain an accepted-options matrix:{Environment.NewLine}{help}");
        }

        var options = new List<string>();
        for (var index = headerIndex + 1; index < lines.Length && lines[index].StartsWith("  --", StringComparison.Ordinal); index++)
        {
            options.Add(lines[index].Trim()[2..]);
        }

        return options;
    }

    private static IReadOnlyList<string> ErrorLines(string error) =>
        error.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private async Task AssertUncommittedFileOutputAsync(
        CSharpSymbolPathCliResult result,
        string outputPath,
        byte[] sentinel,
        string directory)
    {
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(outputPath, Token));
        Assert.Empty(FindOutputTemporaryFiles(directory));
    }

    private static bool ContainsAbsoluteMachinePath(string value)
    {
        for (var index = 0; index + 2 < value.Length; index++)
        {
            if (char.IsAsciiLetter(value[index]) &&
                value[index + 1] == ':' &&
                value[index + 2] is '\\' or '/')
            {
                return true;
            }
        }

        return value.Contains(@"\\", StringComparison.Ordinal) ||
               value.Contains("//?/", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("//./", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var offset = 0; offset <= haystack.Length - needle.Length; offset++)
        {
            if (haystack.AsSpan(offset, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string> RunSourceShowAsync(
        string databasePath,
        string? baseDirectory = null,
        PathDisplayStyle pathStyle = PathDisplayStyle.Absolute)
    {
        var arguments = new List<string>
        {
            "source", "show", "Acceptance.Shared.Linked::LinkedMarker()",
            "--output-format", "json",
            "--path-style", pathStyle == PathDisplayStyle.Absolute ? "absolute" : "relative",
            "--db", databasePath,
        };
        if (baseDirectory is not null)
        {
            arguments.AddRange(["--base-dir", baseDirectory]);
        }

        var result = await _fixture.RunCliAsync(arguments.ToArray());
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.True(string.IsNullOrEmpty(result.StandardError), result.StandardError);
        return result.StandardOutput;
    }

    private static IReadOnlyList<(long Id, string Name)> SymbolIdentities(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("symbols")
            .EnumerateArray()
            .Select(symbol => (
                symbol.GetProperty("id").GetInt64(),
                symbol.GetProperty("fullyQualifiedName").GetString()!))
            .ToArray();
    }

    private static IReadOnlyList<(long SymbolId, long DeclarationId, DeclarationRole Role)> DefinitionIdentity(
        CsIndex.Query.DefinitionResult result) =>
        result.Definitions
            .Select(row => (row.Symbol.Id, row.Declaration.Id, row.Declaration.Role))
            .ToArray();

    private static string DefinitionJsonIdentity(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var matched = string.Join(
            ',',
            root.GetProperty("matched").EnumerateArray()
                .Select(symbol => symbol.GetProperty("id").GetInt64()));
        var definitions = string.Join(
            ',',
            root.GetProperty("definitions").EnumerateArray()
                .Select(definition =>
                    $"{definition.GetProperty("id").GetInt64()}:{definition.GetProperty("declarationRole").GetString()}"));
        return $"matched={matched};definitions={definitions}";
    }

    private void AssertRelocatedLocationPaths(string json, string relocatedContainer)
    {
        var paths = LocationPaths(json);
        Assert.NotEmpty(paths);
        Assert.All(paths, path =>
        {
            Assert.True(Path.IsPathFullyQualified(path), path);
            Assert.StartsWith(
                Path.GetFullPath(relocatedContainer),
                Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_fixture.ContainerPath, path, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static IReadOnlyList<string> LocationPaths(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CollectLocationPaths(document.RootElement)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> AllLocationPaths(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CollectLocationPaths(document.RootElement).ToArray();
    }

    private static IEnumerable<string> CollectLocationPaths(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("location", out var location) &&
                location.ValueKind == JsonValueKind.Object &&
                location.TryGetProperty("path", out var path) &&
                path.ValueKind == JsonValueKind.String)
            {
                yield return path.GetString()!;
            }

            foreach (var property in value.EnumerateObject())
            {
                foreach (var nested in CollectLocationPaths(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                foreach (var nested in CollectLocationPaths(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string NormalizeLocationPaths(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidOperationException("JSON payload was empty.");
        NormalizeLocationPaths(root);
        return root.ToJsonString();
    }

    private static void NormalizeLocationPaths(JsonNode node)
    {
        if (node is JsonObject value)
        {
            if (value.ContainsKey("path") &&
                value.ContainsKey("line") &&
                value.ContainsKey("column") &&
                value.ContainsKey("offset"))
            {
                value["path"] = "<location-path>";
            }

            foreach (var child in value.Select(property => property.Value).Where(child => child is not null).ToArray())
            {
                NormalizeLocationPaths(child!);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(child => child is not null).ToArray())
            {
                NormalizeLocationPaths(child!);
            }
        }
    }

    private sealed record PathConsumerCase(string Name, string[] Arguments);

    private sealed record HelpScope(
        string Name,
        ImmutableArray<string> Prefix,
        ImmutableArray<string> Options);

    private sealed record PresentationCase(
        string Name,
        SymbolPathStyle SymbolPathStyle,
        bool ShortNames,
        PathDisplayStyle PathStyle,
        bool UseAlternateBase,
        string NamespaceCase,
        string OutputFormat);

    private sealed record DependencyCase(string Name, string[] Arguments, string OutputPath);

    private sealed record SemanticDeclaration(string StableKey, string Role);

    private sealed record JsonDeclarationRow(string StableKey, string Role, string Path, string DisplayName);

    private sealed record TableDeclarationRow(string Signature, string Role, string Location);

    private sealed record SemanticEdge(string CallerStableKey, string CalleeStableKey);

    private sealed record CallerTreeJsonNode(long Id, string StableKey, int Depth);

    private sealed record CallerTreeJsonPayload(
        IReadOnlyList<CallerTreeJsonNode> Nodes,
        IReadOnlyList<SemanticEdge> Edges);

    private sealed record OutputPayloadCase(
        string Name,
        string[] Arguments,
        int FinalRecordWriteLines);

    private enum OutputCancellationPoint
    {
        FinalRecord,
        Flush,
        PreCommit,
    }

    private sealed class CancellationPointTextWriter(
        Stream stream,
        CancellationTokenSource cancellation,
        OutputCancellationPoint point,
        int finalRecordWriteLines) : TextWriter
    {
        private readonly StreamWriter _writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true);

        public int WriteLineCount { get; private set; }

        public bool CancellationRaised { get; private set; }

        public override Encoding Encoding => _writer.Encoding;

        public override void WriteLine(string? value)
        {
            _writer.WriteLine(value);
            WriteLineCount++;
            if (point == OutputCancellationPoint.FinalRecord &&
                WriteLineCount == finalRecordWriteLines)
            {
                RaiseCancellation();
            }
        }

        public override void Flush()
        {
            _writer.Flush();
            if (point == OutputCancellationPoint.Flush)
            {
                RaiseCancellation();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _writer.Dispose();
                if (point == OutputCancellationPoint.PreCommit)
                {
                    RaiseCancellation();
                }
            }

            base.Dispose(disposing);
        }

        private void RaiseCancellation()
        {
            if (CancellationRaised)
            {
                return;
            }

            CancellationRaised = true;
            cancellation.Cancel();
        }
    }

    private sealed class FailAfterWriteAndDisposeTextWriter(Stream stream) : TextWriter
    {
        private readonly StreamWriter _writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true);

        public int WriteLineCount { get; private set; }

        public bool DisposeAttempted { get; private set; }

        public override Encoding Encoding => _writer.Encoding;

        public override void WriteLine(string? value)
        {
            _writer.WriteLine(value);
            WriteLineCount++;
            throw new IOException("OH09 primary write failure");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeAttempted = true;
                _writer.Dispose();
                throw new IOException("OH09 cleanup disposal failure");
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ThrowOnFlushTextWriter(Stream stream) : TextWriter
    {
        private readonly StreamWriter _writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true);

        public int WriteLineCount { get; private set; }

        public bool FlushAttempted { get; private set; }

        public override Encoding Encoding => _writer.Encoding;

        public override void WriteLine(string? value)
        {
            _writer.WriteLine(value);
            WriteLineCount++;
        }

        public override void Flush()
        {
            FlushAttempted = true;
            _writer.Flush();
            throw new IOException("OH09 flush failure");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _writer.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
