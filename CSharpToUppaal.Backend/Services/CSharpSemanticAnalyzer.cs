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

namespace CSharpToUppaal.Backend.Services
{
    public interface ICSharpSemanticAnalyzer
    {
        Task<CSharpSemanticAnalysisResult> AnalyzeSourceCodeAsync(string code, string fileName = "Source.cs");
    }

    public class CSharpSemanticAnalysisResult
    {
        public string OriginalCode { get; set; } = string.Empty;
        public string NormalizedCode { get; set; } = string.Empty;
        public string FileName { get; set; } = "Source.cs";
        public bool WasWrapped { get; set; }
        public Compilation Compilation { get; set; } = null!;
        public SyntaxTree SyntaxTree { get; set; } = null!;
        public SemanticModel SemanticModel { get; set; } = null!;
        public CompilationUnitSyntax Root { get; set; } = null!;
        public List<FunctionDescriptor> Functions { get; set; } = new();
        public Dictionary<string, MethodDeclarationSyntax> MethodDeclarationsById { get; set; } = new();
        public Dictionary<string, IMethodSymbol> MethodSymbolsById { get; set; } = new();
        public List<TranslationAssumption> Assumptions { get; set; } = new();
        public List<string> Diagnostics { get; set; } = new();

        public IReadOnlyList<FunctionDescriptor> ResolveClosure(IEnumerable<FunctionSelection> selections)
        {
            var functionById = Functions.ToDictionary(f => f.Id);
            var selected = selections.Where(s => s.IsSelected).ToList();

            if (selected.Count == 0 && Functions.Count > 0)
            {
                var main = Functions.FirstOrDefault(f => f.Name == "Main");
                selected.Add(new FunctionSelection
                {
                    FunctionId = main?.Id ?? Functions[0].Id,
                    IsSelected = true,
                    Mode = FunctionModelingMode.ExplicitAutomaton
                });
            }

            var included = new Dictionary<string, FunctionDescriptor>();
            var stack = new Stack<string>(selected.Select(s => s.FunctionId).Where(functionById.ContainsKey));

            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!functionById.TryGetValue(id, out var function) || included.ContainsKey(id))
                    continue;

                included[id] = function;

                var selection = selected.FirstOrDefault(s => s.FunctionId == id);
                if (selection?.Mode == FunctionModelingMode.Stub)
                    continue;

                foreach (var callId in function.DirectCallIds)
                {
                    if (!included.ContainsKey(callId))
                        stack.Push(callId);
                }
            }

            return included.Values
                .OrderBy(f => f.LineNumber)
                .ThenBy(f => f.Signature, StringComparer.Ordinal)
                .ToList();
        }
    }

    public class CSharpSemanticAnalyzer : ICSharpSemanticAnalyzer
    {
        private static readonly SymbolDisplayFormat FunctionIdFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;

        public async Task<CSharpSemanticAnalysisResult> AnalyzeSourceCodeAsync(string code, string fileName = "Source.cs")
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("C# source code is empty.", nameof(code));

            var normalized = NormalizeLooseMethods(code, out var wasWrapped);
            var syntaxTree = CSharpSyntaxTree.ParseText(normalized, path: fileName);
            var root = await syntaxTree.GetRootAsync().ConfigureAwait(false) as CompilationUnitSyntax
                       ?? throw new InvalidOperationException("Unable to parse C# compilation unit.");

            var compilation = CSharpCompilation.Create(
                "CSharpToUppaalInput",
                new[] { syntaxTree },
                BuildMetadataReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithNullableContextOptions(NullableContextOptions.Enable));

            var semanticModel = compilation.GetSemanticModel(syntaxTree);

            var result = new CSharpSemanticAnalysisResult
            {
                OriginalCode = code,
                NormalizedCode = normalized,
                FileName = fileName,
                WasWrapped = wasWrapped,
                Compilation = compilation,
                SyntaxTree = syntaxTree,
                SemanticModel = semanticModel,
                Root = root
            };

            if (wasWrapped)
            {
                result.Assumptions.Add(new TranslationAssumption
                {
                    Severity = AssumptionSeverity.Info,
                    Category = "Input",
                    Message = "Loose method snippet was wrapped in a synthetic class for Roslyn semantic analysis.",
                    SymbolName = "__InputWrapper",
                    IsUserEditable = false
                });
            }

            foreach (var diagnostic in syntaxTree.GetDiagnostics().Concat(compilation.GetDiagnostics()))
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    result.Diagnostics.Add(diagnostic.ToString());
                }
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

        private static void ExtractFunctions(CSharpSemanticAnalysisResult result)
        {
            foreach (var methodDecl in result.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var symbol = result.SemanticModel.GetDeclaredSymbol(methodDecl);
                if (symbol == null)
                    continue;

                var id = ToFunctionId(symbol);
                var lineSpan = methodDecl.GetLocation().GetLineSpan();
                var descriptor = new FunctionDescriptor
                {
                    Id = id,
                    Name = symbol.Name,
                    DisplayName = $"{symbol.ContainingType?.Name ?? "Global"}.{symbol.Name}",
                    Signature = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Namespace = symbol.ContainingNamespace?.IsGlobalNamespace == true
                        ? string.Empty
                        : symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                    ContainingType = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? string.Empty,
                    ReturnType = symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    IsPublic = symbol.DeclaredAccessibility == Accessibility.Public,
                    IsStatic = symbol.IsStatic,
                    IsAsync = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)),
                    IsSynthetic = result.WasWrapped,
                    LineNumber = lineSpan.StartLinePosition.Line + 1,
                    SourceFile = result.FileName,
                    Body = methodDecl.Body?.ToString() ?? methodDecl.ExpressionBody?.ToString() ?? string.Empty
                };

                foreach (var parameter in symbol.Parameters)
                {
                    descriptor.Parameters.Add(new ParameterInfo
                    {
                        Name = parameter.Name,
                        Type = parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        HasDefaultValue = parameter.HasExplicitDefaultValue,
                        DefaultValue = parameter.HasExplicitDefaultValue ? parameter.ExplicitDefaultValue?.ToString() ?? string.Empty : string.Empty
                    });
                }

                result.Functions.Add(descriptor);
                result.MethodDeclarationsById[id] = methodDecl;
                result.MethodSymbolsById[id] = symbol;
            }
        }

        private static void ExtractCalls(CSharpSemanticAnalysisResult result)
        {
            var knownIds = result.Functions.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            var descriptorById = result.Functions.ToDictionary(f => f.Id);

            foreach (var kv in result.MethodDeclarationsById)
            {
                var caller = descriptorById[kv.Key];
                var invocations = kv.Value.DescendantNodes().OfType<InvocationExpressionSyntax>();

                foreach (var invocation in invocations)
                {
                    var symbolInfo = result.SemanticModel.GetSymbolInfo(invocation);
                    var methodSymbol = symbolInfo.Symbol as IMethodSymbol
                                    ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

                    if (methodSymbol == null)
                    {
                        AddUnresolved(caller, invocation.Expression.ToString());
                        continue;
                    }

                    var callId = ToFunctionId(methodSymbol);
                    if (knownIds.Contains(callId))
                    {
                        if (!caller.DirectCallIds.Contains(callId, StringComparer.Ordinal))
                            caller.DirectCallIds.Add(callId);
                    }
                    else if (!IsIgnoredFrameworkCall(methodSymbol))
                    {
                        AddUnresolved(caller, methodSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
                    }
                }
            }
        }

        private static bool IsIgnoredFrameworkCall(IMethodSymbol symbol)
        {
            var ns = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            return ns.StartsWith("System.Diagnostics", StringComparison.Ordinal)
                || ns.StartsWith("System.Console", StringComparison.Ordinal);
        }

        private static void AddUnresolved(FunctionDescriptor caller, string callName)
        {
            if (!caller.UnresolvedCalls.Contains(callName, StringComparer.Ordinal))
                caller.UnresolvedCalls.Add(callName);
        }

        private static List<MetadataReference> BuildMetadataReferences()
        {
            var references = new List<MetadataReference>();
            var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;

            if (!string.IsNullOrWhiteSpace(trustedAssemblies))
            {
                foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
                {
                    if (File.Exists(path))
                        references.Add(MetadataReference.CreateFromFile(path));
                }
            }

            if (references.Count == 0)
            {
                var assemblies = new[]
                {
                    typeof(object).Assembly,
                    typeof(Enumerable).Assembly,
                    typeof(Console).Assembly
                };

                references.AddRange(assemblies
                    .Where(a => !string.IsNullOrWhiteSpace(a.Location))
                    .Select(a => MetadataReference.CreateFromFile(a.Location)));
            }

            return references;
        }

        private static string NormalizeLooseMethods(string code, out bool wasWrapped)
        {
            wasWrapped = false;
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetCompilationUnitRoot();

            var hasTypeDeclaration = root.DescendantNodes().OfType<TypeDeclarationSyntax>().Any();
            var hasNamespace = root.Members.OfType<BaseNamespaceDeclarationSyntax>().Any();
            var hasMethodDeclaration = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Any();

            if (hasTypeDeclaration || hasNamespace || !hasMethodDeclaration)
                return code;

            wasWrapped = true;
            var usingLines = new List<string>();
            var memberLines = new List<string>();

            foreach (var line in Regex.Split(code, "\r?\n"))
            {
                if (line.TrimStart().StartsWith("using ", StringComparison.Ordinal))
                    usingLines.Add(line);
                else
                    memberLines.Add(line);
            }

            return string.Join(Environment.NewLine, usingLines)
                + Environment.NewLine
                + "public class __InputWrapper"
                + Environment.NewLine
                + "{"
                + Environment.NewLine
                + string.Join(Environment.NewLine, memberLines)
                + Environment.NewLine
                + "}";
        }
    }
}
