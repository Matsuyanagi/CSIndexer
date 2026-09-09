using System.Reflection;
using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;

namespace CsIndex.Core.Tests;

public sealed class RequestHasherTests
{
    [Fact]
    public void Build_UsesSchemaSixAndAnalysisCacheVersionFour()
    {
        var input = new ResolvedInput(
            InputMode.Directory,
            OriginalPath: "input",
            RootPath: "root",
            EntryPaths: []);
        var options = new IndexOptions
        {
            InputPath = "input",
            Configuration = "Release",
            TargetFramework = "net10.0",
            ProfileName = "normalizer-test",
            Defines = ["TRACE", "DEBUG"],
            Undefines = ["LEGACY"],
            Excludes = ["generated"],
        };

        const string currentPayload =
            """{"ToolVersion":"0.1.0","SchemaVersion":6,"AnalysisCacheVersion":4,"InputMode":"Directory","Configuration":"Release","TargetFramework":"net10.0","RuntimeIdentifier":"win-x64","ProfileName":"normalizer-test","Defines":["DEBUG","TRACE"],"Undefines":["LEGACY"],"References":[],"Excludes":["generated"],"GeneratedSource":"Physical","UnityEditor":null}""";
        const string previousPayload =
            """{"ToolVersion":"0.1.0","SchemaVersion":6,"AnalysisCacheVersion":3,"InputMode":"Directory","Configuration":"Release","TargetFramework":"net10.0","RuntimeIdentifier":"win-x64","ProfileName":"normalizer-test","Defines":["DEBUG","TRACE"],"Undefines":["LEGACY"],"References":[],"Excludes":["generated"],"GeneratedSource":"Physical","UnityEditor":null}""";
        const string previousSchemaPayload =
            """{"ToolVersion":"0.1.0","SchemaVersion":4,"AnalysisCacheVersion":3,"InputMode":"Directory","Configuration":"Release","TargetFramework":"net10.0","RuntimeIdentifier":"win-x64","ProfileName":"normalizer-test","Defines":["DEBUG","TRACE"],"Undefines":["LEGACY"],"References":[],"Excludes":["generated"],"GeneratedSource":"Physical","UnityEditor":null}""";

        var actual = RequestHasher.Build(input, options);

        Assert.Equal(6, RequestHasher.SchemaVersion);
        Assert.NotEqual(HashUtilities.Sha256(previousPayload), HashUtilities.Sha256(currentPayload));
        Assert.NotEqual(HashUtilities.Sha256(previousSchemaPayload), HashUtilities.Sha256(currentPayload));
        Assert.Equal(HashUtilities.Sha256(currentPayload), actual);

        var versionField = typeof(RequestHasher).GetField(
            "AnalysisCacheVersion",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(versionField);
        Assert.Equal(4, (int)versionField!.GetRawConstantValue()!);
    }
}
