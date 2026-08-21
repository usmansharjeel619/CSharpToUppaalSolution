using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CSharpToUppaal.Backend.Services;

namespace CSharpToUppaal.GUI
{
    public partial class ProjectSelectionWindow : Window
    {
        public WorkspaceProjectDescriptor? SelectedProject { get; private set; }

        public ProjectSelectionWindow(IEnumerable<WorkspaceProjectDescriptor> projects)
        {
            InitializeComponent();
            var items = projects.ToList();
            ProjectsList.ItemsSource = items;
            ProjectsList.SelectedItem = items.FirstOrDefault(project => project.IsExecutable && !project.IsTestProject)
                                        ?? items.FirstOrDefault(project => !project.IsTestProject)
                                        ?? items.FirstOrDefault();
        }

        private void UseSelectedProject_Click(object sender, RoutedEventArgs e)
        {
            SelectedProject = ProjectsList.SelectedItem as WorkspaceProjectDescriptor;
            if (SelectedProject == null)
            {
                MessageBox.Show("Choose a C# project first.", "No Project Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
        }
    }
}
