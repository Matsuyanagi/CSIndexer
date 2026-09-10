using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CsIndex.Core.Analysis;

internal enum DeclarationFinalizationPhase
{
    Grouping,
    Projection,
    Consistency,
}

public sealed class SemanticExtractor(ProjectFingerprintBuilder projectFingerprintBuilder)
{
    private readonly Dictionary<SourceSymbolLookupKey, string> _sourceSymbolKeys = [];
    private readonly Dictionary<SyntaxTree, string> _sourceTreeProjectKeys = [];
    private readonly HashSet<SyntaxTree> _compilationOnlySourceTrees =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, SymbolData> _declarationProjectionData = [];
    private readonly Dictionary<string, AsyncRole> _declarationAsyncRoles = [];
    private readonly Dictionary<string, AsyncRole> _bodyAsyncRoles = [];
    private readonly HashSet<(string Source, string Target, SymbolRelationKind Kind)> _relationKeys = [];
    private readonly HashSet<(string ImplementingType, string InterfaceMethod, string ImplementationMethod)>
        _interfaceMethodBindingKeys = [];
    private readonly List<ProjectAnalysisState> _projectStates = [];
    private IndexSnapshot _snapshot = null!;
    private SymbolCanonicalizer _canonicalizer = null!;
    private Compilation? _currentCompilation;
    private ProjectAnalysisState? _currentProjectState;

    internal Action? AfterNestedExecutableOwner { get; set; }
    internal Action<DeclarationFinalizationPhase>? AfterDeclarationFinalizationItem { get; set; }

    public async Task ExtractAsync(
        IReadOnlyList<Project> projects,
        IndexSnapshot snapshot,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        var storageRoot = PathNormalizer.NormalizeAbsolute(snapshot.InputRoot);
        var paths = IndexPathResolver.CreateForIndex(
            Path.Combine(storageRoot, ".csindex", "index.sqlite"),
            storageRoot);
        var documentSelection = await AnalysisDocumentSelection.CreateAsync(
            storageRoot,
            projects,
            cancellationToken);
        var mappings = await AnalysisPathMappings.CreateAsync(
            storageRoot,
            paths,
            projects,
            documentSelection,
            cancellationToken);
        snapshot.InputRoot = ".";
        snapshot.IndexRootAnchor = paths.IndexRootAnchor;
        snapshot.Warnings.AddRange(documentSelection.Warnings);
        snapshot.DocumentsExcluded = checked(
            snapshot.DocumentsExcluded + documentSelection.CompilationOnlyCount);
        await ExtractAsync(
            projects,
            mappings,
            documentSelection,
            snapshot,
            includeDiagnostics,
            cancellationToken);
    }

    public Task ExtractAsync(
        PreparedAnalysis prepared,
        IndexSnapshot snapshot,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        prepared.ThrowIfDisposed();
        return ExtractAsync(
            prepared.Projects,
            prepared.Mappings,
            prepared.DocumentSelection,
            snapshot,
            includeDiagnostics,
            cancellationToken);
    }

    private async Task ExtractAsync(
        IReadOnlyList<Project> projects,
        AnalysisPathMappings mappings,
        AnalysisDocumentSelection documentSelection,
        IndexSnapshot snapshot,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        _snapshot = snapshot;
        _canonicalizer = new SymbolCanonicalizer(snapshot.Profile, mappings.GetSourceTreePath);
        _projectStates.Clear();
        _sourceSymbolKeys.Clear();
        _sourceTreeProjectKeys.Clear();
        _compilationOnlySourceTrees.Clear();
        _declarationProjectionData.Clear();
        _declarationAsyncRoles.Clear();
        _bodyAsyncRoles.Clear();
        _relationKeys.Clear();
        _interfaceMethodBindingKeys.Clear();
        _currentCompilation = null;
        _currentProjectState = null;

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Compilation could not be created: {project.Name}");
            var compilationOnlyDocumentIds = documentSelection
                .GetCompilationOnly(project)
                .Select(document => document.DocumentId)
                .ToHashSet();
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                if (project.GetDocumentId(syntaxTree) is { } documentId &&
                    compilationOnlyDocumentIds.Contains(documentId))
                {
                    _compilationOnlySourceTrees.Add(syntaxTree);
                    _sourceTreeProjectKeys[syntaxTree] = mappings.GetProjectKey(project);
                }
            }

            var diagnostics = compilation.GetDiagnostics(cancellationToken);
            var errors = diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            var warnings = diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
            if (includeDiagnostics)
            {
                snapshot.Diagnostics.AddRange(diagnostics
                    .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                    .Select(diagnostic => $"{project.Name}: {diagnostic}"));
            }

            snapshot.CompilationSummaries.Add(new CompilationSummary
            {
                ProjectName = project.Name,
                Errors = errors,
                Warnings = warnings,
            });
            if (errors > 0)
            {
                snapshot.Warnings.Add($"{project.Name}: compilation contains {errors} error(s); partial results were indexed.");
            }

            var projectPath = mappings.GetProjectPath(project);
            var projectKey = mappings.GetProjectKey(project);
            var projectData = new ProjectData
            {
                Key = projectKey,
                Name = project.Name,
                AssemblyName = project.AssemblyName,
                ProjectPath = projectPath,
                TargetFramework = snapshot.Profile.TargetFramework,
                Fingerprint = await projectFingerprintBuilder.BuildAsync(
                    project,
                    projectPath,
                    mappings,
                    documentSelection,
                    cancellationToken),
            };
            snapshot.Projects.Add(projectData);
            var state = new ProjectAnalysisState
            {
                Project = project,
                Compilation = compilation,
                Data = projectData,
                HasMissingReferenceDiagnostics = diagnostics.Any(diagnostic =>
                    diagnostic.Id is "CS0012" or "CS0234" or "CS0246"),
            };
            _projectStates.Add(state);
            await ExtractProjectDocumentsAndDeclarationsAsync(
                state,
                mappings,
                documentSelection,
                cancellationToken);
        }

        foreach (var state in _projectStates)
        {
            await ExtractProjectFactsAsync(state, cancellationToken);
        }

        FinalizeDeclarationProjections(cancellationToken);
        AsyncInvolvementPropagator.Apply(snapshot, cancellationToken);
    }

    private async Task ExtractProjectDocumentsAndDeclarationsAsync(
        ProjectAnalysisState projectState,
        AnalysisPathMappings mappings,
        AnalysisDocumentSelection documentSelection,
        CancellationToken cancellationToken)
    {
        _currentCompilation = projectState.Compilation;
        _currentProjectState = projectState;
        var projectRoot = projectState.Project.FilePath is null
            ? mappings.RuntimeStorageRoot
            : Path.GetDirectoryName(projectState.Project.FilePath)!;
        foreach (var document in projectState.Project.Documents
                     .Where(documentSelection.IsIndexable)
                     .OrderBy(
                         document => document.FilePath is null ? document.Name : mappings.GetDocumentPath(document),
                         StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.FilePath is null || !File.Exists(document.FilePath) ||
                !document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                PathNormalizer.HasPathSegment(projectRoot, document.FilePath, "obj"))
            {
                _snapshot.DocumentsExcluded++;
                continue;
            }

            var path = mappings.GetDocumentPath(document);
            var text = await document.GetTextAsync(cancellationToken);
            var generated = GeneratedCodeDetector.Detect(path, text);
            var contentHash = HashUtilities.Sha256(text.ToString());
            var documentKey = $"{projectState.Data.Key}|document:{path}";

            var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (root is null || semanticModel is null)
            {
                _snapshot.Warnings.Add($"Syntax or semantic model could not be loaded: {path}");
                continue;
            }

            var normalizedDocument = SourceNormalizer.NormalizeDocument(root, cancellationToken);
            var documentData = new DocumentData
            {
                Key = documentKey,
                ProjectKey = projectState.Data.Key,
                NormalizedPath = path,
                ContentHash = contentHash,
                NormalizedSource = normalizedDocument.Text,
                NormalizedSourceHash = normalizedDocument.Hash,
                IsGenerated = generated.IsGenerated,
                GenerationKind = generated.Kind,
            };
            _snapshot.Documents.Add(documentData);
            var documentState = new DocumentAnalysisState
            {
                Document = document,
                Data = documentData,
                NormalizedSource = normalizedDocument,
            };
            projectState.Documents[document.Id] = documentState;

            _sourceTreeProjectKeys[semanticModel.SyntaxTree] = projectState.Data.Key;

            foreach (var (symbol, node) in EnumerateTypeDeclarations(root, semanticModel, cancellationToken))
            {
                var data = _canonicalizer.CreateType(
                    symbol,
                    projectState.Data.Key,
                    documentKey,
                    node.SpanStart,
                    node.Span.Length,
                    generated.IsGenerated);
                UpsertSymbol(data);
                RegisterSourceSymbol(projectState.Data.Key, symbol.OriginalDefinition, data.StableKey);
            }

            foreach (var methodNode in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(methodNode, cancellationToken) is not IMethodSymbol method)
                {
                    continue;
                }

                var containingKey = EnsureType(method.ContainingType);
                var normalizedRange = documentState.NormalizedSource.GetRange(methodNode, cancellationToken);
                var data = _canonicalizer.CreateMethod(
                    method.OriginalDefinition,
                    projectState.Data.Key,
                    documentKey,
                    methodNode.SpanStart,
                    methodNode.Span.Length,
                    generated.IsGenerated,
                    containingKey) with
                {
                    AsyncRole = AsyncSymbolClassifier.Classify(method.OriginalDefinition, projectState.Compilation),
                };
                UpsertSymbol(data);
                AddDeclaration(
                    data,
                    documentState.Data.NormalizedPath,
                    documentState.Data.Key,
                    GetDeclarationRole(method, methodNode),
                    methodNode.SpanStart,
                    methodNode.Span.Length,
                    normalizedRange,
                    generated.IsGenerated);
                RegisterSourceSymbol(projectState.Data.Key, method.OriginalDefinition, data.StableKey);
                documentState.MethodOwners[methodNode.SpanStart] = data.StableKey;
            }

            foreach (var accessorNode in root.DescendantNodes().OfType<AccessorDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(accessorNode, cancellationToken) is not IMethodSymbol method)
                {
                    continue;
                }

                var containingKey = EnsureType(method.ContainingType);
                var normalizedRange = documentState.NormalizedSource.GetRange(accessorNode, cancellationToken);
                var data = _canonicalizer.CreateMethod(
                    method.OriginalDefinition,
                    projectState.Data.Key,
                    documentKey,
                    accessorNode.SpanStart,
                    accessorNode.Span.Length,
                    generated.IsGenerated,
                    containingKey) with
                {
                    AsyncRole = AsyncSymbolClassifier.Classify(method.OriginalDefinition, projectState.Compilation),
                };
                UpsertSymbol(data);
                AddDeclaration(
                    data,
                    documentState.Data.NormalizedPath,
                    documentState.Data.Key,
                    DeclarationRole.Ordinary,
                    accessorNode.SpanStart,
                    accessorNode.Span.Length,
                    normalizedRange,
                    generated.IsGenerated);
                RegisterSourceSymbol(projectState.Data.Key, method.OriginalDefinition, data.StableKey);
                documentState.AccessorOwners[accessorNode.SpanStart] = data.StableKey;
            }

            CreateExpressionBodiedGetterOwners(
                root,
                semanticModel,
                documentState,
                projectState.Data.Key,
                projectState.Compilation,
                cancellationToken);

            CreatePrimaryConstructorOwners(
                root,
                semanticModel,
                documentState,
                projectState.Data.Key,
                projectState.Compilation,
                cancellationToken);

            CreateInitializerOwners(
                root,
                semanticModel,
                documentState,
                projectState.Data.Key,
                cancellationToken);
            CreateTopLevelOwner(
                root,
                documentState,
                semanticModel,
                projectState.Data.Key,
                cancellationToken);
            CreateNestedExecutableOwners(
                root,
                documentState,
                semanticModel,
                projectState.Data.Key,
                projectState.Compilation,
                cancellationToken);

            foreach (var pair in ConditionalDirectiveScanner.Scan(root))
            {
                _snapshot.ConditionalSymbols.Add(new ConditionalSymbolData
                {
                    DocumentKey = documentKey,
                    SymbolName = pair.Key,
                    OccurrenceCount = pair.Value,
                });
            }
        }
    }

    private async Task ExtractProjectFactsAsync(
        ProjectAnalysisState projectState,
        CancellationToken cancellationToken)
    {
        _currentCompilation = projectState.Compilation;
        _currentProjectState = projectState;
        foreach (var documentState in projectState.Documents.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = await documentState.Document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
            var model = await documentState.Document.GetSemanticModelAsync(cancellationToken);
            if (root is null || model is null)
            {
                continue;
            }

            ExtractAsyncOperations(root, model, documentState, cancellationToken);
            ExtractInvocations(root, model, documentState, projectState, cancellationToken);
            ExtractObjectCreations(root, model, documentState, projectState, cancellationToken);
            ExtractMethodReferences(root, model, documentState, cancellationToken);
            ExtractRelations(root, model, cancellationToken);
        }
    }

    private void ExtractAsyncOperations(
        CompilationUnitSyntax root,
        SemanticModel model,
        DocumentAnalysisState documentState,
        CancellationToken cancellationToken)
    {
        foreach (var awaitExpression in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (model.GetOperation(awaitExpression, cancellationToken) is IAwaitOperation)
            {
                AddAsyncRole(documentState.FindOwner(awaitExpression), AsyncRole.ContainsAwait);
            }
        }

        foreach (var forEachStatement in root.DescendantNodes().OfType<CommonForEachStatementSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (model.GetOperation(forEachStatement, cancellationToken) is IForEachLoopOperation
                {
                    IsAsynchronous: true,
                })
            {
                AddAsyncRole(documentState.FindOwner(forEachStatement), AsyncRole.UsesAwaitForEach);
            }
        }

        foreach (var usingStatement in root.DescendantNodes().OfType<UsingStatementSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (model.GetOperation(usingStatement, cancellationToken) is IUsingOperation
                {
                    IsAsynchronous: true,
                })
            {
                AddAsyncRole(documentState.FindOwner(usingStatement), AsyncRole.UsesAwaitUsing);
            }
        }

        foreach (var localDeclaration in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (model.GetOperation(localDeclaration, cancellationToken) is IUsingDeclarationOperation
                {
                    IsAsynchronous: true,
                })
            {
                AddAsyncRole(documentState.FindOwner(localDeclaration), AsyncRole.UsesAwaitUsing);
            }
        }
    }

    private void ExtractInvocations(
        CompilationUnitSyntax root,
        SemanticModel model,
        DocumentAnalysisState documentState,
        ProjectAnalysisState projectState,
        CancellationToken cancellationToken)
    {
        foreach (var invocationSyntax in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callerKey = documentState.FindOwner(invocationSyntax);
            if (callerKey is null)
            {
                continue;
            }

            if (IsNameOf(invocationSyntax))
            {
                ExtractNameOfReference(invocationSyntax, model, documentState, callerKey, cancellationToken);
                continue;
            }

            var operation = model.GetOperation(invocationSyntax, cancellationToken);
            if (operation is IInvocationOperation invocation)
            {
                AddResolvedCall(
                    callerKey,
                    invocation.TargetMethod,
                    ReferenceKind.Invocation,
                    invocationSyntax,
                    documentState.Data.Key,
                    invocationSyntax.Expression.ToString(),
                    invocation.Instance?.Type,
                    documentState,
                    cancellationToken,
                    AsyncOperationClassifier.ClassifyInvocation(invocation, projectState.Compilation));
                continue;
            }

            if (operation is IDynamicInvocationOperation dynamicInvocation)
            {
                var normalizedRange = documentState.NormalizedSource.GetRange(invocationSyntax, cancellationToken);
                _snapshot.Calls.Add(new CallData
                {
                    CallerSymbolKey = callerKey,
                    ReferenceKind = ReferenceKind.DynamicInvocation,
                    DispatchKind = DispatchKind.Dynamic,
                    ResolutionStatus = ResolutionStatus.Dynamic,
                    ResolutionReason = ResolutionReason.DynamicDispatch,
                    DocumentKey = documentState.Data.Key,
                    SourceStart = invocationSyntax.SpanStart,
                    SourceLength = invocationSyntax.Span.Length,
                    NormalizedStart = normalizedRange.Start,
                    NormalizedLength = normalizedRange.Length,
                    UnresolvedName = invocationSyntax.Expression.ToString(),
                    ReceiverTypeKey = SymbolCanonicalizer.FormatType(dynamicInvocation.Operation.Type),
                });
                continue;
            }

            AddUnresolvedCall(
                callerKey,
                invocationSyntax.Expression,
                invocationSyntax,
                documentState.Data.Key,
                model,
                projectState,
                cancellationToken,
                documentState);
        }
    }

    private void ExtractObjectCreations(
        CompilationUnitSyntax root,
        SemanticModel model,
        DocumentAnalysisState documentState,
        ProjectAnalysisState projectState,
        CancellationToken cancellationToken)
    {
        foreach (var creationSyntax in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callerKey = documentState.FindOwner(creationSyntax);
            if (callerKey is null)
            {
                continue;
            }

            if (model.GetOperation(creationSyntax, cancellationToken) is IObjectCreationOperation creation &&
                creation.Constructor is { } constructor)
            {
                AddResolvedCall(
                    callerKey,
                    constructor,
                    ReferenceKind.ObjectCreation,
                    creationSyntax,
                    documentState.Data.Key,
                    creationSyntax.ToString(),
                    creation.Type,
                    documentState,
                    cancellationToken);
            }
            else
            {
                AddUnresolvedCall(
                    callerKey,
                    creationSyntax,
                    creationSyntax,
                    documentState.Data.Key,
                    model,
                    projectState,
                    cancellationToken,
                    documentState);
            }
        }
    }

    private void ExtractMethodReferences(
        CompilationUnitSyntax root,
        SemanticModel model,
        DocumentAnalysisState documentState,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var expressions = root.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(expression => expression is IdentifierNameSyntax or GenericNameSyntax or MemberAccessExpressionSyntax);
        foreach (var expression in expressions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expression.Parent is MemberAccessExpressionSyntax || IsInvocationTarget(expression) ||
                expression.Ancestors().OfType<InvocationExpressionSyntax>().Any(IsNameOf))
            {
                continue;
            }

            var symbolInfo = model.GetSymbolInfo(expression, cancellationToken);
            if (symbolInfo.Symbol is not IMethodSymbol method)
            {
                continue;
            }

            var callerKey = documentState.FindOwner(expression);
            if (callerKey is null)
            {
                continue;
            }

            var operation = model.GetOperation(expression, cancellationToken);
            var kind = operation is IMethodReferenceOperation { Parent: IDelegateCreationOperation }
                ? ReferenceKind.DelegateCreation
                : ReferenceKind.MethodGroup;
            var definitionKey = EnsureMethod(_canonicalizer.NormalizeLogicalMethod(method));
            var unique = $"{callerKey}|{expression.SpanStart}|{definitionKey}|{kind}";
            if (!seen.Add(unique))
            {
                continue;
            }

            var normalizedRange = documentState.NormalizedSource.GetRange(expression, cancellationToken);
            _snapshot.Calls.Add(new CallData
            {
                CallerSymbolKey = callerKey,
                CalleeSymbolKey = EnsureMethod(method),
                CalleeDefinitionKey = definitionKey,
                ReferenceKind = kind,
                DispatchKind = GetDispatchKind(method),
                ResolutionStatus = ResolutionStatus.Resolved,
                ResolutionReason = ResolutionReason.None,
                DocumentKey = documentState.Data.Key,
                SourceStart = expression.SpanStart,
                SourceLength = expression.Span.Length,
                NormalizedStart = normalizedRange.Start,
                NormalizedLength = normalizedRange.Length,
                UnresolvedName = expression.ToString(),
            });
        }
    }

    private void ExtractRelations(CompilationUnitSyntax root, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol type)
            {
                continue;
            }

            var sourceKey = EnsureType(type);
            if (type.TypeKind is TypeKind.Class or TypeKind.Struct)
            {
                ExtractInterfaceMethodBindings(type);
            }

            if (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            {
                AddRelation(sourceKey, EnsureType(baseType.OriginalDefinition), SymbolRelationKind.Inherits);
            }

            foreach (var interfaceType in type.Interfaces)
            {
                AddRelation(sourceKey, EnsureType(interfaceType.OriginalDefinition), SymbolRelationKind.Implements);
            }
        }

        foreach (var declaration in root.DescendantNodes()
                     .Where(node => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax))
        {
            var method = declaration switch
            {
                BaseMethodDeclarationSyntax methodDeclaration => model.GetDeclaredSymbol(methodDeclaration, cancellationToken),
                LocalFunctionStatementSyntax localDeclaration => model.GetDeclaredSymbol(localDeclaration, cancellationToken),
                _ => null,
            } as IMethodSymbol;
            if (method is null)
            {
                continue;
            }

            var sourceKey = EnsureMethod(method);
            if (method.OverriddenMethod is { } overridden)
            {
                AddRelation(
                    sourceKey,
                    EnsureMethod(_canonicalizer.NormalizeLogicalMethod(overridden)),
                    SymbolRelationKind.Overrides);
            }

            foreach (var implemented in method.ExplicitInterfaceImplementations)
            {
                AddRelation(
                    sourceKey,
                    EnsureMethod(_canonicalizer.NormalizeLogicalMethod(implemented)),
                    SymbolRelationKind.ExplicitlyImplements);
            }

            if (method.ContainingType is { } containingType)
            {
                foreach (var interfaceType in containingType.AllInterfaces)
                {
                    foreach (var member in interfaceType.GetMembers(method.Name).OfType<IMethodSymbol>())
                    {
                        if (SymbolEqualityComparer.Default.Equals(
                                containingType.FindImplementationForInterfaceMember(member),
                                method))
                        {
                            AddRelation(
                                sourceKey,
                                EnsureMethod(_canonicalizer.NormalizeLogicalMethod(member)),
                                SymbolRelationKind.ImplicitlyImplements);
                        }
                    }
                }
            }

            if (method.PartialDefinitionPart is { } definition)
            {
                var definitionKey = EnsureMethod(definition);
                if (!StringComparer.Ordinal.Equals(sourceKey, definitionKey))
                {
                    AddRelation(sourceKey, definitionKey, SymbolRelationKind.PartialDefinition);
                }
            }

            if (method.PartialImplementationPart is { } implementation)
            {
                var implementationKey = EnsureMethod(implementation);
                if (!StringComparer.Ordinal.Equals(sourceKey, implementationKey))
                {
                    AddRelation(sourceKey, implementationKey, SymbolRelationKind.PartialImplementation);
                }
            }
        }
    }

    private void ExtractInterfaceMethodBindings(INamedTypeSymbol type)
    {
        var implementingTypeKey = EnsureType(type);
        foreach (var interfaceType in type.AllInterfaces)
        {
            foreach (var interfaceMethod in interfaceType.GetMembers().OfType<IMethodSymbol>())
            {
                if (type.FindImplementationForInterfaceMember(interfaceMethod) is not IMethodSymbol implementation)
                {
                    _snapshot.Diagnostics.Add(
                        $"Interface method implementation was not resolved for '{type.ToDisplayString()}' " +
                        $"and '{interfaceMethod.ToDisplayString()}'.");
                    continue;
                }

                implementation = ResolveContextualImplementation(type, implementation);
                if (implementation.IsImplicitlyDeclared && ResolveSourceProjectKey(implementation) is not null)
                {
                    continue;
                }

                var interfaceMethodKey = EnsureMethod(
                    _canonicalizer.NormalizeLogicalMethod(interfaceMethod));
                var implementationMethodKey = EnsureMethod(
                    _canonicalizer.NormalizeLogicalMethod(implementation));
                var key = (implementingTypeKey, interfaceMethodKey, implementationMethodKey);
                if (_interfaceMethodBindingKeys.Add(key))
                {
                    _snapshot.InterfaceMethodBindings.Add(new InterfaceMethodBindingData
                    {
                        ImplementingTypeKey = implementingTypeKey,
                        InterfaceMethodKey = interfaceMethodKey,
                        ImplementationMethodKey = implementationMethodKey,
                    });
                }
            }
        }
    }

    private static IMethodSymbol ResolveContextualImplementation(
        INamedTypeSymbol implementingType,
        IMethodSymbol implementation)
    {
        for (var currentType = implementingType; currentType is not null; currentType = currentType.BaseType)
        {
            foreach (var candidate in currentType.GetMembers(implementation.Name).OfType<IMethodSymbol>())
            {
                for (var overridden = candidate.OverriddenMethod;
                     overridden is not null;
                     overridden = overridden.OverriddenMethod)
                {
                    if (SymbolEqualityComparer.Default.Equals(
                            overridden.OriginalDefinition,
                            implementation.OriginalDefinition))
                    {
                        return candidate;
                    }
                }
            }
        }

        return implementation;
    }

    private void CreatePrimaryConstructorOwners(
        CompilationUnitSyntax root,
        SemanticModel semanticModel,
        DocumentAnalysisState documentState,
        string projectKey,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                     .Where(candidate => candidate.ParameterList is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol type)
            {
                continue;
            }

            var constructor = type.InstanceConstructors.FirstOrDefault(candidate =>
                !candidate.IsImplicitlyDeclared &&
                candidate.DeclaringSyntaxReferences.Any(reference =>
                    ReferenceEquals(reference.SyntaxTree, declaration.SyntaxTree) &&
                    reference.Span == declaration.Span));
            if (constructor is null || declaration.ParameterList is not { } parameterList)
            {
                continue;
            }

            var containingKey = EnsureType(type);
            var normalizedRange = documentState.NormalizedSource.GetRange(parameterList, cancellationToken);
            var sourceStart = declaration.Identifier.SpanStart;
            var sourceLength = parameterList.Span.End - sourceStart;
            var data = _canonicalizer.CreateMethod(
                constructor.OriginalDefinition,
                projectKey,
                documentState.Data.Key,
                sourceStart,
                sourceLength,
                documentState.Data.IsGenerated,
                containingKey) with
            {
                AsyncRole = AsyncSymbolClassifier.Classify(constructor.OriginalDefinition, compilation),
            };
            UpsertSymbol(data);
            AddDeclaration(
                data,
                documentState.Data.NormalizedPath,
                documentState.Data.Key,
                DeclarationRole.Ordinary,
                sourceStart,
                sourceLength,
                normalizedRange,
                documentState.Data.IsGenerated);
            RegisterSourceSymbol(projectKey, constructor.OriginalDefinition, data.StableKey);
            if (declaration.BaseList is { } baseList &&
                baseList.Types.OfType<PrimaryConstructorBaseTypeSyntax>().FirstOrDefault() is { } primaryBaseType)
            {
                documentState.PrimaryConstructorBaseArgumentOwners[primaryBaseType.ArgumentList.SpanStart] =
                    data.StableKey;
            }
        }
    }

    private void CreateInitializerOwners(
        CompilationUnitSyntax root,
        SemanticModel semanticModel,
        DocumentAnalysisState documentState,
        string projectKey,
        CancellationToken cancellationToken)
    {
        foreach (var initializer in root.DescendantNodes().OfType<EqualsValueClauseSyntax>())
        {
            var declaration = initializer.Parent;
            if (declaration is not PropertyDeclarationSyntax &&
                declaration is not VariableDeclaratorSyntax
                {
                    Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax,
                })
            {
                continue;
            }

            var typeDeclaration = initializer.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            if (typeDeclaration is null)
            {
                continue;
            }

            if (semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not INamedTypeSymbol typeSymbol)
            {
                continue;
            }

            var containingTypeKey = EnsureType(typeSymbol);
            if (!_snapshot.Symbols.TryGetValue(containingTypeKey, out var containingType))
            {
                continue;
            }

            var memberName = declaration switch
            {
                VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                _ => "initializer",
            };
            var isStatic = initializer.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault()
                ?.Modifiers.Any(modifier => modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)) == true;
            var segmentDisplay = $"<initializer:{SymbolCanonicalizer.EscapeIdentifier(memberName)}>";
            var segmentIdentity = $"<initializer:{memberName}>";
            var path = _canonicalizer.CreateSyntheticPath(
                typeSymbol,
                segmentDisplay,
                segmentIdentity,
                CallablePathSegmentKind.Initializer);
            var display = SymbolCanonicalizer.FormatDisplayName(path);
            var stableKey = _canonicalizer.GetSyntheticStableKey(
                containingTypeKey,
                documentState.Data.NormalizedPath,
                segmentIdentity,
                initializer.SpanStart,
                initializer.Span.Length,
                documentState.Data.ContentHash);
            var normalizedRange = documentState.NormalizedSource.GetRange(initializer, cancellationToken);
            var data = new SymbolData
            {
                StableKey = stableKey,
                ProjectKey = projectKey,
                Kind = IndexedSymbolKind.Initializer,
                Name = $"<initializer:{memberName}>",
                NamespaceName = containingType.NamespaceName,
                TypeSimpleName = containingType.TypeSimpleName,
                TypeMetadataName = containingType.TypeMetadataName,
                FullyQualifiedName = display,
                DisplayName = display,
                Path = path,
                ContainingSymbolKey = containingTypeKey,
                ParameterCount = 0,
                SourceDocumentKey = documentState.Data.Key,
                SourceStart = initializer.SpanStart,
                SourceLength = initializer.Span.Length,
                IsStatic = isStatic,
                IsGenerated = documentState.Data.IsGenerated,
            };
            UpsertSymbol(data);
            AddDeclaration(
                data,
                documentState.Data.NormalizedPath,
                documentState.Data.Key,
                DeclarationRole.Ordinary,
                initializer.SpanStart,
                initializer.Span.Length,
                normalizedRange,
                documentState.Data.IsGenerated);
            documentState.InitializerOwners[initializer.SpanStart] = stableKey;
        }
    }

    private void CreateExpressionBodiedGetterOwners(
        CompilationUnitSyntax root,
        SemanticModel semanticModel,
        DocumentAnalysisState documentState,
        string projectKey,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        foreach (var member in root.DescendantNodes().OfType<BasePropertyDeclarationSyntax>()
                     .Where(member => member is PropertyDeclarationSyntax { ExpressionBody: not null } or
                         IndexerDeclarationSyntax { ExpressionBody: not null }))
        {
            var property = member switch
            {
                PropertyDeclarationSyntax propertyDeclaration =>
                    semanticModel.GetDeclaredSymbol(propertyDeclaration, cancellationToken) as IPropertySymbol,
                IndexerDeclarationSyntax indexerDeclaration =>
                    semanticModel.GetDeclaredSymbol(indexerDeclaration, cancellationToken) as IPropertySymbol,
                _ => null,
            };
            if (property?.GetMethod is not { } getter)
            {
                continue;
            }

            var containingKey = EnsureType(getter.ContainingType);
            var normalizedRange = documentState.NormalizedSource.GetRange(member, cancellationToken);
            var data = _canonicalizer.CreateMethod(
                getter.OriginalDefinition,
                projectKey,
                documentState.Data.Key,
                member.SpanStart,
                member.Span.Length,
                documentState.Data.IsGenerated,
                containingKey) with
            {
                AsyncRole = AsyncSymbolClassifier.Classify(getter.OriginalDefinition, compilation),
            };
            UpsertSymbol(data);
            AddDeclaration(
                data,
                documentState.Data.NormalizedPath,
                documentState.Data.Key,
                DeclarationRole.Ordinary,
                member.SpanStart,
                member.Span.Length,
                normalizedRange,
                documentState.Data.IsGenerated);
            RegisterSourceSymbol(projectKey, getter.OriginalDefinition, data.StableKey);
            documentState.ExpressionBodiedMemberOwners[member.SpanStart] = data.StableKey;
        }
    }

    private void CreateTopLevelOwner(
        CompilationUnitSyntax root,
        DocumentAnalysisState documentState,
        SemanticModel semanticModel,
        string projectKey,
        CancellationToken cancellationToken)
    {
        var statements = root.Members.OfType<GlobalStatementSyntax>().ToArray();
        if (statements.Length == 0)
        {
            return;
        }

        var start = statements[0].SpanStart;
        var end = statements[^1].Span.End;
        var programType = semanticModel.Compilation.GetEntryPoint(cancellationToken)?.ContainingType ??
                          semanticModel.Compilation.GetTypeByMetadataName("Program");
        var containingTypeKey = programType is null ? null : EnsureType(programType, projectKey);
        var path = SymbolCanonicalizer.CreateTopLevelPath();
        var display = SymbolCanonicalizer.FormatDisplayName(path);
        var stableKey = _canonicalizer.GetSyntheticStableKey(
            containingTypeKey ?? $"project:{projectKey}",
            documentState.Data.NormalizedPath,
            "top-level-statements",
            start,
            end - start,
            documentState.Data.ContentHash);
        var normalizedRange = documentState.NormalizedSource.GetRange(root, cancellationToken);
        var data = new SymbolData
        {
            StableKey = stableKey,
            ProjectKey = projectKey,
            Kind = IndexedSymbolKind.TopLevelStatements,
            Name = "<top-level-statements>",
            NamespaceName = string.Empty,
            TypeSimpleName = "Program",
            TypeMetadataName = "Program",
            FullyQualifiedName = display,
            DisplayName = display,
            Path = path,
            ContainingSymbolKey = containingTypeKey,
            ParameterCount = 0,
            SourceDocumentKey = documentState.Data.Key,
            SourceStart = start,
            SourceLength = end - start,
            IsGenerated = documentState.Data.IsGenerated,
        };
        UpsertSymbol(data);
        AddDeclaration(
            data,
            documentState.Data.NormalizedPath,
            documentState.Data.Key,
            DeclarationRole.Ordinary,
            start,
            end - start,
            normalizedRange,
            documentState.Data.IsGenerated);
        documentState.TopLevelOwner = stableKey;
    }

    private void CreateNestedExecutableOwners(
        CompilationUnitSyntax root,
        DocumentAnalysisState documentState,
        SemanticModel semanticModel,
        string projectKey,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in root.DescendantNodes().Where(candidate =>
                     candidate is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is LocalFunctionStatementSyntax localFunction)
            {
                CreateLocalFunctionOwner(
                    localFunction,
                    documentState,
                    semanticModel,
                    projectKey,
                    compilation,
                    cancellationToken);
                AfterNestedExecutableOwner?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }

            CreateAnonymousFunctionOwner(
                (AnonymousFunctionExpressionSyntax)node,
                documentState,
                semanticModel,
                projectKey,
                counters,
                cancellationToken);
            AfterNestedExecutableOwner?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void CreateLocalFunctionOwner(
        LocalFunctionStatementSyntax localFunction,
        DocumentAnalysisState documentState,
        SemanticModel semanticModel,
        string projectKey,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        if (semanticModel.GetDeclaredSymbol(localFunction, cancellationToken) is not IMethodSymbol method)
        {
            return;
        }

        var containingKey = documentState.FindOwner(localFunction, includeSelf: false) ??
                            EnsureType(method.ContainingType);
        if (!_snapshot.Symbols.TryGetValue(containingKey, out var containingSymbol) ||
            containingSymbol.Path is not { } containingPath)
        {
            return;
        }

        var normalizedRange = documentState.NormalizedSource.GetRange(localFunction, cancellationToken);
        var data = _canonicalizer.CreateMethod(
            method,
            projectKey,
            documentState.Data.Key,
            localFunction.SpanStart,
            localFunction.Span.Length,
            documentState.Data.IsGenerated,
            containingKey,
            containingPath) with
        {
            AsyncRole = AsyncSymbolClassifier.Classify(method, compilation),
        };
        UpsertSymbol(data);
        AddDeclaration(
            data,
            documentState.Data.NormalizedPath,
            documentState.Data.Key,
            DeclarationRole.Ordinary,
            localFunction.SpanStart,
            localFunction.Span.Length,
            normalizedRange,
            documentState.Data.IsGenerated);
        RegisterSourceSymbol(projectKey, method, data.StableKey);
        documentState.LocalFunctionOwners[localFunction.SpanStart] = data.StableKey;
    }

    private void CreateAnonymousFunctionOwner(
        AnonymousFunctionExpressionSyntax anonymousFunction,
        DocumentAnalysisState documentState,
        SemanticModel semanticModel,
        string projectKey,
        Dictionary<string, int> counters,
        CancellationToken cancellationToken)
    {
        var outerOwner = documentState.FindOwner(anonymousFunction, includeSelf: false);
        if (outerOwner is null ||
            !_snapshot.Symbols.TryGetValue(outerOwner, out var immediateOwnerSymbol) ||
            immediateOwnerSymbol.Path is not { } containingPath)
        {
            return;
        }

        counters.TryGetValue(outerOwner, out var count);
        count++;
        counters[outerOwner] = count;
        var isAnonymousMethod = anonymousFunction is AnonymousMethodExpressionSyntax;
        var segmentKind = isAnonymousMethod
            ? CallablePathSegmentKind.AnonymousMethod
            : CallablePathSegmentKind.Lambda;
        var marker = isAnonymousMethod
            ? $"<anonymous-method#{count}>"
            : $"<lambda#{count}>";
        var stableKey = _canonicalizer.GetSyntheticStableKey(
            outerOwner,
            documentState.Data.NormalizedPath,
            anonymousFunction.Kind().ToString(),
            anonymousFunction.SpanStart,
            anonymousFunction.Span.Length,
            documentState.Data.ContentHash);
        if (semanticModel.GetOperation(anonymousFunction, cancellationToken) is not IAnonymousFunctionOperation operation)
        {
            return;
        }

        var normalizedRange = documentState.NormalizedSource.GetRange(anonymousFunction, cancellationToken);
        var data = _canonicalizer.CreateAnonymousFunction(
            operation.Symbol,
            stableKey,
            outerOwner,
            containingPath,
            marker,
            segmentKind,
            projectKey,
            documentState.Data.Key,
            anonymousFunction.SpanStart,
            anonymousFunction.Span.Length,
            documentState.Data.IsGenerated) with
        {
            AsyncRole = AsyncSymbolClassifier.Classify(operation.Symbol, semanticModel.Compilation),
        };
        UpsertSymbol(data);
        AddDeclaration(
            data,
            documentState.Data.NormalizedPath,
            documentState.Data.Key,
            DeclarationRole.Ordinary,
            anonymousFunction.SpanStart,
            anonymousFunction.Span.Length,
            normalizedRange,
            documentState.Data.IsGenerated);
        documentState.LambdaOwners[anonymousFunction.SpanStart] = stableKey;
    }

    private void ExtractNameOfReference(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        DocumentAnalysisState documentState,
        string callerKey,
        CancellationToken cancellationToken)
    {
        foreach (var expression in invocation.ArgumentList.Arguments
                     .SelectMany(argument => argument.Expression.DescendantNodesAndSelf().OfType<ExpressionSyntax>())
                     .OrderByDescending(expression => expression.Span.Length))
        {
            var symbolInfo = model.GetSymbolInfo(expression, cancellationToken);
            var methods = symbolInfo.Symbol is IMethodSymbol method
                ? [method]
                : symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().ToArray();
            if (methods.Length == 0)
            {
                continue;
            }

            var normalizedRange = documentState.NormalizedSource.GetRange(expression, cancellationToken);
            foreach (var candidate in methods)
            {
                _snapshot.Calls.Add(new CallData
                {
                    CallerSymbolKey = callerKey,
                    CalleeSymbolKey = EnsureMethod(candidate),
                    CalleeDefinitionKey = EnsureMethod(_canonicalizer.NormalizeLogicalMethod(candidate)),
                    ReferenceKind = ReferenceKind.NameOf,
                    DispatchKind = GetDispatchKind(candidate),
                    ResolutionStatus = ResolutionStatus.Resolved,
                    ResolutionReason = ResolutionReason.None,
                    DocumentKey = documentState.Data.Key,
                    SourceStart = expression.SpanStart,
                    SourceLength = expression.Span.Length,
                    NormalizedStart = normalizedRange.Start,
                    NormalizedLength = normalizedRange.Length,
                    UnresolvedName = expression.ToString(),
                });
            }

            return;
        }
    }

    private void AddResolvedCall(
        string callerKey,
        IMethodSymbol target,
        ReferenceKind referenceKind,
        SyntaxNode sourceNode,
        string documentKey,
        string sourceToken,
        ITypeSymbol? receiverType,
        DocumentAnalysisState documentState,
        CancellationToken cancellationToken,
        AsyncUsageKind asyncUsageKind = AsyncUsageKind.None)
    {
        var normalizedRange = documentState.NormalizedSource.GetRange(sourceNode, cancellationToken);
        _snapshot.Calls.Add(new CallData
        {
            CallerSymbolKey = callerKey,
            CalleeSymbolKey = EnsureMethod(target),
            CalleeDefinitionKey = EnsureMethod(_canonicalizer.NormalizeLogicalMethod(target)),
            ReferenceKind = referenceKind,
            DispatchKind = GetDispatchKind(target),
            ResolutionStatus = ResolutionStatus.Resolved,
            ResolutionReason = ResolutionReason.None,
            AsyncUsageKind = asyncUsageKind,
            DocumentKey = documentKey,
            SourceStart = sourceNode.SpanStart,
            SourceLength = sourceNode.Span.Length,
            NormalizedStart = normalizedRange.Start,
            NormalizedLength = normalizedRange.Length,
            UnresolvedName = sourceToken,
            ReceiverTypeKey = SymbolCanonicalizer.FormatType(receiverType),
        });
    }

    private void AddUnresolvedCall(
        string callerKey,
        ExpressionSyntax lookupExpression,
        SyntaxNode sourceNode,
        string documentKey,
        SemanticModel model,
        ProjectAnalysisState projectState,
        CancellationToken cancellationToken,
        DocumentAnalysisState documentState)
    {
        var symbolInfo = model.GetSymbolInfo(lookupExpression, cancellationToken);
        var candidates = NormalizeCandidateKeys(
            symbolInfo.CandidateSymbols.OfType<IMethodSymbol>(),
            _canonicalizer.NormalizeLogicalMethod,
            EnsureMethod);
        var ambiguous = candidates.Length > 0;
        var normalizedRange = documentState.NormalizedSource.GetRange(sourceNode, cancellationToken);
        _snapshot.Calls.Add(new CallData
        {
            CallerSymbolKey = callerKey,
            ReferenceKind = ReferenceKind.Invocation,
            DispatchKind = DispatchKind.Static,
            ResolutionStatus = ambiguous ? ResolutionStatus.Ambiguous : ResolutionStatus.Unresolved,
            ResolutionReason = ambiguous
                ? ResolutionReason.AmbiguousCandidates
                : projectState.HasMissingReferenceDiagnostics
                    ? ResolutionReason.MissingMetadataReference
                    : ResolutionReason.CompilationError,
            DocumentKey = documentKey,
            SourceStart = sourceNode.SpanStart,
            SourceLength = sourceNode.Span.Length,
            NormalizedStart = normalizedRange.Start,
            NormalizedLength = normalizedRange.Length,
            UnresolvedName = lookupExpression.ToString(),
            CandidateSymbolKeys = candidates,
        });
    }

    internal static string[] NormalizeCandidateKeys(
        IEnumerable<IMethodSymbol> candidates,
        Func<IMethodSymbol, IMethodSymbol> normalize,
        Func<IMethodSymbol, string> keySelector) =>
        candidates
            .Select(candidate => keySelector(normalize(candidate)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private DeclarationRole GetDeclarationRole(
        IMethodSymbol method,
        BaseMethodDeclarationSyntax declaration)
    {
        if (method.PartialDefinitionPart is not null)
        {
            return IsCompilationOnlySourceSymbol(method.PartialDefinitionPart)
                ? DeclarationRole.Ordinary
                : DeclarationRole.PartialImplementation;
        }

        if (method.PartialImplementationPart is not null)
        {
            return DeclarationRole.PartialDefinition;
        }

        if (
            declaration.Modifiers.Any(modifier => modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)) &&
            declaration.Body is null &&
            declaration.ExpressionBody is null)
        {
            return DeclarationRole.PartialDefinition;
        }

        return DeclarationRole.Ordinary;
    }

    private bool IsCompilationOnlySourceSymbol(ISymbol symbol)
    {
        var hasSourceLocation = false;
        foreach (var location in symbol.Locations)
        {
            if (!location.IsInSource || location.SourceTree is not { } sourceTree)
            {
                continue;
            }

            hasSourceLocation = true;
            if (!_compilationOnlySourceTrees.Contains(sourceTree))
            {
                return false;
            }
        }

        return hasSourceLocation;
    }

    private void AddDeclaration(
        SymbolData data,
        string storedDocumentPath,
        string documentKey,
        DeclarationRole role,
        int sourceStart,
        int sourceLength,
        NormalizedSourceRange normalizedRange,
        bool isGenerated)
    {
        var key = _canonicalizer.GetDeclarationKey(
            data.StableKey,
            storedDocumentPath,
            sourceStart,
            sourceLength,
            role);
        _snapshot.Declarations[key] = new SymbolDeclarationData
        {
            Key = key,
            SymbolKey = data.StableKey,
            DocumentKey = documentKey,
            Role = role,
            SourceStart = sourceStart,
            SourceLength = sourceLength,
            NormalizedStart = normalizedRange.Start,
            NormalizedLength = normalizedRange.Length,
            IsGenerated = isGenerated,
        };
        _declarationProjectionData[key] = data;
        _declarationAsyncRoles[key] = data.AsyncRole;
    }

    private void FinalizeDeclarationProjections(CancellationToken cancellationToken)
    {
        var declarationsBySymbol = new Dictionary<string, List<SymbolDeclarationData>>(StringComparer.Ordinal);
        foreach (var declaration in _snapshot.Declarations.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!declarationsBySymbol.TryGetValue(declaration.SymbolKey, out var declarations))
            {
                declarations = [];
                declarationsBySymbol.Add(declaration.SymbolKey, declarations);
            }

            declarations.Add(declaration);
            AfterDeclarationFinalizationItem?.Invoke(DeclarationFinalizationPhase.Grouping);
            cancellationToken.ThrowIfCancellationRequested();
        }

        foreach (var symbol in _snapshot.Symbols.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!declarationsBySymbol.TryGetValue(symbol.StableKey, out var declarations) ||
                declarations.Count == 0)
            {
                _snapshot.Symbols[symbol.StableKey] = symbol with
                {
                    SourceDocumentKey = null,
                    SourceStart = null,
                    SourceLength = null,
                };
                continue;
            }

            var preferred = SelectPreferredDeclaration(declarations, cancellationToken);
            var preferredData = _declarationProjectionData[preferred.Key];
            var directAsyncRole = _declarationAsyncRoles.GetValueOrDefault(preferred.Key);
            var bodyAsyncRole = _bodyAsyncRoles.GetValueOrDefault(symbol.StableKey);
            _snapshot.Symbols[symbol.StableKey] = symbol with
            {
                Name = preferredData.Name,
                NamespaceName = preferredData.NamespaceName,
                TypeSimpleName = preferredData.TypeSimpleName,
                TypeMetadataName = preferredData.TypeMetadataName,
                FullyQualifiedName = preferredData.FullyQualifiedName,
                DisplayName = preferredData.DisplayName,
                Path = preferredData.Path,
                Arity = preferredData.Arity,
                ParameterCount = preferredData.ParameterCount,
                MethodKind = preferredData.MethodKind,
                Accessibility = preferredData.Accessibility,
                IsStatic = preferredData.IsStatic,
                IsAbstract = preferredData.IsAbstract,
                IsVirtual = preferredData.IsVirtual,
                IsOverride = preferredData.IsOverride,
                ReturnTypeKey = preferredData.ReturnTypeKey,
                ReturnTypeDisplay = preferredData.ReturnTypeDisplay,
                ConversionTypeKey = preferredData.ConversionTypeKey,
                ConversionTypeDisplay = preferredData.ConversionTypeDisplay,
                Parameters = preferredData.Parameters,
                PreferredDeclarationKey = preferred.Key,
                SourceDocumentKey = null,
                SourceStart = null,
                SourceLength = null,
                IsGenerated = preferred.IsGenerated,
                AsyncRole = directAsyncRole | bodyAsyncRole,
            };
            AfterDeclarationFinalizationItem?.Invoke(DeclarationFinalizationPhase.Projection);
            cancellationToken.ThrowIfCancellationRequested();
        }

        foreach (var symbol in _snapshot.Symbols.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (symbol.PreferredDeclarationKey is not { } declarationKey ||
                !_snapshot.Declarations.TryGetValue(declarationKey, out var declaration) ||
                declaration.SymbolKey != symbol.StableKey)
            {
                if (symbol.PreferredDeclarationKey is not null)
                {
                    throw new InvalidOperationException(
                        $"Preferred declaration '{symbol.PreferredDeclarationKey}' does not belong to '{symbol.StableKey}'.");
                }
            }

            AfterDeclarationFinalizationItem?.Invoke(DeclarationFinalizationPhase.Consistency);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static SymbolDeclarationData SelectPreferredDeclaration(
        IReadOnlyList<SymbolDeclarationData> declarations,
        CancellationToken cancellationToken)
    {
        var preferred = declarations[0];
        for (var index = 1; index < declarations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = declarations[index];
            if (CompareDeclarationPreference(candidate, preferred) < 0)
            {
                preferred = candidate;
            }
        }

        return preferred;
    }

    private static int CompareDeclarationPreference(
        SymbolDeclarationData left,
        SymbolDeclarationData right)
    {
        var result = GetDeclarationRoleRank(left.Role).CompareTo(GetDeclarationRoleRank(right.Role));
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.DocumentKey, right.DocumentKey);
        if (result != 0)
        {
            return result;
        }

        result = left.SourceStart.CompareTo(right.SourceStart);
        return result != 0
            ? result
            : StringComparer.Ordinal.Compare(left.Key, right.Key);
    }

    private static int GetDeclarationRoleRank(DeclarationRole role) => role switch
    {
        DeclarationRole.PartialImplementation => 0,
        DeclarationRole.PartialDefinition => 1,
        _ => 2,
    };

    private string EnsureMethod(IMethodSymbol method)
    {
        var normalized = _canonicalizer.NormalizeLogicalMethod(method);
        var sourceProjectKey = ResolveSourceProjectKey(method);
        if (sourceProjectKey is not null &&
            TryGetSourceSymbolKey(sourceProjectKey, normalized, out var sourceKey))
        {
            return sourceKey;
        }

        var logicalKey = _canonicalizer.GetDefinitionStableKey(normalized, sourceProjectKey);
        if (sourceProjectKey is not null && normalized.IsImplicitlyDeclared)
        {
            return logicalKey;
        }

        if (_snapshot.Symbols.ContainsKey(logicalKey))
        {
            return logicalKey;
        }

        var containingKey = normalized.ContainingType is { } containingType
            ? EnsureType(containingType, sourceProjectKey)
            : null;
        var data = _canonicalizer.CreateMethod(normalized, sourceProjectKey) with
        {
            ProjectKey = ResolvePersistedProjectKey(normalized, sourceProjectKey),
            ContainingSymbolKey = containingKey,
            AsyncRole = AsyncSymbolClassifier.Classify(
                normalized,
                _currentCompilation ?? _projectStates[0].Compilation),
        };
        UpsertSymbol(data);
        return data.StableKey;
    }

    private string EnsureType(INamedTypeSymbol type, string? sourceProjectKey = null)
    {
        var normalized = type.OriginalDefinition;
        sourceProjectKey ??= ResolveSourceProjectKey(normalized);
        if (sourceProjectKey is not null &&
            TryGetSourceSymbolKey(sourceProjectKey, normalized, out var sourceKey))
        {
            return sourceKey;
        }

        var containingKey = normalized.ContainingType is { } containingType
            ? EnsureType(containingType, sourceProjectKey)
            : null;
        var data = _canonicalizer.CreateType(normalized, sourceProjectKey) with
        {
            ProjectKey = ResolvePersistedProjectKey(normalized, sourceProjectKey),
            ContainingSymbolKey = containingKey,
        };

        UpsertSymbol(data);
        return data.StableKey;
    }

    private void RegisterSourceSymbol(string projectKey, ISymbol symbol, string stableKey) =>
        _sourceSymbolKeys[new SourceSymbolLookupKey(projectKey, NormalizeSourceSymbol(symbol))] = stableKey;

    private bool TryGetSourceSymbolKey(string projectKey, ISymbol symbol, out string stableKey) =>
        _sourceSymbolKeys.TryGetValue(
            new SourceSymbolLookupKey(projectKey, NormalizeSourceSymbol(symbol)),
            out stableKey!);

    private string? ResolveSourceProjectKey(ISymbol symbol)
    {
        var normalized = NormalizeSourceSymbol(symbol);
        if (!HasSourceLocations(normalized))
        {
            return null;
        }

        if (_currentProjectState is { } currentProjectState)
        {
            if (ReferenceEquals(normalized.ContainingAssembly, currentProjectState.Compilation.Assembly))
            {
                return currentProjectState.Data.Key;
            }

            foreach (var reference in currentProjectState.Compilation.References.OfType<CompilationReference>())
            {
                var referencedAssembly = currentProjectState.Compilation.GetAssemblyOrModuleSymbol(reference);
                if (!ReferenceEquals(normalized.ContainingAssembly, referencedAssembly))
                {
                    continue;
                }

                var referencedProjectKey = GetReferencedProjectKey(currentProjectState, reference);
                if (referencedProjectKey is not null)
                {
                    return referencedProjectKey;
                }
            }
        }

        return TryGetSourceTreeProjectKey(normalized, out var projectKey) ? projectKey : null;
    }

    private string? ResolvePersistedProjectKey(ISymbol symbol, string? sourceProjectKey) =>
        sourceProjectKey is not null && !HasOnlyCompilationOnlySourceLocations(symbol)
            ? sourceProjectKey
            : null;

    private static bool HasSourceLocations(ISymbol symbol) =>
        EnumerateSourceDispositionSymbols(symbol)
            .SelectMany(sourceSymbol => sourceSymbol.Locations)
            .Any(location => location.IsInSource && location.SourceTree is not null);

    private bool HasOnlyCompilationOnlySourceLocations(ISymbol symbol)
    {
        var hasSourceLocation = false;
        foreach (var sourceSymbol in EnumerateSourceDispositionSymbols(symbol))
        {
            foreach (var location in sourceSymbol.Locations)
            {
                if (!location.IsInSource || location.SourceTree is not { } sourceTree)
                {
                    continue;
                }

                hasSourceLocation = true;
                if (!_compilationOnlySourceTrees.Contains(sourceTree))
                {
                    return false;
                }
            }
        }

        return hasSourceLocation;
    }

    private static IEnumerable<ISymbol> EnumerateSourceDispositionSymbols(ISymbol symbol)
    {
        if (symbol is not IMethodSymbol method)
        {
            yield return symbol;
            yield break;
        }

        var pending = new Stack<IMethodSymbol>();
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        pending.Push(method);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;
            if (current.PartialDefinitionPart is { } definition)
            {
                pending.Push(definition);
            }

            if (current.PartialImplementationPart is { } implementation)
            {
                pending.Push(implementation);
            }

            if (current.ReducedFrom is { } reducedFrom)
            {
                pending.Push(reducedFrom);
            }

            var original = current.OriginalDefinition;
            if (!SymbolEqualityComparer.Default.Equals(original, current))
            {
                pending.Push(original);
            }
        }
    }

    private string? GetReferencedProjectKey(
        ProjectAnalysisState currentProjectState,
        CompilationReference reference)
    {
        foreach (var projectReference in currentProjectState.Project.ProjectReferences)
        {
            var referencedProjectState = _projectStates.FirstOrDefault(projectState =>
                projectState.Project.Id == projectReference.ProjectId);
            if (referencedProjectState is not null &&
                CompilationReferenceTargets(reference, referencedProjectState.Compilation))
            {
                return referencedProjectState.Data.Key;
            }
        }

        return null;
    }

    private static bool CompilationReferenceTargets(CompilationReference reference, Compilation compilation) =>
        ReferenceEquals(reference.Compilation, compilation) ||
        reference.Compilation.SyntaxTrees.Any(referenceTree =>
            compilation.SyntaxTrees.Any(compilationTree => ReferenceEquals(referenceTree, compilationTree)));

    private bool TryGetSourceTreeProjectKey(ISymbol symbol, out string projectKey)
    {
        foreach (var location in symbol.Locations)
        {
            if (location.IsInSource &&
                location.SourceTree is { } sourceTree &&
                _sourceTreeProjectKeys.TryGetValue(sourceTree, out projectKey!))
            {
                return true;
            }
        }

        projectKey = null!;
        return false;
    }

    private ISymbol NormalizeSourceSymbol(ISymbol symbol) =>
        symbol is IMethodSymbol method ? _canonicalizer.NormalizeLogicalMethod(method) : symbol.OriginalDefinition;

    private readonly record struct SourceSymbolLookupKey(string ProjectKey, ISymbol Symbol);

    private void UpsertSymbol(SymbolData symbol)
    {
        if (!_snapshot.Symbols.TryGetValue(symbol.StableKey, out var existing))
        {
            _snapshot.Symbols[symbol.StableKey] = symbol;
            return;
        }

        _snapshot.Symbols[symbol.StableKey] = existing with
        {
            AsyncRole = existing.AsyncRole | symbol.AsyncRole,
        };
    }

    private void AddAsyncRole(string? ownerKey, AsyncRole role)
    {
        if (ownerKey is null || !_snapshot.Symbols.TryGetValue(ownerKey, out var symbol))
        {
            return;
        }

        _bodyAsyncRoles[ownerKey] = _bodyAsyncRoles.GetValueOrDefault(ownerKey) | role;
        UpsertSymbol(symbol with { AsyncRole = symbol.AsyncRole | role });
    }

    private void AddRelation(string sourceKey, string targetKey, SymbolRelationKind kind)
    {
        if (!_relationKeys.Add((sourceKey, targetKey, kind)))
        {
            return;
        }

        _snapshot.Relations.Add(new SymbolRelationData
        {
            SourceSymbolKey = sourceKey,
            TargetSymbolKey = targetKey,
            RelationKind = kind,
        });
    }

    private static DispatchKind GetDispatchKind(IMethodSymbol method) =>
        method.ContainingType?.TypeKind == TypeKind.Interface
            ? DispatchKind.Interface
            : method.IsVirtual || method.IsAbstract || method.IsOverride
                ? DispatchKind.Virtual
                : DispatchKind.Static;

    private static bool IsNameOf(InvocationExpressionSyntax invocation) =>
        invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" };

    private static bool IsInvocationTarget(ExpressionSyntax expression)
    {
        var invocation = expression.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        return invocation is not null && invocation.Expression.Span.Contains(expression.Span);
    }

    private static IEnumerable<(INamedTypeSymbol Symbol, SyntaxNode Node)> EnumerateTypeDeclarations(
        CompilationUnitSyntax root,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is INamedTypeSymbol symbol)
            {
                yield return (symbol, declaration);
            }
        }

        foreach (var declaration in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is INamedTypeSymbol symbol)
            {
                yield return (symbol, declaration);
            }
        }
    }
}
