namespace CsIndex.Core.Symbols;

public sealed record CanonicalTypeSignature(string IdentityKey, string DisplayText);

public enum CanonicalGenericPlaceholderScope
{
    Type,
    Method,
}

public sealed record CanonicalGenericPlaceholder(
    CanonicalGenericPlaceholderScope Scope,
    int Ordinal);

public sealed record CanonicalTypeSelector
{
    public string SyntaxText { get; }

    public IReadOnlyDictionary<string, CanonicalGenericPlaceholder> GenericPlaceholders { get; }

    internal CanonicalTypeSelector(
        string syntaxText,
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders)
    {
        SyntaxText = syntaxText;
        GenericPlaceholders = genericPlaceholders;
    }
}

public sealed record CanonicalParameterSignature(
    CanonicalTypeSignature Type,
    int RefKind);

public sealed record CanonicalMethodSignature(
    int GenericArity,
    IReadOnlyList<string> GenericParameterNames,
    IReadOnlyList<CanonicalParameterSignature> Parameters,
    CanonicalTypeSignature? ReturnType,
    CanonicalTypeSignature? ConversionTargetType);
