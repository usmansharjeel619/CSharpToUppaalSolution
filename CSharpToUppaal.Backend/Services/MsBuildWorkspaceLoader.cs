using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynProject = Microsoft.CodeAnalysis.Project;

namespace CSharpToUppaal.Backend.Services
{
    public sealed class WorkspaceProjectDescriptor
    {
        internal WorkspaceProjectDescriptor(RoslynProject project)
            : this(project.Name, project.FilePath ?? string.Empty, project)
        {
        }

        private WorkspaceProjectDescriptor(string name, string filePath, RoslynProject? roslynProject)
        {
            RoslynProject = roslynProject;
            Name = name;
            FilePath = filePath;
            TargetFramework = ReadFirstTargetFramework(FilePath);
            OutputType = ReadProperty(FilePath, "OutputType");
            IsTestProject = Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || Name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
                || (File.Exists(FilePath) && File.ReadAllText(FilePath).Contains("<IsTestProject>true</IsTestProject>", StringComparison.OrdinalIgnoreCase));
        }

        internal static WorkspaceProjectDescriptor FromProjectFile(string filePath, string? name = null) =>
            new(name ?? Path.GetFileNameWithoutExtension(filePath), filePath, null);

        public string Name { get; }
        public string FilePath { get; }
        public string TargetFramework { get; }
        public string OutputType { get; }
        public bool IsTestProject { get; }
        public RoslynProject? RoslynProject { get; }
        public bool IsExecutable => string.Equals(OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(OutputType, "WinExe", StringComparison.OrdinalIgnoreCase);
        public string DisplayName => $"{Name}  |  {TargetFrameworkOrDefault()}  |  {FilePath}";

        private string TargetFrameworkOrDefault() => string.IsNullOrWhiteSpace(TargetFramework) ? "default target" : TargetFramework;

        private static string ReadFirstTargetFramework(string projectPath)
        {
            var tfm = ReadProperty(projectPath, "TargetFramework");
            if (!string.IsNullOrWhiteSpace(tfm)) return tfm.Trim();
            var tfms = ReadProperty(projectPath, "TargetFrameworks");
            return tfms.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        }

        private static string ReadProperty(string projectPath, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath)) return string.Empty;
            var content = File.ReadAllText(projectPath);
            var match = Regex.Match(content, $"<{propertyName}>(.*?)</{propertyName}>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }
    }

    public sealed class WorkspaceRestoreResult
    {
        public bool Succeeded { get; init; }
        public int ExitCode { get; init; }
        public string Output { get; init; } = string.Empty;
    }

    public sealed class SolutionDiscoveryResult
    {
        public required string SolutionPath { get; init; }
        public required IReadOnlyList<WorkspaceProjectDescriptor> Projects { get; init; }
    }

    public sealed class WorkspaceLoadContext : IDisposable
    {
        private readonly MSBuildWorkspace _workspace;

        internal WorkspaceLoadContext(MSBuildWorkspace workspace, string sourcePath, bool isSolution, IReadOnlyList<WorkspaceProjectDescriptor> projects, IReadOnlyList<string> diagnostics, bool requiresRestore)
        {
            _workspace = workspace;
            SourcePath = sourcePath;
            IsSolution = isSolution;
            Projects = projects;
            LoadDiagnostics = diagnostics;
            RequiresRestore = requiresRestore;
        }

        public string SourcePath { get; }
        public bool IsSolution { get; }
        public IReadOnlyList<WorkspaceProjectDescriptor> Projects { get; }
        public IReadOnlyList<string> LoadDiagnostics { get; }
        public bool RequiresRestore { get; }
        public string Configuration => "Debug";
        public string Platform => "AnyCPU";

        public void Dispose() => _workspace.Dispose();
    }

    public interface IMsBuildWorkspaceLoader
    {
        Task<WorkspaceLoadContext> LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default);
        Task<SolutionDiscoveryResult> DiscoverSolutionAsync(string solutionPath, CancellationToken cancellationToken = default);
        Task<WorkspaceLoadContext> LoadProjectAsync(string projectPath, CancellationToken cancellationToken = default);
        Task<WorkspaceRestoreResult> RestoreAsync(string solutionOrProjectPath, CancellationToken cancellationToken = default);
    }

    /// <summary>Loads saved C# projects through MSBuild so project/NuGet references resolve exactly as they do in the IDE.</summary>
    public sealed class MsBuildWorkspaceLoader : IMsBuildWorkspaceLoader
    {
        private static readonly object LocatorGate = new();

        public Task<WorkspaceLoadContext> LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default) =>
            LoadAsync(solutionPath, isSolution: true, cancellationToken);

        public Task<SolutionDiscoveryResult> DiscoverSolutionAsync(string solutionPath, CancellationToken cancellationToken = default) =>
            Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(solutionPath) || !File.Exists(solutionPath))
                    throw new FileNotFoundException("Solution file was not found.", solutionPath);
                var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
                var projects = File.ReadLines(solutionPath)
                    .Select(line => Regex.Match(line, "^Project\\(.*?\\)\\s*=\\s*\\\"(?<name>[^\\\"]+)\\\",\\s*\\\"(?<path>[^\\\"]+\\.csproj)\\\"", RegexOptions.IgnoreCase))
                    .Where(match => match.Success)
                    .Select(match => new
                    {
                        Name = match.Groups["name"].Value,
                        FilePath = Path.GetFullPath(Path.Combine(solutionDirectory, match.Groups["path"].Value))
                    })
                    .Where(project => File.Exists(project.FilePath))
                    .Select(project => WorkspaceProjectDescriptor.FromProjectFile(project.FilePath, project.Name))
                    .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (projects.Count == 0) throw new InvalidOperationException("No C# projects were found in the selected solution.");
                return new SolutionDiscoveryResult { SolutionPath = solutionPath, Projects = projects };
            }, cancellationToken);

        public Task<WorkspaceLoadContext> LoadProjectAsync(string projectPath, CancellationToken cancellationToken = default) =>
            LoadAsync(projectPath, isSolution: false, cancellationToken);

        public async Task<WorkspaceRestoreResult> RestoreAsync(string solutionOrProjectPath, CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"restore \"{solutionOrProjectPath}\" --nologo",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new WorkspaceRestoreResult
            {
                Succeeded = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = (await standardOutput.ConfigureAwait(false)) + (await standardError.ConfigureAwait(false))
            };
        }

        private static async Task<WorkspaceLoadContext> LoadAsync(string path, bool isSolution, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("Solution or project file was not found.", path);
            EnsureMsBuildRegistered();
            var diagnostics = new List<string>();
            var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
            {
                ["Configuration"] = "Debug",
                ["Platform"] = "AnyCPU"
            });
            workspace.RegisterWorkspaceFailedHandler(args => diagnostics.Add($"{args.Diagnostic.Kind}: {args.Diagnostic.Message}"));

            try
            {
                if (isSolution)
                    await workspace.OpenSolutionAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
                else
                    await workspace.OpenProjectAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);

                var projects = workspace.CurrentSolution.Projects
                    .Where(project => string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal))
                    .Select(project => new WorkspaceProjectDescriptor(project))
                    .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (projects.Count == 0) throw new InvalidOperationException("No C# projects were found in the selected source.");

                var needsRestore = projects.Any(project => RequiresRestore(project.FilePath));
                return new WorkspaceLoadContext(workspace, path, isSolution, projects, diagnostics, needsRestore);
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }

        private static void EnsureMsBuildRegistered()
        {
            lock (LocatorGate)
            {
                if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
            }
        }

        private static bool RequiresRestore(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath)) return false;
            var source = File.ReadAllText(projectPath);
            var usesPackageOrSdk = source.Contains("<PackageReference", StringComparison.OrdinalIgnoreCase)
                                   || source.Contains("<Project Sdk=", StringComparison.OrdinalIgnoreCase);
            return usesPackageOrSdk && !File.Exists(Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json"));
        }
    }
}
