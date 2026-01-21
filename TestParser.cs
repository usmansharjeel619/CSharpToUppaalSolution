using System;
using System.Threading.Tasks;
using CSharpToUppaal.Backend.Parsers;

namespace CSharpToUppaal.Test
{
    class TestParser
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Testing CSharp Parser Backend ===\n");

            var parser = new CSharpParser();

            string testCode = @"using System;

namespace Example
{
    public class Calculator
    {
        public int Add(int a, int b)
        {
            if (a > 0 && b > 0)
            {
                return a + b;
            }
            else
            {
                return 0;
            }
        }
        
        public int Factorial(int n)
        {
            int result = 1;
            for (int i = 2; i <= n; i++)
            {
                result *= i;
            }
            return result;
        }
    }
}";

            Console.WriteLine("Test Code:");
            Console.WriteLine(testCode);
            Console.WriteLine("\n" + new string('=', 60) + "\n");

            try
            {
                Console.WriteLine("Calling ParseSourceCodeAsync...\n");
                var result = await parser.ParseSourceCodeAsync(testCode);

                Console.WriteLine($"\n=== Parse Result ===");
                Console.WriteLine($"Success: {result.Success}");
                Console.WriteLine($"Classes Found: {result.Classes.Count}");
                Console.WriteLine($"Methods Found: {result.Methods.Count}");
                Console.WriteLine($"Errors: {result.Errors.Count}");

                if (result.Errors.Count > 0)
                {
                    Console.WriteLine("\nErrors:");
                    foreach (var error in result.Errors)
                    {
                        Console.WriteLine($"  - {error.Severity}: {error.Message}");
                    }
                }

                if (result.Classes.Count > 0)
                {
                    Console.WriteLine("\nClasses:");
                    foreach (var cls in result.Classes)
                    {
                        Console.WriteLine($"  - {cls.Name} (Public: {cls.IsPublic})");
                    }
                }

                if (result.Methods.Count > 0)
                {
                    Console.WriteLine("\nMethods:");
                    foreach (var method in result.Methods)
                    {
                        Console.WriteLine($"  - {method.Name}");
                        Console.WriteLine($"    Return Type: {method.ReturnType}");
                        Console.WriteLine($"    Parameters: {method.Parameters.Count}");
                        Console.WriteLine($"    Lines of Code: {method.LinesOfCode}");
                        Console.WriteLine($"    Complexity: {method.Complexity?.CyclomaticComplexity ?? 0}");
                        Console.WriteLine($"    Public: {method.IsPublic}, Static: {method.IsStatic}");
                    }
                }

                if (result.Methods.Count > 0)
                {
                    Console.WriteLine("\n✅ SUCCESS: Backend parsing is working!");
                }
                else
                {
                    Console.WriteLine("\n❌ FAILED: No methods were parsed!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ EXCEPTION OCCURRED:");
                Console.WriteLine($"Type: {ex.GetType().FullName}");
                Console.WriteLine($"Message: {ex.Message}");
                Console.WriteLine($"Stack Trace:\n{ex.StackTrace}");
                
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"\nInner Exception:");
                    Console.WriteLine($"Type: {ex.InnerException.GetType().FullName}");
                    Console.WriteLine($"Message: {ex.InnerException.Message}");
                    Console.WriteLine($"Stack Trace:\n{ex.InnerException.StackTrace}");
                }
            }

            Console.WriteLine("\n\nPress any key to exit...");
            Console.ReadKey();
        }
    }
}
