// ViewModels/MainViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSharpToUppaal.Backend.Models;
using CSharpToUppaal.Backend.Services;
using CSharpToUppaal.GUI.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.IO;
using IOPath = System.IO.Path;

#nullable disable
namespace CSharpToUppaal.GUI.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly CSharpToUppaal.Backend.CSharpToUppaalEngine _engine;
        private GuiAppSettings _settings;
        private CSharpToUppaal.Backend.Models.Project _project;
        private Canvas _cfgCanvas;
        private Canvas _uppaalCanvas;

        [ObservableProperty]
        private string _statusMessage = "Ready";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotBusy))]
        private bool _isBusy;

        [ObservableProperty]
        private string _currentProjectName = "No project loaded";

        [ObservableProperty]
        private string _sourceCode = @"using System;

namespace BankSystem
{
    public class Account
    {
        public static void Main()
        {
            int deposits = 500;
            int withdrawals = 200;
            int balance = GetBalance(deposits, withdrawals);
            int updated = ProcessTransaction(balance, 50, 1);
            int total = CalculateInterest(updated, 5, 3);
        }

        public static int GetBalance(int deposits, int withdrawals)
        {
            int balance = deposits - withdrawals;
            if (balance < 0)
            {
                balance = 0;
            }
            return balance;
        }

        public static int ProcessTransaction(int balance, int amount, int txType)
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

        public static int CalculateInterest(int principal, int rate, int years)
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

        [ObservableProperty]
        private string _modelName = "GeneratedModel";

        [ObservableProperty]
        private string _uppaalXml = "<!-- UPPAAL model will be generated here -->";

        [ObservableProperty]
        private bool _isXmlReadOnly = true;

        // ── Layout Fixer tab properties ──
        [ObservableProperty]
        private string _layoutInputXml = "";

        [ObservableProperty]
        private string _layoutOutputXml = "";

        [ObservableProperty]
        private string _layoutReportText = "";

        [ObservableProperty]
        private string _requirementsText = "The generated model must be deadlock free.";

        [ObservableProperty]
        private ObservableCollection<FunctionSelectionViewModel> _functionSelections = new();

        [ObservableProperty]
        private ObservableCollection<TranslationAssumption> _assumptions = new();

        [ObservableProperty]
        private ObservableCollection<VariableDomain> _domains = new();

        [ObservableProperty]
        private ObservableCollection<GeneratedQuery> _generatedQueries = new();

        [ObservableProperty]
        private ObservableCollection<UppaalCompatibilityIssue> _compatibilityIssues = new();

        [ObservableProperty]
        private string _readinessStatus = "Not checked";

        [ObservableProperty]
        private string _generationReportText = "";

        [ObservableProperty]
        private string _settingsSummary = "";

        [ObservableProperty]
        private ObservableCollection<CSharpToUppaal.Backend.Models.MethodInfo> _methods = new();

        partial void OnMethodsChanged(ObservableCollection<CSharpToUppaal.Backend.Models.MethodInfo> value)
        {
            Console.WriteLine($"Methods collection changed. New count: {value?.Count ?? 0}");
            RefreshSelectedMethods();
        }

        private ObservableCollection<MethodInfo> _selectedMethods = [];
        public ObservableCollection<MethodInfo> SelectedMethods
        {
            get => _selectedMethods;
            set => SetProperty(ref _selectedMethods, value);
        }

        [ObservableProperty]
        private CSharpToUppaal.Backend.Models.MethodInfo _selectedMethod;

        [ObservableProperty]
        private ObservableCollection<TreeItemViewModel> _projectItems = new();

        public bool IsNotBusy => !IsBusy;

        public MainViewModel()
        {
            _settings = AppSettingsService.Load();
            _engine = new CSharpToUppaal.Backend.CSharpToUppaalEngine();
            UpdateSettingsSummary();

            // Test: Add a dummy method to verify binding works
            Console.WriteLine("MainViewModel constructor called");
            Console.WriteLine($"Methods collection initialized: {Methods != null}");

            LoadSampleProject();
        }

        public void SetCfgCanvas(Canvas canvas)
        {
            _cfgCanvas = canvas;
        }

        public void SetUppaalCanvas(Canvas canvas)
        {
            _uppaalCanvas = canvas;
        }

        partial void OnSelectedMethodChanged(CSharpToUppaal.Backend.Models.MethodInfo value)
        {
            if (value != null && _cfgCanvas != null)
            {
                _ = DrawCfgAsync(value);
            }
        }

        private async Task DrawCfgAsync(CSharpToUppaal.Backend.Models.MethodInfo method)
        {
            try
            {
                if (_cfgCanvas == null) return;

                StatusMessage = $"Drawing CFG for {method.Name}...";

                _cfgCanvas.Children.Clear();

                var cfg = await _engine.GenerateCfgForMethodAsync(method);

                // Layout parameters
                double nodeWidth = 140;
                double nodeHeight = 64;
                double horizontalSpacing = 220;
                double verticalSpacing = 110;
                double startX = 350; // Center starting position
                double startY = 60;

                // Calculate positions for nodes with hierarchical layout
                var nodePositions = new Dictionary<string, Point>();
                var processedNodes = new HashSet<string>();
                var nodeLevels = new Dictionary<string, int>(); // Track vertical level of each node
                
                // Helper function to get children of a node
                List<string> GetChildren(string nodeId)
                {
                    return cfg.Edges.Where(e => e.FromNodeId == nodeId)
                                   .Select(e => e.ToNodeId)
                                   .ToList();
                }
                
                // Recursive function to layout nodes
                void LayoutNode(string nodeId, int level, double xOffset)
                {
                    if (processedNodes.Contains(nodeId))
                        return;
                        
                    processedNodes.Add(nodeId);
                    nodeLevels[nodeId] = level;
                    
                    var children = GetChildren(nodeId);
                    var outgoingCount = children.Count;
                    
                    // Position current node
                    nodePositions[nodeId] = new Point(startX + xOffset, startY + level * verticalSpacing);
                    
                    if (outgoingCount == 0)
                    {
                        // Leaf node, no children
                        return;
                    }
                    else if (outgoingCount == 1)
                    {
                        // Single child - continue straight down
                        LayoutNode(children[0], level + 1, xOffset);
                    }
                    else if (outgoingCount == 2)
                    {
                        // Branch (typically if/else) - spread children horizontally
                        var node = cfg.Nodes.FirstOrDefault(n => n.Id == nodeId);
                        
                        // Left branch (typically "false" or "else")
                        LayoutNode(children[0], level + 1, xOffset - horizontalSpacing);
                        
                        // Right branch (typically "true" or "then")
                        LayoutNode(children[1], level + 1, xOffset + horizontalSpacing);
                    }
                    else
                    {
                        // Multiple branches - spread them out horizontally
                        double totalWidth = (outgoingCount - 1) * horizontalSpacing;
                        double leftMost = xOffset - totalWidth / 2;
                        
                        for (int i = 0; i < outgoingCount; i++)
                        {
                            LayoutNode(children[i], level + 1, leftMost + i * horizontalSpacing);
                        }
                    }
                }
                
                // Start layout from entry node
                if (!string.IsNullOrEmpty(cfg.EntryNodeId))
                {
                    LayoutNode(cfg.EntryNodeId, 0, 0);
                }
                
                // Handle any unprocessed nodes (in case of disconnected components)
                foreach (var node in cfg.Nodes)
                {
                    if (!processedNodes.Contains(node.Id))
                    {
                        nodePositions[node.Id] = new Point(
                            startX + processedNodes.Count * horizontalSpacing / 2,
                            startY + processedNodes.Count * verticalSpacing
                        );
                    }
                }

                // Draw edges first (so they appear behind nodes)
                foreach (var edge in cfg.Edges)
                {
                    if (nodePositions.ContainsKey(edge.FromNodeId) &&
                        nodePositions.ContainsKey(edge.ToNodeId))
                    {
                        var fromPos = nodePositions[edge.FromNodeId];
                        var toPos = nodePositions[edge.ToNodeId];

                        // Calculate center points
                        double fromCenterX = fromPos.X + nodeWidth / 2;
                        double fromCenterY = fromPos.Y + nodeHeight / 2;
                        double toCenterX = toPos.X + nodeWidth / 2;
                        double toCenterY = toPos.Y + nodeHeight / 2;

                        // Calculate angle between nodes
                        var angle = Math.Atan2(toCenterY - fromCenterY, toCenterX - fromCenterX);
                        
                        // Calculate the edge of the target node (adjust arrow to stop at node boundary)
                        double arrowMargin = 30; // Distance from center to edge of node
                        double arrowTipX = toCenterX - arrowMargin * Math.Cos(angle);
                        double arrowTipY = toCenterY - arrowMargin * Math.Sin(angle);

                        var edgeBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180));

                        // Draw line from source to target (stopping before the node)
                        var line = new Line
                        {
                            X1 = fromCenterX,
                            Y1 = fromCenterY,
                            X2 = arrowTipX,
                            Y2 = arrowTipY,
                            Stroke = edgeBrush,
                            StrokeThickness = 2
                        };

                        _cfgCanvas.Children.Add(line);

                        // Add arrow head
                        var arrowSize = 13;

                        var arrow = new Polygon
                        {
                            Fill = edgeBrush,
                            Stroke = edgeBrush,
                            StrokeThickness = 1,
                            Points = new PointCollection
                            {
                                new Point(arrowTipX, arrowTipY),
                                new Point(
                                    arrowTipX - arrowSize * Math.Cos(angle - Math.PI / 6),
                                    arrowTipY - arrowSize * Math.Sin(angle - Math.PI / 6)
                                ),
                                new Point(
                                    arrowTipX - arrowSize * Math.Cos(angle + Math.PI / 6),
                                    arrowTipY - arrowSize * Math.Sin(angle + Math.PI / 6)
                                )
                            }
                        };
                        _cfgCanvas.Children.Add(arrow);

                        // Add edge label if present
                        if (!string.IsNullOrEmpty(edge.Label))
                        {
                            var labelBg = new Border
                            {
                                Background = new SolidColorBrush(Color.FromArgb(200, 45, 45, 48)),
                                BorderBrush = new SolidColorBrush(Color.FromRgb(100, 160, 220)),
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(3),
                                Padding = new Thickness(4, 2, 4, 2),
                                Child = new TextBlock
                                {
                                    Text = edge.Label,
                                    FontSize = 10,
                                    FontWeight = FontWeights.SemiBold,
                                    Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 255))
                                }
                            };
                            Canvas.SetLeft(labelBg, (fromPos.X + toPos.X) / 2 + nodeWidth / 4);
                            Canvas.SetTop(labelBg, (fromPos.Y + toPos.Y) / 2 + nodeHeight / 4);
                            _cfgCanvas.Children.Add(labelBg);
                        }
                    }
                }

                // Draw nodes
                foreach (var node in cfg.Nodes)
                {
                    if (!nodePositions.ContainsKey(node.Id)) continue;

                    var pos = nodePositions[node.Id];

                    // Choose fill/stroke colors per node type (dark-theme friendly, saturated fills)
                    SolidColorBrush fillColor;
                    SolidColorBrush strokeColor;
                    SolidColorBrush labelColor;
                    SolidColorBrush codeColor;
                    switch (node.Type)
                    {
                        case CSharpToUppaal.Backend.Models.NodeType.Entry:
                            fillColor  = new SolidColorBrush(Color.FromRgb(39, 174, 96));   // green
                            strokeColor = new SolidColorBrush(Color.FromRgb(20, 120, 60));
                            labelColor = new SolidColorBrush(Colors.White);
                            codeColor  = new SolidColorBrush(Color.FromRgb(200, 255, 220));
                            break;
                        case CSharpToUppaal.Backend.Models.NodeType.Exit:
                            fillColor  = new SolidColorBrush(Color.FromRgb(192, 57, 57));   // red
                            strokeColor = new SolidColorBrush(Color.FromRgb(130, 30, 30));
                            labelColor = new SolidColorBrush(Colors.White);
                            codeColor  = new SolidColorBrush(Color.FromRgb(255, 200, 200));
                            break;
                        case CSharpToUppaal.Backend.Models.NodeType.Condition:
                            fillColor  = new SolidColorBrush(Color.FromRgb(41, 128, 185));  // blue
                            strokeColor = new SolidColorBrush(Color.FromRgb(21, 80, 130));
                            labelColor = new SolidColorBrush(Colors.White);
                            codeColor  = new SolidColorBrush(Color.FromRgb(190, 225, 255));
                            break;
                        case CSharpToUppaal.Backend.Models.NodeType.Loop:
                            fillColor  = new SolidColorBrush(Color.FromRgb(142, 68, 173));  // purple
                            strokeColor = new SolidColorBrush(Color.FromRgb(90, 30, 120));
                            labelColor = new SolidColorBrush(Colors.White);
                            codeColor  = new SolidColorBrush(Color.FromRgb(230, 200, 255));
                            break;
                        case CSharpToUppaal.Backend.Models.NodeType.Return:
                            fillColor  = new SolidColorBrush(Color.FromRgb(211, 84, 0));    // orange
                            strokeColor = new SolidColorBrush(Color.FromRgb(140, 50, 0));
                            labelColor = new SolidColorBrush(Colors.White);
                            codeColor  = new SolidColorBrush(Color.FromRgb(255, 220, 180));
                            break;
                        default:
                            fillColor  = new SolidColorBrush(Color.FromRgb(62, 62, 66));    // dark gray
                            strokeColor = new SolidColorBrush(Color.FromRgb(100, 160, 220));
                            labelColor = new SolidColorBrush(Color.FromRgb(241, 241, 241));
                            codeColor  = new SolidColorBrush(Color.FromRgb(180, 200, 220));
                            break;
                    }

                    // Draw node shape
                    Shape shape;
                    if (node.Type == CSharpToUppaal.Backend.Models.NodeType.Condition ||
                        node.Type == CSharpToUppaal.Backend.Models.NodeType.Loop)
                    {
                        // Diamond for conditions and loops
                        shape = new Polygon
                        {
                            Points = new PointCollection
                            {
                                new Point(pos.X + nodeWidth / 2, pos.Y),
                                new Point(pos.X + nodeWidth, pos.Y + nodeHeight / 2),
                                new Point(pos.X + nodeWidth / 2, pos.Y + nodeHeight),
                                new Point(pos.X, pos.Y + nodeHeight / 2)
                            },
                            Fill = fillColor,
                            Stroke = strokeColor,
                            StrokeThickness = 2
                        };
                    }
                    else if (node.Type == CSharpToUppaal.Backend.Models.NodeType.Entry ||
                             node.Type == CSharpToUppaal.Backend.Models.NodeType.Exit)
                    {
                        // Ellipse for entry/exit
                        shape = new Ellipse
                        {
                            Width = nodeWidth,
                            Height = nodeHeight,
                            Fill = fillColor,
                            Stroke = strokeColor,
                            StrokeThickness = 2.5
                        };
                        Canvas.SetLeft(shape, pos.X);
                        Canvas.SetTop(shape, pos.Y);
                    }
                    else
                    {
                        // Rounded rectangle for other nodes
                        shape = new Rectangle
                        {
                            Width = nodeWidth,
                            Height = nodeHeight,
                            Fill = fillColor,
                            Stroke = strokeColor,
                            StrokeThickness = 2,
                            RadiusX = 6,
                            RadiusY = 6
                        };
                        Canvas.SetLeft(shape, pos.X);
                        Canvas.SetTop(shape, pos.Y);
                    }

                    _cfgCanvas.Children.Add(shape);

                    // Add node label
                    var text = new TextBlock
                    {
                        Text = node.Label,
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        Foreground = labelColor,
                        TextAlignment = TextAlignment.Center,
                        Width = nodeWidth,
                        TextWrapping = TextWrapping.Wrap
                    };
                    Canvas.SetLeft(text, pos.X);
                    Canvas.SetTop(text, pos.Y + 6);
                    _cfgCanvas.Children.Add(text);

                    // Add node code (truncated)
                    if (!string.IsNullOrEmpty(node.Code))
                    {
                        var codeText = node.Code.Length > 22 ?
                            node.Code.Substring(0, 19) + "..." : node.Code;

                        var code = new TextBlock
                        {
                            Text = codeText,
                            FontSize = 9,
                            Foreground = codeColor,
                            TextAlignment = TextAlignment.Center,
                            Width = nodeWidth,
                            TextWrapping = TextWrapping.NoWrap
                        };
                        Canvas.SetLeft(code, pos.X);
                        Canvas.SetTop(code, pos.Y + 28);
                        _cfgCanvas.Children.Add(code);
                    }
                }

                // Update canvas size
                if (nodePositions.Any())
                {
                    var maxX = nodePositions.Values.Max(p => p.X) + nodeWidth + 50;
                    var maxY = nodePositions.Values.Max(p => p.Y) + nodeHeight + 50;
                    _cfgCanvas.Width = Math.Max(800, maxX);
                    _cfgCanvas.Height = Math.Max(600, maxY);
                }

                StatusMessage = $"CFG drawn for {method.Name} ({cfg.Nodes.Count} nodes)";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error drawing CFG: {ex.Message}";
            }
        }

        private async Task DrawUppaalModelAsync(CSharpToUppaal.Backend.Models.UppaalModel model)
        {
            try
            {
                if (_uppaalCanvas == null) return;

                StatusMessage = $"Drawing UPPAAL model visualization...";

                _uppaalCanvas.Children.Clear();

                if (model.Templates == null || !model.Templates.Any())
                {
                    // Show message if no templates
                    var noDataText = new TextBlock
                    {
                        Text = "No templates found in UPPAAL model.\nGenerate a model to see visualization.",
                        FontSize = 16,
                        Foreground = Brushes.Gray,
                        TextAlignment = TextAlignment.Center
                    };
                    Canvas.SetLeft(noDataText, 400);
                    Canvas.SetTop(noDataText, 300);
                    _uppaalCanvas.Children.Add(noDataText);
                    return;
                }

                // Layout parameters
                double locationWidth = 100;
                double locationHeight = 70;
                double startX = 80;
                double startY = 80;

                // Calculate total height needed for all templates
                double totalHeight = startY;
                foreach (var template in model.Templates)
                {
                    int locationCount = template.Locations.Count;
                    double radius = Math.Max(150, locationCount * 30);
                    totalHeight += radius * 2 + 200; // Space for template + legend + padding
                }
                totalHeight += 100; // Extra bottom padding

                // Set canvas height dynamically
                _uppaalCanvas.Height = Math.Max(800, totalHeight);
                _uppaalCanvas.Width = 1200;

                int templateOffset = 0;

                foreach (var template in model.Templates)
                {
                    // Add template title
                    var title = new TextBlock
                    {
                        Text = $"Template: {template.Name}",
                        FontSize = 18,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(44, 62, 80))
                    };
                    Canvas.SetLeft(title, startX);
                    Canvas.SetTop(title, startY + templateOffset - 40);
                    _uppaalCanvas.Children.Add(title);

                    // Calculate positions for locations (circular layout)
                    var locationPositions = new Dictionary<string, Point>();
                    int locationCount = template.Locations.Count;
                    double radius = Math.Max(150, locationCount * 30);
                    double angleStep = 2 * Math.PI / Math.Max(locationCount, 1);

                    for (int i = 0; i < template.Locations.Count; i++)
                    {
                        var location = template.Locations[i];
                        double angle = i * angleStep - Math.PI / 2; // Start from top
                        double x = startX + radius + radius * Math.Cos(angle);
                        double y = startY + templateOffset + radius + radius * Math.Sin(angle);
                        locationPositions[location.Id] = new Point(x, y);
                    }

                    // Draw transitions first (so they appear behind locations)
                    foreach (var transition in template.Transitions)
                    {
                        if (locationPositions.ContainsKey(transition.Source) &&
                            locationPositions.ContainsKey(transition.Target))
                        {
                            var fromPos = locationPositions[transition.Source];
                            var toPos = locationPositions[transition.Target];

                            // Check if it's a self-loop
                            if (transition.Source == transition.Target)
                            {
                                // Draw self-loop as an arc
                                var loopPath = new System.Windows.Shapes.Path
                                {
                                    Stroke = Brushes.DarkBlue,
                                    StrokeThickness = 2,
                                    Data = new PathGeometry
                                    {
                                        Figures = new PathFigureCollection
                                        {
                                            new PathFigure
                                            {
                                                StartPoint = new Point(fromPos.X + locationWidth / 2, fromPos.Y),
                                                Segments = new PathSegmentCollection
                                                {
                                                    new ArcSegment
                                                    {
                                                        Point = new Point(fromPos.X + locationWidth, fromPos.Y + locationHeight / 2),
                                                        Size = new Size(30, 30),
                                                        SweepDirection = SweepDirection.Clockwise,
                                                        IsLargeArc = false
                                                    }
                                                }
                                            }
                                        }
                                    }
                                };
                                _uppaalCanvas.Children.Add(loopPath);
                            }
                            else
                            {
                                // Draw transition line
                                var line = new Line
                                {
                                    X1 = fromPos.X + locationWidth / 2,
                                    Y1 = fromPos.Y + locationHeight / 2,
                                    X2 = toPos.X + locationWidth / 2,
                                    Y2 = toPos.Y + locationHeight / 2,
                                    Stroke = Brushes.DarkBlue,
                                    StrokeThickness = 2
                                };
                                _uppaalCanvas.Children.Add(line);

                                // Add arrow head
                                var angle = Math.Atan2(toPos.Y - fromPos.Y, toPos.X - fromPos.X);
                                var arrowSize = 12;

                                var arrow = new Polygon
                                {
                                    Fill = Brushes.DarkBlue,
                                    Points = new PointCollection
                                    {
                                        new Point(toPos.X + locationWidth / 2, toPos.Y + locationHeight / 2),
                                        new Point(
                                            toPos.X + locationWidth / 2 - arrowSize * Math.Cos(angle - Math.PI / 6),
                                            toPos.Y + locationHeight / 2 - arrowSize * Math.Sin(angle - Math.PI / 6)
                                        ),
                                        new Point(
                                            toPos.X + locationWidth / 2 - arrowSize * Math.Cos(angle + Math.PI / 6),
                                            toPos.Y + locationHeight / 2 - arrowSize * Math.Sin(angle + Math.PI / 6)
                                        )
                                    }
                                };
                                _uppaalCanvas.Children.Add(arrow);
                            }

                            // Add transition labels
                            var labelY = (fromPos.Y + toPos.Y) / 2;
                            var labelX = (fromPos.X + toPos.X) / 2;

                            if (!string.IsNullOrEmpty(transition.Guard) || 
                                !string.IsNullOrEmpty(transition.Update) || 
                                !string.IsNullOrEmpty(transition.Synchronization))
                            {
                                var labels = new List<string>();
                                if (!string.IsNullOrEmpty(transition.Guard)) labels.Add($"[{transition.Guard}]");
                                if (!string.IsNullOrEmpty(transition.Synchronization)) labels.Add(transition.Synchronization);
                                if (!string.IsNullOrEmpty(transition.Update)) labels.Add(transition.Update);

                                var label = new TextBlock
                                {
                                    Text = string.Join("\n", labels),
                                    FontSize = 10,
                                    Background = Brushes.White,
                                    Foreground = Brushes.DarkBlue,
                                    Padding = new Thickness(2)
                                };
                                Canvas.SetLeft(label, labelX + locationWidth / 4);
                                Canvas.SetTop(label, labelY);
                                _uppaalCanvas.Children.Add(label);
                            }
                        }
                    }

                    // Draw locations
                    foreach (var location in template.Locations)
                    {
                        if (!locationPositions.ContainsKey(location.Id)) continue;

                        var pos = locationPositions[location.Id];

                        // Choose color based on location type
                        Brush fillColor = Brushes.LightSteelBlue;
                        if (location.IsInitial)
                            fillColor = new SolidColorBrush(Color.FromRgb(144, 238, 144)); // Light green
                        else if (location.IsUrgent)
                            fillColor = new SolidColorBrush(Color.FromRgb(255, 160, 122)); // Light salmon
                        else if (location.IsCommitted)
                            fillColor = new SolidColorBrush(Color.FromRgb(255, 218, 185)); // Peach

                        // Draw location circle
                        var ellipse = new Ellipse
                        {
                            Width = locationWidth,
                            Height = locationHeight,
                            Fill = fillColor,
                            Stroke = Brushes.Black,
                            StrokeThickness = 2
                        };
                        Canvas.SetLeft(ellipse, pos.X);
                        Canvas.SetTop(ellipse, pos.Y);
                        _uppaalCanvas.Children.Add(ellipse);

                        // Draw double circle for initial location
                        if (location.IsInitial)
                        {
                            var innerEllipse = new Ellipse
                            {
                                Width = locationWidth - 8,
                                Height = locationHeight - 8,
                                Fill = Brushes.Transparent,
                                Stroke = Brushes.Black,
                                StrokeThickness = 2
                            };
                            Canvas.SetLeft(innerEllipse, pos.X + 4);
                            Canvas.SetTop(innerEllipse, pos.Y + 4);
                            _uppaalCanvas.Children.Add(innerEllipse);
                        }

                        // Add location name
                        var locationName = new TextBlock
                        {
                            Text = location.Name,
                            FontSize = 12,
                            FontWeight = FontWeights.Bold,
                            Foreground = Brushes.Black,
                            TextAlignment = TextAlignment.Center,
                            Width = locationWidth
                        };
                        Canvas.SetLeft(locationName, pos.X);
                        Canvas.SetTop(locationName, pos.Y + locationHeight / 2 - 8);
                        _uppaalCanvas.Children.Add(locationName);

                        // Add labels (invariant, etc.)
                        if (location.Labels != null && location.Labels.Any())
                        {
                            var labelText = string.Join(", ", location.Labels.Select(l => $"{l.Key}:{l.Value}"));
                            if (!string.IsNullOrEmpty(labelText))
                            {
                                var labels = new TextBlock
                                {
                                    Text = labelText,
                                    FontSize = 9,
                                    Foreground = Brushes.DarkSlateGray,
                                    TextAlignment = TextAlignment.Center,
                                    Width = locationWidth + 40,
                                    TextWrapping = TextWrapping.Wrap
                                };
                                Canvas.SetLeft(labels, pos.X - 20);
                                Canvas.SetTop(labels, pos.Y + locationHeight + 5);
                                _uppaalCanvas.Children.Add(labels);
                            }
                        }
                    }

                    // Add legend
                    if (templateOffset == 0) // Only for first template
                    {
                        // Position legend in top right corner
                        var legendY = 20; // Top of canvas
                        var legendX = _uppaalCanvas.Width - 200; // Right side with 200px width for legend

                        var legendTitle = new TextBlock
                        {
                            Text = "Legend:",
                            FontSize = 12,
                            FontWeight = FontWeights.Bold,
                            Foreground = Brushes.Black
                        };
                        Canvas.SetLeft(legendTitle, legendX);
                        Canvas.SetTop(legendTitle, legendY);
                        _uppaalCanvas.Children.Add(legendTitle);

                        var legendItems = new[]
                        {
                            ("Initial Location", Color.FromRgb(144, 238, 144)),
                            ("Urgent Location", Color.FromRgb(255, 160, 122)),
                            ("Committed Location", Color.FromRgb(255, 218, 185)),
                            ("Normal Location", Colors.LightSteelBlue)
                        };

                        for (int i = 0; i < legendItems.Length; i++)
                        {
                            var (text, color) = legendItems[i];
                            var itemY = legendY + 25 + i * 25;

                            var circle = new Ellipse
                            {
                                Width = 15,
                                Height = 15,
                                Fill = new SolidColorBrush(color),
                                Stroke = Brushes.Black,
                                StrokeThickness = 1
                            };
                            Canvas.SetLeft(circle, legendX);
                            Canvas.SetTop(circle, itemY);
                            _uppaalCanvas.Children.Add(circle);

                            var label = new TextBlock
                            {
                                Text = text,
                                FontSize = 11,
                                Foreground = Brushes.Black,
                                VerticalAlignment = VerticalAlignment.Center
                            };
                            Canvas.SetLeft(label, legendX + 25);
                            Canvas.SetTop(label, itemY);
                            _uppaalCanvas.Children.Add(label);
                        }
                    }

                    templateOffset += (int)(radius * 2 + 200);
                }

                StatusMessage = $"UPPAAL model visualization complete - {model.Templates.Count} template(s)";

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error drawing UPPAAL model: {ex.Message}";
                Console.WriteLine($"Error in DrawUppaalModelAsync: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private async void LoadSampleProject()
        {
            try
            {
                IsBusy = true;
                StatusMessage = "Loading sample project...";

                Console.WriteLine("Creating project...");
                _project = await _engine.CreateProjectAsync("Sample Project", "Demo project with sample code");
                CurrentProjectName = _project.Name;
                Console.WriteLine($"Project created: {_project.Name}");

                Console.WriteLine($"Adding source code (length: {SourceCode.Length})...");
                var sourceFile = await _engine.AddSourceCodeAsync(_project, SourceCode, "Sample.cs");
                Console.WriteLine($"Source file added. Methods found: {sourceFile.Methods.Count}");

                Methods.Clear();
                foreach (var method in sourceFile.Methods)
                {
                    Console.WriteLine($"Adding method: {method.Name}, ReturnType: {method.ReturnType}");
                    Methods.Add(method);
                }
                Console.WriteLine($"Total methods in collection: {Methods.Count}");

                if (Methods.Any())
                {
                    SelectedMethod = Methods.First();
                    Console.WriteLine($"Selected method: {SelectedMethod.Name}");
                }
                else
                {
                    Console.WriteLine("WARNING: No methods found!");
                }

                await RefreshSemanticFunctionListAsync();
                UpdateProjectTree();

                StatusMessage = $"Sample project loaded with {Methods.Count} methods";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR in LoadSampleProject: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                StatusMessage = $"Error loading sample: {ex.Message}";
                MessageBox.Show($"Error loading sample project: {ex.Message}\n\n{ex.StackTrace}", "Error",
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
                    Name = IOPath.GetFileName(sourceFile.FilePath),
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

        private async Task RefreshSemanticFunctionListAsync()
        {
            FunctionSelections.Clear();
            Assumptions.Clear();
            Domains.Clear();
            GeneratedQueries.Clear();
            CompatibilityIssues.Clear();
            ReadinessStatus = "Not checked";
            GenerationReportText = "";

            if (string.IsNullOrWhiteSpace(SourceCode))
                return;

            var analysis = await _engine.AnalyzeSourceCodeAsync(SourceCode, "Source.cs");
            var hasMain = analysis.Functions.Any(f => f.Name == "Main");

            foreach (var function in analysis.Functions.OrderBy(f => f.LineNumber))
            {
                var vm = new FunctionSelectionViewModel(function)
                {
                    IsSelected = hasMain ? function.Name == "Main" : true,
                    Mode = FunctionModelingMode.ExplicitAutomaton
                };
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FunctionSelectionViewModel.IsSelected))
                        RefreshSelectedMethods();
                };
                FunctionSelections.Add(vm);
            }

            foreach (var assumption in analysis.Assumptions)
                Assumptions.Add(assumption);

            foreach (var diagnostic in analysis.Diagnostics)
            {
                Assumptions.Add(new TranslationAssumption
                {
                    Severity = AssumptionSeverity.Warning,
                    Category = "Compilation",
                    Message = diagnostic
                });
            }

            RefreshSelectedMethods();
            StatusMessage = $"Semantic analysis found {FunctionSelections.Count} function(s)";
        }

        private void RefreshSelectedMethods()
        {
            var selectedNames = FunctionSelections
                .Where(f => f.IsSelected)
                .Select(f => f.Function.Name)
                .ToHashSet(StringComparer.Ordinal);

            SelectedMethods.Clear();
            foreach (var method in Methods)
            {
                if (selectedNames.Contains(method.Name))
                    SelectedMethods.Add(method);
            }

            if (SelectedMethod != null && !SelectedMethods.Contains(SelectedMethod))
                SelectedMethod = SelectedMethods.FirstOrDefault();
        }

        private List<FunctionSelection> BuildFunctionSelections()
        {
            return FunctionSelections.Select(f => new FunctionSelection
            {
                FunctionId = f.FunctionId,
                IsSelected = f.IsSelected,
                Mode = f.Mode
            }).ToList();
        }

        private void ApplyGenerationReport(UppaalModel model)
        {
            Assumptions.Clear();
            Domains.Clear();
            GeneratedQueries.Clear();
            CompatibilityIssues.Clear();

            foreach (var assumption in model.GenerationReport.Assumptions)
                Assumptions.Add(assumption);

            foreach (var domain in model.GenerationReport.Domains)
                Domains.Add(domain);

            foreach (var query in model.GenerationReport.Queries)
                GeneratedQueries.Add(query);

            ApplyCompatibilityReport(model.GenerationReport.Compatibility);
            GenerationReportText = model.GenerationReport.Summary;
        }

        private void UpdateSettingsSummary()
        {
            var ollama = _settings.OllamaEnabled ? $"{_settings.OllamaModel} at {_settings.OllamaBaseUrl}" : "Ollama disabled";
            SettingsSummary = $"Export-first mode; {ollama}";
        }

        private void ApplyCompatibilityReport(UppaalCompatibilityResult compatibility)
        {
            CompatibilityIssues.Clear();
            foreach (var issue in compatibility.Issues)
                CompatibilityIssues.Add(issue);

            ReadinessStatus = compatibility.IsReady
                ? $"Ready for UPPAAL 4.1.18 ({compatibility.WarningCount} warning(s))"
                : $"Blocked: {compatibility.ErrorCount} error(s), {compatibility.WarningCount} warning(s)";
        }

        private UppaalCompatibilityResult ValidateCurrentXmlForExport()
        {
            var compatibility = new UppaalCompatibilityValidator().Validate(UppaalXml);
            ApplyCompatibilityReport(compatibility);
            return compatibility;
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
                    FunctionSelections.Clear();
                    Assumptions.Clear();
                    Domains.Clear();
                    GeneratedQueries.Clear();
                    CompatibilityIssues.Clear();
                    ReadinessStatus = "Not checked";
                    GenerationReportText = "";
                    UppaalXml = "<!-- UPPAAL model will be generated here -->";
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
                            IOPath.GetFileNameWithoutExtension(openFileDialog.FileName));
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

                    await RefreshSemanticFunctionListAsync();
                    UpdateProjectTree();

                    StatusMessage = $"Loaded {sourceFile.Methods.Count} methods from {IOPath.GetFileName(openFileDialog.FileName)}";
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

                Console.WriteLine("=== ParseCode called ===");
                Console.WriteLine($"Source code length: {SourceCode?.Length ?? 0}");

                if (string.IsNullOrWhiteSpace(SourceCode))
                {
                    Console.WriteLine("ERROR: Source code is empty!");
                    MessageBox.Show("Please enter some C# code first", "No Code",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (_project == null)
                {
                    Console.WriteLine("Creating new project...");
                    _project = await _engine.CreateProjectAsync("Parsed Project");
                    CurrentProjectName = _project.Name;
                }

                // Clear previous source files so only the current editor content is used
                _project.SourceFiles.Clear();
                _project.GeneratedModels.Clear();

                Console.WriteLine("Adding source code to project...");
                var sourceFile = await _engine.AddSourceCodeAsync(_project, SourceCode, "Source.cs");
                Console.WriteLine($"Source file added. Methods count: {sourceFile.Methods.Count}");

                Methods.Clear();
                Console.WriteLine("Cleared Methods collection");

                foreach (var method in sourceFile.Methods)
                {
                    Console.WriteLine($"Adding method: {method.Name} (ReturnType: {method.ReturnType}, LOC: {method.LinesOfCode})");
                    Methods.Add(method);
                }

                Console.WriteLine($"Total methods in collection after adding: {Methods.Count}");

                if (Methods.Any())
                {
                    SelectedMethod = Methods.First();
                    Console.WriteLine($"Selected first method: {SelectedMethod.Name}");
                }
                else
                {
                    Console.WriteLine("WARNING: Methods collection is empty after parsing!");
                }

                await RefreshSemanticFunctionListAsync();
                UpdateProjectTree();

                StatusMessage = $"Parsed {sourceFile.Methods.Count} methods";
                Console.WriteLine($"=== ParseCode completed with {Methods.Count} methods ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR in ParseCode: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                    Console.WriteLine($"Inner stack trace: {ex.InnerException.StackTrace}");
                }
                StatusMessage = $"Error parsing code: {ex.Message}";
                MessageBox.Show($"Error parsing code: {ex.Message}\n\n{ex.StackTrace}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void SelectAllFunctions()
        {
            foreach (var f in FunctionSelections)
                f.IsSelected = true;
        }

        [RelayCommand]
        private void DeselectAllFunctions()
        {
            foreach (var f in FunctionSelections)
                f.IsSelected = false;
        }

        [RelayCommand]
        private async Task AnalyzeCode()
        {
            try
            {
                IsBusy = true;
                StatusMessage = "Running semantic analysis...";
                await RefreshSemanticFunctionListAsync();
                StatusMessage = $"Analysis complete: {FunctionSelections.Count} function(s), {Assumptions.Count} assumption(s)";
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
        private async Task LoadRequirementsFromFile()
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "Text Files (*.txt)|*.txt|Markdown Files (*.md)|*.md|All files (*.*)|*.*",
                    Title = "Load Requirements from File"
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    RequirementsText = await System.IO.File.ReadAllTextAsync(openFileDialog.FileName);
                    StatusMessage = $"Requirements loaded from {IOPath.GetFileName(openFileDialog.FileName)}";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading requirements: {ex.Message}";
                MessageBox.Show($"Error loading requirements: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private async Task InterpretRequirements()
        {
            try
            {
                if (FunctionSelections.Count == 0)
                    await RefreshSemanticFunctionListAsync();

                IsBusy = true;
                StatusMessage = "Interpreting requirements...";

                var service = new RequirementTranslationService();
                var context = new RequirementTranslationContext
                {
                    Functions = FunctionSelections.Select(f => f.Function).ToList(),
                    Variables = Domains.Select(d => d.Name.Split('.').Last()).Distinct().ToList()
                };

                if (context.Variables.Count == 0)
                {
                    context.Variables = Methods
                        .SelectMany(m => m.Parameters.Select(p => p.Name))
                        .Distinct()
                        .ToList();
                }

                var settings = _settings.ToRequirementSettings();
                var interpretations = await service.InterpretAsync(
                    RequirementsText,
                    context,
                    settings);

                GeneratedQueries.Clear();
                foreach (var query in interpretations.SelectMany(i => i.GeneratedQueries))
                    GeneratedQueries.Add(query);

                // Report which path was used and surface any Ollama error
                var source = settings.Enabled
                    ? (service.LastUsedSource == "ollama" ? "Ollama AI" : "rule-based fallback")
                    : "rule-based";
                var errorNote = string.IsNullOrEmpty(service.LastError) ? string.Empty : $"  ⚠ {service.LastError}";
                StatusMessage = $"Requirements interpreted via {source}: {GeneratedQueries.Count} query/queries generated.{errorNote}";

                if (!string.IsNullOrEmpty(service.LastError))
                    MessageBox.Show(service.LastError, "Ollama Fallback", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error interpreting requirements: {ex.Message}";
                MessageBox.Show($"Error interpreting requirements: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

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

                await DrawCfgAsync(SelectedMethod);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error generating CFG: {ex.Message}";
                MessageBox.Show($"Error generating CFG: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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

                if (FunctionSelections.Count == 0)
                    await RefreshSemanticFunctionListAsync();

                var request = new ModelGenerationRequest
                {
                    ProjectName = ModelName,
                    SourceCode = SourceCode,
                    FileName = _project.SourceFiles.FirstOrDefault()?.FilePath ?? "Source.cs",
                    FunctionSelections = BuildFunctionSelections(),
                    DomainOverrides = Domains.ToList(),
                    UserQueries = GeneratedQueries.ToList(),
                    RequirementsText = GeneratedQueries.Count == 0 ? RequirementsText : string.Empty,
                    RequirementSettings = _settings.ToRequirementSettings()
                };

                var model = await _engine.GenerateModelAsync(request);
                UppaalXml = model.XmlContent;
                IsXmlReadOnly = false;
                ApplyGenerationReport(model);

                // Render visual preview
                await DrawUppaalModelAsync(model);

                StatusMessage = $"Generated UPPAAL model '{model.Name}' with {model.Templates.Count} templates";

                MessageBox.Show($"UPPAAL model '{model.Name}' generated successfully!\n" +
                              $"- Templates: {model.Templates.Count}\n" +
                              $"- Assumptions: {model.GenerationReport.Assumptions.Count}\n" +
                              $"- Queries: {model.GenerationReport.Queries.Count}\n" +
                              $"- Readiness: {ReadinessStatus}\n" +
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

                var compatibility = ValidateCurrentXmlForExport();
                if (!compatibility.IsReady)
                {
                    var details = string.Join(Environment.NewLine, compatibility.Issues
                        .Where(i => i.Severity == UppaalCompatibilitySeverity.Error)
                        .Take(8)
                        .Select(i => $"{i.Position}: {i.Message}"));
                    MessageBox.Show($"Export blocked because the XML is not ready for UPPAAL 4.1.18.\n\n{details}",
                        "UPPAAL Readiness Errors", MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusMessage = $"Export blocked: {compatibility.ErrorCount} UPPAAL readiness error(s).";
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

                    StatusMessage = $"Model exported to {IOPath.GetFileName(saveFileDialog.FileName)}";

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

        // ── Layout Fixer tab commands ──────────────────────────────────

        [RelayCommand]
        private async Task LoadLayoutInput()
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "UPPAAL XML Files (*.xml)|*.xml|All files (*.*)|*.*",
                    Title = "Load UPPAAL XML for Layout Fixing"
                };

                if (openFileDialog.ShowDialog() != true) return;

                IsBusy = true;
                StatusMessage = $"Loading {IOPath.GetFileName(openFileDialog.FileName)}...";

                string rawXml = await System.IO.File.ReadAllTextAsync(openFileDialog.FileName);
                LayoutInputXml = rawXml;
                LayoutOutputXml = "";

                StatusMessage = $"Loaded {IOPath.GetFileName(openFileDialog.FileName)} — click \"Fix Layout\" to process.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading XML: {ex.Message}";
                MessageBox.Show($"Error loading XML:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task FixLayout()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(LayoutInputXml))
                {
                    MessageBox.Show("Please load or paste UPPAAL XML into the Input panel first.",
                        "No Input XML", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                IsBusy = true;
                StatusMessage = "Fixing UPPAAL model layout...";

                var layoutService = new UppaalLayoutService();
                var result = await Task.Run(() => layoutService.FixLayoutWithReport(LayoutInputXml));

                LayoutOutputXml = result.XmlContent;
                LayoutReportText = result.ReportText;

                StatusMessage = "Layout fixed — nodes repositioned, edges re-routed.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error fixing layout: {ex.Message}";
                MessageBox.Show($"Error fixing layout:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task ExportFixedLayout()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(LayoutOutputXml))
                {
                    MessageBox.Show("No fixed XML to export. Run \"Fix Layout\" first.",
                        "No Output", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "UPPAAL XML Files (*.xml)|*.xml|All files (*.*)|*.*",
                    Title = "Export Fixed UPPAAL XML",
                    FileName = "FixedModel.xml"
                };

                if (saveFileDialog.ShowDialog() != true) return;

                IsBusy = true;
                StatusMessage = "Exporting fixed XML...";

                await System.IO.File.WriteAllTextAsync(saveFileDialog.FileName, LayoutOutputXml);

                StatusMessage = $"Exported to {saveFileDialog.FileName}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error exporting XML: {ex.Message}";
                MessageBox.Show($"Error exporting XML:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void ClearLayout()
        {
            LayoutInputXml = "";
            LayoutOutputXml = "";
            LayoutReportText = "";
            StatusMessage = "Layout Fixer cleared.";
        }

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

                    await System.IO.File.WriteAllTextAsync(saveFileDialog.FileName, dotGraph);

                    StatusMessage = $"CFG exported to {IOPath.GetFileName(saveFileDialog.FileName)}";

                    MessageBox.Show($"CFG exported as DOT file:\n{saveFileDialog.FileName}\n\n" +
                                  "You can visualize it with Graphviz:\n" +
                                  $"dot -Tpng \"{saveFileDialog.FileName}\" -o \"{IOPath.ChangeExtension(saveFileDialog.FileName, ".png")}\"",
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
        private async Task ExportCfgPng()
        {
            try
            {
                if (_cfgCanvas == null || _cfgCanvas.Children.Count == 0)
                {
                    MessageBox.Show("Please generate a CFG first", "No CFG to Export",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "PNG Image (*.png)|*.png|All files (*.*)|*.*",
                    Title = "Export CFG as PNG",
                    FileName = SelectedMethod != null ? $"{SelectedMethod.Name}_CFG.png" : "CFG.png"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    IsBusy = true;
                    StatusMessage = "Exporting CFG as PNG...";

                    // Get the actual size of the canvas content
                    double minX = double.MaxValue, minY = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue;

                    foreach (UIElement element in _cfgCanvas.Children)
                    {
                        double left = Canvas.GetLeft(element);
                        double top = Canvas.GetTop(element);

                        if (!double.IsNaN(left) && !double.IsNaN(top))
                        {
                            minX = Math.Min(minX, left);
                            minY = Math.Min(minY, top);

                            if (element is FrameworkElement fe)
                            {
                                maxX = Math.Max(maxX, left + fe.ActualWidth);
                                maxY = Math.Max(maxY, top + fe.ActualHeight);
                            }
                        }
                    }

                    // Add padding
                    double padding = 50;
                    minX = Math.Max(0, minX - padding);
                    minY = Math.Max(0, minY - padding);
                    maxX += padding;
                    maxY += padding;

                    // Calculate dimensions
                    int width = (int)(maxX - minX);
                    int height = (int)(maxY - minY);

                    if (width <= 0 || height <= 0)
                    {
                        width = (int)_cfgCanvas.ActualWidth;
                        height = (int)_cfgCanvas.ActualHeight;
                    }

                    // Create RenderTargetBitmap
                    var renderBitmap = new RenderTargetBitmap(
                        width,
                        height,
                        96, // DPI X
                        96, // DPI Y
                        PixelFormats.Pbgra32);

                    // Render the canvas to bitmap
                    _cfgCanvas.Measure(new Size(width, height));
                    _cfgCanvas.Arrange(new Rect(new Size(width, height)));
                    renderBitmap.Render(_cfgCanvas);

                    // Save as PNG
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

                    using (var fileStream = new FileStream(saveFileDialog.FileName, FileMode.Create))
                    {
                        encoder.Save(fileStream);
                    }

                    StatusMessage = $"CFG exported to {IOPath.GetFileName(saveFileDialog.FileName)}";

                    MessageBox.Show($"CFG exported as PNG image:\n{saveFileDialog.FileName}",
                                  "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error exporting PNG: {ex.Message}";
                MessageBox.Show($"Error exporting CFG as PNG: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task ToggleXmlView()
        {
            try
            {
                // Parse the current XML and refresh visual preview
                if (!string.IsNullOrWhiteSpace(UppaalXml) && !UppaalXml.Contains("<!--"))
                {
                    StatusMessage = "Refreshing visual preview...";
                    
                    // Create a model from current XML
                    var model = new CSharpToUppaal.Backend.Models.UppaalModel
                    {
                        Name = ModelName,
                        XmlContent = UppaalXml,
                        Templates = new List<CSharpToUppaal.Backend.Models.UppaalTemplate>()
                    };

                    // Try to parse XML and extract templates (basic parsing)
                    await ParseAndDrawUppaalXml(UppaalXml);
                    
                    StatusMessage = "Visual preview refreshed";
                }
                else
                {
                    StatusMessage = "Generate a UPPAAL model first to see visual preview";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error refreshing preview: {ex.Message}";
            }
        }

        private async Task ParseAndDrawUppaalXml(string xml)
        {
            // For now, just show a message that the model needs to be regenerated
            // A full XML parser would be needed for complete functionality
            if (_uppaalCanvas != null)
            {
                _uppaalCanvas.Children.Clear();
                
                var messageText = new TextBlock
                {
                    Text = "Visual preview updated.\nNote: To see the latest changes, regenerate the model.",
                    FontSize = 14,
                    Foreground = Brushes.Gray,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Width = 400
                };
                Canvas.SetLeft(messageText, 400);
                Canvas.SetTop(messageText, 300);
                _uppaalCanvas.Children.Add(messageText);
            }
            
            await Task.CompletedTask;
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
                FunctionSelections.Clear();
                Assumptions.Clear();
                Domains.Clear();
                GeneratedQueries.Clear();
                CompatibilityIssues.Clear();
                ReadinessStatus = "Not checked";
                GenerationReportText = "";
                UppaalXml = "<!-- UPPAAL model will be generated here -->";
                ProjectItems.Clear();
                if (_cfgCanvas != null) _cfgCanvas.Children.Clear();
                StatusMessage = "All data cleared";
            }
        }

        [RelayCommand]
        private void ShowCfg() => StatusMessage = "Showing CFG visualization";

        [RelayCommand]
        private void ShowModel() => StatusMessage = "Showing UPPAAL model";

        [RelayCommand]
        private void OpenSettings()
        {
            var settingsWindow = new SettingsWindow(_settings);
            if (settingsWindow.ShowDialog() == true)
            {
                _settings = settingsWindow.Settings;
                UpdateSettingsSummary();
                StatusMessage = "Settings updated";
            }
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

    public partial class FunctionSelectionViewModel : ObservableObject
    {
        public FunctionDescriptor Function { get; }

        public string FunctionId => Function.Id;
        public string Name => Function.DisplayName;
        public string Signature => Function.Signature;
        public string ReturnType => Function.ReturnType;
        public string Parameters => string.Join(", ", Function.Parameters.Select(p => $"{p.Type} {p.Name}"));
        public int LineNumber => Function.LineNumber;
        public string DependencyCount => Function.DirectCallIds.Count.ToString();
        public string Warnings => Function.UnresolvedCalls.Count == 0
            ? string.Empty
            : string.Join("; ", Function.UnresolvedCalls);

        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        private FunctionModelingMode _mode = FunctionModelingMode.ExplicitAutomaton;

        public FunctionSelectionViewModel(FunctionDescriptor function)
        {
            Function = function;
        }
    }

    public class TreeItemViewModel : ObservableObject
    {
        public string Name { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public ObservableCollection<TreeItemViewModel> Children { get; set; } = new();
    }
}
