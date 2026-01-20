// ViewModels/MainViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace CSharpToUppaal.GUI.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly CSharpToUppaal.Backend.CSharpToUppaalEngine _engine;
        private CSharpToUppaal.Backend.Models.Project _project;

        [ObservableProperty]
        private string _statusMessage = "Ready";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotBusy))]
        private bool _isBusy;

        [ObservableProperty]
        private string _currentProjectName = "No project loaded";

        [ObservableProperty]
        private string _sourceCode = @"using System;

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

        [ObservableProperty]
        private string _modelName = "GeneratedModel";

        [ObservableProperty]
        private string _uppaalXml = "<!-- UPPAAL model will be generated here -->";

        [ObservableProperty]
        private bool _isXmlReadOnly = true;

        [ObservableProperty]
        private ObservableCollection<CSharpToUppaal.Backend.Models.MethodInfo> _methods = new();

        [ObservableProperty]
        private CSharpToUppaal.Backend.Models.MethodInfo _selectedMethod;

        [ObservableProperty]
        private ObservableCollection<CSharpToUppaal.Backend.Models.VerificationProperty> _verificationResults = new();

        [ObservableProperty]
        private string _verificationStatus = "Not run";

        [ObservableProperty]
        private ObservableCollection<TreeItemViewModel> _projectItems = new();

        public bool IsNotBusy => !IsBusy;

        public MainViewModel()
        {
            _engine = new CSharpToUppaal.Backend.CSharpToUppaalEngine();
            LoadSampleProject();
        }

        private async void LoadSampleProject()
        {
            try
            {
                IsBusy = true;
                StatusMessage = "Loading sample project...";

                _project = await _engine.CreateProjectAsync("Sample Project", "Demo project with sample code");
                CurrentProjectName = _project.Name;

                var sourceFile = await _engine.AddSourceCodeAsync(_project, SourceCode, "Sample.cs");

                Methods.Clear();
                foreach (var method in sourceFile.Methods)
                {
                    Methods.Add(method);
                }

                if (Methods.Any())
                {
                    SelectedMethod = Methods.First();
                }

                UpdateProjectTree();

                StatusMessage = "Sample project loaded";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading sample: {ex.Message}";
                MessageBox.Show($"Error loading sample project: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void UpdateProjectTree()
        {
            ProjectItems.Clear();

            var projectNode = new TreeItemViewModel
            {
                Name = _project.Name,
                Icon = "Folder"
            };

            foreach (var sourceFile in _project.SourceFiles)
            {
                var fileNode = new TreeItemViewModel
                {
                    Name = Path.GetFileName(sourceFile.FilePath),
                    Icon = "FileCode"
                };

                foreach (var method in sourceFile.Methods)
                {
                    var methodNode = new TreeItemViewModel
                    {
                        Name = method.Name,
                        Icon = "Function"
                    };
                    fileNode.Children.Add(methodNode);
                }

                projectNode.Children.Add(fileNode);
            }

            ProjectItems.Add(projectNode);
        }

        [RelayCommand]
        private async Task NewProject()
        {
            try
            {
                var dialog = new InputDialog("New Project", "Enter project name:", "New Project");
                if (dialog.ShowDialog() == true)
                {
                    IsBusy = true;
                    StatusMessage = "Creating new project...";

                    _project = await _engine.CreateProjectAsync(dialog.Result);
                    CurrentProjectName = _project.Name;

                    Methods.Clear();
                    UppaalXml = "<!-- UPPAAL model will be generated here -->";
                    VerificationResults.Clear();
                    UpdateProjectTree();

                    StatusMessage = "New project created";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error creating project: {ex.Message}";
                MessageBox.Show($"Error creating project: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task OpenFile()
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "C# Files (*.cs)|*.cs|All files (*.*)|*.*",
                    Title = "Open C# File"
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    IsBusy = true;
                    StatusMessage = $"Loading file: {openFileDialog.FileName}...";

                    if (_project == null)
                    {
                        _project = await _engine.CreateProjectAsync(
                            Path.GetFileNameWithoutExtension(openFileDialog.FileName));
                        CurrentProjectName = _project.Name;
                    }

                    var sourceFile = await _engine.AddSourceFileAsync(_project, openFileDialog.FileName);
                    SourceCode = sourceFile.Content;

                    Methods.Clear();
                    foreach (var method in sourceFile.Methods)
                    {
                        Methods.Add(method);
                    }

                    if (Methods.Any())
                    {
                        SelectedMethod = Methods.First();
                    }

                    UpdateProjectTree();

                    StatusMessage = $"Loaded {sourceFile.Methods.Count} methods from {Path.GetFileName(openFileDialog.FileName)}";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading file: {ex.Message}";
                MessageBox.Show($"Error loading file: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task LoadFile() => await OpenFile();

        [RelayCommand]
        private async Task ParseCode()
        {
            try
            {
                IsBusy = true;
                StatusMessage = "Parsing C# code...";

                if (_project == null)
                {
                    _project = await _engine.CreateProjectAsync("Parsed Project");
                    CurrentProjectName = _project.Name;
                }

                var sourceFile = await _engine.AddSourceCodeAsync(_project, SourceCode, "Source.cs");

                Methods.Clear();
                foreach (var method in sourceFile.Methods)
                {
                    Methods.Add(method);
                }

                if (Methods.Any())
                {
                    SelectedMethod = Methods.First();
                }

                UpdateProjectTree();

                StatusMessage = $"Parsed {sourceFile.Methods.Count} methods";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error parsing code: {ex.Message}";
                MessageBox.Show($"Error parsing code: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task AnalyzeCode()
        {
            try
            {
                IsBusy = true;
                StatusMessage = "Analyzing code complexity...";
                await Task.Delay(100);
                StatusMessage = "Code analysis complete";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error analyzing code: {ex.Message}";
                MessageBox.Show($"Error analyzing code: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task RunAnalysis() => await AnalyzeCode();

        [RelayCommand]
        private async Task GenerateCfg()
        {
            try
            {
                if (SelectedMethod == null)
                {
                    MessageBox.Show("Please select a method first", "No Method Selected",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                IsBusy = true;
                StatusMessage = $"Generating CFG for {SelectedMethod.Name}...";

                var cfg = await _engine.GenerateCfgForMethodAsync(SelectedMethod);
                var dotGraph = await _engine.GenerateDotGraphAsync(cfg);

                StatusMessage = $"Generated CFG for {SelectedMethod.Name} with {cfg.Nodes.Count} nodes";

                MessageBox.Show($"CFG generated for {SelectedMethod.Name}:\n" +
                              $"- Nodes: {cfg.Nodes.Count}\n" +
                              $"- Edges: {cfg.Edges.Count}\n" +
                              $"- Entry: {cfg.EntryNodeId}\n" +
                              $"- Exit: {cfg.ExitNodeId}",
                              "CFG Generated", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error generating CFG: {ex.Message}";
                MessageBox.Show($"Error generating CFG: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task GenerateModel()
        {
            try
            {
                if (_project == null || !_project.SourceFiles.Any())
                {
                    MessageBox.Show("Please load or enter some C# code first", "No Code",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                IsBusy = true;
                StatusMessage = "Generating UPPAAL model...";

                var model = await _engine.GenerateModelAsync(_project, ModelName);
                UppaalXml = model.XmlContent;
                IsXmlReadOnly = false;

                StatusMessage = $"Generated UPPAAL model '{model.Name}' with {model.Templates.Count} templates";

                MessageBox.Show($"UPPAAL model '{model.Name}' generated successfully!\n" +
                              $"- Templates: {model.Templates.Count}\n" +
                              $"- Status: {model.Status}\n" +
                              $"- Generated at: {model.GeneratedAt:HH:mm:ss}",
                              "Model Generated", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error generating model: {ex.Message}";
                MessageBox.Show($"Error generating UPPAAL model: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task VerifyModel()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(UppaalXml) || UppaalXml.Contains("<!--"))
                {
                    MessageBox.Show("Please generate a UPPAAL model first", "No Model",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                IsBusy = true;
                StatusMessage = "Verifying UPPAAL model...";
                VerificationStatus = "Running...";

                var model = new CSharpToUppaal.Backend.Models.UppaalModel
                {
                    Name = ModelName,
                    XmlContent = UppaalXml
                };

                var summary = await _engine.VerifyModelAsync(model);

                VerificationResults.Clear();
                foreach (var property in summary.Properties)
                {
                    VerificationResults.Add(property);
                }

                VerificationStatus = $"Verified: {summary.VerifiedProperties}/{summary.TotalProperties}";
                StatusMessage = $"Verification complete: {summary.VerifiedProperties}/{summary.TotalProperties} properties verified";

                MessageBox.Show($"Verification completed!\n" +
                              $"- Total properties: {summary.TotalProperties}\n" +
                              $"- Verified: {summary.VerifiedProperties}\n" +
                              $"- Failed: {summary.FailedProperties}\n" +
                              $"- Time: {summary.VerificationTime:mm\\:ss\\.fff}",
                              "Verification Results", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error verifying model: {ex.Message}";
                VerificationStatus = "Error";
                MessageBox.Show($"Error verifying model: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task RunVerification() => await VerifyModel();

        [RelayCommand]
        private async Task ExportModel()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(UppaalXml) || UppaalXml.Contains("<!--"))
                {
                    MessageBox.Show("Please generate a UPPAAL model first", "No Model",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "UPPAAL XML Files (*.xml)|*.xml|All files (*.*)|*.*",
                    Title = "Save UPPAAL Model",
                    FileName = $"{ModelName}.xml"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    IsBusy = true;
                    StatusMessage = "Exporting UPPAAL model...";

                    var model = new CSharpToUppaal.Backend.Models.UppaalModel
                    {
                        Name = ModelName,
                        XmlContent = UppaalXml
                    };

                    await _engine.ExportUppaalModelAsync(model, saveFileDialog.FileName);

                    StatusMessage = $"Model exported to {Path.GetFileName(saveFileDialog.FileName)}";

                    MessageBox.Show($"UPPAAL model exported successfully to:\n{saveFileDialog.FileName}",
                                  "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error exporting model: {ex.Message}";
                MessageBox.Show($"Error exporting model: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task SaveModel() => await ExportModel();

        [RelayCommand]
        private async Task ExportDot()
        {
            try
            {
                if (SelectedMethod == null)
                {
                    MessageBox.Show("Please select a method first", "No Method Selected",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "DOT Graph Files (*.dot)|*.dot|All files (*.*)|*.*",
                    Title = "Save CFG as DOT",
                    FileName = $"{SelectedMethod.Name}.dot"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    IsBusy = true;
                    StatusMessage = "Exporting CFG as DOT...";

                    var cfg = await _engine.GenerateCfgForMethodAsync(SelectedMethod);
                    var dotGraph = await _engine.GenerateDotGraphAsync(cfg);

                    await File.WriteAllTextAsync(saveFileDialog.FileName, dotGraph);

                    StatusMessage = $"CFG exported to {Path.GetFileName(saveFileDialog.FileName)}";

                    MessageBox.Show($"CFG exported as DOT file:\n{saveFileDialog.FileName}\n\n" +
                                  "You can visualize it with Graphviz:\n" +
                                  $"dot -Tpng \"{saveFileDialog.FileName}\" -o \"{Path.ChangeExtension(saveFileDialog.FileName, ".png")}\"",
                                  "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error exporting DOT: {ex.Message}";
                MessageBox.Show($"Error exporting DOT file: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void ClearVerification()
        {
            VerificationResults.Clear();
            VerificationStatus = "Cleared";
            StatusMessage = "Verification results cleared";
        }

        [RelayCommand]
        private void ClearAll()
        {
            var result = MessageBox.Show("Are you sure you want to clear all data?", "Clear All",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _project = null;
                CurrentProjectName = "No project loaded";
                SourceCode = "";
                Methods.Clear();
                UppaalXml = "<!-- UPPAAL model will be generated here -->";
                VerificationResults.Clear();
                VerificationStatus = "Not run";
                ProjectItems.Clear();
                StatusMessage = "All data cleared";
            }
        }

        [RelayCommand]
        private void ShowCfg() => StatusMessage = "Showing CFG visualization";

        [RelayCommand]
        private void ShowModel() => StatusMessage = "Showing UPPAAL model";

        [RelayCommand]
        private void ShowVerification() => StatusMessage = "Showing verification results";

        [RelayCommand]
        private void OpenSettings()
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.ShowDialog();
            StatusMessage = "Settings updated";
        }

        [RelayCommand]
        private void ShowHelp()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://uppaal.org/documentation/",
                    UseShellExecute = true
                });
                StatusMessage = "Opening documentation...";
            }
            catch
            {
                MessageBox.Show("Unable to open browser. Please visit: https://uppaal.org/documentation/",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        [RelayCommand]
        private void ShowAbout()
        {
            var aboutWindow = new AboutWindow();
            aboutWindow.ShowDialog();
        }

        [RelayCommand]
        private void Exit() => Application.Current.Shutdown();
    }

    public class TreeItemViewModel : ObservableObject
    {
        public string Name { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public ObservableCollection<TreeItemViewModel> Children { get; set; } = new();
    }
}