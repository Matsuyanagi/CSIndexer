namespace CsIndex.Core.Symbols;

public sealed record CanonicalTypeSignature(string IdentityKey, string DisplayText);

public sealed record CanonicalTypeSelector(
    string SyntaxText,
    IReadOnlyDictionary<string, int> GenericPlaceholders);

public sealed record CanonicalParameterSignature(
    CanonicalTypeSignature Type,
    int RefKind);

public sealed record CanonicalMethodSignature(
    int GenericArity,
    IReadOnlyList<string> GenericParameterNames,
    IReadOnlyList<CanonicalParameterSignature> Parameters,
    CanonicalTypeSignature? ReturnType,
    CanonicalTypeSignature? ConversionTargetType);
