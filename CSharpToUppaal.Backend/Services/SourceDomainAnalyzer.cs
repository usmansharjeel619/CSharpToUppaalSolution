using CSharpToUppaal.Backend.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToUppaal.Backend.Services;

/// <summary>Conservative interval execution. Unsupported operations and widening produce unknown, never guessed bounds.</summary>
public sealed class SourceDomainAnalyzer
{
    private readonly CSharpSemanticAnalysisResult _analysis;
    private readonly Dictionary<string, FunctionDescriptor> _functions;
    private readonly Dictionary<string, VariableDomain> _domains = new();
    private readonly Dictionary<string, Interval?> _observed = new();
    private readonly HashSet<string> _active = new();
    private readonly HashSet<string> _stubIds;
    private int _steps;
    private const int Limit = 256;
    private readonly record struct Interval(long Min, long Max);
    private sealed class State : Dictionary<ISymbol, Interval?>
    {
        public State() : base(SymbolEqualityComparer.Default) { }
        public State Copy() { var s = new State(); foreach (var p in this) s[p.Key] = p.Value; return s; }
    }
    public SourceDomainAnalyzer(CSharpSemanticAnalysisResult analysis, IEnumerable<FunctionDescriptor> functions, IEnumerable<FunctionSelection> selections)
    {
        _analysis = analysis; _functions = functions.ToDictionary(f => f.Id);
        _stubIds = selections.Where(s => s.Mode == FunctionModelingMode.Stub).Select(s => s.FunctionId).ToHashSet();
    }
    public static string SymbolKey(ISymbol symbol) => symbol is IParameterSymbol p
        ? $"{CSharpSemanticAnalyzer.ToFunctionId((IMethodSymbol)p.ContainingSymbol)}:parameter:{p.Ordinal}"
        : $"{symbol.ContainingSymbol.ToDisplayString()}:{symbol.Kind}:{symbol.Name}:{symbol.Locations.FirstOrDefault()?.SourceSpan.Start}";

    public List<VariableDomain> Analyze(IEnumerable<FunctionDescriptor> roots)
    {
        foreach (var root in roots) { _steps = 0; Execute(root, root.Parameters.Select(_ => (Interval?)null).ToArray()); }
        // Even unreachable code gets a traceable row, explicitly unknown.
        foreach (var f in _functions.Values)
        {
            if (!_analysis.MethodDeclarationsById.TryGetValue(f.Id, out var method)) continue;
            foreach (var p in method.ParameterList.Parameters)
                if (_analysis.GetSemanticModel(p).GetDeclaredSymbol(p) is { } symbol) Register(f, symbol, "parameter");
            foreach (var v in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                if (_analysis.GetSemanticModel(v).GetDeclaredSymbol(v) is { } symbol) Register(f, symbol, "local");
            if (f.ReturnType != "void") RegisterReturn(f);
        }
        foreach (var d in _domains.Values)
        {
            var range = _observed.GetValueOrDefault(d.SymbolId);
            d.SourceMin = range.HasValue ? (int)range.Value.Min : null;
            d.SourceMax = range.HasValue ? (int)range.Value.Max : null;
            d.Min = range.HasValue ? (int)Math.Min(0, range.Value.Min) : d.IsBoolean ? 0 : -10;
            d.Max = range.HasValue ? (int)Math.Max(0, range.Value.Max) : d.IsBoolean ? 1 : 10;
            d.InferenceStatus = range.HasValue || d.IsBoolean ? "Source-derived" : "Assumed";
            d.SourceEvidence += range.HasValue ? "; conservative source execution, storage includes initialization at 0" : "; unknown input, unsupported operation, overflow, or analysis limit; editable assumed bounds";
        }
        return _domains.Values.ToList();
    }
    private Interval? Execute(FunctionDescriptor f, Interval?[] arguments)
    {
        if (!_analysis.MethodDeclarationsById.TryGetValue(f.Id, out var method) || !_active.Add(f.Id)) return null;
        try
        {
            var state = new State();
            for (int i = 0; i < method.ParameterList.Parameters.Count; i++)
            {
                var syntax = method.ParameterList.Parameters[i];
                var symbol = _analysis.GetSemanticModel(syntax).GetDeclaredSymbol(syntax)!;
                Assign(f, symbol, i < arguments.Length ? arguments[i] : null, state, "parameter");
            }
            var returns = new List<Interval?>();
            if (_stubIds.Contains(f.Id) || ++_steps > 10000) returns.Add(null);
            else if (method.Body != null) Run(method.Body, state, f, returns);
            else if (method.ExpressionBody != null) returns.Add(Eval(method.ExpressionBody.Expression, state, f));
            var result = returns.Count == 0 ? null : returns.Aggregate(Union);
            if (f.ReturnType != "void") Observe(RegisterReturn(f), result);
            return result;
        }
        finally { _active.Remove(f.Id); }
    }
    private bool Run(StatementSyntax statement, State s, FunctionDescriptor f, List<Interval?> returns)
    {
        if (++_steps > 10000) { Invalidate(statement, s, f); returns.Add(null); return true; }
        switch (statement)
        {
            case BlockSyntax block:
                foreach (var child in block.Statements) if (!Run(child, s, f, returns)) return false;
                return true;
            case LocalDeclarationStatementSyntax local:
                Declare(local.Declaration, s, f); return true;
            case ExpressionStatementSyntax expression:
                Eval(expression.Expression, s, f); return true;
            case ReturnStatementSyntax ret:
                returns.Add(ret.Expression == null ? null : Eval(ret.Expression, s, f)); return false;
            case IfStatementSyntax branch:
            {
                var truth = Eval(branch.Condition, s, f);
                var yes = s.Copy(); var no = s.Copy();
                Refine(branch.Condition, yes, true); Refine(branch.Condition, no, false);
                bool a = truth?.Max != 0 && Run(branch.Statement, yes, f, returns);
                bool b = truth?.Min != 1 && (branch.Else == null || Run(branch.Else.Statement, no, f, returns));
                if (a && b) Merge(s, yes, no); else if (a) Copy(s, yes); else if (b) Copy(s, no);
                return a || b;
            }
            case ForStatementSyntax loop:
                if (loop.Declaration != null) Declare(loop.Declaration, s, f);
                foreach (var init in loop.Initializers) Eval(init, s, f);
                return Loop(loop.Condition, loop.Statement, loop.Incrementors, s, f, returns);
            case WhileStatementSyntax loop:
                return Loop(loop.Condition, loop.Statement, Array.Empty<ExpressionSyntax>(), s, f, returns);
            case EmptyStatementSyntax: return true;
            default:
                Invalidate(statement, s, f); returns.Add(null); return true;
        }
    }
    private bool Loop(ExpressionSyntax? condition, StatementSyntax body, IEnumerable<ExpressionSyntax> increments, State s, FunctionDescriptor f, List<Interval?> returns)
    {
        State? exits = null;
        for (int i = 0; i < Limit; i++)
        {
            var test = condition == null ? new Interval(1, 1) : Eval(condition, s, f);
            if (test?.Min != 1)
            {
                var exit = s.Copy(); if (condition != null) Refine(condition, exit, false);
                if (exits == null) exits = exit; else { var old = exits.Copy(); Merge(exits, old, exit); }
            }
            if (test?.Max == 0) { if (exits != null) Copy(s, exits); return true; }
            if (condition != null) Refine(condition, s, true);
            if (!Run(body, s, f, returns)) { if (exits != null) Copy(s, exits); return exits != null; }
            foreach (var increment in increments) Eval(increment, s, f);
        }
        // Widen all modified state, including increments, rather than treating a loop guard as a global bound.
        Invalidate(body, s, f);
        foreach (var e in increments) Invalidate(e, s, f);
        if (exits != null) { var copy = s.Copy(); Merge(s, copy, exits); }
        returns.Add(null);
        return true;
    }
    private void Declare(VariableDeclarationSyntax declaration, State s, FunctionDescriptor f)
    {
        foreach (var v in declaration.Variables)
        {
            var symbol = _analysis.GetSemanticModel(v).GetDeclaredSymbol(v);
            if (symbol != null) Assign(f, symbol, v.Initializer == null ? null : Eval(v.Initializer.Value, s, f), s, "local");
        }
    }
    private Interval? Eval(ExpressionSyntax e, State s, FunctionDescriptor f)
    {
        var semantic = _analysis.GetSemanticModel(e);
        var field = semantic.GetSymbolInfo(e).Symbol as IFieldSymbol;
        if (field != null && field.DeclaringSyntaxReferences.Length > 0)
        {
            var declaration = field.DeclaringSyntaxReferences[0].GetSyntax() as VariableDeclaratorSyntax;
            var initializer = declaration?.Initializer?.Value;
            var known = initializer == null ? default : _analysis.GetSemanticModel(initializer).GetConstantValue(initializer);
            if ((field.IsConst || field.IsReadOnly) && known.HasValue && known.Value is int n)
            { Observe(Register(f, field, "field"), new Interval(n, n)); return new Interval(n, n); }
            Observe(Register(f, field, "field"), null);
        }
        var constant = semantic.GetConstantValue(e);
        if (constant.HasValue)
        {
            try { var n = Convert.ToInt64(constant.Value); return Bounded(n, n); } catch { return null; }
        }
        if (e is ParenthesizedExpressionSyntax p) return Eval(p.Expression, s, f);
        if (e is IdentifierNameSyntax or MemberAccessExpressionSyntax)
        {
            var symbol = semantic.GetSymbolInfo(e).Symbol;
            if (symbol == null) return null;
            if (!s.TryGetValue(symbol, out var value))
            {
                // Mutable fields may persist between driver cycles; do not infer their lifetime range from an initializer.
                if (symbol is IFieldSymbol) Assign(f, symbol, null, s, "field");
                return null;
            }
            return value;
        }
        if (e is InvocationExpressionSyntax call)
        {
            var args = call.ArgumentList.Arguments.Select(a => Eval(a.Expression, s, f)).ToArray();
            var method = semantic.GetSymbolInfo(call).Symbol as IMethodSymbol;
            if (call.ArgumentList.Arguments.Any(a => a.RefKindKeyword.RawKind != 0)) { Invalidate(call, s, f); return null; }
            if (method != null && _functions.TryGetValue(CSharpSemanticAnalyzer.ToFunctionId(method), out var target))
            {
                var reordered = new Interval?[target.Parameters.Count];
                for (int i = 0; i < args.Length; i++)
                {
                    var label = call.ArgumentList.Arguments[i].NameColon?.Name.Identifier.ValueText;
                    var index = label == null ? i : target.Parameters.FindIndex(p => p.Name == label);
                    if (index >= 0 && index < reordered.Length) reordered[index] = args[i];
                }
                return Execute(target, reordered);
            }
            foreach (var symbol in s.Keys.Where(k => k is IFieldSymbol).ToList()) Assign(f, symbol, null, s, "field");
            return null;
        }
        if (e is AssignmentExpressionSyntax assignment)
        {
            var right = Eval(assignment.Right, s, f);
            if (assignment.OperatorToken.Text != "=") right = Binary(assignment.OperatorToken.Text.TrimEnd('='), Eval(assignment.Left, s, f), right);
            var symbol = semantic.GetSymbolInfo(assignment.Left).Symbol;
            if (symbol != null) Assign(f, symbol, right, s, symbol is IFieldSymbol ? "field" : "local");
            return right;
        }
        if (e is BinaryExpressionSyntax b) return Binary(b.OperatorToken.Text, Eval(b.Left, s, f), Eval(b.Right, s, f));
        if (e is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax)
        {
            var operand = e is PrefixUnaryExpressionSyntax pre ? pre.Operand : ((PostfixUnaryExpressionSyntax)e).Operand;
            var op = e is PrefixUnaryExpressionSyntax pre2 ? pre2.OperatorToken.Text : ((PostfixUnaryExpressionSyntax)e).OperatorToken.Text;
            var value = Eval(operand, s, f);
            if (op is "++" or "--")
            {
                var next = Binary(op == "++" ? "+" : "-", value, new Interval(1, 1));
                if (semantic.GetSymbolInfo(operand).Symbol is { } symbol) Assign(f, symbol, next, s, "local");
                return e is PostfixUnaryExpressionSyntax ? value : next;
            }
            return op == "-" && value.HasValue ? Bounded(-value.Value.Max, -value.Value.Min) : op == "!" && value.HasValue ? new Interval(1 - value.Value.Max, 1 - value.Value.Min) : null;
        }
        if (e is ConditionalExpressionSyntax choice)
        {
            var test = Eval(choice.Condition, s, f);
            if (test?.Min == 1) return Eval(choice.WhenTrue, s, f);
            if (test?.Max == 0) return Eval(choice.WhenFalse, s, f);
            var a = s.Copy(); var alternative = s.Copy();
            var result = Union(Eval(choice.WhenTrue, a, f), Eval(choice.WhenFalse, alternative, f)); Merge(s, a, alternative); return result;
        }
        return null;
    }
    private static Interval? Binary(string op, Interval? a, Interval? b)
    {
        if (!a.HasValue || !b.HasValue) return null;
        var x = a.Value; var y = b.Value;
        switch (op)
        {
            case "+": return Bounded(x.Min + y.Min, x.Max + y.Max);
            case "-": return Bounded(x.Min - y.Max, x.Max - y.Min);
            case "*": var products = new[] { x.Min * y.Min, x.Min * y.Max, x.Max * y.Min, x.Max * y.Max }; return Bounded(products.Min(), products.Max());
            case "/": if (y.Min <= 0 && y.Max >= 0) return null; var quotients = new[] { x.Min / y.Min, x.Min / y.Max, x.Max / y.Min, x.Max / y.Max }; return Bounded(quotients.Min(), quotients.Max());
            case "<": return Truth(x.Max < y.Min, x.Min >= y.Max);
            case "<=": return Truth(x.Max <= y.Min, x.Min > y.Max);
            case ">": return Truth(x.Min > y.Max, x.Max <= y.Min);
            case ">=": return Truth(x.Min >= y.Max, x.Max < y.Min);
            case "==": return Truth(x.Min == x.Max && x == y, x.Max < y.Min || y.Max < x.Min);
            case "!=": return Truth(x.Max < y.Min || y.Max < x.Min, x.Min == x.Max && x == y);
            case "&&": return Truth(x.Min == 1 && y.Min == 1, x.Max == 0 || y.Max == 0);
            case "||": return Truth(x.Min == 1 || y.Min == 1, x.Max == 0 && y.Max == 0);
            default: return null;
        }
    }
    private static Interval Truth(bool yes, bool no) => yes ? new(1, 1) : no ? new(0, 0) : new(0, 1);
    private static Interval? Bounded(long min, long max) => min < int.MinValue || max > int.MaxValue ? null : new Interval(min, max);
    private static Interval? Union(Interval? a, Interval? b) => a.HasValue && b.HasValue ? new Interval(Math.Min(a.Value.Min, b.Value.Min), Math.Max(a.Value.Max, b.Value.Max)) : null;
    private void Refine(ExpressionSyntax expression, State state, bool truth)
    {
        if (expression is not BinaryExpressionSyntax b) return;
        var symbol = _analysis.GetSemanticModel(b.Left).GetSymbolInfo(b.Left).Symbol;
        var constant = _analysis.GetSemanticModel(b.Right).GetConstantValue(b.Right);
        if (symbol == null || !constant.HasValue || constant.Value is not int n || !state.TryGetValue(symbol, out var range) || !range.HasValue) return;
        var r = range.Value; var op = b.OperatorToken.Text;
        if (!truth) op = op switch { "<" => ">=", "<=" => ">", ">" => "<=", ">=" => "<", "==" => "!=", _ => "==" };
        state[symbol] = op switch { "<" => new(r.Min, Math.Min(r.Max, (long)n - 1)), "<=" => new(r.Min, Math.Min(r.Max, n)), ">" => new(Math.Max(r.Min, (long)n + 1), r.Max), ">=" => new(Math.Max(r.Min, n), r.Max), "==" => new(n, n), _ => r };
    }
    private void Invalidate(SyntaxNode node, State state, FunctionDescriptor f)
    {
        foreach (var symbol in state.Keys.ToList()) Assign(f, symbol, null, state, symbol is IFieldSymbol ? "field" : "local");
    }
    private static void Copy(State target, State source) { target.Clear(); foreach (var p in source) target[p.Key] = p.Value; }
    private static void Merge(State target, State a, State b)
    {
        target.Clear(); foreach (var key in a.Keys.Concat(b.Keys).Distinct(SymbolEqualityComparer.Default)) target[key] = Union(a.GetValueOrDefault(key), b.GetValueOrDefault(key));
    }
    private void Assign(FunctionDescriptor f, ISymbol symbol, Interval? value, State state, string role)
    { state[symbol] = value; Observe(Register(f, symbol, role), value); }
    private void Observe(VariableDomain d, Interval? value)
    { _observed[d.SymbolId] = _observed.TryGetValue(d.SymbolId, out var old) ? Union(old, value) : value; }
    private VariableDomain RegisterReturn(FunctionDescriptor f) => RegisterCore(f, f.Id + ":return", "ret", f.ReturnType, "return", f.LineNumber, "Function");
    private VariableDomain Register(FunctionDescriptor f, ISymbol symbol, string role)
    {
        var type = symbol switch { ILocalSymbol l => l.Type, IParameterSymbol p => p.Type, IFieldSymbol field => field.Type, _ => null };
        var line = (symbol.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line ?? f.LineNumber - 1) + 1;
        var block = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax().Ancestors().OfType<BlockSyntax>().FirstOrDefault();
        var blockLine = block == null ? line : block.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var scope = symbol is ILocalSymbol ? "Block at line " + blockLine : symbol is IFieldSymbol ? "Class state" : "Function";
        var domain = RegisterCore(f, SymbolKey(symbol), symbol.Name, type?.ToDisplayString() ?? "int", role, line, scope);
        if (symbol is IFieldSymbol fieldSymbol)
        {
            domain.OwnerNamespace = fieldSymbol.ContainingNamespace.ToDisplayString();
            domain.OwnerType = fieldSymbol.ContainingType.ToDisplayString();
            domain.OwnerFunction = "(class state)";
            domain.Name = fieldSymbol.ContainingType.ToDisplayString() + "." + fieldSymbol.Name;
            domain.EmittedName = "state_" + RequirementTranslationService.Sanitize(fieldSymbol.ContainingType.ToDisplayString() + "_" + fieldSymbol.Name);
        }
        return domain;
    }
    private VariableDomain RegisterCore(FunctionDescriptor f, string key, string name, string type, string role, int line, string scope)
    {
        if (_domains.TryGetValue(key, out var existing)) return existing;
        var d = new VariableDomain { SymbolId = key, Name = f.DisplayName + "." + name, Type = type, IsBoolean = type == "bool", OwnerNamespace = f.Namespace, OwnerType = f.ContainingType, OwnerFunction = f.Signature, CodeScope = scope, Source = role, SourceEvidence = $"{f.SourceFile}:{line}", EmittedName = name };
        _domains[key] = d; return d;
    }
}
