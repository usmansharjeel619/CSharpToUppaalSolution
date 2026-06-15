using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CSharpToUppaal.Backend.Models;

namespace CSharpToUppaal.Backend.Services
{
    public interface IRequirementTranslationService
    {
        Task<List<RequirementInterpretation>> InterpretAsync(
            string requirementsText,
            RequirementTranslationContext context,
            OllamaRequirementSettings settings,
            CancellationToken cancellationToken = default);
    }

    public class RequirementTranslationContext
    {
        public List<FunctionDescriptor> Functions { get; set; } = new();
        public List<string> Variables { get; set; } = new();
    }

    public class RequirementTranslationService : IRequirementTranslationService
    {
        private readonly HttpClient _httpClient;

        public RequirementTranslationService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
        }

        public string LastUsedSource { get; private set; } = "rules";
        public string LastError { get; private set; } = string.Empty;

        public async Task<List<RequirementInterpretation>> InterpretAsync(
            string requirementsText,
            RequirementTranslationContext context,
            OllamaRequirementSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastError = string.Empty;
            var lines = SplitRequirements(requirementsText);
            if (lines.Count == 0)
                return new List<RequirementInterpretation>();

            if (settings.Enabled)
            {
                try
                {
                    var ollama = await TryInterpretWithOllamaAsync(lines, context, settings, cancellationToken)
                        .ConfigureAwait(false);
                    if (ollama.Count > 0)
                    {
                        LastUsedSource = "ollama";
                        return ollama;
                    }

                    LastError = "Ollama returned no results — falling back to rules.";
                }
                catch (Exception ex)
                {
                    LastError = $"Ollama unavailable ({ex.Message}) — falling back to rules.";
                }
            }

            LastUsedSource = "rules";
            return lines.Select(line => InterpretWithRules(line, context)).ToList();
        }

        private async Task<List<RequirementInterpretation>> TryInterpretWithOllamaAsync(
            List<string> lines,
            RequirementTranslationContext context,
            OllamaRequirementSettings settings,
            CancellationToken cancellationToken)
        {
            var baseUrl = settings.BaseUrl.TrimEnd('/');
            var endpoint = $"{baseUrl}/chat";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)));

            var schema = new
            {
                type = "object",
                properties = new
                {
                    requirements = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                text = new { type = "string" },
                                kind = new { type = "string", @enum = new[] { "Reachability", "Safety", "Liveness", "LeadsTo", "DeadlockFreedom", "Unknown" } },
                                formula = new { type = "string" },
                                comment = new { type = "string" },
                                confidence = new { type = "number" }
                            },
                            required = new[] { "text", "kind", "formula", "comment", "confidence" }
                        }
                    }
                },
                required = new[] { "requirements" }
            };

            var prompt = new StringBuilder();
            prompt.AppendLine("Translate each design requirement into an executable UPPAAL symbolic query.");
            prompt.AppendLine("Allowed forms: A[] predicate, E<> predicate, A<> predicate, E[] predicate, or trigger --> target.");
            prompt.AppendLine("Use only known process/location names and variables. If not mappable, return kind Unknown and an empty formula.");
            prompt.AppendLine("Known functions/processes:");
            foreach (var function in context.Functions)
                prompt.AppendLine($"- {function.DisplayName}, process {ProcessName(function)}, done location {ProcessName(function)}.Done");
            prompt.AppendLine("Known variables:");
            foreach (var variable in context.Variables.Distinct(StringComparer.Ordinal))
                prompt.AppendLine($"- {Sanitize(variable)}");
            prompt.AppendLine("Requirements:");
            foreach (var line in lines)
                prompt.AppendLine($"- {line}");

            var payload = new
            {
                model = settings.Model,
                stream = false,
                format = schema,
                options = new { temperature = 0 },
                messages = new[]
                {
                    new { role = "system", content = "You only return JSON matching the provided schema." },
                    new { role = "user", content = prompt.ToString() }
                }
            };

            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(endpoint, content, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var responseJson = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var root = JsonNode.Parse(responseJson);
            var messageContent = root?["message"]?["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(messageContent))
                return new List<RequirementInterpretation>();

            var interpreted = JsonNode.Parse(messageContent);
            var requirementNodes = interpreted?["requirements"]?.AsArray();
            if (requirementNodes == null)
                return new List<RequirementInterpretation>();

            var results = new List<RequirementInterpretation>();
            foreach (var node in requirementNodes)
            {
                var text = node?["text"]?.GetValue<string>() ?? string.Empty;
                var kindText = node?["kind"]?.GetValue<string>() ?? "Unknown";
                var formula = node?["formula"]?.GetValue<string>() ?? string.Empty;
                var comment = node?["comment"]?.GetValue<string>() ?? string.Empty;
                var confidence = node?["confidence"]?.GetValue<double>() ?? 0;

                var interpretation = new RequirementInterpretation
                {
                    RequirementText = text,
                    Kind = Enum.TryParse<RequirementKind>(kindText, out var kind) ? kind : RequirementKind.Unknown,
                    Confidence = confidence,
                    Status = ValidateFormula(formula, context) ? "Mapped" : "Needs review"
                };

                if (!string.IsNullOrWhiteSpace(formula) && interpretation.Status == "Mapped")
                {
                    var queryName = BuildQueryName(text, results.Count + 1);
                    interpretation.GeneratedQueries.Add(new GeneratedQuery
                    {
                        Name = queryName,
                        Formula = formula,
                        Comment = string.IsNullOrWhiteSpace(comment) ? text : comment,
                        Source = "ollama",
                        IsEditable = true,
                        IsValidated = true
                    });
                }

                results.Add(interpretation);
            }

            return results;
        }

        private static RequirementInterpretation InterpretWithRules(string requirement, RequirementTranslationContext context)
        {
            var lower = requirement.ToLowerInvariant();
            var interpretation = new RequirementInterpretation
            {
                RequirementText = requirement,
                Confidence = 0.65,
                Status = "Mapped by rules"
            };

            if (lower.Contains("deadlock", StringComparison.Ordinal))
            {
                interpretation.Kind = RequirementKind.DeadlockFreedom;
                interpretation.GeneratedQueries.Add(new GeneratedQuery
                {
                    Name = "Req_NoDeadlock",
                    Formula = "A[] not deadlock",
                    Comment = requirement,
                    Source = "rules"
                });
                return interpretation;
            }

            var matchedFunction = context.Functions.FirstOrDefault(f =>
                lower.Contains(f.Name.ToLowerInvariant(), StringComparison.Ordinal)
                || lower.Contains(f.DisplayName.ToLowerInvariant(), StringComparison.Ordinal));

            if (matchedFunction != null && (lower.Contains("eventually", StringComparison.Ordinal)
                                            || lower.Contains("reach", StringComparison.Ordinal)
                                            || lower.Contains("complete", StringComparison.Ordinal)
                                            || lower.Contains("finish", StringComparison.Ordinal)))
            {
                interpretation.Kind = RequirementKind.Reachability;
                interpretation.GeneratedQueries.Add(new GeneratedQuery
                {
                    Name = $"Req_Reach_{Sanitize(matchedFunction.Name)}",
                    Formula = $"E<> {ProcessName(matchedFunction)}.Done",
                    Comment = requirement,
                    Source = "rules"
                });
                return interpretation;
            }

            var predicate = ExtractPredicate(requirement, context);
            if (!string.IsNullOrWhiteSpace(predicate))
            {
                interpretation.Kind = lower.Contains("eventually", StringComparison.Ordinal)
                    ? RequirementKind.Liveness
                    : RequirementKind.Safety;
                interpretation.Predicate = predicate;
                interpretation.GeneratedQueries.Add(new GeneratedQuery
                {
                    Name = $"Req_{interpretation.Kind}",
                    Formula = interpretation.Kind == RequirementKind.Liveness ? $"A<> {predicate}" : $"A[] {predicate}",
                    Comment = requirement,
                    Source = "rules"
                });
                return interpretation;
            }

            interpretation.Kind = RequirementKind.Unknown;
            interpretation.Confidence = 0.1;
            interpretation.Status = "Needs review";
            return interpretation;
        }

        private static string ExtractPredicate(string requirement, RequirementTranslationContext context)
        {
            foreach (var variable in context.Variables.Distinct(StringComparer.Ordinal))
            {
                var idx = requirement.IndexOf(variable, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    continue;

                var predicate = requirement[idx..]
                    .Replace(" is ", " == ", StringComparison.OrdinalIgnoreCase)
                    .Replace(" equals ", " == ", StringComparison.OrdinalIgnoreCase)
                    .Replace(" not equal ", " != ", StringComparison.OrdinalIgnoreCase);

                return SanitizePredicate(predicate, context);
            }

            return string.Empty;
        }

        private static string SanitizePredicate(string predicate, RequirementTranslationContext context)
        {
            var result = predicate;
            foreach (var variable in context.Variables.Distinct(StringComparer.Ordinal))
                result = result.Replace(variable, Sanitize(variable), StringComparison.OrdinalIgnoreCase);

            return result.Trim().TrimEnd('.');
        }

        private static string BuildQueryName(string requirementText, int index)
        {
            var words = requirementText
                .Split(new[] { ' ', '\t', '.', ',', ':', ';', '!' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Take(3)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
            var suffix = string.Concat(words);
            return string.IsNullOrWhiteSpace(suffix) ? $"Req_{index}" : $"Req_{Sanitize(suffix)}";
        }

        private static bool ValidateFormula(string formula, RequirementTranslationContext context)
        {
            if (string.IsNullOrWhiteSpace(formula))
                return false;

            if (formula.Contains("deadlock", StringComparison.Ordinal))
                return true;

            return context.Functions.Any(f => formula.Contains(ProcessName(f), StringComparison.Ordinal))
                || context.Variables.Any(v => formula.Contains(Sanitize(v), StringComparison.Ordinal));
        }

        private static List<string> SplitRequirements(string requirementsText)
        {
            return requirementsText
                .Split(new[] { "\r\n", "\n", ";" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim().TrimStart('-', '*').Trim())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .ToList();
        }

        private static string ProcessName(FunctionDescriptor function)
            => Sanitize($"P_{function.DisplayName}");

        private static string Sanitize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "value";

            var sb = new StringBuilder();
            foreach (var ch in raw)
                sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');

            var value = sb.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(value))
                value = "value";
            if (!char.IsLetter(value[0]) && value[0] != '_')
                value = "_" + value;

            return value.Length > 80 ? value[..80] : value;
        }
    }
}
