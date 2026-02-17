using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using CSharpToUppaal.Backend.Generators;
using CSharpToUppaal.Backend.Mappers;
using CSharpToUppaal.Backend.Models;
using CSharpToUppaal.Backend.Parsers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Project = CSharpToUppaal.Backend.Models.Project;

namespace CSharpToUppaal.Backend.Services
{
    public interface IUppaalGeneratorService
    {
        Task<UppaalModel> GenerateModelFromCodeAsync(string code, string modelName);
        Task<UppaalModel> GenerateModelFromCfgAsync(ControlFlowGraph cfg, string modelName);
        Task<string> GenerateUppaalXmlAsync(UppaalModel model);
        Task<UppaalModel> GenerateModelFromProjectAsync(Project project);
    }

    public class UppaalGeneratorService : IUppaalGeneratorService
    {
        private readonly ICfgGeneratorService _cfgGenerator;

        public UppaalGeneratorService(ICfgGeneratorService cfgGenerator)
        {
            _cfgGenerator = cfgGenerator;
        }

        public async Task<UppaalModel> GenerateModelFromCodeAsync(string code, string modelName)
        {
            try
            {
                Console.WriteLine($"Generating UPPAAL model from code");

                // Parse C# code and generate CFG
                var parser = new CSharpParser();
                var parseResult = await parser.ParseSourceCodeAsync(code);

                if (!parseResult.Methods.Any())
                {
                    throw new InvalidOperationException("No methods found in the code");
                }

                var model = new UppaalModel
                {
                    Name = modelName,
                    Status = ModelGenerationStatus.InProgress
                };

                // Generate CFG for each method and create templates
                var templates = new List<UppaalTemplate>();
                foreach (var method in parseResult.Methods)
                {
                    var cfg = await _cfgGenerator.GenerateCfgFromMethodAsync(method);
                    var template = MapCfgToUppaalTemplate(cfg);
                    templates.Add(template);
                }

                model.Templates = templates;
                model.ParsedMethods = parseResult.Methods;
                model.XmlContent = await GenerateUppaalXmlAsync(model);
                model.Status = ModelGenerationStatus.Success;
                model.StatusMessage = "Model generated successfully";

                Console.WriteLine($"Generated UPPAAL model: {modelName} with {templates.Count} templates");

                return model;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating UPPAAL model from code: {ex.Message}");
                return new UppaalModel
                {
                    Name = modelName,
                    Status = ModelGenerationStatus.GenerationError,
                    StatusMessage = $"Failed to generate model: {ex.Message}"
                };
            }
        }

        public async Task<UppaalModel> GenerateModelFromCfgAsync(ControlFlowGraph cfg, string modelName)
        {
            try
            {
                var template = MapCfgToUppaalTemplate(cfg);
                var model = new UppaalModel
                {
                    Name = modelName,
                    Templates = new List<UppaalTemplate> { template },
                    Status = ModelGenerationStatus.Success
                };

                model.XmlContent = await GenerateUppaalXmlAsync(model);
                return model;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating UPPAAL model from CFG: {ex.Message}");
                throw new UppaalGenerationException($"Failed to generate UPPAAL model from CFG", ex);
            }
        }

        public async Task<UppaalModel> GenerateModelFromProjectAsync(Project project)
        {
            try
            {
                Console.WriteLine($"Generating UPPAAL model from project: {project.Name}");

                var model = new UppaalModel
                {
                    Name = $"{project.Name}_Model",
                    Status = ModelGenerationStatus.InProgress
                };

                var templates = new List<UppaalTemplate>();
                var allMethods = new List<Models.MethodInfo>();

                foreach (var sourceFile in project.SourceFiles)
                {
                    foreach (var method in sourceFile.Methods)
                    {
                        var cfg = await _cfgGenerator.GenerateCfgFromMethodAsync(method);
                        var template = MapCfgToUppaalTemplate(cfg);
                        templates.Add(template);
                        allMethods.Add(method);
                    }
                }

                model.Templates = templates;
                model.ParsedMethods = allMethods;
                model.XmlContent = await GenerateUppaalXmlAsync(model);
                model.Status = ModelGenerationStatus.Success;
                model.StatusMessage = $"Generated model from {project.SourceFiles.Count} source files";

                project.GeneratedModels.Add(model);

                Console.WriteLine($"Generated UPPAAL model for project: {project.Name}");

                return model;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating UPPAAL model from project: {ex.Message}");
                return new UppaalModel
                {
                    Name = $"{project.Name}_Model",
                    Status = ModelGenerationStatus.GenerationError,
                    StatusMessage = $"Failed to generate model: {ex.Message}"
                };
            }
        }

        public async Task<string> GenerateUppaalXmlAsync(UppaalModel model)
        {
            try
            {
                // Determine which methods are converted to UPPAAL functions
                // so we can exclude them from templates (avoid "Duplicate definition" errors)
                var functionNames = new HashSet<string>();
                if (model.ParsedMethods != null && model.ParsedMethods.Count > 0)
                {
                    var calledMethods = FindCalledMethods(model.ParsedMethods);
                    foreach (var m in calledMethods)
                        functionNames.Add(m.Name);
                }

                var ntaElement = new XElement("nta",
                    GenerateGlobalDeclaration(model),
                    GenerateTemplates(model.Templates, functionNames),
                    GenerateSystemDeclaration(model.Templates, functionNames),
                    GenerateQueries()
                );

                // UPPAAL 4.x expects no <?xml?> declaration — just DOCTYPE + nta
                var xdoc = new XDocument(
                    new XDocumentType("nta",
                        "-//Uppaal Team//DTD Flat System 1.1//EN",
                        "http://www.it.uu.se/research/group/darts/uppaal/flat-1_1.dtd",
                        null),
                    ntaElement
                );

                return xdoc.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating UPPAAL XML: {ex.Message}");
                throw new UppaalGenerationException("Failed to generate UPPAAL XML", ex);
            }
        }

        private UppaalTemplate MapCfgToUppaalTemplate(ControlFlowGraph cfg)
        {
            var mapper = new CfgToUppaalMapper();
            return mapper.Map(cfg);
        }

        private XElement GenerateGlobalDeclaration(UppaalModel model)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// Global declarations");

            // Detect which methods are called by other methods, and generate UPPAAL functions for them
            if (model.ParsedMethods != null && model.ParsedMethods.Count > 0)
            {
                var calledMethods = FindCalledMethods(model.ParsedMethods);
                if (calledMethods.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("// --- Functions converted from C# methods ---");
                    foreach (var method in calledMethods)
                    {
                        var uppaalFunc = ConvertMethodToUppaalFunction(method);
                        if (!string.IsNullOrEmpty(uppaalFunc))
                        {
                            sb.AppendLine();
                            sb.Append(uppaalFunc);
                        }
                    }
                }
            }

            return new XElement("declaration", new XText(sb.ToString()));
        }

        /// <summary>
        /// Finds methods that are called by other methods (i.e., they appear as function calls in other method bodies).
        /// </summary>
        private List<Models.MethodInfo> FindCalledMethods(List<Models.MethodInfo> methods)
        {
            var calledNames = new HashSet<string>();

            foreach (var method in methods)
            {
                if (string.IsNullOrEmpty(method.Body)) continue;

                // Parse the body and look for InvocationExpressionSyntax
                var tree = CSharpSyntaxTree.ParseText(method.Body);
                var root = tree.GetRoot();
                var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

                foreach (var invocation in invocations)
                {
                    string calledName = null;
                    if (invocation.Expression is IdentifierNameSyntax id)
                        calledName = id.Identifier.Text;
                    else if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                        calledName = memberAccess.Name.Identifier.Text;

                    if (calledName != null)
                        calledNames.Add(calledName);
                }
            }

            // Return methods whose names match called names
            return methods.Where(m => calledNames.Contains(m.Name)).ToList();
        }

        /// <summary>
        /// Converts a C# method into a UPPAAL function declaration.
        /// UPPAAL requires: all local declarations at the top of a block (before any statements),
        /// and for-loop inits must be expressions (no declarations like "int i = 0").
        /// </summary>
        private string ConvertMethodToUppaalFunction(Models.MethodInfo method)
        {
            if (string.IsNullOrEmpty(method.Body)) return string.Empty;

            var sb = new StringBuilder();

            // Return type
            string uppaalRetType = MapCSharpTypeToUppaalType(method.ReturnType);

            // Parameters
            var paramList = new List<string>();
            var paramNames = new HashSet<string>();
            foreach (var p in method.Parameters)
            {
                string pType = MapCSharpTypeToUppaalType(p.Type);
                paramList.Add($"{pType} {p.Name}");
                paramNames.Add(p.Name);
            }

            sb.AppendLine($"{uppaalRetType} {method.Name}({string.Join(", ", paramList)}) {{");

            // Parse body
            var tree = CSharpSyntaxTree.ParseText(method.Body);
            var root = tree.GetRoot();
            var block = root.DescendantNodes().OfType<BlockSyntax>().FirstOrDefault();

            if (block != null)
            {
                // --- Pass 1: Collect ALL local variable declarations (including nested) ---
                // UPPAAL requires all declarations at the top of the function block.
                var localVars = new Dictionary<string, string>(); // name -> uppaalType
                CollectAllLocalVariables(block, localVars, paramNames);

                // Emit all local variable declarations at the top
                foreach (var kv in localVars)
                {
                    sb.AppendLine($"    {kv.Value} {kv.Key};");
                }

                // --- Pass 2: Emit statements, converting declarations to assignments ---
                foreach (var statement in block.Statements)
                {
                    var lines = ConvertStatementToUppaal(statement, 1);
                    sb.Append(lines);
                }
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Recursively collects all local variable declarations from a block and all nested blocks.
        /// This includes variables from local declarations, for-loop initializers, and nested scopes.
        /// </summary>
        private void CollectAllLocalVariables(SyntaxNode node, Dictionary<string, string> vars, HashSet<string> paramNames)
        {
            foreach (var descendant in node.DescendantNodes())
            {
                if (descendant is LocalDeclarationStatementSyntax localDecl)
                {
                    string typeName = MapCSharpTypeToUppaalType(localDecl.Declaration.Type.ToString());
                    foreach (var variable in localDecl.Declaration.Variables)
                    {
                        string name = variable.Identifier.Text;
                        if (!paramNames.Contains(name) && !vars.ContainsKey(name))
                            vars[name] = typeName;
                    }
                }
                else if (descendant is ForStatementSyntax forStmt && forStmt.Declaration != null)
                {
                    string typeName = MapCSharpTypeToUppaalType(forStmt.Declaration.Type.ToString());
                    foreach (var variable in forStmt.Declaration.Variables)
                    {
                        string name = variable.Identifier.Text;
                        if (!paramNames.Contains(name) && !vars.ContainsKey(name))
                            vars[name] = typeName;
                    }
                }
            }
        }

        /// <summary>
        /// Recursively converts a C# statement into UPPAAL function body syntax.
        /// Local declarations are emitted as assignments (variables are pre-declared at the top).
        /// For-loop initializers have their type stripped (variable is pre-declared).
        /// </summary>
        private string ConvertStatementToUppaal(StatementSyntax statement, int indent)
        {
            var prefix = new string(' ', indent * 4);
            var sb = new StringBuilder();

            switch (statement)
            {
                case LocalDeclarationStatementSyntax localDecl:
                {
                    // Variable is already declared at the top — just emit the assignment
                    foreach (var variable in localDecl.Declaration.Variables)
                    {
                        if (variable.Initializer != null)
                            sb.AppendLine($"{prefix}{variable.Identifier.Text} = {ConvertExpression(variable.Initializer.Value.ToString())};");
                        // If no initializer, nothing to emit (declaration is already at top)
                    }
                    break;
                }

                case ExpressionStatementSyntax exprStmt:
                {
                    sb.AppendLine($"{prefix}{ConvertExpression(exprStmt.Expression.ToString())};");
                    break;
                }

                case ReturnStatementSyntax returnStmt:
                {
                    if (returnStmt.Expression != null)
                        sb.AppendLine($"{prefix}return {ConvertExpression(returnStmt.Expression.ToString())};");
                    else
                        sb.AppendLine($"{prefix}return;");
                    break;
                }

                case IfStatementSyntax ifStmt:
                {
                    sb.AppendLine($"{prefix}if ({ConvertExpression(ifStmt.Condition.ToString())}) {{");
                    if (ifStmt.Statement is BlockSyntax ifBlock)
                    {
                        foreach (var s in ifBlock.Statements)
                            sb.Append(ConvertStatementToUppaal(s, indent + 1));
                    }
                    else
                    {
                        sb.Append(ConvertStatementToUppaal(ifStmt.Statement, indent + 1));
                    }
                    sb.AppendLine($"{prefix}}}");

                    if (ifStmt.Else != null)
                    {
                        // Check for "else if"
                        if (ifStmt.Else.Statement is IfStatementSyntax elseIf)
                        {
                            sb.Append($"{prefix}else ");
                            // Remove leading indent from the recursive call since we already have "else "
                            var elseIfStr = ConvertStatementToUppaal(elseIf, indent);
                            sb.Append(elseIfStr.TrimStart());
                        }
                        else
                        {
                            sb.AppendLine($"{prefix}else {{");
                            if (ifStmt.Else.Statement is BlockSyntax elseBlock)
                            {
                                foreach (var s in elseBlock.Statements)
                                    sb.Append(ConvertStatementToUppaal(s, indent + 1));
                            }
                            else
                            {
                                sb.Append(ConvertStatementToUppaal(ifStmt.Else.Statement, indent + 1));
                            }
                            sb.AppendLine($"{prefix}}}");
                        }
                    }
                    break;
                }

                case ForStatementSyntax forStmt:
                {
                    // UPPAAL for-loop init must be an expression, not a declaration.
                    // The variable is already declared at the top of the function.
                    string init = "";
                    if (forStmt.Declaration != null)
                    {
                        // Strip the type — just emit "varName = value" assignments
                        var inits = forStmt.Declaration.Variables
                            .Where(v => v.Initializer != null)
                            .Select(v => $"{v.Identifier.Text} = {ConvertExpression(v.Initializer.Value.ToString())}");
                        init = string.Join(", ", inits);
                    }
                    else if (forStmt.Initializers.Any())
                    {
                        init = string.Join(", ", forStmt.Initializers.Select(i => ConvertExpression(i.ToString())));
                    }

                    string cond = forStmt.Condition != null ? ConvertExpression(forStmt.Condition.ToString()) : "true";
                    string incr = string.Join(", ", forStmt.Incrementors.Select(i => ConvertExpression(i.ToString())));

                    sb.AppendLine($"{prefix}for ({init}; {cond}; {incr}) {{");
                    if (forStmt.Statement is BlockSyntax forBlock)
                    {
                        foreach (var s in forBlock.Statements)
                            sb.Append(ConvertStatementToUppaal(s, indent + 1));
                    }
                    else
                    {
                        sb.Append(ConvertStatementToUppaal(forStmt.Statement, indent + 1));
                    }
                    sb.AppendLine($"{prefix}}}");
                    break;
                }

                case WhileStatementSyntax whileStmt:
                {
                    sb.AppendLine($"{prefix}while ({ConvertExpression(whileStmt.Condition.ToString())}) {{");
                    if (whileStmt.Statement is BlockSyntax whileBlock)
                    {
                        foreach (var s in whileBlock.Statements)
                            sb.Append(ConvertStatementToUppaal(s, indent + 1));
                    }
                    else
                    {
                        sb.Append(ConvertStatementToUppaal(whileStmt.Statement, indent + 1));
                    }
                    sb.AppendLine($"{prefix}}}");
                    break;
                }

                case BlockSyntax blockStmt:
                {
                    foreach (var s in blockStmt.Statements)
                        sb.Append(ConvertStatementToUppaal(s, indent));
                    break;
                }

                default:
                    // Fallback: emit as-is
                    sb.AppendLine($"{prefix}{ConvertExpression(statement.ToString().Trim())};");
                    break;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Converts a C# expression string to UPPAAL-compatible syntax.
        /// UPPAAL functions support standard C operators, so most expressions need no conversion.
        /// </summary>
        private string ConvertExpression(string expr)
        {
            // UPPAAL function bodies support ++, --, +=, etc., and all C operators
            // No special conversion needed for: +, -, *, /, %, ==, !=, <, >, <=, >=, &&, ||, !
            return expr.Trim();
        }

        private string MapCSharpTypeToUppaalType(string csharpType)
        {
            switch (csharpType?.Trim().ToLower())
            {
                case "int":
                case "int32":
                case "int16":
                case "int64":
                case "short":
                case "long":
                case "byte":
                    return "int";
                case "bool":
                case "boolean":
                    return "bool";
                case "double":
                case "float":
                case "decimal":
                    return "int"; // UPPAAL has no floats; approximate as int
                case "void":
                    return "void";
                default:
                    return "int";
            }
        }

        private IEnumerable<XElement> GenerateTemplates(List<UppaalTemplate> templates, HashSet<string> functionNames)
        {
            int templateIndex = 0;
            int globalIdCounter = 0; // Shared counter across all templates for unique id values
            
            foreach (var template in templates)
            {
                // Skip templates that are already generated as UPPAAL functions in the global declaration
                if (functionNames.Contains(template.Name))
                    continue;

                // Remap location IDs to UPPAAL 4.1 compatible format (id0, id1, id2, ...)
                var idRemap = new Dictionary<string, string>();
                foreach (var loc in template.Locations)
                {
                    var newId = $"id{globalIdCounter++}";
                    idRemap[loc.Id] = newId;
                    loc.Id = newId;
                }
                // Remap transition source/target references
                foreach (var trans in template.Transitions)
                {
                    if (idRemap.ContainsKey(trans.Source))
                        trans.Source = idRemap[trans.Source];
                    if (idRemap.ContainsKey(trans.Target))
                        trans.Target = idRemap[trans.Target];
                }

                // Store position info for transition generation
                var (locationElements, positionMap, levels, backEdges) = GenerateLocationsWithPositions(template.Locations, template.Transitions, templateIndex);
                
                yield return new XElement("template",
                    new XElement("name", template.Name),
                    GenerateTemplateDeclaration(template),
                    locationElements,
                    GenerateInitialLocation(template.Locations),
                    GenerateTransitions(template.Transitions, positionMap, levels, backEdges)
                );
                
                templateIndex++;
            }
        }

        private XElement GenerateTemplateDeclaration(UppaalTemplate template)
        {
            if (string.IsNullOrEmpty(template.Declaration))
                return new XElement("declaration", "// Template declarations");

            return new XElement("declaration",
                new XText(template.Declaration)
            );
        }

        private (IEnumerable<XElement> elements, Dictionary<string, (int x, int y)> positions, Dictionary<string, int> levels, HashSet<(string, string)> backEdges) 
            GenerateLocationsWithPositions(List<UppaalLocation> locations, List<UppaalTransition> transitions, int templateIndex)
        {
            var locationElements = new List<XElement>();
            
            int verticalSpacing = 100;
            int horizontalSpacing = 170;
            int startY = -200;

            // Build adjacency graph
            var outgoing = new Dictionary<string, List<string>>();
            var incoming = new Dictionary<string, List<string>>();
            
            foreach (var trans in transitions)
            {
                if (!outgoing.ContainsKey(trans.Source))
                    outgoing[trans.Source] = new List<string>();
                if (!incoming.ContainsKey(trans.Target))
                    incoming[trans.Target] = new List<string>();
                    
                outgoing[trans.Source].Add(trans.Target);
                incoming[trans.Target].Add(trans.Source);
            }
            
            var entryLoc = locations.FirstOrDefault(l => l.Name == "Entry" || l.IsInitial);
            
            // --- Step 1: Identify true back-edges via DFS on-path detection ---
            var backEdges = new HashSet<(string, string)>();
            {
                var onPath = new HashSet<string>();
                var dfsVisited = new HashSet<string>();
                
                void FindBackEdges(string nodeId)
                {
                    onPath.Add(nodeId);
                    dfsVisited.Add(nodeId);
                    
                    if (outgoing.ContainsKey(nodeId))
                    {
                        foreach (var nextId in outgoing[nodeId])
                        {
                            if (onPath.Contains(nextId))
                            {
                                backEdges.Add((nodeId, nextId));
                            }
                            else if (!dfsVisited.Contains(nextId))
                            {
                                FindBackEdges(nextId);
                            }
                        }
                    }
                    
                    onPath.Remove(nodeId);
                }
                
                if (entryLoc != null)
                    FindBackEdges(entryLoc.Id);
            }
            
            // --- Step 2: Assign levels (Y) via longest-path from entry, ignoring back-edges ---
            var levels = new Dictionary<string, int>();
            if (entryLoc != null)
            {
                // Topological sort on DAG (ignoring back-edges)
                var inDegree = new Dictionary<string, int>();
                foreach (var loc in locations)
                    inDegree[loc.Id] = 0;
                    
                foreach (var trans in transitions)
                {
                    if (!backEdges.Contains((trans.Source, trans.Target)))
                    {
                        if (inDegree.ContainsKey(trans.Target))
                            inDegree[trans.Target]++;
                    }
                }
                
                // Use longest-path algorithm for nicer layout
                var dist = new Dictionary<string, int>();
                foreach (var loc in locations)
                    dist[loc.Id] = -1;
                dist[entryLoc.Id] = 0;
                
                var queue = new Queue<string>();
                queue.Enqueue(entryLoc.Id);
                
                // BFS in topological order, always taking max distance
                var processed = new HashSet<string>();
                int safetyCounter = 0;
                int maxIterations = locations.Count * locations.Count + 100;
                
                while (queue.Count > 0 && safetyCounter++ < maxIterations)
                {
                    var current = queue.Dequeue();
                    if (processed.Contains(current)) continue;
                    
                    // Check all predecessors (non-back-edge) have been processed
                    bool ready = true;
                    if (incoming.ContainsKey(current))
                    {
                        foreach (var pred in incoming[current])
                        {
                            if (!backEdges.Contains((pred, current)) && !processed.Contains(pred) && pred != current)
                            {
                                ready = false;
                                break;
                            }
                        }
                    }
                    
                    if (!ready && current != entryLoc.Id)
                    {
                        queue.Enqueue(current); // re-queue
                        continue;
                    }
                    
                    processed.Add(current);
                    
                    if (outgoing.ContainsKey(current))
                    {
                        foreach (var next in outgoing[current])
                        {
                            if (backEdges.Contains((current, next))) continue;
                            int newDist = dist[current] + 1;
                            if (!dist.ContainsKey(next) || newDist > dist[next])
                                dist[next] = newDist;
                            if (!processed.Contains(next))
                                queue.Enqueue(next);
                        }
                    }
                }
                
                foreach (var loc in locations)
                    levels[loc.Id] = dist.ContainsKey(loc.Id) && dist[loc.Id] >= 0 ? dist[loc.Id] : 0;
            }
            else
            {
                foreach (var loc in locations)
                    levels[loc.Id] = 0;
            }
            
            // --- Step 3: Assign X positions using DFS tree traversal to respect branch structure ---
            // This prevents edge crossings by keeping subtrees together.
            var positionMap = new Dictionary<string, (int x, int y)>();
            var xSlotAssigned = new Dictionary<string, double>();
            
            // Track the next available X slot globally (like a "cursor" moving left to right)
            double nextXSlot = 0;
            
            // DFS that assigns X positions in left-to-right subtree order
            var xVisited = new HashSet<string>();
            
            void AssignXPositions(string nodeId)
            {
                if (xVisited.Contains(nodeId)) return;
                xVisited.Add(nodeId);
                
                // Get non-back-edge children, sorted to keep left branch first
                var children = new List<string>();
                if (outgoing.ContainsKey(nodeId))
                {
                    foreach (var child in outgoing[nodeId])
                    {
                        if (!backEdges.Contains((nodeId, child)) && !xVisited.Contains(child))
                        {
                            children.Add(child);
                        }
                    }
                }
                
                if (children.Count == 0)
                {
                    // Leaf node: assign the next available slot
                    xSlotAssigned[nodeId] = nextXSlot;
                    nextXSlot++;
                }
                else
                {
                    // Internal node: recurse into children, then center over them
                    double firstChildSlot = double.MaxValue;
                    double lastChildSlot = double.MinValue;
                    
                    foreach (var child in children)
                    {
                        AssignXPositions(child);
                        if (xSlotAssigned.ContainsKey(child))
                        {
                            firstChildSlot = Math.Min(firstChildSlot, xSlotAssigned[child]);
                            lastChildSlot = Math.Max(lastChildSlot, xSlotAssigned[child]);
                        }
                    }
                    
                    if (firstChildSlot <= lastChildSlot)
                    {
                        xSlotAssigned[nodeId] = (firstChildSlot + lastChildSlot) / 2.0;
                    }
                    else
                    {
                        xSlotAssigned[nodeId] = nextXSlot;
                        nextXSlot++;
                    }
                }
            }
            
            if (entryLoc != null)
            {
                AssignXPositions(entryLoc.Id);
            }
            
            // Assign any unvisited nodes
            foreach (var loc in locations)
            {
                if (!xSlotAssigned.ContainsKey(loc.Id))
                {
                    xSlotAssigned[loc.Id] = nextXSlot;
                    nextXSlot++;
                }
            }
            
            // Convert slot positions to pixel coordinates, centered around x=0
            double minSlot = xSlotAssigned.Values.Min();
            double maxSlot = xSlotAssigned.Values.Max();
            double centerSlot = (minSlot + maxSlot) / 2.0;
            
            foreach (var loc in locations)
            {
                int level = levels[loc.Id];
                int y = startY + level * verticalSpacing;
                int x = (int)((xSlotAssigned[loc.Id] - centerSlot) * horizontalSpacing);
                positionMap[loc.Id] = (x, y);
            }

            // Generate location elements
            foreach (var location in locations)
            {
                var (x, y) = positionMap[location.Id];

                var locationElement = new XElement("location",
                    new XAttribute("id", location.Id),
                    new XAttribute("x", x),
                    new XAttribute("y", y),
                    new XElement("name",
                        new XAttribute("x", x - 45),
                        new XAttribute("y", y - 35),
                        location.Name
                    )
                );

                if (location.IsUrgent)
                    locationElement.Add(new XElement("urgent"));

                if (location.IsCommitted)
                    locationElement.Add(new XElement("committed"));

                foreach (var label in location.Labels)
                {
                    if (label.Key == "invariant")
                    {
                        locationElement.Add(new XElement("label",
                            new XAttribute("kind", label.Key),
                            new XAttribute("x", x + 50),
                            new XAttribute("y", y + 10),
                            label.Value
                        ));
                    }
                }

                locationElements.Add(locationElement);
            }
            
            return (locationElements, positionMap, levels, backEdges);
        }

        private XElement GenerateInitialLocation(List<UppaalLocation> locations)
        {
            var initialLocation = locations.FirstOrDefault(l => l.IsInitial);
            if (initialLocation == null)
            {
                // Default to first location if no initial is set
                initialLocation = locations.FirstOrDefault();
            }
            
            if (initialLocation == null)
                return null;

            return new XElement("init",
                new XAttribute("ref", initialLocation.Id)
            );
        }

        private IEnumerable<XElement> GenerateTransitions(List<UppaalTransition> transitions, 
            Dictionary<string, (int x, int y)> positionMap, 
            Dictionary<string, int> levels,
            HashSet<(string, string)> backEdges)
        {
            // Track how many transitions leave each source node for label offset
            var sourceEdgeIndex = new Dictionary<string, int>();
            
            // Build a list of all node positions for overlap detection
            var allPositions = positionMap.Values.ToList();
            
            // Pre-compute: for each source, count outgoing non-back-edge transitions
            var outgoingCount = new Dictionary<string, int>();
            foreach (var t in transitions)
            {
                if (!backEdges.Contains((t.Source, t.Target)))
                {
                    if (!outgoingCount.ContainsKey(t.Source))
                        outgoingCount[t.Source] = 0;
                    outgoingCount[t.Source]++;
                }
            }

            foreach (var transition in transitions)
            {
                var transitionElement = new XElement("transition",
                    new XElement("source", new XAttribute("ref", transition.Source)),
                    new XElement("target", new XAttribute("ref", transition.Target))
                );

                int labelX = 20;
                int labelY = -15;
                bool isBackEdge = backEdges.Contains((transition.Source, transition.Target));
                
                // Collect nail elements separately — UPPAAL DTD requires labels before nails
                var nailElements = new List<XElement>();

                if (positionMap.ContainsKey(transition.Source) && positionMap.ContainsKey(transition.Target))
                {
                    var sourcePos = positionMap[transition.Source];
                    var targetPos = positionMap[transition.Target];

                    if (isBackEdge)
                    {
                        // Back-edge: route to the left with nails
                        int leftMost = Math.Min(sourcePos.x, targetPos.x);
                        int nailX = leftMost - 150;
                        labelX = nailX - 10;
                        labelY = (sourcePos.y + targetPos.y) / 2;

                        nailElements.Add(new XElement("nail",
                            new XAttribute("x", nailX),
                            new XAttribute("y", sourcePos.y)
                        ));
                        nailElements.Add(new XElement("nail",
                            new XAttribute("x", nailX),
                            new XAttribute("y", targetPos.y)
                        ));
                    }
                    else
                    {
                        // Check if this edge would overlap with another edge from the same source.
                        // This happens when a condition node has two outgoing edges and one "skips"
                        // over intermediate nodes to reach a further target in the same column.
                        int levelDiff = (levels.ContainsKey(transition.Target) ? levels[transition.Target] : 0)
                                      - (levels.ContainsKey(transition.Source) ? levels[transition.Source] : 0);
                        int srcOutCount = outgoingCount.ContainsKey(transition.Source) ? outgoingCount[transition.Source] : 1;
                        
                        bool needsDetour = false;
                        
                        // If this source has multiple outgoing edges AND the edge spans more than 1 level
                        // AND the source and target are in roughly the same column, we need to detour
                        if (srcOutCount >= 2 && levelDiff > 1)
                        {
                            // Check if there are intermediate nodes between source and target
                            // that would cause visual overlap
                            int minY = Math.Min(sourcePos.y, targetPos.y);
                            int maxY = Math.Max(sourcePos.y, targetPos.y);
                            int xThreshold = 60; // consider "same column" if within this range
                            
                            foreach (var pos in allPositions)
                            {
                                if (pos.y > minY && pos.y < maxY && Math.Abs(pos.x - sourcePos.x) < xThreshold)
                                {
                                    needsDetour = true;
                                    break;
                                }
                            }
                        }
                        
                        if (needsDetour)
                        {
                            // Route this edge to the right side to avoid overlapping the straight-down edge
                            int detourX = Math.Max(sourcePos.x, targetPos.x) + 130;
                            
                            nailElements.Add(new XElement("nail",
                                new XAttribute("x", detourX),
                                new XAttribute("y", sourcePos.y + 30)
                            ));
                            nailElements.Add(new XElement("nail",
                                new XAttribute("x", detourX),
                                new XAttribute("y", targetPos.y - 30)
                            ));
                            
                            // Label goes beside the detour
                            labelX = detourX + 10;
                            labelY = (sourcePos.y + targetPos.y) / 2 - 10;
                        }
                        else
                        {
                            // Normal forward edge: place label at the midpoint
                            int midX = (sourcePos.x + targetPos.x) / 2;
                            int midY = (sourcePos.y + targetPos.y) / 2;

                            if (!sourceEdgeIndex.ContainsKey(transition.Source))
                                sourceEdgeIndex[transition.Source] = 0;
                            sourceEdgeIndex[transition.Source]++;

                            int dx = targetPos.x - sourcePos.x;
                            int sideOffset = dx < -20 ? -15 : (dx > 20 ? 15 : 8);
                            
                            labelX = midX + sideOffset;
                            labelY = midY - 15;
                        }
                    }
                }

                int currentLabelY = labelY;

                // Add select if present
                if (!string.IsNullOrEmpty(transition.Select))
                {
                    transitionElement.Add(new XElement("label",
                        new XAttribute("kind", "select"),
                        new XAttribute("x", labelX),
                        new XAttribute("y", currentLabelY),
                        transition.Select
                    ));
                    currentLabelY += 16;
                }

                // Add guard if present
                if (!string.IsNullOrEmpty(transition.Guard))
                {
                    transitionElement.Add(new XElement("label",
                        new XAttribute("kind", "guard"),
                        new XAttribute("x", labelX),
                        new XAttribute("y", currentLabelY),
                        transition.Guard
                    ));
                    currentLabelY += 16;
                }

                // Add synchronisation if present
                if (!string.IsNullOrEmpty(transition.Synchronization))
                {
                    transitionElement.Add(new XElement("label",
                        new XAttribute("kind", "synchronisation"),
                        new XAttribute("x", labelX),
                        new XAttribute("y", currentLabelY),
                        transition.Synchronization
                    ));
                    currentLabelY += 16;
                }

                // Add assignment (update) if present
                if (!string.IsNullOrEmpty(transition.Update))
                {
                    transitionElement.Add(new XElement("label",
                        new XAttribute("kind", "assignment"),
                        new XAttribute("x", labelX),
                        new XAttribute("y", currentLabelY),
                        transition.Update
                    ));
                }

                // Add nails AFTER all labels — UPPAAL DTD requires: source, target, label*, nail*
                foreach (var nail in nailElements)
                {
                    transitionElement.Add(nail);
                }

                yield return transitionElement;
            }
        }

        private XElement GenerateSystemDeclaration(List<UppaalTemplate> templates, HashSet<string> functionNames)
        {
            // Exclude templates that are generated as functions (they don't run as processes)
            var templateNames = templates.Where(t => !functionNames.Contains(t.Name)).Select(t => t.Name);
            var systemText = $"system {string.Join(", ", templateNames)};";

            return new XElement("system",
                new XText(systemText)
            );
        }

        private XElement GenerateQueries()
        {
            return new XElement("queries",
                new XElement("query",
                    new XElement("formula", "A[] not deadlock"),
                    new XElement("comment", "Verify that the system is deadlock-free")
                )
            );
        }
    }

    public class UppaalGenerationException : Exception
    {
        public UppaalGenerationException(string message) : base(message) { }
        public UppaalGenerationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// StringWriter that reports UTF-8 encoding so XDocument.Save produces encoding="utf-8" in the XML declaration.
    /// </summary>
    internal class Utf8StringWriter : System.IO.StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}