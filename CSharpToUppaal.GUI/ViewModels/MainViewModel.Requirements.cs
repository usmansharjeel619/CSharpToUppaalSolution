using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSharpToUppaal.Backend.Models;
using CSharpToUppaal.Backend.Services;
using CSharpToUppaal.Backend.Verification;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;

namespace CSharpToUppaal.GUI.ViewModels;

public partial class MainViewModel
{
    private UppaalModel? _lastModel;
    private bool _sourceDirty;
    private bool _geometryStale = true;
    private int _revision;
    public ICollectionView DomainGroups => CollectionViewSource.GetDefaultView(Domains);
    public ObservableCollection<string> RequirementSymbols { get; } = new();
    public ObservableCollection<string> RequirementLocations { get; } = new();
    public string[] TemplateKinds { get; } = { "Invariant", "Forbidden state", "Possible reachability", "Guaranteed eventually", "Conditional response", "Deadlock freedom" };
    public string[] EvaluationScopes { get; } = { "Always", "On completion" };
    public ObservableCollection<string> ComparisonOperators { get; } = new(new[] { "==", "!=", ">", ">=", "<", "<=" });
    public string[] BooleanValues { get; } = { "true", "false" };
    [ObservableProperty] private string _templateKind = "Invariant";
    [ObservableProperty] private string _templateSymbol = "";
    [ObservableProperty] private string _templateOperator = ">=";
    [ObservableProperty] private string _templateValue = "0";
    [ObservableProperty] private string _templateScope = "Always";
    [ObservableProperty] private string _templateTarget = "";
    [ObservableProperty] private FunctionSelectionViewModel? _templateFunction;
    [ObservableProperty] private bool _templateBoolean;
    [ObservableProperty] private string _requirementHelp = "Generate the model to populate its variables and locations. Each card is interpreted independently.";

    private void InitializeRequirements()
    {
        DomainGroups.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VariableDomain.OwnerNamespace)));
        DomainGroups.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VariableDomain.OwnerType)));
        DomainGroups.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VariableDomain.OwnerFunction)));
        DomainGroups.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VariableDomain.CodeScope)));
        RequirementEntries.CollectionChanged += RequirementsChanged;
        GeneratedQueries.CollectionChanged += QueriesChanged;
        Domains.CollectionChanged += (_, e) =>
        {
            if (e.NewItems == null) return;
            foreach (VariableDomain domain in e.NewItems)
                domain.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName is nameof(VariableDomain.Min) or nameof(VariableDomain.Max))
                    { domain.InferenceStatus = "User override"; InvalidateModel(); }
                };
        };
    }

    private void RequirementsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Move) return;
        if (e.OldItems != null) foreach (RequirementEntry entry in e.OldItems)
        { entry.PropertyChanged -= RequirementChanged; RemoveEntryQueries(entry.Id); }
        if (e.NewItems != null) foreach (RequirementEntry entry in e.NewItems) entry.PropertyChanged += RequirementChanged;
        var ids = RequirementEntries.Select(r => r.Id).ToHashSet();
        foreach (var query in GeneratedQueries.Where(q => q.RequirementId.Length > 0 && !ids.Contains(q.RequirementId)).ToList()) GeneratedQueries.Remove(query);
        ResetVerification();
    }
    private void RequirementChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RequirementEntry entry || e.PropertyName != nameof(RequirementEntry.Text)) return;
        // Editing the prose of a generated card explicitly changes it into free text.
        entry.GuidedKind = null;
        entry.FunctionId = ""; entry.ReferencedFunctionIds.Clear();
        entry.Status = "Changed: reinterpret this requirement";
        RemoveEntryQueries(entry.Id);
        ResetVerification();
    }
    private void QueriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems == null) return;
        foreach (GeneratedQuery query in e.NewItems)
            query.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(GeneratedQuery.Formula)) return;
                _revision++;
                query.Source = "manual"; query.IsValidated = false; query.VerificationStatus = "Not run";
                RefreshQueryGrid();
            };
    }
    private void RefreshQueryGrid() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => CollectionViewSource.GetDefaultView(GeneratedQueries).Refresh());
    private void ResetVerification()
    {
        _revision++;
        foreach (var q in GeneratedQueries) q.VerificationStatus = "Not run";
        if (_lastModel != null) _lastModel.GenerationReport.VerifytaResult = new();
    }
    private void RemoveEntryQueries(string id)
    { foreach (var q in GeneratedQueries.Where(q => q.RequirementId == id).ToList()) GeneratedQueries.Remove(q); }
    private void InvalidateModel()
    {
        _geometryStale = true;
        ResetVerification();
        ReadinessStatus = "Changed: regenerate before export or verification";
        RequirementSymbols.Clear(); RequirementLocations.Clear();
    }
    partial void OnSourceCodeChanged(string value) { _sourceDirty = true; InvalidateModel(); }
    partial void OnSingleFunctionModeChanged(bool value) { InvalidateModel(); RecomputeFunctionScope(); }
    partial void OnSelectedEntryFunctionChanged(FunctionSelectionViewModel value) { InvalidateModel(); RecomputeFunctionScope(); }
    partial void OnTemplateSymbolChanged(string value)
    {
        TemplateBoolean = _lastModel != null && QueryValidationService.FromModel(_lastModel).SymbolTypes.GetValueOrDefault(value) == "bool";
        ComparisonOperators.Clear();
        foreach (var op in TemplateBoolean ? new[] { "==", "!=" } : new[] { "==", "!=", ">", ">=", "<", "<=" }) ComparisonOperators.Add(op);
        TemplateOperator = TemplateBoolean ? "==" : ">=";
        TemplateValue = TemplateBoolean ? "true" : "0";
    }
    private void UpdateRequirementContext(UppaalModel model)
    {
        _lastModel = model; _geometryStale = false;
        var context = QueryValidationService.FromModel(model);
        RequirementSymbols.Clear(); RequirementLocations.Clear();
        foreach (var symbol in context.SymbolTypes.Keys.Order()) RequirementSymbols.Add(symbol);
        foreach (var location in context.Locations.Order()) RequirementLocations.Add(location);
        foreach (var interpretation in model.GenerationReport.Interpretations)
        {
            var entry = RequirementEntries.FirstOrDefault(e => e.Id == interpretation.RequirementId);
            if (entry != null) entry.Status = interpretation.Status;
        }
        RequirementHelp = "Choose a template, symbol and value, then add a card. ‘Always’ includes initialization; ‘On completion’ checks the selected function at Done.";
    }
    [RelayCommand]
    private void AddGuidedRequirement()
    {
        if (_geometryStale || _lastModel == null) { RequirementHelp = "Generate the current model before choosing template symbols."; return; }
        var kind = TemplateKind switch
        {
            "Possible reachability" => RequirementKind.Reachability,
            "Guaranteed eventually" => RequirementKind.Liveness,
            "Conditional response" => RequirementKind.LeadsTo,
            "Deadlock freedom" => RequirementKind.DeadlockFreedom,
            _ => RequirementKind.Safety
        };
        var context = QueryValidationService.FromModel(_lastModel);
        var isLocation = context.Locations.Contains(TemplateSymbol);
        var entry = new RequirementEntry
        {
            GuidedKind = kind, Variable = TemplateSymbol, Operator = isLocation ? "" : TemplateOperator,
            Value = TemplateValue, Scope = TemplateScope, Target = TemplateTarget,
            FunctionId = TemplateFunction?.FunctionId ?? "", Forbidden = TemplateKind == "Forbidden state",
            Text = kind == RequirementKind.DeadlockFreedom ? "The model is deadlock free." : $"{TemplateKind}: {TemplateSymbol} {(isLocation ? "" : TemplateOperator + " " + TemplateValue)} ({TemplateScope})" + (kind == RequirementKind.LeadsTo ? $" leads to {TemplateTarget}" : "")
        };
        RequirementEntries.Add(entry);
        _ = InterpretRequirement(entry);
    }
    [RelayCommand]
    private async Task InterpretRequirement(RequirementEntry entry)
    {
        if (_geometryStale || _lastModel == null) { entry.Status = "Regenerate the model to update available symbols."; return; }
        try
        {
            var interpretation = (await new RequirementTranslationService().InterpretEntriesAsync(new[] { entry }, QueryValidationService.FromModel(_lastModel), _settings.ToRequirementSettings())).Single();
            RemoveEntryQueries(entry.Id);
            foreach (var q in interpretation.GeneratedQueries) GeneratedQueries.Add(q);
            RefreshQueryGrid();
        }
        catch (Exception ex) { entry.Status = "Needs review: " + ex.Message; }
    }
    [RelayCommand] private void MoveRequirementUp(RequirementEntry entry)
    { var index = RequirementEntries.IndexOf(entry); if (index > 0) RequirementEntries.Move(index, index - 1); }
    [RelayCommand] private void MoveRequirementDown(RequirementEntry entry)
    { var index = RequirementEntries.IndexOf(entry); if (index >= 0 && index + 1 < RequirementEntries.Count) RequirementEntries.Move(index, index + 1); }

    private void SynchronizeQueriesForExport()
    {
        if (_geometryStale) throw new InvalidOperationException("The source, function scope, or domains changed. Regenerate the model first.");
        UppaalXml = QueryValidationService.ApplyQueries(UppaalXml, GeneratedQueries);
        RefreshQueryGrid();
    }

    [RelayCommand]
    private async Task VerifyQueries()
    {
        try
        {
            SynchronizeQueriesForExport();
            IsBusy = true;
            var queries = GeneratedQueries.ToList();
            var revision = _revision;
            var results = await new UppaalVerifier(string.IsNullOrWhiteSpace(_settings.VerifytaPath) ? null : _settings.VerifytaPath).VerifyPropertiesAsync(UppaalXml, queries.Select(q => q.Formula).ToList());
            if (revision != _revision) { StatusMessage = "The model changed during verification. Results discarded; verify the updated model."; return; }
            for (int i = 0; i < queries.Count; i++)
            {
                var result = results[i];
                queries[i].VerificationStatus = result.Result == "NotConfigured" ? "Not configured" : !string.IsNullOrEmpty(result.ErrorMessage) ? "Error" : result.IsVerified ? "Passed" : result.Result.Contains("NOT satisfied", StringComparison.OrdinalIgnoreCase) ? "Failed" : "Error";
            }
            StatusMessage = results.Any(r => r.Result == "NotConfigured") ? "verifyta was not found. Structural checks passed; behavioral verification was not run." : "Verification finished; see each query's result.";
            if (_lastModel != null)
                _lastModel.GenerationReport.VerifytaResult = new VerifytaResult { Properties = results, Status = results.Any(r => r.Result == "NotConfigured") ? VerifytaStatus.NotConfigured : queries.Any(q => q.VerificationStatus == "Error") ? VerifytaStatus.Error : results.All(r => r.IsVerified) ? VerifytaStatus.Passed : VerifytaStatus.Failed };
        }
        catch (Exception ex) { StatusMessage = "Verification: " + ex.Message; }
        finally { IsBusy = false; }
    }
}
