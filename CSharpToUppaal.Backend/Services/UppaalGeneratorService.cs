using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using CSharpToUppaal.Backend.Generators;
using CSharpToUppaal.Backend.Mappers;
using CSharpToUppaal.Backend.Models;
using CSharpToUppaal.Backend.Parsers;

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

                foreach (var sourceFile in project.SourceFiles)
                {
                    foreach (var method in sourceFile.Methods)
                    {
                        var cfg = await _cfgGenerator.GenerateCfgFromMethodAsync(method);
                        var template = MapCfgToUppaalTemplate(cfg);
                        templates.Add(template);
                    }
                }

                model.Templates = templates;
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
                var ntaElement = new XElement("nta",
                    GenerateGlobalDeclaration(),
                    GenerateTemplates(model.Templates),
                    GenerateSystemDeclaration(model.Templates),
                    GenerateQueries()
                );

                var xdoc = new XDocument(
                    new XDeclaration("1.0", "utf-8", null),
                    new XDocumentType("nta",
                        "-//Uppaal Team//DTD Flat System 1.6//EN",
                        "http://www.it.uu.se/research/group/darts/uppaal/flat-1_6.dtd",
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

        private XElement GenerateGlobalDeclaration()
        {
            return new XElement("declaration",
                new XText(@"
// Global declarations generated from C# code
typedef int[0,100] id_t;
broadcast chan request, response;
clock globalClock;

// Global variables
int[0,10] globalCounter = 0;
bool systemActive = true;
")
            );
        }

        private IEnumerable<XElement> GenerateTemplates(List<UppaalTemplate> templates)
        {
            int templateIndex = 0;
            
            foreach (var template in templates)
            {
                // Store position info for transition generation
                var (locationElements, positionMap, levels) = GenerateLocationsWithPositions(template.Locations, template.Transitions, templateIndex);
                
                yield return new XElement("template",
                    new XElement("name", template.Name),
                    GenerateTemplateDeclaration(template),
                    locationElements,
                    GenerateInitialLocation(template.Locations),
                    GenerateTransitions(template.Transitions, positionMap, levels)
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

        private (IEnumerable<XElement> elements, Dictionary<string, (int x, int y)> positions, Dictionary<string, int> levels) 
            GenerateLocationsWithPositions(List<UppaalLocation> locations, List<UppaalTransition> transitions, int templateIndex)
        {
            var locationElements = new List<XElement>();
            
            // Reduced spacing for better compact layout
            double horizontalSpacing = 200;
            double verticalSpacing = 100;
            double startX = 0;
            double startY = -200;

            // Build adjacency graph to understand structure
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
            
            // Assign levels using modified BFS that handles cycles properly
            var levels = new Dictionary<string, int>();
            var visited = new HashSet<string>();
            var entryLoc = locations.FirstOrDefault(l => l.Name == "Entry" || l.IsInitial);
            
            // First pass: Identify back-edges (edges that go to already visited or same-level nodes)
            var backEdges = new HashSet<(string, string)>();
            
            void DFS(string nodeId, int depth, HashSet<string> pathSet)
            {
                if (pathSet.Contains(nodeId))
                {
                    // Found a back-edge
                    return;
                }
                
                pathSet.Add(nodeId);
                
                if (outgoing.ContainsKey(nodeId))
                {
                    foreach (var nextId in outgoing[nodeId])
                    {
                        if (pathSet.Contains(nextId) || (levels.ContainsKey(nextId) && levels[nextId] <= depth))
                        {
                            // This is a back-edge
                            backEdges.Add((nodeId, nextId));
                        }
                        else
                        {
                            DFS(nextId, depth + 1, pathSet);
                        }
                    }
                }
                
                pathSet.Remove(nodeId);
            }
            
            if (entryLoc != null)
            {
                // Do DFS to find back-edges
                DFS(entryLoc.Id, 0, new HashSet<string>());
                
                // Now do BFS ignoring back-edges
                var queue = new Queue<(string id, int level)>();
                queue.Enqueue((entryLoc.Id, 0));
                levels[entryLoc.Id] = 0;
                visited.Add(entryLoc.Id);
                
                while (queue.Count > 0)
                {
                    var (currentId, currentLevel) = queue.Dequeue();
                    
                    if (outgoing.ContainsKey(currentId))
                    {
                        foreach (var nextId in outgoing[currentId])
                        {
                            // Skip back-edges
                            if (backEdges.Contains((currentId, nextId)))
                                continue;
                                
                            if (!levels.ContainsKey(nextId))
                            {
                                levels[nextId] = currentLevel + 1;
                                visited.Add(nextId);
                                queue.Enqueue((nextId, currentLevel + 1));
                            }
                            else
                            {
                                // Update to max level
                                levels[nextId] = Math.Max(levels[nextId], currentLevel + 1);
                            }
                        }
                    }
                }
                
                // Assign level to any unreached nodes (shouldn't happen in well-formed CFG)
                foreach (var loc in locations)
                {
                    if (!levels.ContainsKey(loc.Id))
                    {
                        levels[loc.Id] = 0;
                    }
                }
            }
            
            // Assign horizontal positions for nodes at the same level (for branching)
            var levelNodes = new Dictionary<int, List<string>>();
            foreach (var loc in locations)
            {
                int level = levels.ContainsKey(loc.Id) ? levels[loc.Id] : 0;
                if (!levelNodes.ContainsKey(level))
                    levelNodes[level] = new List<string>();
                levelNodes[level].Add(loc.Id);
            }
            
            // Calculate final positions
            var positionMap = new Dictionary<string, (int x, int y)>();
            
            foreach (var loc in locations)
            {
                int level = levels.ContainsKey(loc.Id) ? levels[loc.Id] : 0;
                int y = (int)(startY + level * verticalSpacing);
                
                var nodesAtLevel = levelNodes[level];
                int indexAtLevel = nodesAtLevel.IndexOf(loc.Id);
                int totalAtLevel = nodesAtLevel.Count;
                
                int x;
                if (totalAtLevel == 1)
                {
                    // Single node at this level - center it
                    x = (int)startX;
                }
                else if (totalAtLevel == 2)
                {
                    // Two nodes - place left and right
                    x = (int)(startX + (indexAtLevel == 0 ? -horizontalSpacing/2 : horizontalSpacing/2));
                }
                else
                {
                    // Multiple nodes - spread them out
                    double totalWidth = (totalAtLevel - 1) * horizontalSpacing / 1.5;
                    x = (int)(startX - totalWidth/2 + indexAtLevel * (horizontalSpacing / 1.5));
                }
                
                positionMap[loc.Id] = (x, y);
            }

            // Generate location elements
            foreach (var location in locations)
            {
                if (!positionMap.ContainsKey(location.Id))
                {
                    positionMap[location.Id] = (0, 0);
                }
                
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
                    locationElement.Add(new XElement("label",
                        new XAttribute("kind", label.Key),
                        new XAttribute("x", x + 50),
                        new XAttribute("y", y + 10),
                        label.Value
                    ));
                }

                locationElements.Add(locationElement);
            }
            
            return (locationElements, positionMap, levels);
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
            Dictionary<string, int> levels)
        {
            int transitionId = 0;
            
            foreach (var transition in transitions)
            {
                var transitionElement = new XElement("transition",
                    new XAttribute("id", $"id{transitionId}"),
                    new XElement("source", new XAttribute("ref", transition.Source)),
                    new XElement("target", new XAttribute("ref", transition.Target))
                );

                // Position labels to the side of edges
                int labelOffsetX = 20;
                int labelOffsetY = -15;

                // Add select if present (for parametric models)
                if (!string.IsNullOrEmpty(transition.Select))
                {
                    transitionElement.Add(new XElement("label",
                        new XAttribute("kind", "select"),
                        new XAttribute("x", labelOffsetX),
                        new XAttribute("y", labelOffsetY),
                        transition.Select
                    ));
                    labelOffsetY += 16;
                }

                // Add guard if present
                if (!string.IsNullOrEmpty(transition.Guard))
                {
                    transitionElement.Add(new XElement("label",
                        new XAttribute("kind", "guard"),
                        new XAttribute("x", labelOffsetX),
                        new XAttribute("y", labelOffsetY),
                        transition.Guard
                    ));
                    labelOffsetY += 16;
                }

                // Add synchronisation if present
                if (!string.IsNullOrEmpty(transition.Synchronization))
                {
                    transitionElement.Add(new XElement("label",
                        new XAttribute("kind", "synchronisation"),
                        new XAttribute("x", labelOffsetX),
                        new XAttribute("y", labelOffsetY),
                        transition.Synchronization
                    ));
                    labelOffsetY += 16;
                }

                // Add assignment (update) if present
                if (!string.IsNullOrEmpty(transition.Update))
                {
                    transitionElement.Add(new XElement("label",
                        new XAttribute("kind", "assignment"),
                        new XAttribute("x", labelOffsetX),
                        new XAttribute("y", labelOffsetY),
                        transition.Update
                    ));
                }

                // Detect back-edges (loop-back edges) and add nail points to avoid overlap
                if (positionMap.ContainsKey(transition.Source) && positionMap.ContainsKey(transition.Target))
                {
                    var sourcePos = positionMap[transition.Source];
                    var targetPos = positionMap[transition.Target];
                    int sourceLevel = levels.ContainsKey(transition.Source) ? levels[transition.Source] : 0;
                    int targetLevel = levels.ContainsKey(transition.Target) ? levels[transition.Target] : 0;
                    
                    // If target is at same level or higher (back-edge/loop-back)
                    if (targetLevel <= sourceLevel)
                    {
                        // Add nail points to curve the edge to the left
                        int nailOffset = -150; // Go 150 pixels to the left
                        
                        // Add two nails to create a nice curve on the left side
                        transitionElement.Add(new XElement("nail",
                            new XAttribute("x", sourcePos.x + nailOffset),
                            new XAttribute("y", sourcePos.y)
                        ));
                        transitionElement.Add(new XElement("nail",
                            new XAttribute("x", targetPos.x + nailOffset),
                            new XAttribute("y", targetPos.y)
                        ));
                    }
                }

                yield return transitionElement;
                transitionId++;
            }
        }

        private XElement GenerateSystemDeclaration(List<UppaalTemplate> templates)
        {
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
}