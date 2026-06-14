using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using CSharpToUppaal.Backend.Models;

#nullable disable
namespace CSharpToUppaal.Backend.Verification
{
    public interface IUppaalVerifier
    {
        Task<VerificationSummary> VerifyModelAsync(UppaalModel model);
        Task<bool> CheckModelValidityAsync(string xmlContent);
        Task<List<VerificationProperty>> VerifyPropertiesAsync(string xmlContent, List<string> properties);
    }

    public class UppaalVerifier : IUppaalVerifier
    {
        private readonly string _verifytaPath;

        public UppaalVerifier(string verifytaPath = null)
        {
            _verifytaPath = verifytaPath ?? FindVerifytaPath();
        }

        public async Task<VerificationSummary> VerifyModelAsync(UppaalModel model)
        {
            var summary = new VerificationSummary
            {
                VerifiedAt = DateTime.UtcNow
            };

            try
            {
                Console.WriteLine($"Verifying UPPAAL model: {model.Name}");

                var stopwatch = Stopwatch.StartNew();

                if (!IsVerifytaConfigured())
                {
                    stopwatch.Stop();
                    summary.VerificationTime = stopwatch.Elapsed;
                    summary.Properties.Add(new VerificationProperty
                    {
                        Name = "VerifytaNotConfigured",
                        Formula = "A[] not deadlock",
                        IsVerified = false,
                        Result = "NotConfigured",
                        ErrorMessage = "UPPAAL verifyta executable was not found. Configure the verifyta path in Settings."
                    });
                    summary.TotalProperties = 1;
                    summary.FailedProperties = 1;
                    return summary;
                }

                // Create temporary files
                var tempXmlFile = System.IO.Path.GetTempFileName() + ".xml";
                var tempQueryFile = System.IO.Path.GetTempFileName() + ".q";

                await System.IO.File.WriteAllTextAsync(tempXmlFile, model.XmlContent);
                await System.IO.File.WriteAllTextAsync(tempQueryFile, GenerateQueryFile(model.XmlContent));

                // Run verifyta
                var result = await RunVerifytaAsync(tempXmlFile, tempQueryFile);

                stopwatch.Stop();
                summary.VerificationTime = stopwatch.Elapsed;

                // Parse results
                var properties = ParseVerificationOutput(result);
                summary.Properties = properties;
                summary.TotalProperties = properties.Count;
                summary.VerifiedProperties = properties.Count(p => p.IsVerified);
                summary.FailedProperties = properties.Count(p => !p.IsVerified);

                // Cleanup
                System.IO.File.Delete(tempXmlFile);
                System.IO.File.Delete(tempQueryFile);

                Console.WriteLine($"Verification completed for {model.Name}: {summary.VerifiedProperties}/{summary.TotalProperties} properties verified");

                return summary;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error verifying model: {model.Name}: {ex.Message}");
                summary.Properties.Add(new VerificationProperty
                {
                    Name = "Error",
                    IsVerified = false,
                    ErrorMessage = ex.Message
                });
                return summary;
            }
        }

        public async Task<bool> CheckModelValidityAsync(string xmlContent)
        {
            try
            {
                if (!IsVerifytaConfigured())
                    return false;

                var tempFile = System.IO.Path.GetTempFileName() + ".xml";
                await System.IO.File.WriteAllTextAsync(tempFile, xmlContent);

                var query = "// Simple query to check model validity\nE<> true";
                var tempQueryFile = System.IO.Path.GetTempFileName() + ".q";
                await System.IO.File.WriteAllTextAsync(tempQueryFile, query);

                var result = await RunVerifytaAsync(tempFile, tempQueryFile);

                System.IO.File.Delete(tempFile);
                System.IO.File.Delete(tempQueryFile);

                return !result.Contains("error") && !result.Contains("Error");
            }
            catch
            {
                return false;
            }
        }

        public async Task<List<VerificationProperty>> VerifyPropertiesAsync(string xmlContent, List<string> properties)
        {
            var verifiedProperties = new List<VerificationProperty>();

            if (!IsVerifytaConfigured())
            {
                foreach (var property in properties)
                {
                    verifiedProperties.Add(new VerificationProperty
                    {
                        Name = $"Property_{verifiedProperties.Count + 1}",
                        Formula = property,
                        IsVerified = false,
                        Result = "NotConfigured",
                        ErrorMessage = "UPPAAL verifyta executable was not found. Configure the verifyta path in Settings."
                    });
                }

                return verifiedProperties;
            }

            foreach (var property in properties)
            {
                var verificationProperty = new VerificationProperty
                {
                    Name = $"Property_{verifiedProperties.Count + 1}",
                    Formula = property
                };

                try
                {
                    var tempXmlFile = System.IO.Path.GetTempFileName() + ".xml";
                    var tempQueryFile = System.IO.Path.GetTempFileName() + ".q";

                    await System.IO.File.WriteAllTextAsync(tempXmlFile, xmlContent);
                    await System.IO.File.WriteAllTextAsync(tempQueryFile, property);

                    var stopwatch = Stopwatch.StartNew();
                    var result = await RunVerifytaAsync(tempXmlFile, tempQueryFile);
                    stopwatch.Stop();

                    verificationProperty.VerificationTime = stopwatch.Elapsed;
                    verificationProperty.IsVerified = ParseSingleResult(result);
                    verificationProperty.Result = result;

                    // Cleanup
                    System.IO.File.Delete(tempXmlFile);
                    System.IO.File.Delete(tempQueryFile);
                }
                catch (Exception ex)
                {
                    verificationProperty.IsVerified = false;
                    verificationProperty.ErrorMessage = ex.Message;
                }

                verifiedProperties.Add(verificationProperty);
            }

            return verifiedProperties;
        }

        private async Task<string> RunVerifytaAsync(string modelFile, string queryFile)
        {
            if (!IsVerifytaConfigured())
            {
                throw new VerificationException("UPPAAL verifyta executable was not found. Configure the verifyta path in Settings.");
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = _verifytaPath,
                Arguments = $"\"{modelFile}\" \"{queryFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0 || !string.IsNullOrEmpty(error))
            {
                throw new VerificationException($"UPPAAL verification error: {error}{Environment.NewLine}{output}");
            }

            return output;
        }

        private string GenerateQueryFile(string xmlContent)
        {
            try
            {
                var doc = XDocument.Parse(RemoveDoctype(xmlContent));
                var formulas = doc.Descendants("query")
                    .Select(q => q.Element("formula")?.Value?.Trim())
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (formulas.Count > 0)
                    return string.Join(Environment.NewLine, formulas);
            }
            catch
            {
                // Fall through to the mandatory safety check.
            }

            return "A[] not deadlock";
        }

        private List<VerificationProperty> ParseVerificationOutput(string output)
        {
            var properties = new List<VerificationProperty>();
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.Contains("is satisfied") || line.Contains("is not satisfied"))
                {
                    var parts = line.Split(':');
                    if (parts.Length >= 2)
                    {
                        var property = new VerificationProperty
                        {
                            Formula = parts[0].Trim(),
                            IsVerified = line.Contains("is satisfied"),
                            Result = parts[1].Trim()
                        };

                        // Extract property name from formula
                        if (property.Formula.Contains("deadlock"))
                            property.Name = "NoDeadlock";
                        else if (property.Formula.Contains("Stable"))
                            property.Name = "ReachStableState";
                        else if (property.Formula.Contains("Request"))
                            property.Name = "RequestResponse";
                        else if (property.Formula.Contains("globalCounter"))
                            property.Name = "CounterBound";
                        else
                            property.Name = $"Property_{properties.Count + 1}";

                        properties.Add(property);
                    }
                }
            }

            return properties;
        }

        private bool ParseSingleResult(string output)
        {
            return output.Contains("is satisfied");
        }

        private bool IsVerifytaConfigured()
        {
            if (string.IsNullOrWhiteSpace(_verifytaPath))
                return false;

            if (System.IO.File.Exists(_verifytaPath))
                return true;

            return false;
        }

        private static string RemoveDoctype(string xml)
        {
            var lines = xml.Split('\n');
            return string.Join("\n", lines.Where(l => !l.TrimStart().StartsWith("<!DOCTYPE", StringComparison.Ordinal)));
        }

        private string FindVerifytaPath()
        {
            // Common UPPAAL installation paths
            var possiblePaths = new[]
            {
                @"C:\Program Files\UPPAAL-5.0.0\app\bin\verifyta.exe",
                @"C:\Program Files\Uppaal\bin-Windows\verifyta.exe",
                @"C:\Program Files (x86)\Uppaal\bin-Windows\verifyta.exe",
                @"C:\Uppaal\bin-Windows\verifyta.exe",
                @"/usr/local/bin/verifyta",
                @"/usr/bin/verifyta"
            };

            foreach (var path in possiblePaths)
            {
                if (System.IO.File.Exists(path))
                    return path;
            }

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathEnv.Split(System.IO.Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                var candidate = System.IO.Path.Combine(dir, OperatingSystem.IsWindows() ? "verifyta.exe" : "verifyta");
                if (System.IO.File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }

    public class VerificationException : Exception
    {
        public VerificationException(string message) : base(message) { }
        public VerificationException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
