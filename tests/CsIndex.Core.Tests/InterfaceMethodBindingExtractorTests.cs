using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;

namespace CsIndex.Core.Tests;

public sealed class InterfaceMethodBindingExtractorTests
{
    private const string Source = """
        public interface IPlayable { void Play(); }
        public interface IDefaultPlayable { void Play() { } }

        public abstract class AbstractPlayer : IPlayable
        {
            public abstract void Play();
        }

        public class BasePlayer
        {
            public virtual void Play() { }
        }

        public class InheritedPlayer : BasePlayer, IPlayable { }

        public class ProInheritedPlayer : InheritedPlayer
        {
            public override void Play() { }
        }

        public class ExplicitPlayer : IPlayable
        {
            void IPlayable.Play() { }
        }

        public class DefaultPlayer : IDefaultPlayable { }

        public partial class PartialPlayer : IPlayable
        {
            public void Play() { }
        }
        public partial class PartialPlayer { }
        """;

    [Fact]
    public async Task AnalyzeAsync_RecordsContextualInterfaceMethodBindings()
    {
        var snapshot = await AnalyzeAsync(Source);
        var bindings = snapshot.InterfaceMethodBindings;

        AssertBinding(snapshot, bindings, "AbstractPlayer", "IPlayable", "AbstractPlayer");
        AssertBinding(snapshot, bindings, "InheritedPlayer", "IPlayable", "BasePlayer");
        AssertBinding(snapshot, bindings, "ProInheritedPlayer", "IPlayable", "ProInheritedPlayer");
        AssertBinding(snapshot, bindings, "ExplicitPlayer", "IPlayable", "ExplicitPlayer");
        AssertBinding(snapshot, bindings, "DefaultPlayer", "IDefaultPlayable", "IDefaultPlayable");
        Assert.Single(bindings, binding =>
            snapshot.Symbols[binding.ImplementingTypeKey].TypeSimpleName == "PartialPlayer" &&
            snapshot.Symbols[binding.InterfaceMethodKey].TypeSimpleName == "IPlayable");
    }

    [Fact]
    public async Task AnalyzeAsync_PersistsTypeKindForClassesAndInterfaces()
    {
        var snapshot = await AnalyzeAsync(Source);
        Assert.Equal(
            (int)Microsoft.CodeAnalysis.TypeKind.Interface,
            snapshot.Symbols.Values.Single(symbol => symbol.TypeSimpleName == "IPlayable" &&
                                                     symbol.Kind == IndexedSymbolKind.Type).TypeKind);
        Assert.Equal(
            (int)Microsoft.CodeAnalysis.TypeKind.Class,
            snapshot.Symbols.Values.Single(symbol => symbol.TypeSimpleName == "InheritedPlayer" &&
                                                     symbol.Kind == IndexedSymbolKind.Type).TypeKind);
    }

    [Fact]
    public async Task AnalyzeAsync_ReportsUnresolvedInterfaceMethodBinding()
    {
        const string source = """
            public interface IPlayable { void Play(); }
            public class MissingPlayer : IPlayable { }
            """;

        var snapshot = await AnalyzeAsync(source);

        Assert.Single(snapshot.Diagnostics, diagnostic =>
            diagnostic.Contains("MissingPlayer", StringComparison.Ordinal) &&
            diagnostic.Contains("IPlayable.Play()", StringComparison.Ordinal));
        Assert.Empty(snapshot.InterfaceMethodBindings);
    }

    private static void AssertBinding(
        IndexSnapshot snapshot,
        IEnumerable<InterfaceMethodBindingData> bindings,
        string implementingTypeName,
        string interfaceTypeName,
        string implementationTypeName)
    {
        Assert.Single(bindings, binding =>
            snapshot.Symbols[binding.ImplementingTypeKey].TypeSimpleName == implementingTypeName &&
            snapshot.Symbols[binding.InterfaceMethodKey].TypeSimpleName == interfaceTypeName &&
            snapshot.Symbols[binding.ImplementationMethodKey].TypeSimpleName == implementationTypeName);
    }

    private static async Task<IndexSnapshot> AnalyzeAsync(string source)
    {
        using var temporary = new TempDirectory();
        temporary.Write("Source.cs", source);
        var options = new IndexOptions
        {
            InputPath = temporary.Path,
            ForcedMode = InputMode.Directory,
        };
        var coordinator = AnalysisCoordinator.CreateDefault();
        var input = coordinator.ResolveInput(options);
        var fingerprint = await coordinator.BuildInputFingerprintAsync(
            input,
            options,
            TestContext.Current.CancellationToken);
        var result = await coordinator.AnalyzeAsync(
            input,
            options,
            fingerprint,
            RequestHasher.Build(input, options),
            TestContext.Current.CancellationToken);
        return result.Snapshot;
    }
}
