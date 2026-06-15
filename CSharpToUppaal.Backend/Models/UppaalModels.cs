using System;
using System.Collections.Generic;

namespace CSharpToUppaal.Backend.Models
{
    public enum NodeType
    {
        Entry,
        Exit,
        Statement,
        Condition,
        Loop,
        MethodCall,
        Assignment,
        Return,
        Declaration,
        Merge
    }

    public class CfgNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Label { get; set; } = string.Empty;
        public NodeType Type { get; set; }
        public string Code { get; set; } = string.Empty;
        public List<string> Successors { get; set; } = new();
        public Dictionary<string, object> Properties { get; set; } = new();
    }

    public class CfgEdge
    {
        public string FromNodeId { get; set; } = string.Empty;
        public string ToNodeId { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }

    public class ControlFlowGraph
    {
        public string MethodName { get; set; } = string.Empty;
        public string ReturnType { get; set; } = "void";
        public List<CfgNode> Nodes { get; set; } = new();
        public List<CfgEdge> Edges { get; set; } = new();
        public string EntryNodeId { get; set; } = string.Empty;
        public string ExitNodeId { get; set; } = string.Empty;
        public Dictionary<string, string> Variables { get; set; } = new();
        public List<ParameterInfo> Parameters { get; set; } = new();
    }

    public class MethodInfo
    {
        public string Name { get; set; } = string.Empty;
        public string ReturnType { get; set; } = string.Empty;
        public bool IsPublic { get; set; }
        public bool IsStatic { get; set; }
        public bool IsAsync { get; set; }
        public string Body { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public List<ParameterInfo> Parameters { get; set; } = new();
        public ComplexityMetrics Complexity { get; set; } = new();
        public int LinesOfCode { get; set; }
    }

    public class ParameterInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool HasDefaultValue { get; set; }
        public string DefaultValue { get; set; } = string.Empty;
    }

    public class ComplexityMetrics
    {
        public int CyclomaticComplexity { get; set; }
        public int LinesOfCode { get; set; }
        public int ParameterCount { get; set; }
        public int NestingDepth { get; set; }
        public int MethodCalls { get; set; }
    }

    public class ParseResult
    {
        public string FilePath { get; set; } = string.Empty;
        public bool Success { get; set; }
        public List<ParseError> Errors { get; set; } = new();
        public List<MethodInfo> Methods { get; set; } = new();
        public List<ClassInfo> Classes { get; set; } = new();
    }

    public class ParseError
    {
        public string Message { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public ParseErrorSeverity Severity { get; set; }
    }

    public enum ParseErrorSeverity
    {
        Warning,
        Error,
        Critical
    }

    public class ClassInfo
    {
        public string Name { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public bool IsPublic { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsSealed { get; set; }
        public string BaseTypes { get; set; } = string.Empty;
        public int Location { get; set; }
        public List<PropertyInfo> Properties { get; set; } = new();
    }

    public class PropertyInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool HasGetter { get; set; }
        public bool HasSetter { get; set; }
    }

    public class UppaalLocation
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, string> Labels { get; set; } = new();
        public bool IsInitial { get; set; }
        public bool IsUrgent { get; set; }
        public bool IsCommitted { get; set; }
    }

    public class UppaalTransition
    {
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Select { get; set; } = string.Empty;
        public string Guard { get; set; } = string.Empty;
        public string Synchronization { get; set; } = string.Empty;
        public string Update { get; set; } = string.Empty;
        public List<string> Comments { get; set; } = new();
        public List<UppaalNail> Nails { get; set; } = new();
    }

    public class UppaalNail
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    public class UppaalTemplate
    {
        public string Name { get; set; } = string.Empty;
        public List<UppaalLocation> Locations { get; set; } = new();
        public List<UppaalTransition> Transitions { get; set; } = new();
        public string Declaration { get; set; } = string.Empty;
    }

    public class UppaalModel
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string XmlContent { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public ModelGenerationStatus Status { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public List<UppaalTemplate> Templates { get; set; } = new();
        public List<MethodInfo> ParsedMethods { get; set; } = new();
        public VerificationSummary VerificationSummary { get; set; } = new();
        public GenerationReport GenerationReport { get; set; } = new();
    }

    public enum ModelGenerationStatus
    {
        Pending,
        InProgress,
        Success,
        ParseError,
        GenerationError,
        ValidationError
    }

    public class VerificationSummary
    {
        public int TotalProperties { get; set; }
        public int VerifiedProperties { get; set; }
        public int FailedProperties { get; set; }
        public List<VerificationProperty> Properties { get; set; } = new();
        public TimeSpan VerificationTime { get; set; }
        public DateTime VerifiedAt { get; set; } = DateTime.UtcNow;
    }

    public class VerificationProperty
    {
        public string Name { get; set; } = string.Empty;
        public string Formula { get; set; } = string.Empty;
        public bool IsVerified { get; set; }
        public string Result { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public TimeSpan VerificationTime { get; set; }
    }

    public class Project
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastModified { get; set; }
        public List<SourceFile> SourceFiles { get; set; } = new();
        public List<UppaalModel> GeneratedModels { get; set; } = new();
        public List<VerificationSummary> VerificationResults { get; set; } = new();
    }

    public class SourceFile
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string FilePath { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
        public string Language { get; set; } = string.Empty;
        public List<MethodInfo> Methods { get; set; } = new();
        public List<ClassInfo> Classes { get; set; } = new();
    }

    public enum FunctionModelingMode
    {
        ExplicitAutomaton,
        CodeBlock,
        Stub
    }

    public enum AssumptionSeverity
    {
        Info,
        Warning,
        Error
    }

    public enum RequirementKind
    {
        Unknown,
        Reachability,
        Safety,
        Liveness,
        LeadsTo,
        DeadlockFreedom
    }

    public enum VerifytaStatus
    {
        NotRun,
        Passed,
        Failed,
        Error,
        NotConfigured
    }

    public class FunctionDescriptor
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;
        public string ContainingType { get; set; } = string.Empty;
        public string ReturnType { get; set; } = string.Empty;
        public bool IsPublic { get; set; }
        public bool IsStatic { get; set; }
        public bool IsAsync { get; set; }
        public bool IsSynthetic { get; set; }
        public bool IsUnresolvedStub { get; set; }
        public int LineNumber { get; set; }
        public string SourceFile { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public List<ParameterInfo> Parameters { get; set; } = new();
        public List<string> DirectCallIds { get; set; } = new();
        public List<string> UnresolvedCalls { get; set; } = new();
    }

    public class FunctionSelection
    {
        public string FunctionId { get; set; } = string.Empty;
        public bool IsSelected { get; set; }
        public FunctionModelingMode Mode { get; set; } = FunctionModelingMode.ExplicitAutomaton;
    }

    public class VariableDomain
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "int";
        public int Min { get; set; } = -10;
        public int Max { get; set; } = 10;
        public bool IsBoolean { get; set; }
        public bool IsEditable { get; set; } = true;
        public string Source { get; set; } = "default";
        public List<string> AllowedValues { get; set; } = new();

        public string ToUppaalSelectType()
        {
            if (IsBoolean || Type.Equals("bool", StringComparison.OrdinalIgnoreCase))
                return "bool";

            return $"int[{Min},{Max}]";
        }

        public string ToUppaalDeclType()
        {
            if (IsBoolean || Type.Equals("bool", StringComparison.OrdinalIgnoreCase))
                return "bool";

            return $"int[{Min},{Max}]";
        }

        public string DefaultValue()
        {
            return IsBoolean || Type.Equals("bool", StringComparison.OrdinalIgnoreCase) ? "false" : "0";
        }
    }

    public class TranslationAssumption
    {
        public AssumptionSeverity Severity { get; set; } = AssumptionSeverity.Info;
        public string Category { get; set; } = string.Empty;
        public string SymbolName { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public bool IsUserEditable { get; set; }
    }

    public class RequirementInterpretation
    {
        public string RequirementText { get; set; } = string.Empty;
        public RequirementKind Kind { get; set; } = RequirementKind.Unknown;
        public string Predicate { get; set; } = string.Empty;
        public string TriggerPredicate { get; set; } = string.Empty;
        public string TargetPredicate { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Status { get; set; } = string.Empty;
        public List<GeneratedQuery> GeneratedQueries { get; set; } = new();
    }

    public class GeneratedQuery
    {
        public string Name { get; set; } = string.Empty;
        public string Formula { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string Source { get; set; } = "auto";
        public bool IsEditable { get; set; } = true;
        public bool IsValidated { get; set; } = true;
    }

    public class GenerationReport
    {
        public List<FunctionDescriptor> Functions { get; set; } = new();
        public List<FunctionDescriptor> IncludedFunctions { get; set; } = new();
        public List<TranslationAssumption> Assumptions { get; set; } = new();
        public List<VariableDomain> Domains { get; set; } = new();
        public List<GeneratedQuery> Queries { get; set; } = new();
        public VerifytaResult VerifytaResult { get; set; } = new();
        public UppaalCompatibilityResult Compatibility { get; set; } = new();
        public LayoutFixResult Layout { get; set; } = new();
        public string Summary { get; set; } = string.Empty;
    }

    public class VerifytaResult
    {
        public VerifytaStatus Status { get; set; } = VerifytaStatus.NotRun;
        public string VerifytaPath { get; set; } = string.Empty;
        public int ExitCode { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public List<VerificationProperty> Properties { get; set; } = new();
    }

    public class ModelGenerationRequest
    {
        public string ProjectName { get; set; } = "GeneratedModel";
        public string SourceCode { get; set; } = string.Empty;
        public string FileName { get; set; } = "Source.cs";
        public List<FunctionSelection> FunctionSelections { get; set; } = new();
        public List<VariableDomain> DomainOverrides { get; set; } = new();
        public List<GeneratedQuery> UserQueries { get; set; } = new();
        public string RequirementsText { get; set; } = string.Empty;
        public OllamaRequirementSettings RequirementSettings { get; set; } = new();
    }

    public class OllamaRequirementSettings
    {
        public bool Enabled { get; set; }
        public string BaseUrl { get; set; } = "http://localhost:11434/api";
        public string Model { get; set; } = "llama3:latest";
        public int TimeoutSeconds { get; set; } = 120;
    }

    public class LayoutFixResult
    {
        public string XmlContent { get; set; } = string.Empty;
        public string ReportText { get; set; } = string.Empty;
        public List<LayoutTemplateReport> Templates { get; set; } = new();
    }

    public class LayoutTemplateReport
    {
        public string TemplateName { get; set; } = string.Empty;
        public int LocationCount { get; set; }
        public int TransitionCount { get; set; }
        public int UnreachableLocationCount { get; set; }
        public int EdgeCrossingCount { get; set; }
        public List<string> UnreachableLocations { get; set; } = new();
        public List<string> RemovedLocations { get; set; } = new();
    }

    public enum UppaalCompatibilitySeverity
    {
        Info,
        Warning,
        Error
    }

    public class UppaalCompatibilityIssue
    {
        public UppaalCompatibilitySeverity Severity { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Position { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class UppaalCompatibilityResult
    {
        public bool IsReady => Issues.TrueForAll(i => i.Severity != UppaalCompatibilitySeverity.Error);
        public List<UppaalCompatibilityIssue> Issues { get; set; } = new();

        public int ErrorCount => Issues.FindAll(i => i.Severity == UppaalCompatibilitySeverity.Error).Count;
        public int WarningCount => Issues.FindAll(i => i.Severity == UppaalCompatibilitySeverity.Warning).Count;
    }
}
