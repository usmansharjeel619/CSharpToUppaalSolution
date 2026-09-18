using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CSharpToUppaal.Backend.Models;
using CSharpToUppaal.Backend.Services;
using CSharpToUppaal.GUI.ViewModels;
using Xunit;

namespace CSharpToUppaal.GUI.Tests;

public class RequirementsUiTests
{
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "WPF test timed out");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        Assert.True(done(), "Asynchronous WPF operation did not complete");
    }
    private static void Apply(MainViewModel vm, UppaalModel model)
    {
        vm.UppaalXml = model.XmlContent;
        typeof(MainViewModel).GetMethod("ApplyGenerationReport", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, new object[] { model });
    }
    [Fact]
    public void EditingRemovingAndReorderingRequirementsKeepQueryOwnership()
    {
        Sta(() =>
        {
            var vm = new MainViewModel(); PumpUntil(() => !vm.IsBusy);
            var first = vm.RequirementEntries[0];
            vm.AddRequirementCommand.Execute(null);
            var second = vm.RequirementEntries[1]; second.Text = "Other requirement";
            var q = new GeneratedQuery { RequirementId = first.Id, Formula = "A[] not deadlock", Source = "rules" };
            var other = new GeneratedQuery { RequirementId = second.Id, Formula = "A[] not deadlock", Source = "rules" };
            vm.GeneratedQueries.Add(q); vm.GeneratedQueries.Add(other);
            vm.MoveRequirementDownCommand.Execute(first);
            Assert.Contains(q, vm.GeneratedQueries);
            first.Text = "Changed requirement";
            Assert.DoesNotContain(q, vm.GeneratedQueries);
            Assert.Contains(other, vm.GeneratedQueries);
            Assert.Contains("Changed", first.Status);
            vm.RemoveRequirementCommand.Execute(second);
            Assert.DoesNotContain(other, vm.GeneratedQueries);
        });
    }
    [Fact]
    public void SingleEntryFiltersModelFunctionsAndInvalidatesResults()
    {
        Sta(() =>
        {
            var vm = new MainViewModel(); PumpUntil(() => !vm.IsBusy);
            vm.SelectedEntryFunction = vm.FunctionSelections.Single(f => f.Name.EndsWith("GetBalance"));
            vm.SingleFunctionMode = true;
            Assert.Single(vm.ModelFunctionSelections);
            Assert.Single(vm.SelectedMethods);
            Assert.Contains("regenerate", vm.ReadinessStatus);
            vm.SingleFunctionMode = false;
            Assert.True(vm.ModelFunctionSelections.Count > 1);
        });
    }
    [Fact]
    public void IndividualInterpretationPreservesOtherQueriesAndDomainsAreGrouped()
    {
        Sta(() =>
        {
            var vm = new MainViewModel(); PumpUntil(() => !vm.IsBusy);
            var task = new UppaalGeneratorService().GenerateModelFromCodeAsync(vm.SourceCode, "Sample");
            PumpUntil(() => task.IsCompleted); var model = task.GetAwaiter().GetResult();
            Assert.Equal(ModelGenerationStatus.Success, model.Status);
            Apply(vm, model);
            var automaticCount = vm.GeneratedQueries.Count;
            var entry = vm.RequirementEntries[0];
            var interpreted = vm.InterpretRequirementCommand.ExecuteAsync(entry); PumpUntil(() => interpreted.IsCompleted); interpreted.GetAwaiter().GetResult();
            Assert.Equal(automaticCount + 1, vm.GeneratedQueries.Count);
            interpreted = vm.InterpretRequirementCommand.ExecuteAsync(entry); PumpUntil(() => interpreted.IsCompleted);
            Assert.Equal(automaticCount + 1, vm.GeneratedQueries.Count);
            Assert.Equal(4, vm.DomainGroups.GroupDescriptions.Count);
            vm.Domains[0].Max++;
            Assert.Equal("User override", vm.Domains[0].InferenceStatus);
            Assert.Contains("regenerate", vm.ReadinessStatus);
        });
    }

    [Fact]
    public void RequirementsAndDomainsViewsRenderWithBindings()
    {
        Sta(() =>
        {
            var app = new CSharpToUppaal.GUI.App(); app.InitializeComponent();
            var window = new CSharpToUppaal.GUI.MainWindow();
            var vm = (MainViewModel)window.DataContext; PumpUntil(() => !vm.IsBusy);
            var task = new UppaalGeneratorService().GenerateModelFromCodeAsync(vm.SourceCode, "Sample"); PumpUntil(() => task.IsCompleted);
            Apply(vm, task.GetAwaiter().GetResult());
            window.Width = 1400; window.Height = 900;
            var surface = (FrameworkElement)window.Content;
            window.Content = null;
            surface.DataContext = vm;
            surface.Resources.MergedDictionaries.Add(window.Resources);
            surface.Measure(new Size(1400, 900)); surface.Arrange(new Rect(0, 0, 1400, 900)); surface.UpdateLayout();
            var tabs = Descendants<TabControl>(surface).First();
            foreach (var title in new[] { "Requirements", "Model Info", "Functions" })
            {
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header.ToString() == title);
                surface.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1400, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                var folder = System.IO.Path.Combine(AppContext.BaseDirectory, "ui-artifacts"); System.IO.Directory.CreateDirectory(folder);
                using var file = System.IO.File.Create(System.IO.Path.Combine(folder, title.Replace(" ", "-") + ".png"));
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                Assert.Contains(Descendants<DataGrid>(tabs), grid => grid.Items.Count > 0);
                if (title == "Requirements") Assert.Contains(Descendants<TextBox>(tabs), box => box.Text.Contains("deadlock"));
                if (title == "Functions")
                {
                    var grid = Descendants<DataGrid>(tabs).Single(g => ReferenceEquals(g.ItemsSource, vm.ModelFunctionSelections));
                    var included = vm.ModelFunctionSelections.ToArray();
                    var entryPoints = included.Select(f => f.IsSelected).ToArray();
                    Assert.NotEmpty(included);
                    foreach (var function in included)
                    {
                        grid.SelectedItem = function;
                        grid.CurrentCell = new DataGridCellInfo(function, grid.Columns[0]);
                        surface.UpdateLayout();
                        Assert.Same(function, vm.SelectedModelFunction);
                        Assert.False(grid.BeginEdit());
                        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(function);
                        var indicator = Descendants<CheckBox>(row).First();
                        Assert.False(indicator.IsHitTestVisible);
                        Assert.False(indicator.Focusable);
                        Assert.Equal(included, vm.ModelFunctionSelections.ToArray());
                        Assert.Equal(entryPoints, included.Select(f => f.IsSelected).ToArray());
                    }
                }
            }
            window.Close();
        });
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
