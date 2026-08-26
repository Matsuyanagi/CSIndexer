using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

public sealed class SymbolResolutionFixture : IDisposable
{
    private const string ResolutionSource = """
        using System;

        namespace Namespace1.Namespace2
        {
            public class Class1
            {
                public class Class2
                {
                    public void Method1()
                    {
                        void Local() { }
                        void Outer()
                        {
                            void Local() { }
                            Local();
                        }

                        Action first = () => { };
                        Action second = delegate { };
                        Action third = () =>
                        {
                            Action nested = () => { };
                            nested();
                        };

                        Local();
                        Outer();
                        first();
                        second();
                        third();
                    }

                    public void Method2()
                    {
                        void Local() { }
                        Local();
                    }
                }
            }
        }

        namespace Namespace1.Namespace2.Namespace3
        {
            public class Class2
            {
                public void Method1()
                {
                    void Local() { }
                    Action first = () => { };
                    Local();
                    first();
                }
            }
        }

        namespace Company.Namespace1.Namespace2
        {
            public class Class1
            {
                public class Class2
                {
                    public void Method1()
                    {
                        void Other() { }
                        Action first = () => { };
                        Other();
                        first();
                    }
                }
            }
        }

        public class Class1
        {
            public class Class2
            {
                public void Method1() { }
            }
        }

        namespace @global
        {
            public class Class1
            {
                public class Class2
                {
                    public void Method1() { }
                }
            }
        }

        namespace CaseNamespace
        {
            public class CaseType
            {
                public class InnerType
                {
                    public void CaseTarget() { }
                }
            }
        }

        namespace SiblingScopes
        {
            public sealed class Host
            {
                public void Run()
                {
                    {
                        void Local() { }
                        Local();
                    }

                    {
                        void Local() { }
                        Local();
                    }
                }
            }
        }

        #if SECONDARY
        namespace ProfileScope
        {
            public sealed class SecondaryOnly
            {
                public void Marker() { }
            }
        }
        #endif
        """;

    private const string SignatureSource = """
        namespace Signatures
        {
            public unsafe sealed class SignatureHost
            {
                public void Root()
                {
                    void Local() { }
                    Local();
                }

                public void Root(int value)
                {
                    void Local(int localValue) { }
                    Local(value);
                }

                public void Root<T>(T value)
                {
                    void Local<U>(U localValue) { }
                    Local(value);
                }

                public void Alias(int value) { }
                public void Generic<T>(T value) { }
                public void RefValue(ref int value) { }
                public void OutValue(out int value) => value = 0;
                public void InValue(in int value) { }
                public void RefReadonly(ref readonly int value) { }
                public void NullableReference(string? value) { }
                public void NullableValue(int value) { }
                public void NullableValue(int? value) { }
                public void Arrays(int[] values, string[,] matrix) { }
                public void Pointer(int* value) { }
                public void Tuple((int Number, string Text) value) { }
                public void FunctionPointer(delegate*<int, void> callback) { }
            }
        }
        """;

    private const string SpecialSource = """
        using System;
        using System.Threading.Tasks;

        namespace Catalog
        {
            public interface IContract
            {
                void Run();
            }

            public readonly struct Number
            {
                public Number(int value) => Value = value;
                public int Value { get; }

                public static Number operator +(Number left, Number right) => left;
                public static Number operator checked +(Number left, Number right) => left;
                public static implicit operator int(Number value) => value.Value;
                public static explicit operator string(Number value) => value.Value.ToString();
            }

            public readonly struct CheckedNumber
            {
                public static explicit operator checked int(CheckedNumber value) => 0;
            }

            public sealed class SpecialHost : IContract, IDisposable
            {
                public SpecialHost(int number, string text) { }
                static SpecialHost() { }
                ~SpecialHost() { }

                public Action Factory = () => { };
                public string Name { get; init; } = string.Empty;
                public int Value { get; set; }

                public string this[int index]
                {
                    get => index.ToString();
                    set { }
                }

                public event EventHandler Changed
                {
                    add { }
                    remove { }
                }

                void IContract.Run() { }
                void IDisposable.Dispose() { }
            }

            public sealed class AsyncInitializerHost
            {
                public Func<Task> Factory = async () => await Task.Yield();
            }
        }
        """;

    private const string PartialDefinitionSource = """
        namespace Partials;

        public partial class PartialHost
        {
            partial void PartialWork();
            partial void DefinitionOnly();
        }
        """;

    private const string PartialImplementationSource = """
        namespace Partials;

        public partial class PartialHost
        {
            partial void PartialWork()
            {
                const string marker = "ImplementationMarker";
                _ = marker;
            }
        }
        """;

    private const string TopLevelSource = """
        using System;
        using System.Threading.Tasks;

        void TopLocal()
        {
            Action callback = delegate { };
            callback();
        }

        TopLocal();
        await Task.Yield();
        """;

    public SymbolResolutionFixture()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "csindex-symbol-resolution-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "Resolution.cs"), ResolutionSource);
        File.WriteAllText(Path.Combine(RootPath, "Signatures.cs"), SignatureSource);
        File.WriteAllText(Path.Combine(RootPath, "Specials.cs"), SpecialSource);
        File.WriteAllText(Path.Combine(RootPath, "PartialDefinition.cs"), PartialDefinitionSource);
        File.WriteAllText(Path.Combine(RootPath, "PartialImplementation.cs"), PartialImplementationSource);
        File.WriteAllText(Path.Combine(RootPath, "TopLevel.cs"), TopLevelSource);
        DatabasePath = Path.Combine(RootPath, ".csindex", "index.sqlite");
        BuildTask = BuildAsync();
    }

    public string RootPath { get; }

    public string DatabasePath { get; }

    public Task BuildTask { get; }

    public string? PrimaryProfileName => null;

    public string SecondaryProfileName => "secondary";

    public QueryRepository Repository => new SqliteIndex(DatabasePath).CreateQueryRepository();

    public async Task<StoredProfile> GetProfileAsync(
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        await BuildTask;
        return await Repository.GetProfileAsync(profileName, cancellationToken);
    }

    public void Dispose()
    {
        BuildTask.GetAwaiter().GetResult();
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    private async Task BuildAsync()
    {
        await BuildProfileAsync(SecondaryProfileName, ["SECONDARY"]);
        await BuildProfileAsync(PrimaryProfileName, []);
    }

    private async Task BuildProfileAsync(string? profileName, IReadOnlyList<string> defines)
    {
        var options = new IndexOptions
        {
            InputPath = RootPath,
            ForcedMode = InputMode.Directory,
            ProfileName = profileName,
            Defines = defines,
        };
        var coordinator = AnalysisCoordinator.CreateDefault();
        var input = coordinator.ResolveInput(options);
        var fingerprint = await coordinator.BuildInputFingerprintAsync(
            input,
            options,
            CancellationToken.None);
        var requestHash = RequestHasher.Build(input, options);
        var result = await coordinator.AnalyzeAsync(
            input,
            options,
            fingerprint,
            requestHash,
            CancellationToken.None);
        await new SqliteIndex(DatabasePath).SaveAsync(result.Snapshot);
    }
}
