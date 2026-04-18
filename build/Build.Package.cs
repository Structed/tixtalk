using System.IO.Compression;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;

partial class Build
{
    [PathVariable("nfpm")]
    readonly Tool Nfpm = null!;

    Target Package => _ => _
        .DependsOn(Publish)
        .Executes(() =>
        {
            PackagesDirectory.CreateOrCleanDirectory();

            // Windows: zip
            var winZip = PackagesDirectory / "tixtalk-win-x64.zip";
            ZipFile.CreateFromDirectory(
                PublishDirectory / "win-x64",
                winZip);
            Serilog.Log.Information("Created {File}", winZip);

            // Linux: .deb and .rpm via nfpm
            var nfpmConfig = RootDirectory / "nfpm.yaml";

            Nfpm(
                $"package -f {nfpmConfig} -p deb -t {PackagesDirectory / "tixtalk-linux-amd64.deb"}",
                workingDirectory: RootDirectory);
            Serilog.Log.Information("Created .deb package");

            Nfpm(
                $"package -f {nfpmConfig} -p rpm -t {PackagesDirectory / "tixtalk-linux-amd64.rpm"}",
                workingDirectory: RootDirectory);
            Serilog.Log.Information("Created .rpm package");
        });
}
