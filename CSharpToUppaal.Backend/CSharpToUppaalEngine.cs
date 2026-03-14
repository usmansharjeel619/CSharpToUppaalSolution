using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CSharpToUppaal.Backend.Generators;
using CSharpToUppaal.Backend.Models;
using CSharpToUppaal.Backend.Parsers;
using CSharpToUppaal.Backend.Services;
using CSharpToUppaal.Backend.Verification;

namespace CSharpToUppaal.Backend
{
    public interface ICSharpToUppaalEngine
    {
        Task<Project> CreateProjectAsync(string name, string description = "");
        Task<SourceFile> AddSourceFileAsync(Project project, string filePath);
        Task<SourceFile> AddSourceCodeAsync(Project project, string code, string fileName = "Source.cs");
        Task<UppaalModel> GenerateModelAsync(Project project, string modelName = null);
        Task<VerificationSummary> VerifyModelAsync(UppaalModel model);
        Task<List<ControlFlowGraph>> GenerateCfgsAsync(Project project);
        Task<ControlFlowGraph> GenerateCfgForMethodAsync(MethodInfo method);
        Task<string> ExportUppaalModelAsync(UppaalModel model, string filePath);
        Task<string> GenerateDotGraphAsync(ControlFlowGraph cfg);

        /// <summary>
        /// Takes a UPPAAL XML string with jumbled/overlapping layout and returns
        /// a new XML string with clean, non-overlapping positions.
        /// </summary>
        string FixUppaalLayout(string uppaalXml);
    }

    public class CSharpToUppaalEngine : ICSharpToUppaalEngine
    {
        private readonly ICSharpParserService _parser;
        private readonly ICfgGeneratorService _cfgGenerator;
        private readonly IUppaalGeneratorService _uppaalGenerator;
        private readonly IUppaalVerifier _verifier;
        private readonly IUppaalLayoutService _layoutService;

        public CSharpToUppaalEngine(
            ICSharpParserService parser = null,
            ICfgGeneratorService cfgGenerator = null,
            IUppaalGeneratorService uppaalGenerator = null,
            IUppaalVerifier verifier = null,
            IUppaalLayoutService layoutService = null)
        {
            _parser = parser ?? new CSharpParser();
            _cfgGenerator = cfgGenerator ?? new CfgGeneratorService(_parser);
            _uppaalGenerator = uppaalGenerator ?? new UppaalGeneratorService(_cfgGenerator);
            _verifier = verifier ?? new UppaalVerifier();
            _layoutService = layoutService ?? new UppaalLayoutService();
        }

        public async Task<Project> CreateProjectAsync(string name, string description = "")
        {
            try
            {
                Console.WriteLine($"Creating project: {name}");
                return new Project
                {
                    Name = name,
                    Description = description,
                    CreatedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating project: {name}: {ex.Message}");
                throw;
            }
        }

        public async Task<SourceFile> AddSourceFileAsync(Project project, string filePath)
        {
            try
            {
                Console.WriteLine($"Adding source file to project {project.Name}: {filePath}");

                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"File not found: {filePath}");
                }

                var content = await File.ReadAllTextAsync(filePath);
                var parseResult = await _parser.ParseSourceFileAsync(filePath);

                var sourceFile = new SourceFile
                {
                    FilePath = filePath,
                    Content = content,
                    Language = Path.GetExtension(filePath).ToLower() == ".cs" ? "C#" : "Unknown",
                    Methods = parseResult.Methods,
                    Classes = parseResult.Classes
                };

                project.SourceFiles.Add(sourceFile);
                project.LastModified = DateTime.UtcNow;

                Console.WriteLine($"Added {parseResult.Methods.Count} methods from {filePath} to project {project.Name}");

                return sourceFile;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding source file {filePath} to project {project.Name}: {ex.Message}");
                throw;
            }
        }

        public async Task<SourceFile> AddSourceCodeAsync(Project project, string code, string fileName = "Source.cs")
        {
            try
            {
                Console.WriteLine($"Adding source code to project {project.Name}");

                var parseResult = await _parser.ParseSourceCodeAsync(code);

                var sourceFile = new SourceFile
                {
                    FilePath = fileName,
                    Content = code,
                    Language = "C#",
                    Methods = parseResult.Methods,
                    Classes = parseResult.Classes
                };

                project.SourceFiles.Add(sourceFile);
                project.LastModified = DateTime.UtcNow;

                Console.WriteLine($"Added {parseResult.Methods.Count} methods from source code to project {project.Name}");

                return sourceFile;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding source code to project {project.Name}: {ex.Message}");
                throw;
            }
        }

        public async Task<UppaalModel> GenerateModelAsync(Project project, string modelName = null)
        {
            try
            {
                modelName ??= $"{project.Name}_Model";
                Console.WriteLine($"Generating UPPAAL model for project {project.Name}");

                var model = await _uppaalGenerator.GenerateModelFromProjectAsync(project);
                model.Name = modelName;

                project.GeneratedModels.Add(model);

                Console.WriteLine($"Generated UPPAAL model {modelName} for project {project.Name}");

                return model;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating UPPAAL model for project {project.Name}: {ex.Message}");
                throw;
            }
        }

        public async Task<VerificationSummary> VerifyModelAsync(UppaalModel model)
        {
            try
            {
                Console.WriteLine($"Verifying UPPAAL model: {model.Name}");
                var summary = await _verifier.VerifyModelAsync(model);
                model.VerificationSummary = summary;
                return summary;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error verifying UPPAAL model: {model.Name}: {ex.Message}");
                throw;
            }
        }

        public async Task<List<ControlFlowGraph>> GenerateCfgsAsync(Project project)
        {
            try
            {
                Console.WriteLine($"Generating CFGs for project {project.Name}");

                var cfgs = new List<ControlFlowGraph>();

                foreach (var sourceFile in project.SourceFiles)
                {
                    foreach (var method in sourceFile.Methods)
                    {
                        var cfg = await _cfgGenerator.GenerateCfgFromMethodAsync(method);
                        cfgs.Add(cfg);
                    }
                }

                Console.WriteLine($"Generated {cfgs.Count} CFGs for project {project.Name}");

                return cfgs;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating CFGs for project {project.Name}: {ex.Message}");
                throw;
            }
        }

        public async Task<ControlFlowGraph> GenerateCfgForMethodAsync(MethodInfo method)
        {
            try
            {
                Console.WriteLine($"Generating CFG for method: {method.Name}");
                return await _cfgGenerator.GenerateCfgFromMethodAsync(method);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating CFG for method: {method.Name}: {ex.Message}");
                throw;
            }
        }

        public async Task<string> ExportUppaalModelAsync(UppaalModel model, string filePath)
        {
            try
            {
                Console.WriteLine($"Exporting UPPAAL model to: {filePath}");
                await File.WriteAllTextAsync(filePath, model.XmlContent);
                return filePath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error exporting UPPAAL model to: {filePath}: {ex.Message}");
                throw;
            }
        }

        public async Task<string> GenerateDotGraphAsync(ControlFlowGraph cfg)
        {
            var dotBuilder = new System.Text.StringBuilder();

            dotBuilder.AppendLine($"digraph {cfg.MethodName} {{");
            dotBuilder.AppendLine("  rankdir=TB;");
            dotBuilder.AppendLine("  node [shape=box, style=rounded];");

            // Nodes
            foreach (var node in cfg.Nodes)
            {
                var shape = node.Type switch
                {
                    NodeType.Entry => "ellipse",
                    NodeType.Exit => "ellipse",
                    NodeType.Condition => "diamond",
                    NodeType.Loop => "diamond",
                    _ => "box"
                };

                var color = node.Type switch
                {
                    NodeType.Entry => "green",
                    NodeType.Exit => "red",
                    NodeType.Condition => "lightblue",
                    NodeType.Loop => "yellow",
                    _ => "white"
                };

                dotBuilder.AppendLine($"  {node.Id} [label=\"{node.Label}\\n{node.Code.Replace("\"", "\\\"")}\", shape={shape}, fillcolor={color}, style=\"filled,rounded\"];");
            }

            // Edges
            foreach (var edge in cfg.Edges)
            {
                var label = !string.IsNullOrEmpty(edge.Label) ? $" [label=\"{edge.Label}\"]" : "";
                dotBuilder.AppendLine($"  {edge.FromNodeId} -> {edge.ToNodeId}{label};");
            }

            dotBuilder.AppendLine("}");

            return dotBuilder.ToString();
        }

        public string FixUppaalLayout(string uppaalXml)
        {
            try
            {
                Console.WriteLine("Fixing UPPAAL XML layout...");
                var result = _layoutService.FixLayout(uppaalXml);
                Console.WriteLine("Layout fixed successfully.");
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fixing layout: {ex.Message}");
                throw;
            }
        }
    }
}