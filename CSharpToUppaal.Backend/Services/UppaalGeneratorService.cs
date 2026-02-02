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
                yield return new XElement("template",
                    new XElement("name", template.Name),
                    GenerateTemplateDeclaration(template),
                    GenerateLocations(template.Locations, templateIndex),
                    GenerateInitialLocation(template.Locations),
                    GenerateTransitions(template.Transitions)
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

        private IEnumerable<XElement> GenerateLocations(List<UppaalLocation> locations, int templateIndex)
        {
            // Use a hierarchical top-to-bottom layout similar to CFG visualization
            int locationCount = locations.Count;
            double nodeWidth = 120;
            double nodeHeight = 60;
            double horizontalSpacing = 200;
            double verticalSpacing = 120;
            double startX = 0;
            double startY = 0;

            for (int i = 0; i < locations.Count; i++)
            {
                var location = locations[i];
                
                // Simple vertical layout with some horizontal spread for branches
                int x = (int)(startX + (i % 3) * horizontalSpacing);
                int y = (int)(startY + (i / 3) * verticalSpacing);

                var locationElement = new XElement("location",
                    new XAttribute("id", location.Id),
                    new XAttribute("x", x),
                    new XAttribute("y", y),
                    new XElement("name",
                        new XAttribute("x", x - 30),
                        new XAttribute("y", y - 30),
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
                        new XAttribute("x", x - 40),
                        new XAttribute("y", y + 20),
                        label.Value
                    ));
                }

                yield return locationElement;
            }
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

        private IEnumerable<XElement> GenerateTransitions(List<UppaalTransition> transitions)
        {
            int transitionId = 0;
            
            foreach (var transition in transitions)
            {
                var transitionElement = new XElement("transition",
                    new XAttribute("id", $"id{transitionId}"),
                    new XElement("source", new XAttribute("ref", transition.Source)),
                    new XElement("target", new XAttribute("ref", transition.Target))
                );

                // Calculate offset for label positioning based on transition direction
                int labelOffsetX = 10;
                int labelOffsetY = -20;

                if (!string.IsNullOrEmpty(transition.Guard))
                {
                    transitionElement.Add(new XElement("label",
                        new XAttribute("kind", "guard"),
                        new XAttribute("x", labelOffsetX),
                        new XAttribute("y", labelOffsetY),
                        transition.Guard
                    ));
                    labelOffsetY += 20; // Stack labels vertically
                }

                if (!string.IsNullOrEmpty(transition.Update))
                {
                    transitionElement.Add(new XElement("label",
                        new XAttribute("kind", "assignment"),
                        new XAttribute("x", labelOffsetX),
                        new XAttribute("y", labelOffsetY),
                        transition.Update
                    ));
                    labelOffsetY += 20;
                }

                if (!string.IsNullOrEmpty(transition.Synchronization))
                {
                    transitionElement.Add(new XElement("label",
                        new XAttribute("kind", "synchronisation"),
                        new XAttribute("x", labelOffsetX),
                        new XAttribute("y", labelOffsetY),
                        transition.Synchronization
                    ));
                    labelOffsetY += 20;
                }

                if (transition.Comments.Any())
                {
                    transitionElement.Add(new XElement("label",
                        new XAttribute("kind", "comments"),
                        new XAttribute("x", labelOffsetX),
                        new XAttribute("y", labelOffsetY),
                        string.Join("\n", transition.Comments)
                    ));
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