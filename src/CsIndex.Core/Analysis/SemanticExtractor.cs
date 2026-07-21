using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CsIndex.Core.Analysis;

public sealed class SemanticExtractor(ProjectFingerprintBuilder projectFingerprintBuilder)
{
    private readonly Dictionary<ISymbol, string> _sourceSymbolKeys = new(SymbolEqualityComparer.Default);
    private readonly HashSet<(string Source, string Target, SymbolRelationKind Kind)> _relationKeys = [];
    private readonly List<ProjectAnalysisState> _projectStates = [];
    private IndexSnapshot _snapshot = null!;
    private SymbolCanonicalizer _canonicalizer = null!;
    private Compilation? _currentCompilation;

    public async Task ExtractAsync(
        IReadOnlyList<Project> projects,
        IndexSnapshot snapshot,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        _snapshot = snapshot;
        _canonicalizer = new SymbolCanonicalizer(snapshot.Profile);
        _projectStates.Clear();
        _sourceSymbolKeys.Clear();
        _relationKeys.Clear();

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Compilation could not be created: {project.Name}");
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

            var projectKey = project.FilePath is null
                ? $"directory:{project.Name}"
                : PathNormalizer.Normalize(project.FilePath);
            var projectData = new ProjectData
            {
                Key = projectKey,
                Name = project.Name,
                AssemblyName = project.AssemblyName,
                ProjectPath = project.FilePath is null ? null : PathNormalizer.Normalize(project.FilePath),
                TargetFramework = snapshot.Profile.TargetFramework,
                Fingerprint = await projectFingerprintBuilder.BuildAsync(project, cancellationToken),
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
            await ExtractProjectDocumentsAndDeclarationsAsync(state, cancellationToken);
        }

        foreach (var state in _projectStates)
        {
            await ExtractProjectFactsAsync(state, cancellationToken);
        }

        AsyncInvolvementPropagator.Apply(snapshot);
    }

    private async Task ExtractProjectDocumentsAndDeclarationsAsync(
        ProjectAnalysisState projectState,
        CancellationToken cancellationToken)
    {
        var projectRoot = projectState.Project.FilePath is null
            ? _snapshot.InputRoot
            : Path.GetDirectoryName(projectState.Project.FilePath)!;
        foreach (var document in projectState.Project.Documents
                     .OrderBy(document => document.FilePath ?? document.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.FilePath is null || !File.Exists(document.FilePath) ||
                !document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                PathNormalizer.HasPathSegment(projectRoot, document.FilePath, "obj"))
            {
                _snapshot.DocumentsExcluded++;
                continue;
            }

            var path = PathNormalizer.Normalize(document.FilePath);
            var text = await document.GetTextAsync(cancellationToken);
            var generated = GeneratedCodeDetector.Detect(path, text);
            var contentHash = HashUtilities.Sha256(text.ToString());
            var documentKey = $"{projectState.Data.Key}|{path}";
            var documentData = new DocumentData
            {
                Key = documentKey,
                ProjectKey = projectState.Data.Key,
                NormalizedPath = path,
                ContentHash = contentHash,
                IsGenerated = generated.IsGenerated,
                GenerationKind = generated.Kind,
            };
            _snapshot.Documents.Add(documentData);
            var documentState = new DocumentAnalysisState { Document = document, Data = documentData };
            projectState.Documents[document.Id] = documentState;

            var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (root is null || semanticModel is null)
            {
                _snapshot.Warnings.Add($"Syntax or semantic model could not be loaded: {path}");
                continue;
            }

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
                _sourceSymbolKeys[symbol.OriginalDefinition] = data.StableKey;
            }

            foreach (var methodNode in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(methodNode, cancellationToken) is not IMethodSymbol method)
                {
                    continue;
                }

                var containingKey = EnsureType(method.ContainingType);
                var data = _canonicalizer.CreateMethod(
                    method.OriginalDefinition,
                    actualTarget: false,
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
                _sourceSymbolKeys[method.OriginalDefinition] = data.StableKey;
                documentState.MethodOwners[methodNode.SpanStart] = data.StableKey;
            }

            foreach (var localNode in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(localNode, cancellationToken) is not IMethodSymbol method)
                {
                    continue;
                }

                var containingKey = documentState.FindOwner(localNode, includeSelf: false) ??
                                    EnsureType(method.ContainingType);
                var data = _canonicalizer.CreateMethod(
                    method,
                    actualTarget: false,
                    projectState.Data.Key,
                    documentKey,
                    localNode.SpanStart,
                    localNode.Span.Length,
                    generated.IsGenerated,
                    containingKey) with
                {
                    AsyncRole = AsyncSymbolClassifier.Classify(method, projectState.Compilation),
                };
                UpsertSymbol(data);
                _sourceSymbolKeys[method] = data.StableKey;
                documentState.LocalFunctionOwners[localNode.SpanStart] = data.StableKey;
            }

            CreateInitializerOwners(root, documentState, projectState.Data.Key);
            CreateTopLevelOwner(root, documentState, projectState.Data.Key);
            CreateLambdaOwners(root, documentState, semanticModel, projectState.Data.Key, cancellationToken);

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
                    invocationSyntax.SpanStart,
                    invocationSyntax.Span.Length,
                    documentState.Data.Key,
                    invocation.Instance?.Type,
                    AsyncOperationClassifier.ClassifyInvocation(invocation));
                continue;
            }

            if (operation is IDynamicInvocationOperation dynamicInvocation)
            {
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
                    UnresolvedName = invocationSyntax.Expression.ToString(),
                    ReceiverTypeKey = SymbolCanonicalizer.FormatType(dynamicInvocation.Operation.Type),
                });
                continue;
            }

            AddUnresolvedCall(
                callerKey,
                invocationSyntax.Expression,
                invocationSyntax.SpanStart,
                invocationSyntax.Span.Length,
                documentState.Data.Key,
                model,
                projectState,
                cancellationToken);
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
                    creationSyntax.SpanStart,
                    creationSyntax.Span.Length,
                    documentState.Data.Key,
                    creation.Type);
            }
            else
            {
                AddUnresolvedCall(
                    callerKey,
                    creationSyntax,
                    creationSyntax.SpanStart,
                    creationSyntax.Span.Length,
                    documentState.Data.Key,
                    model,
                    projectState,
                    cancellationToken);
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
            var definitionKey = EnsureMethod(_canonicalizer.NormalizeMethod(method), actualTarget: false);
            var unique = $"{callerKey}|{expression.SpanStart}|{definitionKey}|{kind}";
            if (!seen.Add(unique))
            {
                continue;
            }

            _snapshot.Calls.Add(new CallData
            {
                CallerSymbolKey = callerKey,
                CalleeSymbolKey = EnsureMethod(method, actualTarget: true),
                CalleeDefinitionKey = definitionKey,
                ReferenceKind = kind,
                DispatchKind = GetDispatchKind(method),
                ResolutionStatus = ResolutionStatus.Resolved,
                ResolutionReason = ResolutionReason.None,
                DocumentKey = documentState.Data.Key,
                SourceStart = expression.SpanStart,
                SourceLength = expression.Span.Length,
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

            var sourceKey = EnsureMethod(method, actualTarget: false);
            if (method.OverriddenMethod is { } overridden)
            {
                AddRelation(
                    sourceKey,
                    EnsureMethod(_canonicalizer.NormalizeMethod(overridden), actualTarget: false),
                    SymbolRelationKind.Overrides);
            }

            foreach (var implemented in method.ExplicitInterfaceImplementations)
            {
                AddRelation(
                    sourceKey,
                    EnsureMethod(_canonicalizer.NormalizeMethod(implemented), actualTarget: false),
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
                                EnsureMethod(_canonicalizer.NormalizeMethod(member), actualTarget: false),
                                SymbolRelationKind.ImplicitlyImplements);
                        }
                    }
                }
            }

            if (method.PartialDefinitionPart is { } definition)
            {
                AddRelation(
                    sourceKey,
                    EnsureMethod(definition, actualTarget: false),
                    SymbolRelationKind.PartialDefinition);
            }

            if (method.PartialImplementationPart is { } implementation)
            {
                AddRelation(
                    sourceKey,
                    EnsureMethod(implementation, actualTarget: false),
                    SymbolRelationKind.PartialImplementation);
            }
        }
    }

    private void CreateInitializerOwners(
        CompilationUnitSyntax root,
        DocumentAnalysisState documentState,
        string projectKey)
    {
        foreach (var initializer in root.DescendantNodes().OfType<EqualsValueClauseSyntax>())
        {
            var declaration = initializer.Parent;
            var typeDeclaration = initializer.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            if (typeDeclaration is null ||
                initializer.Ancestors().Any(ancestor => ancestor is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax))
            {
                continue;
            }

            var containingTypeKey = _snapshot.Symbols.Values.FirstOrDefault(symbol =>
                symbol.SourceDocumentKey == documentState.Data.Key &&
                symbol.Kind == IndexedSymbolKind.Type &&
                symbol.SourceStart == typeDeclaration.SpanStart)?.StableKey;
            if (containingTypeKey is null)
            {
                continue;
            }

            var name = declaration switch
            {
                VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                _ => "initializer",
            };
            var isStatic = initializer.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault()
                ?.Modifiers.Any(modifier => modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)) == true;
            var display = declaration is PropertyDeclarationSyntax
                ? $"{name}::<initializer>"
                : isStatic ? "<static-initializer>" : "<instance-initializer>";
            var stableKey = _canonicalizer.GetSyntheticStableKey(
                containingTypeKey,
                documentState.Data.NormalizedPath,
                display,
                initializer.SpanStart,
                initializer.Span.Length,
                documentState.Data.ContentHash);
            UpsertSymbol(new SymbolData
            {
                StableKey = stableKey,
                ProjectKey = projectKey,
                Kind = IndexedSymbolKind.Initializer,
                Name = display,
                NamespaceName = string.Empty,
                FullyQualifiedName = display,
                DisplayName = display,
                ContainingSymbolKey = containingTypeKey,
                ParameterCount = 0,
                SourceDocumentKey = documentState.Data.Key,
                SourceStart = initializer.SpanStart,
                SourceLength = initializer.Span.Length,
                IsStatic = isStatic,
                IsGenerated = documentState.Data.IsGenerated,
            });
            documentState.InitializerOwners[initializer.SpanStart] = stableKey;
        }
    }

    private void CreateTopLevelOwner(
        CompilationUnitSyntax root,
        DocumentAnalysisState documentState,
        string projectKey)
    {
        var statements = root.Members.OfType<GlobalStatementSyntax>().ToArray();
        if (statements.Length == 0)
        {
            return;
        }

        var start = statements[0].SpanStart;
        var end = statements[^1].Span.End;
        var stableKey = _canonicalizer.GetSyntheticStableKey(
            $"project:{projectKey}",
            documentState.Data.NormalizedPath,
            "top-level-statements",
            start,
            end - start,
            documentState.Data.ContentHash);
        UpsertSymbol(new SymbolData
        {
            StableKey = stableKey,
            ProjectKey = projectKey,
            Kind = IndexedSymbolKind.TopLevelStatements,
            Name = "<top-level-statements>",
            NamespaceName = string.Empty,
            FullyQualifiedName = "Program::<top-level-statements>",
            DisplayName = "Program::<top-level-statements>",
            ParameterCount = 0,
            SourceDocumentKey = documentState.Data.Key,
            SourceStart = start,
            SourceLength = end - start,
            IsGenerated = documentState.Data.IsGenerated,
        });
        documentState.TopLevelOwner = stableKey;
    }

    private void CreateLambdaOwners(
        CompilationUnitSyntax root,
        DocumentAnalysisState documentState,
        SemanticModel semanticModel,
        string projectKey,
        CancellationToken cancellationToken)
    {
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var lambda in root.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
        {
            var outerOwner = documentState.FindOwner(lambda, includeSelf: false);
            if (outerOwner is null)
            {
                continue;
            }

            counters.TryGetValue(outerOwner, out var count);
            count++;
            counters[outerOwner] = count;
            var displayOwner = _snapshot.Symbols.TryGetValue(outerOwner, out var ownerSymbol)
                ? ownerSymbol.DisplayName
                : outerOwner;
            var display = $"{displayOwner}::<lambda#{count}>";
            var stableKey = _canonicalizer.GetSyntheticStableKey(
                outerOwner,
                documentState.Data.NormalizedPath,
                lambda.Kind().ToString(),
                lambda.SpanStart,
                lambda.Span.Length,
                documentState.Data.ContentHash);
            var operation = semanticModel.GetOperation(lambda, cancellationToken) as IAnonymousFunctionOperation;
            UpsertSymbol(new SymbolData
            {
                StableKey = stableKey,
                ProjectKey = projectKey,
                Kind = IndexedSymbolKind.Lambda,
                Name = $"<lambda#{count}>",
                NamespaceName = ownerSymbol?.NamespaceName ?? string.Empty,
                TypeSimpleName = ownerSymbol?.TypeSimpleName,
                TypeMetadataName = ownerSymbol?.TypeMetadataName,
                FullyQualifiedName = display,
                DisplayName = display,
                ContainingSymbolKey = outerOwner,
                ParameterCount = operation?.Symbol.Parameters.Length ?? 0,
                MethodKind = operation is null ? null : (int)operation.Symbol.MethodKind,
                SourceDocumentKey = documentState.Data.Key,
                SourceStart = lambda.SpanStart,
                SourceLength = lambda.Span.Length,
                IsGenerated = documentState.Data.IsGenerated,
                AsyncRole = operation is null
                    ? AsyncRole.None
                    : AsyncSymbolClassifier.Classify(operation.Symbol, semanticModel.Compilation),
                Parameters = operation?.Symbol.Parameters.Select(parameter => new MethodParameterData
                {
                    Ordinal = parameter.Ordinal,
                    Name = parameter.Name,
                    TypeKey = SymbolCanonicalizer.FormatType(parameter.Type),
                    RefKind = (int)parameter.RefKind,
                    IsOptional = parameter.IsOptional,
                }).ToArray() ?? [],
            });
            documentState.LambdaOwners[lambda.SpanStart] = stableKey;
        }
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

            foreach (var candidate in methods)
            {
                _snapshot.Calls.Add(new CallData
                {
                    CallerSymbolKey = callerKey,
                    CalleeSymbolKey = EnsureMethod(candidate, actualTarget: true),
                    CalleeDefinitionKey = EnsureMethod(_canonicalizer.NormalizeMethod(candidate), actualTarget: false),
                    ReferenceKind = ReferenceKind.NameOf,
                    DispatchKind = GetDispatchKind(candidate),
                    ResolutionStatus = ResolutionStatus.Resolved,
                    ResolutionReason = ResolutionReason.None,
                    DocumentKey = documentState.Data.Key,
                    SourceStart = expression.SpanStart,
                    SourceLength = expression.Span.Length,
                });
            }

            return;
        }
    }

    private void AddResolvedCall(
        string callerKey,
        IMethodSymbol target,
        ReferenceKind referenceKind,
        int sourceStart,
        int sourceLength,
        string documentKey,
        ITypeSymbol? receiverType,
        AsyncUsageKind asyncUsageKind = AsyncUsageKind.None)
    {
        _snapshot.Calls.Add(new CallData
        {
            CallerSymbolKey = callerKey,
            CalleeSymbolKey = EnsureMethod(target, actualTarget: true),
            CalleeDefinitionKey = EnsureMethod(_canonicalizer.NormalizeMethod(target), actualTarget: false),
            ReferenceKind = referenceKind,
            DispatchKind = GetDispatchKind(target),
            ResolutionStatus = ResolutionStatus.Resolved,
            ResolutionReason = ResolutionReason.None,
            AsyncUsageKind = asyncUsageKind,
            DocumentKey = documentKey,
            SourceStart = sourceStart,
            SourceLength = sourceLength,
            ReceiverTypeKey = SymbolCanonicalizer.FormatType(receiverType),
        });
    }

    private void AddUnresolvedCall(
        string callerKey,
        ExpressionSyntax expression,
        int sourceStart,
        int sourceLength,
        string documentKey,
        SemanticModel model,
        ProjectAnalysisState projectState,
        CancellationToken cancellationToken)
    {
        var symbolInfo = model.GetSymbolInfo(expression, cancellationToken);
        var candidates = symbolInfo.CandidateSymbols.OfType<IMethodSymbol>()
            .Select(method => EnsureMethod(_canonicalizer.NormalizeMethod(method), actualTarget: false))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var ambiguous = candidates.Length > 0;
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
            SourceStart = sourceStart,
            SourceLength = sourceLength,
            UnresolvedName = expression.ToString(),
            CandidateSymbolKeys = candidates,
        });
    }

    private string EnsureMethod(IMethodSymbol method, bool actualTarget)
    {
        var normalized = actualTarget ? method : _canonicalizer.NormalizeMethod(method);
        if (!actualTarget && _sourceSymbolKeys.TryGetValue(normalized, out var sourceKey))
        {
            return sourceKey;
        }

        var data = _canonicalizer.CreateMethod(normalized, actualTarget) with
        {
            AsyncRole = AsyncSymbolClassifier.Classify(
                normalized,
                _currentCompilation ?? _projectStates[0].Compilation),
        };
        EnsureType(normalized.ContainingType);
        UpsertSymbol(data);
        return data.StableKey;
    }

    private string EnsureType(INamedTypeSymbol type)
    {
        var normalized = type.OriginalDefinition;
        if (_sourceSymbolKeys.TryGetValue(normalized, out var sourceKey))
        {
            return sourceKey;
        }

        var data = _canonicalizer.CreateType(normalized);
        if (normalized.ContainingType is not null)
        {
            EnsureType(normalized.ContainingType);
        }

        UpsertSymbol(data);
        return data.StableKey;
    }

    private void UpsertSymbol(SymbolData symbol)
    {
        if (!_snapshot.Symbols.TryGetValue(symbol.StableKey, out var existing))
        {
            _snapshot.Symbols[symbol.StableKey] = symbol;
            return;
        }

        var preferred = existing.SourceDocumentKey is null && symbol.SourceDocumentKey is not null
            ? symbol
            : existing;
        _snapshot.Symbols[symbol.StableKey] = preferred with
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
