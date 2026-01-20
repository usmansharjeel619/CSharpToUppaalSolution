// Controls/CodeEditor.xaml.cs
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.CodeAnalysis.Differencing;
using System;
using System.Windows;
using System.Windows.Controls;

namespace CSharpToUppaal.GUI.Controls
{
    public partial class CodeEditor : UserControl
    {
        private FoldingManager _foldingManager;
        private BraceFoldingStrategy _foldingStrategy;

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register("Text", typeof(string), typeof(CodeEditor),
                new FrameworkPropertyMetadata(string.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnTextChanged));

        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register("IsReadOnly", typeof(bool), typeof(CodeEditor),
                new PropertyMetadata(false));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public bool IsReadOnly
        {
            get => (bool)GetValue(IsReadOnlyProperty);
            set => SetValue(IsReadOnlyProperty, value);
        }

        public CodeEditor()
        {
            InitializeComponent();
            InitializeEditor();
        }

        private void InitializeEditor()
        {
            // Set C# syntax highlighting
            editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
            editor.ShowLineNumbers = true;
            editor.WordWrap = false;
            editor.FontFamily = new System.Windows.Media.FontFamily("Consolas");
            editor.FontSize = 12;

            // Setup search panel
            SearchPanel.Install(editor);

            // Setup folding
            _foldingManager = FoldingManager.Install(editor.TextArea);
            _foldingStrategy = new BraceFoldingStrategy();

            // Update folding when text changes
            editor.TextChanged += (s, e) => UpdateFoldings();
        }

        private void UpdateFoldings()
        {
            if (_foldingManager != null && _foldingStrategy != null)
            {
                _foldingStrategy.UpdateFoldings(_foldingManager, editor.Document);
            }
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (CodeEditor)d;
            if (control.editor.Text != (string)e.NewValue)
            {
                control.editor.Text = (string)e.NewValue;
                control.UpdateFoldings();
            }
        }

        private void Editor_TextChanged(object sender, EventArgs e)
        {
            if (Text != editor.Text)
            {
                Text = editor.Text;
            }
        }
    }

    public class BraceFoldingStrategy
    {
        public void UpdateFoldings(FoldingManager manager, ICSharpCode.AvalonEdit.Document.TextDocument document)
        {
            // Simplified folding strategy - in production, implement proper brace matching
        }
    }
}