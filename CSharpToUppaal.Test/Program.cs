using System;
using System.IO;
using System.Threading.Tasks;
using CSharpToUppaal.Backend.Parsers;
using CSharpToUppaal.Backend.Generators;
using CSharpToUppaal.Backend.Services;

Console.WriteLine("=== Testing CSharp Parser Backend ===\n");

var parser = new CSharpParser();
var cfgGenerator = new CfgGeneratorService(parser);
var uppaalGenerator = new UppaalGeneratorService(cfgGenerator);

string testCode = @"using System;

namespace BankSystem
{
    public class Account
    {
        public int GetBalance(int deposits, int withdrawals)
        {
            int balance = deposits - withdrawals;
            if (balance < 0)
            {
                balance = 0;
            }
            return balance;
        }

        public int ProcessTransaction(int balance, int amount, int txType)
        {
            int result = balance;
            if (txType == 1)
            {
                result = balance + amount;
            }
            else if (txType == 2 && balance >= amount)
            {
                result = balance - amount;
            }
            return result;
        }

        public int CalculateInterest(int principal, int rate, int years)
        {
            int amount = principal;
            for (int i = 0; i < years; i++)
            {
                int interest = amount * rate / 100;
                amount = amount + interest;
            }
            return amount;
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
        
        // Test CFG generation
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("Testing CFG Generation...\n");
        
        try
        {
            var firstMethod = result.Methods[0];
            Console.WriteLine($"Generating CFG for method: {firstMethod.Name}");
            
            var cfg = await cfgGenerator.GenerateCfgFromMethodAsync(firstMethod);
            
            Console.WriteLine($"\n=== CFG Result ===");
            Console.WriteLine($"Method: {cfg.MethodName}");
            Console.WriteLine($"Nodes: {cfg.Nodes.Count}");
            Console.WriteLine($"Edges: {cfg.Edges.Count}");
            Console.WriteLine($"Entry Node: {cfg.EntryNodeId}");
            Console.WriteLine($"Exit Node: {cfg.ExitNodeId}");
            
            Console.WriteLine("\nNodes:");
            foreach (var node in cfg.Nodes)
            {
                Console.WriteLine($"  - {node.Label} ({node.Type})");
            }
            
            Console.WriteLine("\nEdges:");
            foreach (var edge in cfg.Edges)
            {
                var fromNode = cfg.Nodes.FirstOrDefault(n => n.Id == edge.FromNodeId);
                var toNode = cfg.Nodes.FirstOrDefault(n => n.Id == edge.ToNodeId);
                Console.WriteLine($"  - {fromNode?.Label} → {toNode?.Label} {(string.IsNullOrEmpty(edge.Label) ? "" : $"[{edge.Label}]")}");
            }
            
            Console.WriteLine("\n✅ SUCCESS: CFG generation is working!");
        }
        catch (Exception cfgEx)
        {
            Console.WriteLine($"\n❌ CFG GENERATION FAILED:");
            Console.WriteLine($"Type: {cfgEx.GetType().FullName}");
            Console.WriteLine($"Message: {cfgEx.Message}");
            Console.WriteLine($"Stack Trace:\n{cfgEx.StackTrace}");
            
            if (cfgEx.InnerException != null)
            {
                Console.WriteLine($"\nInner Exception:");
                Console.WriteLine($"Type: {cfgEx.InnerException.GetType().FullName}");
                Console.WriteLine($"Message: {cfgEx.InnerException.Message}");
            }
        }

        // Test UPPAAL XML generation
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("Testing UPPAAL XML Generation");
        Console.WriteLine(new string('=', 60) + "\n");

        try
        {
            var uppaalModel = await uppaalGenerator.GenerateModelFromCodeAsync(testCode, "TestModel");
            
            Console.WriteLine($"UPPAAL Model Status: {uppaalModel.Status}");
            Console.WriteLine($"Status Message: {uppaalModel.StatusMessage}");
            Console.WriteLine($"Templates Count: {uppaalModel.Templates.Count}");
            
            Console.WriteLine("\n--- Generated UPPAAL XML ---\n");
            Console.WriteLine(uppaalModel.XmlContent);
            Console.WriteLine("\n--- End of XML ---\n");

            // Save to file for inspection
            var outputPath = Path.Combine(Directory.GetCurrentDirectory(), "TestModel.xml");
            File.WriteAllText(outputPath, uppaalModel.XmlContent);
            Console.WriteLine($"✅ XML saved to: {outputPath}");
            
            Console.WriteLine("\n✅ SUCCESS: UPPAAL XML generation is working!");
        }
        catch (Exception uppaalEx)
        {
            Console.WriteLine($"\n❌ UPPAAL GENERATION FAILED:");
            Console.WriteLine($"Type: {uppaalEx.GetType().FullName}");
            Console.WriteLine($"Message: {uppaalEx.Message}");
            Console.WriteLine($"Stack Trace:\n{uppaalEx.StackTrace}");
            
            if (uppaalEx.InnerException != null)
            {
                Console.WriteLine($"\nInner Exception:");
                Console.WriteLine($"Type: {uppaalEx.InnerException.GetType().FullName}");
                Console.WriteLine($"Message: {uppaalEx.InnerException.Message}");
            }
        }
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
