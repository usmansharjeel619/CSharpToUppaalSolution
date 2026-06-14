using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CSharpToUppaal.Backend.Models;

namespace CSharpToUppaal.Backend.Services
{
    public interface IUppaalCompatibilityValidator
    {
        UppaalCompatibilityResult Validate(string uppaalXml);
    }

    public sealed class UppaalCompatibilityValidator : IUppaalCompatibilityValidator
    {
        private static readonly HashSet<string> TransitionLabelKinds = new(StringComparer.Ordinal)
        {
            "select",
            "guard",
            "synchronisation",
            "assignment"
        };

        private static readonly HashSet<string> LocationLabelKinds = new(StringComparer.Ordinal)
        {
            "invariant",
            "exponentialrate",
            "comments"
        };

        public UppaalCompatibilityResult Validate(string uppaalXml)
        {
            var result = new UppaalCompatibilityResult();
            if (string.IsNullOrWhiteSpace(uppaalXml))
            {
                Add(result, UppaalCompatibilitySeverity.Error, "XML", "document", "UPPAAL XML is empty.");
                return result;
            }

            XDocument doc;
            try
            {
                doc = XDocument.Parse(RemoveDoctype(uppaalXml), LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex)
            {
                Add(result, UppaalCompatibilitySeverity.Error, "XML", "document", $"XML is not well formed: {ex.Message}");
                return result;
            }

            var nta = doc.Element("nta");
            if (nta == null)
            {
                Add(result, UppaalCompatibilitySeverity.Error, "XML", "document", "Root <nta> element is missing.");
                return result;
            }

            ValidateTemplates(result, nta);
            ValidateDeclarations(result, nta);
            ValidateQueries(result, nta);
            ValidateGeneratedFunctionCalls(result, nta);

            if (result.Issues.Count == 0)
            {
                Add(result, UppaalCompatibilitySeverity.Info, "Compatibility", "document", "UPPAAL 4.1.18 static readiness checks passed.");
            }

            return result;
        }

        private static void ValidateTemplates(UppaalCompatibilityResult result, XElement nta)
        {
            var templates = nta.Elements("template").ToList();
            foreach (var duplicate in templates
                .Select(t => (Name: t.Element("name")?.Value?.Trim() ?? string.Empty, Element: t))
                .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .GroupBy(t => t.Name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1))
            {
                Add(result, UppaalCompatibilitySeverity.Error, "Template", duplicate.Key, $"Duplicate template name '{duplicate.Key}'.");
            }

            foreach (var template in templates)
            {
                var templateName = template.Element("name")?.Value?.Trim() ?? "template";
                var locations = template.Elements("location").ToList();
                var ids = locations
                    .Select(l => l.Attribute("id")?.Value ?? string.Empty)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var duplicate in locations
                    .Select(l => l.Element("name")?.Value?.Trim() ?? string.Empty)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .GroupBy(n => n, StringComparer.Ordinal)
                    .Where(g => g.Count() > 1))
                {
                    Add(result, UppaalCompatibilitySeverity.Error, "Location", $"{templateName}/{duplicate.Key}", $"Duplicate location name '{duplicate.Key}' in template '{templateName}'.");
                }

                var initRef = template.Element("init")?.Attribute("ref")?.Value;
                if (string.IsNullOrWhiteSpace(initRef))
                {
                    Add(result, UppaalCompatibilitySeverity.Error, "Init", templateName, $"Template '{templateName}' has no <init> reference.");
                }
                else if (!ids.Contains(initRef))
                {
                    Add(result, UppaalCompatibilitySeverity.Error, "Init", $"{templateName}/{initRef}", $"Template '{templateName}' init reference '{initRef}' does not match any location.");
                }

                foreach (var location in locations)
                {
                    var locationName = location.Element("name")?.Value?.Trim() ?? location.Attribute("id")?.Value ?? "location";
                    foreach (var label in location.Elements("label"))
                    {
                        var kind = label.Attribute("kind")?.Value ?? string.Empty;
                        if (!LocationLabelKinds.Contains(kind))
                        {
                            Add(result, UppaalCompatibilitySeverity.Error, "Label", $"{templateName}/{locationName}", $"Location label kind '{kind}' is not supported by UPPAAL 4.1.18.");
                        }
                    }
                }

                foreach (var transition in template.Elements("transition"))
                {
                    var source = transition.Element("source")?.Attribute("ref")?.Value ?? string.Empty;
                    var target = transition.Element("target")?.Attribute("ref")?.Value ?? string.Empty;
                    var position = $"{templateName}/{source}->{target}";

                    if (!ids.Contains(source))
                        Add(result, UppaalCompatibilitySeverity.Error, "Transition", position, $"Transition source '{source}' does not match any location.");
                    if (!ids.Contains(target))
                        Add(result, UppaalCompatibilitySeverity.Error, "Transition", position, $"Transition target '{target}' does not match any location.");

                    var seenNail = false;
                    foreach (var child in transition.Elements())
                    {
                        if (child.Name.LocalName == "nail")
                        {
                            seenNail = true;
                            continue;
                        }

                        if (child.Name.LocalName != "label")
                            continue;

                        if (seenNail)
                        {
                            Add(result, UppaalCompatibilitySeverity.Error, "Transition", position, "Transition labels must appear before nails.");
                        }

                        var kind = child.Attribute("kind")?.Value ?? string.Empty;
                        if (!TransitionLabelKinds.Contains(kind))
                        {
                            Add(result, UppaalCompatibilitySeverity.Error, "Label", position, $"Transition label kind '{kind}' is not supported by UPPAAL 4.1.18.");
                        }
                    }
                }
            }
        }

        private static void ValidateDeclarations(UppaalCompatibilityResult result, XElement nta)
        {
            foreach (var declaration in nta.Elements("template").Select(t => (Template: t.Element("name")?.Value?.Trim() ?? "template", Text: t.Element("declaration")?.Value ?? string.Empty)))
            {
                foreach (var duplicate in FindDuplicateSimpleDeclarations(declaration.Text))
                {
                    Add(result, UppaalCompatibilitySeverity.Error, "Declaration", declaration.Template, $"Duplicate declaration of '{duplicate}' in template '{declaration.Template}'.");
                }
            }

            var global = nta.Element("declaration")?.Value ?? string.Empty;
            foreach (var duplicate in Regex.Matches(global, @"\b(?:void|int|bool)\s+(fn_[A-Za-z_][A-Za-z0-9_]*)\s*\(")
                .Select(m => m.Groups[1].Value)
                .GroupBy(n => n, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key))
            {
                Add(result, UppaalCompatibilitySeverity.Error, "Declaration", "global", $"Duplicate generated function '{duplicate}'.");
            }
        }

        private static IEnumerable<string> FindDuplicateSimpleDeclarations(string declaration)
        {
            return Regex.Matches(declaration, @"^\s*(?:int|bool|clock|chan)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:;|=)", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .GroupBy(n => n, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
        }

        private static void ValidateQueries(UppaalCompatibilityResult result, XElement nta)
        {
            foreach (var query in nta.Descendants("query"))
            {
                var formula = query.Element("formula")?.Value?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(formula))
                {
                    Add(result, UppaalCompatibilitySeverity.Warning, "Query", "query", "Query formula is empty.");
                    continue;
                }

                if (!formula.StartsWith("A[]", StringComparison.Ordinal)
                    && !formula.StartsWith("E<>", StringComparison.Ordinal)
                    && !formula.StartsWith("A<>", StringComparison.Ordinal)
                    && !formula.StartsWith("E[]", StringComparison.Ordinal)
                    && !formula.Contains("-->", StringComparison.Ordinal))
                {
                    Add(result, UppaalCompatibilitySeverity.Warning, "Query", formula, $"Query '{formula}' is not one of the supported UPPAAL symbolic query forms.");
                }
            }
        }

        private static void ValidateGeneratedFunctionCalls(UppaalCompatibilityResult result, XElement nta)
        {
            var declarations = nta.Element("declaration")?.Value ?? string.Empty;
            var declaredFunctions = Regex.Matches(declarations, @"\b(?:void|int|bool)\s+(fn_[A-Za-z_][A-Za-z0-9_]*)\s*\(")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            var allText = string.Join(Environment.NewLine, nta.Descendants("label").Select(l => l.Value))
                + Environment.NewLine
                + declarations;

            foreach (var call in Regex.Matches(allText, @"\b(fn_[A-Za-z_][A-Za-z0-9_]*)\s*\(")
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .Where(name => !declaredFunctions.Contains(name)))
            {
                Add(result, UppaalCompatibilitySeverity.Error, "FunctionCall", call, $"Generated function call '{call}' has no declaration.");
            }
        }

        private static void Add(UppaalCompatibilityResult result, UppaalCompatibilitySeverity severity, string category, string position, string message)
        {
            result.Issues.Add(new UppaalCompatibilityIssue
            {
                Severity = severity,
                Category = category,
                Position = position,
                Message = message
            });
        }

        private static string RemoveDoctype(string xml)
        {
            var lines = xml.Split('\n');
            return string.Join("\n", lines.Where(l => !l.TrimStart().StartsWith("<!DOCTYPE", StringComparison.Ordinal)));
        }
    }
}
