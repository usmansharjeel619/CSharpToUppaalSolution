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
        public string Guard { get; set; } = string.Empty;
        public string Update { get; set; } = string.Empty;
        public string Synchronization { get; set; } = string.Empty;
        public List<string> Comments { get; set; } = new();
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
        public VerificationSummary VerificationSummary { get; set; } = new();
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
}