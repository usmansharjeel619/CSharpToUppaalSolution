using CSharpToUppaal.Backend.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

#nullable disable
namespace CSharpToUppaal.Backend.Parsers
{
    public interface ICSharpParserService
    {
        Task<ParseResult> ParseSourceFileAsync(string filePath);
        Task<ParseResult> ParseSourceCodeAsync(string code, string language = "C#");
        Task<MethodInfo> AnalyzeMethodAsync(MethodDeclarationSyntax method);
        Task<ComplexityMetrics> CalculateComplexityAsync(string code);
    }

    public class CSharpParser : ICSharpParserService
    {
        public async Task<ParseResult> ParseSourceFileAsync(string filePath)
        {
            try
            {
                Console.WriteLine($"Parsing source file: {filePath}");

                var code = await File.ReadAllTextAsync(filePath);
                return await ParseSourceCodeAsync(code);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing source file: {filePath}: {ex.Message}");
                return new ParseResult
                {
                    FilePath = filePath,
                    Success = false,
                    Errors = new List<ParseError>
                    {
                        new ParseError
                        {
                            Message = ex.Message,
                            LineNumber = 0,
                            Severity = ParseErrorSeverity.Error
                        }
                    }
                };
            }
        }

        public async Task<ParseResult> ParseSourceCodeAsync(string code, string language = "C#")
        {
            Console.WriteLine("=== ParseSourceCodeAsync STARTED ===");
            try
            {
                Console.WriteLine($"Parsing source code, length: {code?.Length ?? 0}");

                var parseResult = new ParseResult
                {
                    Success = true,
                    Errors = new List<ParseError>(),
                    Methods = new List<MethodInfo>(),
                    Classes = new List<ClassInfo>()
                };

                if (string.IsNullOrWhiteSpace(code))
                {
                    Console.WriteLine("ERROR: Source code is null or empty");
                    parseResult.Success = false;
                    parseResult.Errors.Add(new ParseError
                    {
                        Message = "Empty source code",
                        Severity = ParseErrorSeverity.Error
                    });
                    return parseResult;
                }

                Console.WriteLine("Parsing syntax tree...");
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = await tree.GetRootAsync();
                Console.WriteLine("Syntax tree parsed successfully");

                SemanticModel semanticModel = null;
                
                try
                {
                    Console.WriteLine("Attempting to create semantic model...");
                    // Create a compilation with necessary references to get semantic model
                    // Get all assemblies from the current app domain that are loaded
                    var assemblies = new[]
                    {
                        typeof(object).Assembly,           // System.Private.CoreLib
                        typeof(Enumerable).Assembly,       // System.Linq
                        typeof(Console).Assembly,          // System.Console
                    };

                    Console.WriteLine($"Loading {assemblies.Length} assembly references...");
                    var references = assemblies
                        .Where(a => !string.IsNullOrEmpty(a.Location))
                        .Select(a => MetadataReference.CreateFromFile(a.Location))
                        .ToList();

                    Console.WriteLine($"Creating compilation with {references.Count} references...");
                    var compilation = CSharpCompilation.Create(
                        "TempAssembly",
                        syntaxTrees: new[] { tree },
                        references: references,
                        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                    Console.WriteLine("Getting semantic model...");
                    semanticModel = compilation.GetSemanticModel(tree);
                    Console.WriteLine("Semantic model created successfully!");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not create semantic model: {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
                    Console.WriteLine("Continuing with syntax-only parsing...");
                }

                // Extract classes
                if (semanticModel != null)
                {
                    Console.WriteLine("Using semantic model for class extraction");
                    ExtractClasses(root, semanticModel, parseResult);
                }
                else
                {
                    Console.WriteLine("Using syntax-only for class extraction");
                    ExtractClassesWithoutSemanticModel(root, parseResult);
                }

                // Extract methods
                if (semanticModel != null)
                {
                    Console.WriteLine("Using semantic model for method extraction");
                    ExtractMethods(root, semanticModel, parseResult);
                }
                else
                {
                    Console.WriteLine("Using syntax-only for method extraction");
                    ExtractMethodsWithoutSemanticModel(root, parseResult);
                }

                Console.WriteLine($"Parsed {parseResult.Methods.Count} methods and {parseResult.Classes.Count} classes");
                
                // Debug: Print method names
                foreach (var method in parseResult.Methods)
                {
                    Console.WriteLine($"  - Method: {method.Name} (Return: {method.ReturnType}, Lines: {method.LinesOfCode})");
                }

                Console.WriteLine("=== ParseSourceCodeAsync COMPLETED SUCCESSFULLY ===");
                return parseResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"=== FATAL ERROR in ParseSourceCodeAsync ===");
                Console.WriteLine($"Exception Type: {ex.GetType().FullName}");
                Console.WriteLine($"Error message: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                    Console.WriteLine($"Inner stack trace: {ex.InnerException.StackTrace}");
                }
                return new ParseResult
                {
                    Success = false,
                    Errors = new List<ParseError>
                    {
                        new ParseError
                        {
                            Message = ex.Message,
                            LineNumber = 0,
                            Severity = ParseErrorSeverity.Error
                        }
                    }
                };
            }
        }

        private void ExtractClasses(SyntaxNode root, SemanticModel semanticModel, ParseResult parseResult)
        {
            var classDeclarations = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>();

            foreach (var classDecl in classDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(classDecl);
                if (symbol == null) continue;

                var classInfo = new ClassInfo
                {
                    Name = symbol.Name,
                    FullName = symbol.ToDisplayString(),
                    IsPublic = symbol.DeclaredAccessibility == Accessibility.Public,
                    IsAbstract = symbol.IsAbstract,
                    IsSealed = symbol.IsSealed,
                    BaseTypes = symbol.BaseType?.ToDisplayString(),
                    Location = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                };

                // Extract properties
                var properties = classDecl.DescendantNodes()
                    .OfType<PropertyDeclarationSyntax>();
                foreach (var prop in properties)
                {
                    var propSymbol = semanticModel.GetDeclaredSymbol(prop);
                    if (propSymbol != null)
                    {
                        classInfo.Properties.Add(new PropertyInfo
                        {
                            Name = propSymbol.Name,
                            Type = propSymbol.Type.ToDisplayString(),
                            HasGetter = prop.AccessorList?.Accessors.Any(a => a.Keyword.Text == "get") == true,
                            HasSetter = prop.AccessorList?.Accessors.Any(a => a.Keyword.Text == "set") == true
                        });
                    }
                }

                parseResult.Classes.Add(classInfo);
            }
        }

        private void ExtractMethods(SyntaxNode root, SemanticModel semanticModel, ParseResult parseResult)
        {
            var methodDeclarations = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>();

            foreach (var methodDecl in methodDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(methodDecl);
                if (symbol == null) continue;

                var methodInfo = new MethodInfo
                {
                    Name = symbol.Name,
                    ReturnType = symbol.ReturnType.ToDisplayString(),
                    IsPublic = symbol.DeclaredAccessibility == Accessibility.Public,
                    IsStatic = symbol.IsStatic,
                    IsAsync = methodDecl.Modifiers.Any(m => m.Text == "async"),
                    LineNumber = methodDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Body = methodDecl.Body?.ToString() ?? methodDecl.ExpressionBody?.ToString()
                };

                // Extract parameters
                foreach (var param in symbol.Parameters)
                {
                    methodInfo.Parameters.Add(new ParameterInfo
                    {
                        Name = param.Name,
                        Type = param.Type.ToDisplayString(),
                        HasDefaultValue = param.HasExplicitDefaultValue,
                        DefaultValue = param.HasExplicitDefaultValue ? param.ExplicitDefaultValue?.ToString() : null
                    });
                }

                // Calculate complexity metrics
                methodInfo.Complexity = CalculateComplexity(methodDecl);
                methodInfo.LinesOfCode = CalculateLinesOfCode(methodDecl);

                parseResult.Methods.Add(methodInfo);
            }
        }

        public async Task<MethodInfo> AnalyzeMethodAsync(MethodDeclarationSyntax method)
        {
            return await Task.Run(() =>
            {
                var methodInfo = new MethodInfo
                {
                    Name = method.Identifier.Text,
                    ReturnType = method.ReturnType.ToString(),
                    IsPublic = method.Modifiers.Any(m => m.Text == "public"),
                    IsStatic = method.Modifiers.Any(m => m.Text == "static"),
                    IsAsync = method.Modifiers.Any(m => m.Text == "async"),
                    Body = method.Body?.ToString() ?? method.ExpressionBody?.ToString(),
                    LinesOfCode = CalculateLinesOfCode(method)
                };

                methodInfo.Complexity = CalculateComplexity(method);

                return methodInfo;
            });
        }

        private ComplexityMetrics CalculateComplexity(MethodDeclarationSyntax method)
        {
            var visitor = new ComplexityVisitor();
            visitor.Visit(method);
            return visitor.GetMetrics();
        }

        private int CalculateLinesOfCode(MethodDeclarationSyntax method)
        {
            var body = method.Body;
            if (body == null)
            {
                var exprBody = method.ExpressionBody;
                return exprBody != null ? 1 : 0;
            }

            var lines = body.ToString().Split('\n');
            return lines.Count(line => !string.IsNullOrWhiteSpace(line));
        }

        public async Task<ComplexityMetrics> CalculateComplexityAsync(string code)
        {
            return await Task.Run(() =>
            {
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetRoot();

                var methodDeclarations = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>();

                var totalMetrics = new ComplexityMetrics();

                foreach (var method in methodDeclarations)
                {
                    var metrics = CalculateComplexity(method);
                    totalMetrics.CyclomaticComplexity += metrics.CyclomaticComplexity;
                    totalMetrics.LinesOfCode += metrics.LinesOfCode;
                    totalMetrics.ParameterCount += metrics.ParameterCount;
                    totalMetrics.NestingDepth = Math.Max(totalMetrics.NestingDepth, metrics.NestingDepth);
                    totalMetrics.MethodCalls += metrics.MethodCalls;
                }

                return totalMetrics;
            });
        }

        private class ComplexityVisitor : CSharpSyntaxWalker
        {
            private int _cyclomaticComplexity = 1;
            private int _nestingDepth = 0;
            private int _maxNestingDepth = 0;
            private int _methodCalls = 0;
            private int _parameterCount = 0;

            public override void VisitIfStatement(IfStatementSyntax node)
            {
                _cyclomaticComplexity++;
                _nestingDepth++;
                _maxNestingDepth = Math.Max(_maxNestingDepth, _nestingDepth);
                base.VisitIfStatement(node);
                _nestingDepth--;
            }

            public override void VisitWhileStatement(WhileStatementSyntax node)
            {
                _cyclomaticComplexity++;
                _nestingDepth++;
                _maxNestingDepth = Math.Max(_maxNestingDepth, _nestingDepth);
                base.VisitWhileStatement(node);
                _nestingDepth--;
            }

            public override void VisitForStatement(ForStatementSyntax node)
            {
                _cyclomaticComplexity++;
                _nestingDepth++;
                _maxNestingDepth = Math.Max(_maxNestingDepth, _nestingDepth);
                base.VisitForStatement(node);
                _nestingDepth--;
            }

            public override void VisitForEachStatement(ForEachStatementSyntax node)
            {
                _cyclomaticComplexity++;
                _nestingDepth++;
                _maxNestingDepth = Math.Max(_maxNestingDepth, _nestingDepth);
                base.VisitForEachStatement(node);
                _nestingDepth--;
            }

            public override void VisitSwitchStatement(SwitchStatementSyntax node)
            {
                var sectionsCount = node.Sections.Count;
                _cyclomaticComplexity += sectionsCount;
                _nestingDepth++;
                _maxNestingDepth = Math.Max(_maxNestingDepth, _nestingDepth);
                base.VisitSwitchStatement(node);
                _nestingDepth--;
            }

            public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
            {
                _cyclomaticComplexity++;
                base.VisitConditionalExpression(node);
            }

            public override void VisitBinaryExpression(BinaryExpressionSyntax node)
            {
                if (node.OperatorToken.Text == "&&" || node.OperatorToken.Text == "||")
                {
                    _cyclomaticComplexity++;
                }
                base.VisitBinaryExpression(node);
            }

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                _methodCalls++;
                base.VisitInvocationExpression(node);
            }

            public override void VisitParameter(ParameterSyntax node)
            {
                _parameterCount++;
                base.VisitParameter(node);
            }

            public ComplexityMetrics GetMetrics()
            {
                return new ComplexityMetrics
                {
                    CyclomaticComplexity = _cyclomaticComplexity,
                    NestingDepth = _maxNestingDepth,
                    MethodCalls = _methodCalls,
                    ParameterCount = _parameterCount
                };
            }
        }

        // Fallback methods for parsing without semantic model
        private void ExtractClassesWithoutSemanticModel(SyntaxNode root, ParseResult parseResult)
        {
            var classDeclarations = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>();

            foreach (var classDecl in classDeclarations)
            {
                var classInfo = new ClassInfo
                {
                    Name = classDecl.Identifier.Text,
                    FullName = classDecl.Identifier.Text,
                    IsPublic = classDecl.Modifiers.Any(m => m.Text == "public"),
                    IsAbstract = classDecl.Modifiers.Any(m => m.Text == "abstract"),
                    IsSealed = classDecl.Modifiers.Any(m => m.Text == "sealed"),
                    BaseTypes = classDecl.BaseList?.Types.FirstOrDefault()?.ToString(),
                    Location = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                };

                // Extract properties
                var properties = classDecl.DescendantNodes()
                    .OfType<PropertyDeclarationSyntax>();
                foreach (var prop in properties)
                {
                    classInfo.Properties.Add(new PropertyInfo
                    {
                        Name = prop.Identifier.Text,
                        Type = prop.Type.ToString(),
                        HasGetter = prop.AccessorList?.Accessors.Any(a => a.Keyword.Text == "get") == true,
                        HasSetter = prop.AccessorList?.Accessors.Any(a => a.Keyword.Text == "set") == true
                    });
                }

                parseResult.Classes.Add(classInfo);
            }
        }

        private void ExtractMethodsWithoutSemanticModel(SyntaxNode root, ParseResult parseResult)
        {
            try
            {
                Console.WriteLine("ExtractMethodsWithoutSemanticModel: Starting method extraction...");
                
                var methodDeclarations = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .ToList();

                Console.WriteLine($"ExtractMethodsWithoutSemanticModel: Found {methodDeclarations.Count} method declarations");

                foreach (var methodDecl in methodDeclarations)
                {
                    try
                    {
                        Console.WriteLine($"ExtractMethodsWithoutSemanticModel: Processing method {methodDecl.Identifier.Text}");
                        
                        var methodInfo = new MethodInfo
                        {
                            Name = methodDecl.Identifier.Text,
                            ReturnType = methodDecl.ReturnType.ToString(),
                            IsPublic = methodDecl.Modifiers.Any(m => m.Text == "public"),
                            IsStatic = methodDecl.Modifiers.Any(m => m.Text == "static"),
                            IsAsync = methodDecl.Modifiers.Any(m => m.Text == "async"),
                            LineNumber = methodDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            Body = methodDecl.Body?.ToString() ?? methodDecl.ExpressionBody?.ToString()
                        };

                        // Extract parameters
                        foreach (var param in methodDecl.ParameterList.Parameters)
                        {
                            methodInfo.Parameters.Add(new ParameterInfo
                            {
                                Name = param.Identifier.Text,
                                Type = param.Type?.ToString() ?? "var",
                                HasDefaultValue = param.Default != null,
                                DefaultValue = param.Default?.Value?.ToString()
                            });
                        }

                        // Calculate complexity metrics
                        try
                        {
                            methodInfo.Complexity = CalculateComplexity(methodDecl);
                            methodInfo.LinesOfCode = CalculateLinesOfCode(methodDecl);
                        }
                        catch (Exception complexityEx)
                        {
                            Console.WriteLine($"Warning: Could not calculate complexity for {methodInfo.Name}: {complexityEx.Message}");
                            methodInfo.Complexity = new ComplexityMetrics { CyclomaticComplexity = 1 };
                            methodInfo.LinesOfCode = 0;
                        }

                        parseResult.Methods.Add(methodInfo);
                        Console.WriteLine($"ExtractMethodsWithoutSemanticModel: Successfully added method {methodInfo.Name}");
                    }
                    catch (Exception methodEx)
                    {
                        Console.WriteLine($"Error extracting method: {methodEx.Message}");
                        Console.WriteLine($"Stack trace: {methodEx.StackTrace}");
                    }
                }
                
                Console.WriteLine($"ExtractMethodsWithoutSemanticModel: Completed. Total methods extracted: {parseResult.Methods.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR in ExtractMethodsWithoutSemanticModel: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }
    }
}
