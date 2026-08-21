using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CSharpToUppaal.Backend.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynProject = Microsoft.CodeAnalysis.Project;

namespace CSharpToUppaal.Backend.Services
{
    public interface ICSharpSemanticAnalyzer
    {
        Task<CSharpSemanticAnalysisResult> AnalyzeSourceCodeAsync(string code, string fileName = "Source.cs");
        Task<CSharpSemanticAnalysisResult> AnalyzeProjectAsync(RoslynProject project);
    }

    /// <summary>Semantic data for either one source file or every document in a Roslyn project.</summary>
    public class CSharpSemanticAnalysisResult
    {
        public string OriginalCode { get; set; } = string.Empty;
        public string NormalizedCode { get; set; } = string.Empty;
        public string FileName { get; set; } = "Source.cs";
        public bool WasWrapped { get; set; }
        public bool IsWorkspaceProject { get; set; }
        public string ProjectName { get; set; } = string.Empty;
        public string TargetFramework { get; set; } = string.Empty;
        public Compilation Compilation { get; set; } = null!;
        public SyntaxTree SyntaxTree { get; set; } = null!;
        public SemanticModel SemanticModel { get; set; } = null!;
        public CompilationUnitSyntax Root { get; set; } = null!;
        public List<CompilationUnitSyntax> Roots { get; set; } = new();
        public Dictionary<SyntaxTree, SemanticModel> SemanticModels { get; set; } = new();
        public List<FunctionDescriptor> Functions { get; set; } = new();
        public Dictionary<string, MethodDeclarationSyntax> MethodDeclarationsById { get; set; } = new();
        public Dictionary<string, IMethodSymbol> MethodSymbolsById { get; set; } = new();
        public List<TranslationAssumption> Assumptions { get; set; } = new();
        public List<string> Diagnostics { get; set; } = new();
        public List<string> SyntaxDiagnostics { get; set; } = new();

        public bool HasSyntaxErrors => SyntaxDiagnostics.Count > 0;

        public SemanticModel GetSemanticModel(SyntaxNode node) =>
            SemanticModels.TryGetValue(node.SyntaxTree, out var model) ? model : SemanticModel;

        public IEnumerable<VariableDeclaratorSyntax> GetVariableDeclarators() =>
            Roots.SelectMany(root => root.DescendantNodes().OfType<VariableDeclaratorSyntax>());

        public IReadOnlyList<FunctionDescriptor> ResolveClosure(IEnumerable<FunctionSelection> selections)
        {
            var functionById = Functions.ToDictionary(f => f.Id);
            var selected = selections.Where(s => s.IsSelected).ToList();
            if (selected.Count == 0 && Functions.Count > 0)
            {
                var main = Functions.FirstOrDefault(f => f.Name == "Main");
                selected.Add(new FunctionSelection { FunctionId = main?.Id ?? Functions[0].Id, IsSelected = true, Mode = FunctionModelingMode.ExplicitAutomaton });
            }

            var included = new Dictionary<string, FunctionDescriptor>();
            var stack = new Stack<string>(selected.Select(s => s.FunctionId).Where(functionById.ContainsKey));
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!functionById.TryGetValue(id, out var function) || included.ContainsKey(id)) continue;
                included[id] = function;
                if (selected.FirstOrDefault(s => s.FunctionId == id)?.Mode == FunctionModelingMode.Stub) continue;
                foreach (var callId in function.DirectCallIds)
                    if (!included.ContainsKey(callId)) stack.Push(callId);
            }

            return included.Values.OrderBy(f => f.SourceFile, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.LineNumber)
                .ThenBy(f => f.Signature, StringComparer.Ordinal).ToList();
        }
    }

    public class CSharpSemanticAnalyzer : ICSharpSemanticAnalyzer
    {
        private static readonly SymbolDisplayFormat FunctionIdFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;

        public async Task<CSharpSemanticAnalysisResult> AnalyzeSourceCodeAsync(string code, string fileName = "Source.cs")
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("C# source code is empty.", nameof(code));
            var normalized = NormalizeLooseMethods(code, out var wasWrapped);
            var tree = CSharpSyntaxTree.ParseText(normalized, path: fileName);
            var root = await tree.GetRootAsync().ConfigureAwait(false) as CompilationUnitSyntax
                       ?? throw new InvalidOperationException("Unable to parse C# compilation unit.");
            var compilation = CSharpCompilation.Create("CSharpToUppaalInput", new[] { tree }, BuildMetadataReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithNullableContextOptions(NullableContextOptions.Enable));
            var result = CreateResult(compilation, new[] { (tree, root) }, fileName);
            result.OriginalCode = code;
            result.NormalizedCode = normalized;
            result.WasWrapped = wasWrapped;
            if (wasWrapped)
                result.Assumptions.Add(new TranslationAssumption { Severity = AssumptionSeverity.Info, Category = "Input", Message = "Loose method snippet was wrapped in a synthetic class for Roslyn semantic analysis.", SymbolName = "__InputWrapper", IsUserEditable = false });
            CollectDiagnostics(result, new[] { tree });
            ExtractFunctions(result);
            ExtractCalls(result);
            return result;
        }

        public async Task<CSharpSemanticAnalysisResult> AnalyzeProjectAsync(RoslynProject project)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (!string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal)) throw new ArgumentException("The selected project is not a C# project.", nameof(project));

            // Project analyzers/source generators can take minutes on enterprise solutions
            // and do not contribute to the C# -> UPPAAL semantic mapping. Keep references,
            // documents and compiler semantics, but exclude analyzers from this read-only pass.
            project = project.WithAnalyzerReferences(Array.Empty<AnalyzerReference>());
            var compilation = await Task.Run(() => project.GetCompilationAsync()).ConfigureAwait(false)
                              ?? throw new InvalidOperationException($"Unable to create a compilation for '{project.Name}'.");
            var roots = new List<(SyntaxTree Tree, CompilationUnitSyntax Root)>();
            foreach (var document in project.Documents.Where(document => document.SourceCodeKind == SourceCodeKind.Regular))
            {
                var tree = await document.GetSyntaxTreeAsync().ConfigureAwait(false);
                var root = await document.GetSyntaxRootAsync().ConfigureAwait(false) as CompilationUnitSyntax;
                if (tree != null && root != null) roots.Add((tree, root));
            }
            if (roots.Count == 0) throw new InvalidOperationException($"The selected project '{project.Name}' has no C# source documents.");

            var result = CreateResult(compilation, roots, roots[0].Tree.FilePath ?? project.FilePath ?? "Source.cs");
            result.IsWorkspaceProject = true;
            result.ProjectName = project.Name;
            result.TargetFramework = GetTargetFramework(project);
            var includeFullCompilationDiagnostics = roots.Count <= 200;
            CollectDiagnostics(result, roots.Select(entry => entry.Tree), includeFullCompilationDiagnostics);
            if (!includeFullCompilationDiagnostics)
            {
                result.Assumptions.Add(new TranslationAssumption
                {
                    Severity = AssumptionSeverity.Info,
                    Category = "Performance",
                    Message = $"Full compiler diagnostics were deferred for this {roots.Count}-file project to keep loading responsive. Syntax diagnostics and semantic call resolution are still performed.",
                    IsUserEditable = false
                });
            }
            ExtractFunctions(result);
            ExtractCalls(result);
            return result;
        }

        public static string ToFunctionId(IMethodSymbol symbol)
        {
            var stable = symbol.ReducedFrom ?? symbol.OriginalDefinition ?? symbol;
            return stable.ToDisplayString(FunctionIdFormat);
        }

        private static CSharpSemanticAnalysisResult CreateResult(Compilation compilation, IEnumerable<(SyntaxTree Tree, CompilationUnitSyntax Root)> trees, string fileName)
        {
            var entries = trees.ToList();
            var result = new CSharpSemanticAnalysisResult
            {
                FileName = fileName,
                Compilation = compilation,
                SyntaxTree = entries[0].Tree,
                SemanticModel = compilation.GetSemanticModel(entries[0].Tree),
                Root = entries[0].Root,
                Roots = entries.Select(entry => entry.Root).ToList()
            };
            foreach (var entry in entries) result.SemanticModels[entry.Tree] = compilation.GetSemanticModel(entry.Tree);
            return result;
        }

        private static string GetTargetFramework(RoslynProject project) =>
            project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.TargetFramework", out var framework) ? framework : string.Empty;

        private static void CollectDiagnostics(CSharpSemanticAnalysisResult result, IEnumerable<SyntaxTree> trees, bool includeCompilationDiagnostics = true)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var diagnostic in trees.SelectMany(tree => tree.GetDiagnostics()).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                var text = diagnostic.ToString();
                if (seen.Add(text)) { result.SyntaxDiagnostics.Add(text); result.Diagnostics.Add(text); }
            }
            if (!includeCompilationDiagnostics) return;
            foreach (var diagnostic in result.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                var text = diagnostic.ToString();
                if (seen.Add(text)) result.Diagnostics.Add(text);
            }
        }

        private static void ExtractFunctions(CSharpSemanticAnalysisResult result)
        {
            foreach (var root in result.Roots)
            {
                var model = result.GetSemanticModel(root);
                foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(methodDecl);
                    if (symbol == null) continue;
                    var id = ToFunctionId(symbol);
                    var lineSpan = methodDecl.GetLocation().GetLineSpan();
                    var descriptor = new FunctionDescriptor
                    {
                        Id = id, Name = symbol.Name, DisplayName = $"{symbol.ContainingType?.Name ?? "Global"}.{symbol.Name}",
                        Signature = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        Namespace = symbol.ContainingNamespace?.IsGlobalNamespace == true ? string.Empty : symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                        ContainingType = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? string.Empty,
                        ReturnType = symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        IsPublic = symbol.DeclaredAccessibility == Accessibility.Public, IsStatic = symbol.IsStatic,
                        IsAsync = methodDecl.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AsyncKeyword)), IsSynthetic = result.WasWrapped,
                        LineNumber = lineSpan.StartLinePosition.Line + 1, SourceFile = methodDecl.SyntaxTree.FilePath ?? result.FileName,
                        Body = methodDecl.Body?.ToString() ?? methodDecl.ExpressionBody?.ToString() ?? string.Empty
                    };
                    foreach (var parameter in symbol.Parameters)
                        descriptor.Parameters.Add(new ParameterInfo { Name = parameter.Name, Type = parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), HasDefaultValue = parameter.HasExplicitDefaultValue, DefaultValue = parameter.HasExplicitDefaultValue ? parameter.ExplicitDefaultValue?.ToString() ?? string.Empty : string.Empty });
                    result.Functions.Add(descriptor);
                    result.MethodDeclarationsById[id] = methodDecl;
                    result.MethodSymbolsById[id] = symbol;
                }
            }
        }

        private static void ExtractCalls(CSharpSemanticAnalysisResult result)
        {
            var knownIds = result.Functions.Select(function => function.Id).ToHashSet(StringComparer.Ordinal);
            var descriptorById = result.Functions.ToDictionary(function => function.Id);
            foreach (var entry in result.MethodDeclarationsById)
            {
                var caller = descriptorById[entry.Key];
                var model = result.GetSemanticModel(entry.Value);
                foreach (var invocation in entry.Value.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var info = model.GetSymbolInfo(invocation);
                    var method = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    if (method == null) { AddUnresolved(caller, invocation.Expression.ToString()); continue; }
                    var callId = ToFunctionId(method);
                    if (knownIds.Contains(callId))
                    {
                        if (!caller.DirectCallIds.Contains(callId, StringComparer.Ordinal)) caller.DirectCallIds.Add(callId);
                    }
                    else if (!IsIgnoredFrameworkCall(method)) AddUnresolved(caller, method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
                }
            }
        }

        private static bool IsIgnoredFrameworkCall(IMethodSymbol symbol)
        {
            var ns = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            return ns.StartsWith("System.Diagnostics", StringComparison.Ordinal) || ns.StartsWith("System.Console", StringComparison.Ordinal);
        }

        private static void AddUnresolved(FunctionDescriptor caller, string callName)
        {
            if (!caller.UnresolvedCalls.Contains(callName, StringComparer.Ordinal)) caller.UnresolvedCalls.Add(callName);
        }

        private static List<MetadataReference> BuildMetadataReferences()
        {
            var references = new List<MetadataReference>();
            var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (!string.IsNullOrWhiteSpace(trustedAssemblies))
                foreach (var path in trustedAssemblies.Split(Path.PathSeparator)) if (File.Exists(path)) references.Add(MetadataReference.CreateFromFile(path));
            if (references.Count == 0)
                references.AddRange(new[] { typeof(object).Assembly, typeof(Enumerable).Assembly, typeof(Console).Assembly }
                    .Where(assembly => !string.IsNullOrWhiteSpace(assembly.Location)).Select(assembly => MetadataReference.CreateFromFile(assembly.Location)));
            return references;
        }

        private static string NormalizeLooseMethods(string code, out bool wasWrapped)
        {
            wasWrapped = false;
            var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();
            if (root.DescendantNodes().OfType<TypeDeclarationSyntax>().Any() || root.Members.OfType<BaseNamespaceDeclarationSyntax>().Any() || !root.DescendantNodes().OfType<MethodDeclarationSyntax>().Any()) return code;
            wasWrapped = true;
            var usingLines = new List<string>(); var memberLines = new List<string>();
            foreach (var line in Regex.Split(code, "\\r?\\n")) if (line.TrimStart().StartsWith("using ", StringComparison.Ordinal)) usingLines.Add(line); else memberLines.Add(line);
            return string.Join(Environment.NewLine, usingLines) + Environment.NewLine + "public class __InputWrapper" + Environment.NewLine + "{" + Environment.NewLine + string.Join(Environment.NewLine, memberLines) + Environment.NewLine + "}";
        }
    }
}
