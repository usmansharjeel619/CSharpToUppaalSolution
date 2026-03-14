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

                // Collect all method names so CFG generator can detect inter-method calls
                var allMethodNames = parseResult.Methods.Select(m => m.Name).ToHashSet();
                if (_cfgGenerator is CfgGeneratorService cfgGen)
                {
                    cfgGen.SetKnownMethodNames(allMethodNames);
                }

                // Generate CFG for each method and create templates
                var templates = new List<UppaalTemplate>();
                var cfgs = new List<ControlFlowGraph>();
                foreach (var method in parseResult.Methods)
                {
                    var cfg = await _cfgGenerator.GenerateCfgFromMethodAsync(method);
                    cfgs.Add(cfg);
                    var template = MapCfgToUppaalTemplate(cfg);
                    templates.Add(template);
                }

                model.Templates = templates;
                model.ParsedMethods = parseResult.Methods;
                model.XmlContent = await GenerateUppaalXmlAsync(model, cfgs);
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

                model.XmlContent = await GenerateUppaalXmlAsync(model, new List<ControlFlowGraph> { cfg });
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
                var cfgs = new List<ControlFlowGraph>();

                // Collect all method names first
                var allMethodNames = project.SourceFiles
                    .SelectMany(sf => sf.Methods)
                    .Select(m => m.Name)
                    .ToHashSet();
                if (_cfgGenerator is CfgGeneratorService cfgGen)
                {
                    cfgGen.SetKnownMethodNames(allMethodNames);
                }

                foreach (var sourceFile in project.SourceFiles)
                {
                    foreach (var method in sourceFile.Methods)
                    {
                        var cfg = await _cfgGenerator.GenerateCfgFromMethodAsync(method);
                        cfgs.Add(cfg);
                        var template = MapCfgToUppaalTemplate(cfg);
                        templates.Add(template);
                        allMethods.Add(method);
                    }
                }

                model.Templates = templates;
                model.ParsedMethods = allMethods;
                model.XmlContent = await GenerateUppaalXmlAsync(model, cfgs);
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
            return await GenerateUppaalXmlAsync(model, null);
        }

        /// <summary>
        /// Core XML generation. When cfgs are supplied, inter-method calls are modelled
        /// via synchronisation channels (Controller pattern) instead of UPPAAL functions.
        /// </summary>
        public async Task<string> GenerateUppaalXmlAsync(UppaalModel model, List<ControlFlowGraph> cfgs)
        {
            try
            {
                // --- Detect inter-method calls across all CFGs ---
                // Build a mapping: callerMethod -> list of calledMethod names (in order)
                var callerToCalls = new Dictionary<string, List<string>>();  // callerMethodName -> ordered called names
                var calledMethodNames = new HashSet<string>();
                var cfgByName = new Dictionary<string, ControlFlowGraph>();

                if (cfgs != null)
                {
                    foreach (var cfg in cfgs)
                        cfgByName[cfg.MethodName] = cfg;

                    foreach (var cfg in cfgs)
                    {
                        var callsInOrder = new List<string>();
                        foreach (var node in cfg.Nodes)
                        {
                            if (node.Type == NodeType.MethodCall && node.Properties.ContainsKey("calledMethod"))
                            {
                                string calledName = node.Properties["calledMethod"].ToString();
                                if (cfgByName.ContainsKey(calledName))
                                {
                                    callsInOrder.Add(calledName);
                                    calledMethodNames.Add(calledName);
                                }
                            }
                        }
                        if (callsInOrder.Count > 0)
                            callerToCalls[cfg.MethodName] = callsInOrder;
                    }
                }

                // Determine which templates are "callers" (they contain method call nodes)
                // and which are "callees" (they are called by other methods)
                bool hasSyncChannels = calledMethodNames.Count > 0;

                // --- Generate global declaration ---
                var globalDecl = GenerateGlobalDeclarationForSync(model, cfgs, calledMethodNames, cfgByName);

                // --- Generate templates ---
                var allTemplateElements = new List<XElement>();
                int globalIdCounter = 0;
                int templateIndex = 0;

                foreach (var template in model.Templates)
                {
                    bool isCaller = callerToCalls.ContainsKey(template.Name);
                    bool isCallee = calledMethodNames.Contains(template.Name);
                    var cfg = cfgByName.ContainsKey(template.Name) ? cfgByName[template.Name] : null;

                    var templateElement = GenerateSingleTemplate(
                        template, cfg, isCaller, isCallee, calledMethodNames,
                        ref globalIdCounter, templateIndex);
                    allTemplateElements.Add(templateElement);
                    templateIndex++;
                }

                // --- Generate system declaration ---
                var templateNames = model.Templates.Select(t => t.Name).ToList();
                var systemText = $"system {string.Join(", ", templateNames)};";

                var ntaElement = new XElement("nta",
                    new XElement("declaration", new XText(globalDecl)),
                    allTemplateElements,
                    new XElement("system", new XText(systemText)),
                    GenerateQueries()
                );

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

        /// <summary>
        /// Generates the global declaration block with shared variables and sync channels
        /// for the Controller/callee pattern.
        /// </summary>
        private string GenerateGlobalDeclarationForSync(
            UppaalModel model,
            List<ControlFlowGraph> cfgs,
            HashSet<string> calledMethodNames,
            Dictionary<string, ControlFlowGraph> cfgByName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// Global declarations");

            if (cfgs != null && calledMethodNames.Count > 0)
            {
                // Collect all variables used across all CFGs as shared globals
                // so that caller and callee can communicate results.
                sb.AppendLine();
                sb.AppendLine("// --- Shared variables for inter-method communication ---");
                var globalVars = new Dictionary<string, string>(); // varName -> uppaalType
                foreach (var cfg in cfgs)
                {
                    foreach (var kv in cfg.Variables)
                    {
                        if (!globalVars.ContainsKey(kv.Key))
                            globalVars[kv.Key] = MapCSharpTypeToUppaalType(kv.Value);
                    }
                    // Also collect parameters
                    foreach (var param in cfg.Parameters)
                    {
                        if (!globalVars.ContainsKey(param.Name))
                            globalVars[param.Name] = MapCSharpTypeToUppaalType(param.Type);
                    }
                }

                // Add shared_result variable for passing return values
                if (!globalVars.ContainsKey("shared_result"))
                    sb.AppendLine("int shared_result;");

                foreach (var kv in globalVars)
                {
                    sb.AppendLine($"{kv.Value} {kv.Key};");
                }

                // Generate sync channels for each callee method
                sb.AppendLine();
                sb.AppendLine("// --- Synchronization channels ---");
                foreach (var calleeName in calledMethodNames)
                {
                    string shortName = GetChannelPrefix(calleeName);
                    sb.AppendLine($"chan {shortName}_call, {shortName}_done;");
                }
            }

            return sb.ToString();
        }

        private string GetChannelPrefix(string methodName)
        {
            // Create a short channel prefix from method name
            // e.g. "GetBalance" -> "gb", "ProcessTransaction" -> "pt"
            if (string.IsNullOrEmpty(methodName)) return "m";

            // Use lowercase initials of camelCase/PascalCase parts
            var parts = new List<char>();
            parts.Add(char.ToLower(methodName[0]));
            for (int i = 1; i < methodName.Length; i++)
            {
                if (char.IsUpper(methodName[i]))
                    parts.Add(char.ToLower(methodName[i]));
            }
            string prefix = new string(parts.ToArray());
            // Ensure it's at least 2 chars
            if (prefix.Length < 2 && methodName.Length >= 2)
                prefix = methodName.Substring(0, 2).ToLower();
            return prefix;
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
            // Legacy overload — delegates to the new per-template method with no sync support
            int globalIdCounter = 0;
            int templateIndex = 0;
            foreach (var template in templates)
            {
                if (functionNames.Contains(template.Name))
                    continue;

                yield return GenerateSingleTemplate(
                    template, null, false, false, new HashSet<string>(),
                    ref globalIdCounter, templateIndex);
                templateIndex++;
            }
        }

        /// <summary>
        /// Generates a single UPPAAL &lt;template&gt; element.
        /// When isCaller is true, MethodCall nodes produce call!/done? sync transitions.
        /// When isCallee is true, the template wraps with a Waiting state and call?/done! channel edges,
        /// and the Exit state loops back to Waiting (no deadlock).
        /// ALL templates (caller and callee alike) loop from Exit back to their start
        /// so there is never a deadlock.
        /// </summary>
        private XElement GenerateSingleTemplate(
            UppaalTemplate template,
            ControlFlowGraph cfg,
            bool isCaller,
            bool isCallee,
            HashSet<string> calledMethodNames,
            ref int globalIdCounter,
            int templateIndex)
        {
            // Remap location IDs
            var idRemap = new Dictionary<string, string>();
            foreach (var loc in template.Locations)
            {
                var newId = $"id{globalIdCounter++}";
                idRemap[loc.Id] = newId;
                loc.Id = newId;
            }
            foreach (var trans in template.Transitions)
            {
                if (idRemap.ContainsKey(trans.Source))
                    trans.Source = idRemap[trans.Source];
                if (idRemap.ContainsKey(trans.Target))
                    trans.Target = idRemap[trans.Target];
            }

            // --- Handle callee wrapping ---
            if (isCallee)
            {
                WrapAsCallee(template, cfg, ref globalIdCounter);
            }

            // --- Handle caller: expand MethodCall nodes into call!/done? pairs ---
            if (isCaller && cfg != null)
            {
                ExpandMethodCallNodes(template, cfg, calledMethodNames, ref globalIdCounter);
                // Clear local declarations — all variables are promoted to global scope
                template.Declaration = "";
            }

            // --- For ALL templates: ensure Exit loops back to the initial location ---
            // This prevents deadlock. Callees already have this (Done->Waiting),
            // but callers / standalone methods need it too.
            if (!isCallee)
            {
                AddExitToEntryLoopBack(template, cfg, ref globalIdCounter);
            }

            // Build the position layout and XML elements
            var (locationElements, positionMap, levels, backEdges) =
                GenerateLocationsWithPositions(template.Locations, template.Transitions, templateIndex);

            var initialLoc = template.Locations.FirstOrDefault(l => l.IsInitial)
                         ?? template.Locations.FirstOrDefault();
            string initialLocId = initialLoc?.Id ?? "";

            return new XElement("template",
                new XElement("name", template.Name),
                GenerateTemplateDeclaration(template),
                locationElements,
                GenerateInitialLocation(template.Locations),
                GenerateTransitions(template.Transitions, positionMap, levels, backEdges, initialLocId)
            );
        }

        /// <summary>
        /// Adds an Exit → Entry loop-back for non-callee templates (callers / standalone).
        /// Exit transitions are redirected through a Done (urgent) location that resets
        /// variables and returns to Entry, ensuring no deadlock.
        /// </summary>
        private void AddExitToEntryLoopBack(UppaalTemplate template, ControlFlowGraph cfg, ref int globalIdCounter)
        {
            var entryLoc = template.Locations.FirstOrDefault(l => l.IsInitial)
                        ?? template.Locations.FirstOrDefault(l => l.Name == "Entry");
            var exitLoc = template.Locations.FirstOrDefault(l => l.Name == "Exit");

            if (entryLoc == null || exitLoc == null) return;

            // Check if there's already a transition from Exit back (avoid duplicates)
            bool alreadyHasLoopBack = template.Transitions.Any(t => t.Source == exitLoc.Id);
            if (alreadyHasLoopBack) return;

            // Create a Done location (urgent, fires immediately)
            var doneLoc = new UppaalLocation
            {
                Id = $"id{globalIdCounter++}",
                Name = "Done",
                IsUrgent = true
            };
            template.Locations.Add(doneLoc);

            // Exit --> Done
            template.Transitions.Add(new UppaalTransition
            {
                Source = exitLoc.Id,
                Target = doneLoc.Id
            });

            // Done --> Entry (reset variables)
            var resetVars = new List<string>();
            if (cfg != null)
            {
                foreach (var kv in cfg.Variables)
                {
                    var sanitized = SanitizeVariableNameForUppaal(kv.Key);
                    resetVars.Add($"{sanitized} = 0");
                }
            }

            template.Transitions.Add(new UppaalTransition
            {
                Source = doneLoc.Id,
                Target = entryLoc.Id,
                Update = resetVars.Count > 0 ? string.Join(", ", resetVars) : ""
            });
        }

        /// <summary>
        /// Wraps a callee template with Waiting/Done states and sync channel edges.
        /// The Exit location's outgoing edges are replaced so that it signals done!
        /// and loops back to Waiting.
        /// </summary>
        private void WrapAsCallee(UppaalTemplate template, ControlFlowGraph cfg, ref int globalIdCounter)
        {
            string channelPrefix = GetChannelPrefix(template.Name);

            // Find the Entry and Exit locations
            var entryLoc = template.Locations.FirstOrDefault(l => l.Name == "Entry");
            var exitLoc = template.Locations.FirstOrDefault(l => l.Name == "Exit");

            // Create Waiting location (new initial state)
            var waitingLoc = new UppaalLocation
            {
                Id = $"id{globalIdCounter++}",
                Name = "Waiting",
                IsInitial = true
            };

            // Un-mark old Entry as initial
            if (entryLoc != null)
                entryLoc.IsInitial = false;

            // Add Waiting at the beginning
            template.Locations.Insert(0, waitingLoc);

            // Add transition: Waiting --[call?]--> Entry
            template.Transitions.Add(new UppaalTransition
            {
                Source = waitingLoc.Id,
                Target = entryLoc?.Id ?? template.Locations[1].Id,
                Synchronization = $"{channelPrefix}_call?"
            });

            if (exitLoc != null)
            {
                // Create a Done location (signals done)
                var doneLoc = new UppaalLocation
                {
                    Id = $"id{globalIdCounter++}",
                    Name = "Done",
                    IsUrgent = true  // urgent so it fires immediately
                };
                template.Locations.Add(doneLoc);

                // Remove all transitions FROM Exit
                template.Transitions.RemoveAll(t => t.Source == exitLoc.Id);

                // Exit --> Done: signal done! and optionally publish shared_result
                // Look at the Exit node to find if there's a return value
                var returnUpdate = "";
                if (cfg != null)
                {
                    var returnNode = cfg.Nodes.LastOrDefault(n => n.Type == NodeType.Return);
                    if (returnNode != null && !string.IsNullOrEmpty(returnNode.Code))
                    {
                        // Extract the return expression, e.g. "return balance;" -> "shared_result = balance"
                        var retCode = returnNode.Code.Trim().TrimEnd(';');
                        if (retCode.StartsWith("return "))
                        {
                            var retExpr = retCode.Substring("return ".Length).Trim();
                            if (!string.IsNullOrEmpty(retExpr))
                                returnUpdate = $"shared_result = {retExpr}";
                        }
                    }
                }

                template.Transitions.Add(new UppaalTransition
                {
                    Source = exitLoc.Id,
                    Target = doneLoc.Id,
                    Update = returnUpdate,
                    Synchronization = $"{channelPrefix}_done!"
                });

                // Done --> Waiting: reset local variables and loop back
                var resetVars = new List<string>();
                if (cfg != null)
                {
                    foreach (var kv in cfg.Variables)
                    {
                        var sanitized = SanitizeVariableNameForUppaal(kv.Key);
                        resetVars.Add($"{sanitized} = 0");
                    }
                }

                template.Transitions.Add(new UppaalTransition
                {
                    Source = doneLoc.Id,
                    Target = waitingLoc.Id,
                    Update = resetVars.Count > 0 ? string.Join(", ", resetVars) : ""
                });
            }

            // Clear the template's local declaration — variables are now global
            template.Declaration = "";
        }

        private string SanitizeVariableNameForUppaal(string name)
        {
            if (string.IsNullOrEmpty(name)) return "var";
            var sanitized = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (sanitized.Length > 0 && !char.IsLetter(sanitized[0]) && sanitized[0] != '_')
                sanitized = "_" + sanitized;
            if (string.IsNullOrEmpty(sanitized)) return "var";
            return sanitized;
        }

        /// <summary>
        /// Expands MethodCall CFG nodes into call!/done? synchronization transition pairs
        /// in the caller template.
        /// For each MethodCall node, we replace it with:
        ///   ... --> SendCall --[x_call!]--> WaitDone --[x_done?]--> next ...
        /// </summary>
        private void ExpandMethodCallNodes(
            UppaalTemplate template,
            ControlFlowGraph cfg,
            HashSet<string> calledMethodNames,
            ref int globalIdCounter)
        {
            // Build a mapping from CFG node IDs to UPPAAL location IDs
            // We need to find which locations correspond to MethodCall nodes
            var cfgNodeToLocId = new Dictionary<string, string>();
            for (int i = 0; i < cfg.Nodes.Count && i < template.Locations.Count; i++)
            {
                cfgNodeToLocId[cfg.Nodes[i].Id] = template.Locations[i].Id;
            }

            // Find all MethodCall nodes
            var methodCallNodes = cfg.Nodes.Where(n => n.Type == NodeType.MethodCall).ToList();

            foreach (var callNode in methodCallNodes)
            {
                if (!callNode.Properties.ContainsKey("calledMethod")) continue;
                string calledName = callNode.Properties["calledMethod"].ToString();
                if (!calledMethodNames.Contains(calledName)) continue;

                string channelPrefix = GetChannelPrefix(calledName);

                // Find the corresponding location in the template
                if (!cfgNodeToLocId.ContainsKey(callNode.Id)) continue;
                string callLocId = cfgNodeToLocId[callNode.Id];

                var callLoc = template.Locations.FirstOrDefault(l => l.Id == callLocId);
                if (callLoc == null) continue;

                // Rename the call location to indicate it's a call send
                callLoc.Name = $"Call_{calledName}";
                callLoc.IsUrgent = true; // Send immediately

                // Create WaitDone location
                var waitDoneLoc = new UppaalLocation
                {
                    Id = $"id{globalIdCounter++}",
                    Name = $"Wait_{calledName}"
                };
                template.Locations.Add(waitDoneLoc);

                // Build argument assignment for the call (write args to shared variables)
                var argAssignments = new List<string>();
                if (callNode.Properties.ContainsKey("argCount"))
                {
                    int argCount = Convert.ToInt32(callNode.Properties["argCount"]);
                    // For now, assign args to global variables based on the callee's parameter names
                    // We need to look up the callee's parameter names
                    var calleeMethod = cfg.Parameters; // This is the caller's params, not callee's
                    for (int i = 0; i < argCount; i++)
                    {
                        if (callNode.Properties.ContainsKey($"arg{i}"))
                        {
                            // The argument expression
                            string argExpr = callNode.Properties[$"arg{i}"].ToString();
                            // We'll assign to a shared variable — the callee will read from globals
                            // This is handled by the global declaration
                        }
                    }
                }

                // Find all outgoing transitions from callLoc and redirect them through WaitDone
                var outgoingFromCall = template.Transitions.Where(t => t.Source == callLocId).ToList();
                foreach (var trans in outgoingFromCall)
                {
                    // Move these transitions to come from WaitDone instead
                    trans.Source = waitDoneLoc.Id;

                    // If there's an assignTarget, add the assignment on the done? edge
                    if (callNode.Properties.ContainsKey("assignTarget"))
                    {
                        string assignTarget = callNode.Properties["assignTarget"].ToString();
                        trans.Update = string.IsNullOrEmpty(trans.Update)
                            ? $"{assignTarget} = shared_result"
                            : $"{assignTarget} = shared_result, {trans.Update}";
                    }
                }

                // Find all incoming transitions TO callLoc and add the call! sync
                var incomingToCall = template.Transitions.Where(t => t.Target == callLocId).ToList();

                // Add transition: callLoc --[call!]--> WaitDone
                template.Transitions.Add(new UppaalTransition
                {
                    Source = callLocId,
                    Target = waitDoneLoc.Id,
                    Synchronization = $"{channelPrefix}_call!"
                });

                // Add transition: WaitDone --[done?]--> (already handled by redirected transitions)
                // Actually, we need to add the done? sync to the outgoing transitions from WaitDone
                foreach (var trans in outgoingFromCall)
                {
                    trans.Synchronization = $"{channelPrefix}_done?";
                }
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
            HashSet<(string, string)> backEdges,
            string initialLocationId)
        {
            // Track how many transitions leave each source node for label offset
            var sourceEdgeIndex = new Dictionary<string, int>();
            
            // Build a list of all node positions for overlap detection
            var allPositions = positionMap.Values.ToList();

            // Track the rightmost nail X used by previous RIGHT-side back-edges
            // and the leftmost nail X for LEFT-side back-edges so they stack apart.
            int maxBackEdgeNailX = int.MinValue;   // for right-side (exit loops)
            int minBackEdgeNailX = int.MaxValue;   // for left-side  (for loops)
            
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
                        // Determine whether this back-edge is an "exit loop" (Done→Waiting /
                        // Done→Entry) or an "internal loop" (e.g. for-loop increment → condition).
                        // Exit loops target the initial location; internal loops don't.
                        bool isExitLoop = transition.Target == initialLocationId;

                        int minBackY = Math.Min(sourcePos.y, targetPos.y);
                        int maxBackY = Math.Max(sourcePos.y, targetPos.y);

                        if (isExitLoop)
                        {
                            // ---- EXIT LOOP: route RIGHT ----
                            int rightMost = Math.Max(sourcePos.x, targetPos.x);
                            foreach (var pos in allPositions)
                            {
                                if (pos.y >= minBackY - 20 && pos.y <= maxBackY + 20)
                                    if (pos.x > rightMost) rightMost = pos.x;
                            }

                            int nailX = rightMost + 170;
                            if (maxBackEdgeNailX != int.MinValue && nailX <= maxBackEdgeNailX)
                                nailX = maxBackEdgeNailX + 80;
                            maxBackEdgeNailX = Math.Max(maxBackEdgeNailX, nailX);

                            labelX = nailX + 10;
                            labelY = (sourcePos.y + targetPos.y) / 2;

                            nailElements.Add(new XElement("nail",
                                new XAttribute("x", nailX),
                                new XAttribute("y", sourcePos.y)));
                            nailElements.Add(new XElement("nail",
                                new XAttribute("x", nailX),
                                new XAttribute("y", targetPos.y)));
                        }
                        else
                        {
                            // ---- INTERNAL LOOP (for / while): route LEFT ----
                            int leftMost = Math.Min(sourcePos.x, targetPos.x);
                            foreach (var pos in allPositions)
                            {
                                if (pos.y >= minBackY - 20 && pos.y <= maxBackY + 20)
                                    if (pos.x < leftMost) leftMost = pos.x;
                            }

                            int nailX = leftMost - 150;
                            if (minBackEdgeNailX != int.MaxValue && nailX >= minBackEdgeNailX)
                                nailX = minBackEdgeNailX - 80;
                            minBackEdgeNailX = Math.Min(minBackEdgeNailX, nailX);

                            labelX = nailX - 10;
                            labelY = (sourcePos.y + targetPos.y) / 2;

                            nailElements.Add(new XElement("nail",
                                new XAttribute("x", nailX),
                                new XAttribute("y", sourcePos.y)));
                            nailElements.Add(new XElement("nail",
                                new XAttribute("x", nailX),
                                new XAttribute("y", targetPos.y)));
                        }
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
            // All templates are now instantiated as processes (no more UPPAAL functions)
            var templateNames = templates.Select(t => t.Name);
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