namespace CsIndex.Core.Symbols;

internal static class SymbolPathDisplayShortener
{
    private static readonly string[] ConversionMarkers =
    [
        "conversion:implicit:",
        "conversion:explicit:",
        "checked-conversion:implicit:",
        "checked-conversion:explicit:",
    ];

    private static readonly string[] InterfaceMarkers =
    [
        "explicit:",
        "get:",
        "set:",
        "init:",
        "add:",
        "remove:",
    ];

    public static string ShortenExecutablePath(string identityPath, string displayPath)
    {
        if (string.IsNullOrEmpty(identityPath) && string.IsNullOrEmpty(displayPath))
        {
            return displayPath;
        }

        if (string.IsNullOrEmpty(identityPath) || string.IsNullOrEmpty(displayPath))
        {
            throw CreateMismatch("empty executable path", identityPath, displayPath);
        }

        try
        {
            var identities = SplitSegments(identityPath);
            var displays = SplitSegments(displayPath);
            RequireSameCount(identities, displays, "executable segment", identityPath, displayPath);

            return string.Join(
                '.',
                identities.Zip(displays, ShortenSegment));
        }
        catch (StructuralMismatchException exception)
        {
            throw CreateMismatch(exception.Category, identityPath, displayPath);
        }
    }

    private static IReadOnlyList<string> SplitSegments(string value) =>
        SplitTopLevel(value, '.', requireNonEmpty: true);

    private static string ShortenSegment(string identity, string display)
    {
        var identityParts = ParseSegment(identity);
        var displayParts = ParseSegment(display);
        if (identityParts.HasParameters != displayParts.HasParameters)
        {
            ThrowMismatch("parameter list");
        }

        var displayPrefix = displayParts.Prefix;
        if (identityParts.Prefix.StartsWith("[", StringComparison.Ordinal) !=
            displayPrefix.StartsWith("[", StringComparison.Ordinal))
        {
            ThrowMismatch("special payload");
        }

        var shortenedPrefix = displayPrefix.StartsWith("[", StringComparison.Ordinal)
            ? ShortenSpecialPayload(identityParts.Prefix, displayPrefix)
            : displayPrefix;

        if (!identityParts.HasParameters)
        {
            return shortenedPrefix;
        }

        return $"{shortenedPrefix}({ShortenParameterList(identityParts.Parameters, displayParts.Parameters)})";
    }

    private static string ShortenParameterList(string identity, string display)
    {
        var identities = SplitTopLevel(identity, ',', requireNonEmpty: false);
        var displays = SplitTopLevel(display, ',', requireNonEmpty: false);
        RequireSameCount(identities, displays, "parameter", identity, display);
        if (identities.Count == 0)
        {
            return string.Empty;
        }

        var shortened = new string[identities.Count];
        for (var index = 0; index < identities.Count; index++)
        {
            var identityParameter = StripParameterPrefix(identities[index], out var identityPrefix);
            var displayParameter = StripParameterPrefix(displays[index], out var displayPrefix);
            if (!string.Equals(identityPrefix, displayPrefix, StringComparison.Ordinal))
            {
                ThrowMismatch("parameter ref-kind");
            }

            if (string.IsNullOrEmpty(identityParameter) || string.IsNullOrEmpty(displayParameter))
            {
                ThrowMismatch("parameter type");
            }

            shortened[index] = $"{displayPrefix}{ShortenType(identityParameter, displayParameter)}";
        }

        return string.Join(',', shortened);
    }

    private static string ShortenSpecialPayload(string identity, string display)
    {
        var identityPayload = ParseBracketed(identity);
        var displayPayload = ParseBracketed(display);
        var identityMarker = GetSpecialMarker(identityPayload.Content, out var identityMarkerKind);
        var displayMarker = GetSpecialMarker(displayPayload.Content, out var displayMarkerKind);
        if (identityMarkerKind != displayMarkerKind ||
            !string.Equals(identityMarker, displayMarker, StringComparison.Ordinal))
        {
            ThrowMismatch("special payload");
        }

        if (identityMarkerKind == SpecialMarkerKind.None)
        {
            return display;
        }

        try
        {
            if (identityMarkerKind == SpecialMarkerKind.Conversion)
            {
                var identityType = identityPayload.Content[identityMarker!.Length..];
                var displayType = displayPayload.Content[displayMarker!.Length..];
                return $"[{displayMarker}{ShortenType(identityType, displayType)}]{displayPayload.Suffix}";
            }

            if (!identityPayload.Content.Contains("::", StringComparison.Ordinal))
            {
                return display;
            }

            var identityMemberDot = FindLastTopLevelDot(identityPayload.Content);
            var displayMemberDot = FindLastTopLevelDot(displayPayload.Content);
            if (identityMemberDot <= identityMarker!.Length || displayMemberDot <= displayMarker!.Length)
            {
                ThrowMismatch("special payload");
            }

            var interfaceIdentityType = identityPayload.Content[
                identityMarker!.Length..identityMemberDot];
            var interfaceDisplayType = displayPayload.Content[
                displayMarker!.Length..displayMemberDot];
            var displayMember = displayPayload.Content[(displayMemberDot + 1)..];
            return $"[{displayMarker}{ShortenType(interfaceIdentityType, interfaceDisplayType)}.{displayMember}]{displayPayload.Suffix}";
        }
        catch (StructuralMismatchException exception) when (exception.Category == "type")
        {
            throw new StructuralMismatchException("special payload");
        }
    }

    private static string ShortenType(string identity, string display)
    {
        try
        {
            return SymbolSignatureCanonicalizer.FormatTypeDisplay(
                new CanonicalTypeSignature(identity, display),
                shortNames: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new StructuralMismatchException("type");
        }
    }

    private static IReadOnlyList<string> SplitTopLevel(
        string value,
        char separator,
        bool requireNonEmpty)
    {
        if (value.Length == 0)
        {
            return [];
        }

        var result = new List<string>();
        var start = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var parenthesisDepth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth == 0)
                    {
                        ThrowMismatch("delimiter nesting");
                    }

                    bracketDepth--;
                    break;
                case '(':
                    parenthesisDepth++;
                    break;
                case ')':
                    if (parenthesisDepth == 0)
                    {
                        ThrowMismatch("delimiter nesting");
                    }

                    parenthesisDepth--;
                    break;
                case '<' when bracketDepth == 0:
                    angleDepth++;
                    break;
                case '>' when bracketDepth == 0:
                    if (angleDepth == 0)
                    {
                        ThrowMismatch("delimiter nesting");
                    }

                    angleDepth--;
                    break;
                default:
                    if (value[index] == separator &&
                        angleDepth == 0 &&
                        bracketDepth == 0 &&
                        parenthesisDepth == 0)
                    {
                        var segment = value[start..index];
                        if (requireNonEmpty && segment.Length == 0)
                        {
                            ThrowMismatch("delimiter nesting");
                        }

                        result.Add(segment);
                        start = index + 1;
                    }

                    break;
            }
        }

        if (angleDepth != 0 || bracketDepth != 0 || parenthesisDepth != 0)
        {
            ThrowMismatch("delimiter nesting");
        }

        var tail = value[start..];
        if (requireNonEmpty && tail.Length == 0)
        {
            ThrowMismatch("delimiter nesting");
        }

        result.Add(tail);
        return result;
    }

    private static SegmentParts ParseSegment(string value)
    {
        var parameterStart = -1;
        var angleDepth = 0;
        var bracketDepth = 0;
        var parenthesisDepth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth == 0)
                    {
                        ThrowMismatch("delimiter nesting");
                    }

                    bracketDepth--;
                    break;
                case '<' when bracketDepth == 0:
                    angleDepth++;
                    break;
                case '>' when bracketDepth == 0:
                    if (angleDepth == 0)
                    {
                        ThrowMismatch("delimiter nesting");
                    }

                    angleDepth--;
                    break;
                case '(' when bracketDepth == 0 && angleDepth == 0 && parenthesisDepth == 0:
                    parameterStart = index;
                    parenthesisDepth = 1;
                    break;
                case '(':
                    parenthesisDepth++;
                    break;
                case ')':
                    if (parenthesisDepth == 0)
                    {
                        ThrowMismatch("delimiter nesting");
                    }

                    parenthesisDepth--;
                    if (parenthesisDepth == 0 && index != value.Length - 1)
                    {
                        ThrowMismatch("delimiter nesting");
                    }

                    break;
            }
        }

        if (angleDepth != 0 || bracketDepth != 0 || parenthesisDepth != 0)
        {
            ThrowMismatch("delimiter nesting");
        }

        return parameterStart < 0
            ? new SegmentParts(value, string.Empty, HasParameters: false)
            : new SegmentParts(
                value[..parameterStart],
                value[(parameterStart + 1)..^1],
                HasParameters: true);
    }

    private static BracketedPayload ParseBracketed(string value)
    {
        if (value.Length == 0 || value[0] != '[')
        {
            ThrowMismatch("special payload");
        }

        var depth = 1;
        for (var index = 1; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    if (depth == 0)
                    {
                        return new BracketedPayload(
                            value[1..index],
                            value[(index + 1)..]);
                    }

                    break;
            }
        }

        ThrowMismatch("delimiter nesting");
        return default;
    }

    private static int FindLastTopLevelDot(string value)
    {
        var last = -1;
        var angleDepth = 0;
        var bracketDepth = 0;
        var parenthesisDepth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth == 0)
                    {
                        ThrowMismatch("delimiter nesting");
                    }

                    bracketDepth--;
                    break;
                case '(':
                    parenthesisDepth++;
                    break;
                case ')':
                    if (parenthesisDepth == 0)
                    {
                        ThrowMismatch("delimiter nesting");
                    }

                    parenthesisDepth--;
                    break;
                case '<' when bracketDepth == 0:
                    angleDepth++;
                    break;
                case '>' when bracketDepth == 0:
                    if (angleDepth == 0)
                    {
                        ThrowMismatch("delimiter nesting");
                    }

                    angleDepth--;
                    break;
                case '.' when angleDepth == 0 && bracketDepth == 0 && parenthesisDepth == 0:
                    last = index;
                    break;
            }
        }

        if (angleDepth != 0 || bracketDepth != 0 || parenthesisDepth != 0)
        {
            ThrowMismatch("delimiter nesting");
        }

        return last;
    }

    private static string StripParameterPrefix(string parameter, out string prefix)
    {
        foreach (var candidate in new[] { "ref readonly ", "ref ", "out ", "in " })
        {
            if (parameter.StartsWith(candidate, StringComparison.Ordinal))
            {
                prefix = candidate;
                return parameter[candidate.Length..];
            }
        }

        prefix = string.Empty;
        return parameter;
    }

    private static string? GetSpecialMarker(string payload, out SpecialMarkerKind kind)
    {
        foreach (var marker in ConversionMarkers)
        {
            if (payload.StartsWith(marker, StringComparison.Ordinal))
            {
                kind = SpecialMarkerKind.Conversion;
                return marker;
            }
        }

        foreach (var marker in InterfaceMarkers)
        {
            if (payload.StartsWith(marker, StringComparison.Ordinal))
            {
                kind = SpecialMarkerKind.Interface;
                return marker;
            }
        }

        kind = SpecialMarkerKind.None;
        return null;
    }

    private static void RequireSameCount(
        IReadOnlyCollection<string> identities,
        IReadOnlyCollection<string> displays,
        string category,
        string identity,
        string display)
    {
        if (identities.Count != displays.Count)
        {
            throw new StructuralMismatchException($"{category} count");
        }
    }

    private static InvalidOperationException CreateMismatch(
        string category,
        string identity,
        string display) =>
        new($"Symbol executable path mismatch ({category}): identity '{identity}', display '{display}'.");

    private static void ThrowMismatch(string category) =>
        throw new StructuralMismatchException(category);

    private sealed record SegmentParts(string Prefix, string Parameters, bool HasParameters);

    private readonly record struct BracketedPayload(string Content, string Suffix);

    private enum SpecialMarkerKind
    {
        None,
        Conversion,
        Interface,
    }

    private sealed class StructuralMismatchException(string category) : Exception
    {
        public string Category { get; } = category;
    }
}
