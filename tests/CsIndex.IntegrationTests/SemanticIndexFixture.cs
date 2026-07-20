using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

public sealed class SemanticIndexFixture : IDisposable
{
    private const string MainSource = """
        using System;

        namespace Alpha
        {
            public class AClass
            {
                public void Play() { }
                public void Play(string name) { }
                public void Execute()
                {
                    Play();
                    Play("song");
                }
            }

            public class BClass
            {
                public void Play() { }
            }

            public class DistinctCaller
            {
                public void Execute(AClass a, BClass b)
                {
                    _ = new AClass();
                    a.Play();
                    b.Play();
                }
            }

            public class CommentPlayer
            {
                public void Play() { }
                public void Execute()
                {
                    // Play();
                    /* Play(); */
                }
            }

            public class LambdaPlayer
            {
                public void Play() { }
                public void Execute()
                {
                    Action action = () => Play();
                }
            }

            public class LocalPlayer
            {
                public void Play() { }
                public void Execute()
                {
                    void Local() { Play(); }
                    Local();
                }
            }

            public class Player { }

            public static class PlayerExtensions
            {
                public static void PlayExt(this Player player) { }
            }

            public class ExtensionCaller
            {
                public void Execute(Player player) => player.PlayExt();
            }

            public class Converter
            {
                public T Convert<T>(object value) => (T)value;
                public void Execute()
                {
                    Convert<int>(1);
                    Convert<string>("x");
                }
            }

            public class ReferenceKinds
            {
                public void Target() { }
                public void Execute()
                {
                    Action action = Target;
                    _ = nameof(Target);
                }
            }

            public abstract class BaseClass
            {
                public abstract void Run();
            }

            public sealed class XClass : BaseClass
            {
                public override void Run() { }
            }

            public sealed class YClass : BaseClass
            {
                public override void Run() { }
            }

            public class VirtualCaller
            {
                public void Execute(BaseClass value) => value.Run();
            }

            public class PlatformPlayer
            {
                public void Execute()
                {
        #if WINDOWS
                    PlayWindows();
        #else
                    PlayOther();
        #endif
                }

                private void PlayWindows() { }
                private void PlayOther() { }
            }
        }

        namespace GameNS
        {
            public class Player
            {
                public void Play() { }
            }
        }

        namespace PianoNS
        {
            public class Player
            {
                public void Play() { }
            }
        }
        """;

    private const string GeneratedSource = """
        namespace GeneratedCode
        {
            public class GeneratedCaller
            {
                public void Execute(GameNS.Player player) => player.Play();
            }
        }
        """;

    public SemanticIndexFixture()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "csindex-integration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        MainSourcePath = Path.Combine(RootPath, "Main.cs");
        File.WriteAllText(MainSourcePath, MainSource);
        File.WriteAllText(Path.Combine(RootPath, "GeneratedCaller.g.cs"), GeneratedSource);
        DatabasePath = Path.Combine(RootPath, ".csindex", "index.sqlite");
        BuildTask = BuildAsync();
    }

    public string RootPath { get; }
    public string MainSourcePath { get; }
    public string DatabasePath { get; }
    public Task BuildTask { get; }
    public SemanticQueryService Query => new(new SqliteIndex(DatabasePath).CreateQueryRepository());

    public string GetLocation(string text)
    {
        var source = File.ReadAllText(MainSourcePath);
        var offset = source.IndexOf(text, StringComparison.Ordinal);
        if (offset < 0)
        {
            throw new InvalidOperationException($"Text was not found: {text}");
        }

        var point = SourcePositionResolver.ResolveOffset(MainSourcePath, offset);
        return $"{MainSourcePath}:{point.Line}:{point.Column}";
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
        var options = new IndexOptions { InputPath = RootPath, ForcedMode = InputMode.Directory };
        var coordinator = AnalysisCoordinator.CreateDefault();
        var input = coordinator.ResolveInput(options);
        var fingerprint = await coordinator.BuildInputFingerprintAsync(input, options, CancellationToken.None);
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
