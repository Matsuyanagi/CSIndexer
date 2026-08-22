using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Storage;
using Microsoft.Data.Sqlite;

namespace CsIndex.IntegrationTests;

public sealed class SemanticIndexFixture : IDisposable
{
    private const string MainSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        namespace Cysharp.Threading.Tasks
        {
            public readonly struct UniTask<T> { }
            public readonly struct UniTaskVoid { }
        }

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

            public interface IPlayable { void Play(); }
            public interface IAdvancedPlayable : IPlayable { }

            public class Pianist : IPlayable
            {
                public virtual void Play() => PianistBody();
                private void PianistBody() { }
            }

            public class ProPianist : Pianist
            {
                public override void Play() => ProPianistBody();
                private void ProPianistBody() { }
            }

            public class Game : IPlayable
            {
                public void Play() => GameBody();
                private void GameBody() { }
            }

            public class Baseball
            {
                public void Play() => BaseballBody();
                private void BaseballBody() { }
            }

            public class InheritedBase
            {
                public virtual void Play() => BaseBody();
                private void BaseBody() { }
            }

            public class D1 : InheritedBase, IAdvancedPlayable { }

            public class D2 : D1
            {
                public override void Play() => D2Body();
                private void D2Body() { }
            }

            public class OtherBranch : InheritedBase
            {
                public override void Play() => OtherBody();
                private void OtherBody() { }
            }

            public class OverrideSearchCaller
            {
                public void Execute(
                    IPlayable contract,
                    Pianist pianist,
                    ProPianist professional,
                    Game game,
                    Baseball baseball,
                    D1 d1,
                    D2 d2,
                    OtherBranch other)
                {
                    contract.Play();
                    pianist.Play();
                    professional.Play();
                    game.Play();
                    baseball.Play();
                    d1.Play();
                    d2.Play();
                    other.Play();
                }
            }

            public class HidingPlayer : D1
            {
                public new void Play() => HiddenBody();
                private void HiddenBody() { }
            }

            public class HidingBase
            {
                public void Select(int value) { }
            }

            public class HidingMiddle : HidingBase
            {
                public void Select(string value) { }
            }

            public class HidingLeaf : HidingMiddle { }

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

            public class LocalBase
            {
                public virtual void Local() => InheritedLocalBody();
                private void InheritedLocalBody() { }
            }

            public class LocalPlayer : LocalBase
            {
                public void Play() { }
                public void Execute()
                {
                    void Local() { Play(); }
                    Local();
                }
            }

            public class DescendantCallees
            {
                public void Execute()
                {
                    DirectCall();
                    Action outer = () =>
                    {
                        OuterLambdaCall();
                        _ = new InnerCreated();
                        Action nested = () =>
                        {
                            FirstNestedLambdaCall();
                            void Local()
                            {
                                Action deeplyNested = () => SecondNestedLambdaCall();
                            }
                        };
                    };
                }

                private void DirectCall() { }
                private void OuterLambdaCall() { }
                private void FirstNestedLambdaCall() { }
                private void SecondNestedLambdaCall() { }

                private sealed class InnerCreated { }
            }

            public class AsyncPlayer
            {
                public async Task ExecuteAsync() => await Task.Yield();

                public void Sync() { }

                public void WithAsyncLambda()
                {
                    Func<Task> action = async () => await Task.Yield();
                }
            }

            public class AsyncStatusCases
            {
                public async Task DeclaredTaskAsync() => await Task.Yield();
                public Task<int> TaskResult() => Task.FromResult(1);
                public ValueTask ValueTaskResult() => default;
                public Cysharp.Threading.Tasks.UniTask<int> UniTaskResult() => default;
                public Cysharp.Threading.Tasks.UniTaskVoid FireAndForget() => default;

                public async IAsyncEnumerable<int> StreamAsync()
                {
                    await Task.Yield();
                    yield return 1;
                }

                public void SyncSuffixAsync() { }

                public void OuterWithAsyncLambda()
                {
                    Func<Task> nested = async () => await Task.Yield();
                    _ = nested;
                }

                public void OuterWithAsyncLocal()
                {
                    async Task NestedLocalAsync() => await Task.Yield();
                    Func<Task> nested = NestedLocalAsync;
                    _ = nested;
                }
            }

            public class AsyncOverrideBase
            {
                public virtual void Run() { }
            }

            public class AsyncOverrideDerived : AsyncOverrideBase
            {
                public override async void Run() => await Task.Yield();
            }

            public sealed class FunctionKinds
            {
                public FunctionKinds() { }

                public int Value { get; set; }

                public void Regular() { }

                public void LocalOwner()
                {
                    void Local() { }
                    Local();
                }

                public static FunctionKinds operator +(FunctionKinds left, FunctionKinds right) => left;

                public static implicit operator int(FunctionKinds value) => value.Value;
            }

            public class AsyncGraph
            {
                public void Start() => Middle();
                public void Middle() => EndAsync();
                public async Task EndAsync() { await Task.Yield(); }
                public async Task SelfAsync() { await Task.Yield(); }
                public void Unreachable() { }

                public void CycleStart() => CycleMiddle();
                public void CycleMiddle()
                {
                    CycleStart();
                    EndAsync();
                }

                public void EqualStart()
                {
                    EqualLeft();
                    EqualRight();
                }

                public void EqualLeft() => EqualEndAsync();
                public void EqualRight() => EqualEndAsync();
                public async Task EqualEndAsync() { await Task.Yield(); }

                public void ALambdaPathOwner()
                {
                    Action action = () => Start();
                    _ = action;
                }

                public void ZLambdaPathOwner()
                {
                    Action action = () => Start();
                    _ = action;
                }
            }

            public sealed class GraphCreated
            {
                public GraphCreated() { }
            }

            public class CallerGraph
            {
                public void DirectTarget() { }
                public void DirectCaller() => DirectTarget();

                public void DepthTarget() { }
                public void DepthOne() => DepthTarget();
                public void DepthTwo() => DepthOne();
                public void DepthThree() => DepthTwo();
                public void DepthFour() => DepthThree();

                public void RecursiveTarget() { }
                public void RecursiveRight()
                {
                    RecursiveLeft();
                    RecursiveTarget();
                }

                public void RecursiveLeft() => RecursiveRight();

                public void BoundaryTarget() { }
                public void BoundaryLeft()
                {
                    BoundaryTarget();
                    BoundaryRight();
                }

                public void BoundaryRight()
                {
                    BoundaryTarget();
                    BoundaryLeft();
                }

                public void LambdaTarget() { }
                public void LambdaOwner()
                {
                    Action action = () => LambdaTarget();
                    _ = action;
                }

                public void LambdaTreeOwner()
                {
                    Action action = () => { };
                    _ = action;
                }

                public void LambdaRootCaller() { }

                public void OrderingTarget() { }
                public void ZCaller() => OrderingTarget();
                public void ACaller() => OrderingTarget();

                public void Root() { }
                public void A() => Root();
                public void Z() => Root();
                public void A2() => Z();
                public void Z2() => A();

                public void MetadataTarget() { }
                public void MetadataCaller()
                {
                    MetadataTarget();
                    object value = new object();
                    _ = value.ToString();
                }

                public static void FilterTarget() { }
                public void AllowedFilterCaller() => FilterTarget();

                public void ObjectCreator() => _ = new GraphCreated();
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

        namespace Tokyo
        {
            public class Gamer
            {
                public void Play() { PrintVar("required"); }
                public void Play(string name) { PrintVar(name); }
                private static void PrintVar(string value) { }
            }

            public class SourceBodies
            {
                public void Match()
                {
                    // comment-only-marker
                    var literal = "/*keep*/ //keep";
                    PrintVar("required");
                    _ = literal;
                }

                public void Excluded()
                {
                    PrintVar("required");
                    BlockedMarker();
                }

                public void Other() { PrintVar("other"); }

                private static void PrintVar(string value) { }
                private static void BlockedMarker() { }
            }

            public class LambdaSearch
            {
                public static Action Field = () => LambdaMarker("field");
                public static Action Property { get; } = () => LambdaMarker("property");
                public static event Action? Changed = () => LambdaMarker("event");

                public void Function()
                {
                    Action first = () => LambdaMarker("first");
                    Action outer = () =>
                    {
                        Action nested = () => LambdaMarker("nested");
                        _ = nested;
                    };
                    _ = first;
                    _ = outer;
                }

                private static void LambdaMarker(string value) { }
            }

            public class MetadataCaller
            {
                public void CallMetadata()
                {
                    object value = new object();
                    _ = value.ToString();
                }
            }

        #if SECONDARY
            public class SecondaryOnly
            {
                public void Play() { }
            }
        #endif
        }

        namespace Fukuoka
        {
            public class Gamer
            {
                public void Pray() { }
            }
        }

        namespace System
        {
            public static class SourceFilterCaller
            {
                public static void Call() => Alpha.CallerGraph.FilterTarget();
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

    private const string LosslessSource = """"
        namespace Alpha
        {
            public sealed class LosslessSource
            {
                public string LiteralControls() => """
        first	line
        second
        """;
            }
        }
        """";

    public SemanticIndexFixture()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "csindex-integration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        MainSourcePath = Path.Combine(RootPath, "Main.cs");
        File.WriteAllText(MainSourcePath, MainSource);
        File.WriteAllText(Path.Combine(RootPath, "GeneratedCaller.g.cs"), GeneratedSource);
        File.WriteAllText(Path.Combine(RootPath, "LosslessSource.cs"), LosslessSource);
        DatabasePath = Path.Combine(RootPath, ".csindex", "index.sqlite");
        BuildTask = BuildAsync();
    }

    public string RootPath { get; }
    public string MainSourcePath { get; }
    public string DatabasePath { get; }
    public Task BuildTask { get; }
    public string? PrimaryProfileName => null;
    public string SecondaryProfileName => "secondary";
    public SemanticQueryService Query => new(new SqliteIndex(DatabasePath).CreateQueryRepository());
    public QueryRepository Repository => new SqliteIndex(DatabasePath).CreateQueryRepository();

    public Task ReindexPrimaryProfileAsync() => BuildProfileAsync(PrimaryProfileName, []);

    public async Task<StoredSymbol> GetStoredSymbolAsync(
        string displayName,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        var repository = Repository;
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        var symbols = await repository.FindExecutableSymbolsAsync(
            profile.Id,
            sourceOnly: false,
            cancellationToken);
        var symbol = symbols.Single(value => value.DisplayName == displayName);
        var preferredDeclarations = await repository.GetPreferredDeclarationsAsync(
            profile.Id,
            [symbol.Id],
            includeSourceText: true,
            cancellationToken);
        return symbol with { PreferredDeclaration = preferredDeclarations.SingleOrDefault() };
    }

    public async Task SetAsyncNextSymbolIdAsync(
        string displayName,
        long? asyncNextSymbolId,
        string? profileName = null,
        bool allowMissingTarget = false,
        CancellationToken cancellationToken = default)
    {
        var profile = await Repository.GetProfileAsync(profileName, cancellationToken);
        var symbol = await GetStoredSymbolAsync(displayName, profileName, cancellationToken);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (allowMissingTarget)
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        command.CommandText = """
            UPDATE symbols
            SET async_next_symbol_id = $async_next_symbol_id
            WHERE analysis_profile_id = $profile_id
              AND id = $symbol_id;
            """;
        command.Parameters.AddWithValue("$async_next_symbol_id", (object?)asyncNextSymbolId ?? DBNull.Value);
        command.Parameters.AddWithValue("$profile_id", profile.Id);
        command.Parameters.AddWithValue("$symbol_id", symbol.Id);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException(
                $"Expected one symbol named '{displayName}' in profile '{profile.Name}', but updated {updated}.");
        }
    }

    public async Task SetAsyncPathStateAsync(
        long symbolId,
        AsyncRole asyncRole,
        int? asyncInvolvementDepth,
        long? asyncNextSymbolId,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await Repository.GetProfileAsync(profileName, cancellationToken);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE symbols
            SET async_role = $async_role,
                async_involvement_depth = $async_involvement_depth,
                async_next_symbol_id = $async_next_symbol_id
            WHERE analysis_profile_id = $profile_id
              AND id = $symbol_id;
            """;
        command.Parameters.AddWithValue("$async_role", (int)asyncRole);
        command.Parameters.AddWithValue("$async_involvement_depth", (object?)asyncInvolvementDepth ?? DBNull.Value);
        command.Parameters.AddWithValue("$async_next_symbol_id", (object?)asyncNextSymbolId ?? DBNull.Value);
        command.Parameters.AddWithValue("$profile_id", profile.Id);
        command.Parameters.AddWithValue("$symbol_id", symbolId);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException(
                $"Expected one symbol ID '{symbolId}' in profile '{profile.Name}', but updated {updated}.");
        }
    }

    public async Task SetSymbolSourceDefinitionAsync(
        long symbolId,
        string? normalizedSource,
        string? documentPath,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await Repository.GetProfileAsync(profileName, cancellationToken);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE symbols
            SET preferred_declaration_id = CASE
                WHEN $normalized_source IS NULL OR $document_path IS NULL THEN NULL
                ELSE (
                    SELECT d.id
                    FROM symbol_declarations d
                    JOIN documents doc ON doc.id = d.document_id
                    WHERE d.symbol_id = $symbol_id
                      AND doc.normalized_path = $document_path
                    ORDER BY
                        CASE d.declaration_role
                            WHEN 2 THEN 0
                            WHEN 3 THEN 1
                            WHEN 1 THEN 2
                            ELSE 3
                        END,
                        d.id
                    LIMIT 1)
            END
            WHERE analysis_profile_id = $profile_id
              AND id = $symbol_id;
            """;
        command.Parameters.AddWithValue("$normalized_source", (object?)normalizedSource ?? DBNull.Value);
        command.Parameters.AddWithValue("$document_path", (object?)documentPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$profile_id", profile.Id);
        command.Parameters.AddWithValue("$symbol_id", symbolId);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException(
                $"Expected one symbol ID '{symbolId}' in profile '{profile.Name}', but updated {updated}.");
        }
    }

    public async Task AddResolvedCallAsync(
        string callerDisplayName,
        string calleeDisplayName,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await Repository.GetProfileAsync(profileName, cancellationToken);
        var caller = await GetStoredSymbolAsync(callerDisplayName, profileName, cancellationToken);
        var callee = await GetStoredSymbolAsync(calleeDisplayName, profileName, cancellationToken);
        var calleeDeclaration = callee.PreferredDeclaration ?? throw new InvalidOperationException(
            $"Expected callee '{calleeDisplayName}' in profile '{profile.Name}' to have a preferred declaration.");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO calls(
                analysis_profile_id, caller_symbol_id, callee_symbol_id, callee_definition_id,
                reference_kind, dispatch_kind, resolution_status, resolution_reason, async_usage_kind,
                document_id, source_start, source_length, unresolved_name, receiver_type_key)
            VALUES(
                $profile_id, $caller_id, $callee_id, $callee_id,
                $reference_kind, $dispatch_kind, $resolution_status, $resolution_reason, $async_usage_kind,
                $document_id, $source_start, 1, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$profile_id", profile.Id);
        command.Parameters.AddWithValue("$caller_id", caller.Id);
        command.Parameters.AddWithValue("$callee_id", callee.Id);
        command.Parameters.AddWithValue("$document_id", calleeDeclaration.DocumentId);
        command.Parameters.AddWithValue("$source_start", calleeDeclaration.SourceStart);
        command.Parameters.AddWithValue("$reference_kind", (int)ReferenceKind.Invocation);
        command.Parameters.AddWithValue("$dispatch_kind", (int)DispatchKind.Static);
        command.Parameters.AddWithValue("$resolution_status", (int)ResolutionStatus.Resolved);
        command.Parameters.AddWithValue("$resolution_reason", (int)ResolutionReason.None);
        command.Parameters.AddWithValue("$async_usage_kind", (int)AsyncUsageKind.None);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        if (inserted != 1)
        {
            throw new InvalidOperationException(
                $"Expected one call from '{callerDisplayName}' to '{calleeDisplayName}' in profile '{profile.Name}', but inserted {inserted}.");
        }
    }

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
