using System.Text.RegularExpressions;
using System.Xml.Linq;
using CSharpToUppaal.Backend.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToUppaal.Backend.Services;

/// <summary>Validates the supported symbolic-query grammar against emitted declarations and locations.</summary>
public static class QueryValidationService
{
    public static RequirementKind Category(string formula)
    {
        var f = formula.Trim();
        if (f == "A[] not deadlock") return RequirementKind.Sanity;
        if (f.StartsWith("A<>") || f.Contains("-->")) return RequirementKind.Liveness;
        if (f.StartsWith("E<>" ) || f.StartsWith("E[]")) return RequirementKind.Reachability;
        return RequirementKind.Safety;
    }
    public static RequirementTranslationContext FromModel(UppaalModel model)
    {
        var context = FromXml(model.XmlContent);
        context.Functions = model.GenerationReport.IncludedFunctions;
        context.ProcessNames = model.GenerationReport.ProcessNames;
        return context;
    }

    public static RequirementTranslationContext FromXml(string xml)
    {
        var context = new RequirementTranslationContext { IsGeneratedContext = true };
        var doc = XDocument.Parse(xml);
        void Declarations(string text, string prefix)
        {
            text = Regex.Replace(text, @"//[^\r\n]*|/\*[\s\S]*?\*/", "");
            foreach (Match m in Regex.Matches(text, @"\b(?:const\s+)?(int(?:\s*\[[^\]]+\])?|bool|clock)\s+([A-Za-z_]\w*)\s*(?:=|;|,)") )
                context.SymbolTypes[prefix + m.Groups[2].Value] = m.Groups[1].Value == "bool" ? "bool" : "int";
        }
        Declarations(doc.Root?.Element("declaration")?.Value ?? "", "");
        foreach (var template in doc.Descendants("template"))
        {
            var name = template.Element("name")!.Value;
            Declarations(template.Element("declaration")?.Value ?? "", name + ".");
            foreach (var location in template.Elements("location"))
            {
                var loc = location.Element("name")?.Value;
                if (!string.IsNullOrEmpty(loc))
                {
                    context.SymbolTypes[name + "." + loc] = "bool";
                    context.Locations.Add(name + "." + loc);
                }
            }
        }
        foreach (var key in context.SymbolTypes.Keys.Except(context.Locations))
            context.VariableReferences[key] = key;
        foreach (var group in context.VariableReferences.Keys.ToList().GroupBy(k => k.Split('.').Last(), StringComparer.OrdinalIgnoreCase))
            if (group.Count() == 1) context.VariableReferences[group.Key] = group.First();
        context.Variables = context.VariableReferences.Keys.ToList();
        return context;
    }

    public static bool Validate(string formula, RequirementTranslationContext context, out string diagnostic)
    {
        diagnostic = "";
        try
        {
            var match = Regex.Match(formula.Trim(), @"^(A\[\]|E<>|A<>|E\[\])\s+(.+)$", RegexOptions.Singleline);
            var predicates = match.Success ? new[] { match.Groups[2].Value } : formula.Split("-->", StringSplitOptions.TrimEntries);
            if (!match.Success && predicates.Length != 2) throw new FormatException("Choose a supported temporal operator.");
            foreach (var predicate in predicates)
            {
                var normalized = Regex.Replace(predicate, @"\band\b", "&&");
                normalized = Regex.Replace(normalized, @"\bor\b", "||");
                normalized = Regex.Replace(normalized, @"\bnot\b", "!");
                var expression = SyntaxFactory.ParseExpression(normalized);
                if (expression.ContainsDiagnostics || expression.ToFullString() != normalized) throw new FormatException("Invalid predicate syntax.");
                if (TypeOf(expression, context) != "bool") throw new FormatException("A query predicate must be Boolean.");
            }
            return true;
        }
        catch (FormatException ex) { diagnostic = ex.Message; return false; }
    }

    private static string TypeOf(ExpressionSyntax e, RequirementTranslationContext c)
    {
        if (e is ParenthesizedExpressionSyntax p) return TypeOf(p.Expression, c);
        if (e is LiteralExpressionSyntax l)
        {
            if (l.IsKind(SyntaxKind.TrueLiteralExpression) || l.IsKind(SyntaxKind.FalseLiteralExpression)) return "bool";
            if (l.Token.Value is int) return "int";
        }
        if (e is IdentifierNameSyntax or MemberAccessExpressionSyntax)
        {
            var name = e.ToString();
            if (name == "deadlock") return "bool";
            if (c.SymbolTypes.TryGetValue(name, out var type)) return type;
            if (!c.IsGeneratedContext)
            {
                if (c.VariableReferences.Values.Contains(name) || c.Variables.Contains(name)) return "int";
                if (c.Functions.Any(f => name == c.Process(f) + ".Done")) return "bool";
            }
            throw new FormatException($"Unknown or out-of-scope symbol: {name}.");
        }
        if (e is PrefixUnaryExpressionSyntax u)
        {
            var type = TypeOf(u.Operand, c);
            if (u.IsKind(SyntaxKind.LogicalNotExpression) && type == "bool") return "bool";
            if ((u.IsKind(SyntaxKind.UnaryMinusExpression) || u.IsKind(SyntaxKind.UnaryPlusExpression)) && type == "int") return "int";
        }
        if (e is BinaryExpressionSyntax b)
        {
            var left = TypeOf(b.Left, c); var right = TypeOf(b.Right, c);
            var op = b.OperatorToken.Text;
            if (op is "&&" or "||" && left == "bool" && right == "bool") return "bool";
            if (op is "==" or "!=" && left == right) return "bool";
            if (op is "<" or ">" or "<=" or ">=" && left == "int" && right == "int") return "bool";
            if (op is "+" or "-" or "*" or "/" or "%" && left == "int" && right == "int") return "int";
        }
        throw new FormatException($"Unsupported expression or incompatible types: {e}.");
    }

    public static string ApplyQueries(string xml, IEnumerable<GeneratedQuery> queries)
    {
        var context = FromXml(xml);
        var doc = XDocument.Parse(xml);
        var output = new XElement("queries");
        foreach (var q in queries)
        {
            q.IsValidated = Validate(q.Formula, context, out var diagnostic);
            q.ValidationDiagnostics = diagnostic;
            if (!q.IsValidated) throw new InvalidOperationException($"{q.Name}: {diagnostic}");
            output.Add(new XElement("query", new XElement("formula", q.Formula), new XElement("comment", q.Comment)));
        }
        doc.Root!.Element("queries")?.Remove();
        doc.Root.Add(output);
        return doc.ToString();
    }
}
