using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration (default: Debug locally, Release on CI)")]
    readonly string Configuration = IsLocalBuild ? "Debug" : "Release";

    static readonly string[] RuntimeIdentifiers = ["win-x64", "linux-x64"];

    AbsolutePath CliProject => RootDirectory / "cli" / "TixTalk.Cli.csproj";
    AbsolutePath OutputDirectory => RootDirectory / "output";
    AbsolutePath PublishDirectory => OutputDirectory / "publish";
    AbsolutePath PackagesDirectory => OutputDirectory / "packages";

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            OutputDirectory.CreateOrCleanDirectory();
            (RootDirectory / "cli" / "bin").DeleteDirectory();
            (RootDirectory / "cli" / "obj").DeleteDirectory();
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(CliProject));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(CliProject)
                .SetConfiguration(Configuration)
                .SetNoRestore(true)
                .EnableNoLogo());
        });

    Target Publish => _ => _
        .DependsOn(Clean, Restore)
        .Executes(() =>
        {
            foreach (var rid in RuntimeIdentifiers)
            {
                DotNetPublish(s => s
                    .SetProject(CliProject)
                    .SetConfiguration("Release")
                    .SetRuntime(rid)
                    .SetSelfContained(true)
                    .SetPublishSingleFile(true)
                    .SetPublishTrimmed(true)
                    .SetOutput(PublishDirectory / rid)
                    .SetProperty("DebugType", "embedded")
                    .SetProperty("DebugSymbols", "false")
                    .SetProperty("TrimMode", "partial")
                    .EnableNoLogo());

                Serilog.Log.Information("Published {Rid} → {Path}", rid, PublishDirectory / rid);
            }
        });
}
