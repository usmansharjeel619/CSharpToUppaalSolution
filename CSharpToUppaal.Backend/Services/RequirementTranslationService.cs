using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
        /// <summary>
        /// Maps a source-level variable name to the UPPAAL expression that refers to
        /// it in the generated system (for example, deposits -> P_Account_Main.deposits).
        /// </summary>
        public Dictionary<string, string> VariableReferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                string detail;
                try
                {
                    var errNode = JsonNode.Parse(errorBody);
                    detail = errNode?["error"]?.GetValue<string>() ?? errorBody;
                }
                catch
                {
                    detail = errorBody;
                }
                if (string.IsNullOrWhiteSpace(detail))
                    detail = response.ReasonPhrase ?? response.StatusCode.ToString();
                throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
            }
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
            foreach (var variable in context.Variables
                         .Concat(context.VariableReferences.Keys)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(variable => variable.Length))
            {
                var variablePattern = Regex.Escape(variable);
                var qualitativeComparison = Regex.Match(
                    requirement,
                    $@"(?ix)^\s*(?:the\s+)?{variablePattern}\s+
                        (?:(?:must|should)\s+)?
                        (?:(?:remain|stay|be|is)\s+)?
                        (?<quality>positive|negative|non[-\s]?negative|non[-\s]?positive)\s*\.?\s*$");

                if (qualitativeComparison.Success)
                {
                    var qualitativeOperator = qualitativeComparison.Groups["quality"].Value.ToLowerInvariant() switch
                    {
                        "positive" => "> 0",
                        "negative" => "< 0",
                        "non-negative" or "nonnegative" => ">= 0",
                        "non-positive" or "nonpositive" => "<= 0",
                        _ => string.Empty
                    };

                    if (!string.IsNullOrEmpty(qualitativeOperator))
                        return $"{ResolveVariableReference(variable, context)} {qualitativeOperator}";
                }

                var comparison = Regex.Match(
                    requirement,
                    $@"(?ix)^\s*(?:the\s+)?{variablePattern}\s+
                        (?:(?:must|should)(?:\s+be)?\s+|is\s+)?
                        (?<operator>
                            greater\s+than|more\s+than|above|
                            at\s+least|not\s+less\s+than|
                            less\s+than|fewer\s+than|below|
                            at\s+most|no\s+more\s+than|not\s+greater\s+than|
                            not\s+equal\s+to|different\s+from|
                            equal\s+to|equals|equal\s+to|is
                        )\s+
                        (?<value>-?\d+|true|false)\s*\.?\s*$");

                if (!comparison.Success)
                    continue;

                var op = comparison.Groups["operator"].Value.ToLowerInvariant() switch
                {
                    "greater than" or "more than" or "above" => ">",
                    "at least" or "not less than" => ">=",
                    "less than" or "fewer than" or "below" => "<",
                    "at most" or "no more than" or "not greater than" => "<=",
                    "not equal to" or "different from" => "!=",
                    "equal to" or "equals" or "is" => "==",
                    _ => string.Empty
                };

                if (string.IsNullOrEmpty(op))
                    continue;

                return $"{ResolveVariableReference(variable, context)} {op} {comparison.Groups["value"].Value.ToLowerInvariant()}";
            }

            return string.Empty;
        }

        private static string ResolveVariableReference(string variable, RequirementTranslationContext context)
        {
            return context.VariableReferences.TryGetValue(variable, out var reference)
                ? reference
                : Sanitize(variable);
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

            var normalized = formula.Trim();
            if (normalized.Equals("A[] not deadlock", StringComparison.Ordinal))
                return true;

            // Reject natural-language text even when it contains a valid variable name.
            if (Regex.IsMatch(normalized, @"\b(must|should|greater than|less than|equal to)\b", RegexOptions.IgnoreCase))
                return false;

            var queryMatch = Regex.Match(normalized, @"^(?:A\[\]|E<>|A<>|E\[\])\s+(.+)$");
            var leadsToMatch = Regex.Match(normalized, @"^(.+)\s+-->\s+(.+)$");
            if (!queryMatch.Success && !leadsToMatch.Success)
                return false;

            if (!Regex.IsMatch(normalized, @"(?:==|!=|>=|<=|(?<!-)>(?!>)|(?<!<)<(?!<)|\.Done\b|\btrue\b|\bfalse\b)"))
                return false;

            return context.Functions.Any(function => normalized.Contains(ProcessName(function), StringComparison.Ordinal))
                || context.VariableReferences.Values.Any(reference => normalized.Contains(reference, StringComparison.Ordinal))
                || context.Variables.Any(variable => normalized.Contains(Sanitize(variable), StringComparison.Ordinal));
        }

        private static List<string> SplitRequirements(string requirementsText)
        {
            return requirementsText
                .Split(new[] { "\r\n", "\n", ";" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim().TrimStart('-', '*').Trim())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .ToList();
        }

        public static string ProcessName(FunctionDescriptor function)
            => Sanitize($"P_{function.DisplayName}");

        public static string Sanitize(string raw)
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
