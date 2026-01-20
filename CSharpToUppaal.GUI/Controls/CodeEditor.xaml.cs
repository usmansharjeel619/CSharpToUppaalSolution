// Controls/CodeEditor.xaml.cs
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Search;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CSharpToUppaal.GUI.Controls
{
    public partial class CodeEditor : UserControl
    {
        private FoldingManager _foldingManager;
        private BraceFoldingStrategy _foldingStrategy;

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(CodeEditor),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnTextPropertyChanged,
                    null,
                    false,
                    UpdateSourceTrigger.PropertyChanged));

        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register(
                nameof(IsReadOnly),
                typeof(bool),
                typeof(CodeEditor),
                new PropertyMetadata(false, OnIsReadOnlyChanged));

        private static bool _isUpdating = false;

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
            editor.FontSize = 13;

            // Dark theme colors
            editor.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x26));
            editor.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF1, 0xF1, 0xF1));
            editor.LineNumbersForeground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x85, 0x85, 0x85));

            // Setup search panel
            SearchPanel.Install(editor);

            // Setup folding
            _foldingManager = FoldingManager.Install(editor.TextArea);
            _foldingStrategy = new BraceFoldingStrategy();

            // Handle text changes from the editor
            editor.TextChanged += Editor_TextChanged;

            // Set initial text
            if (!string.IsNullOrEmpty(Text))
            {
                editor.Text = Text;
            }
        }

        private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (_isUpdating) return;

            var control = (CodeEditor)d;
            var newText = e.NewValue as string ?? string.Empty;

            if (control.editor.Text != newText)
            {
                _isUpdating = true;
                control.editor.Text = newText;
                control.UpdateFoldings();
                _isUpdating = false;
            }
        }

        private static void OnIsReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (CodeEditor)d;
            control.editor.IsReadOnly = (bool)e.NewValue;
        }

        private void Editor_TextChanged(object sender, EventArgs e)
        {
            if (_isUpdating) return;

            _isUpdating = true;
            Text = editor.Text;
            UpdateFoldings();
            _isUpdating = false;
        }

        private void UpdateFoldings()
        {
            if (_foldingManager != null && _foldingStrategy != null)
            {
                try
                {
                    _foldingStrategy.UpdateFoldings(_foldingManager, editor.Document);
                }
                catch
                {
                    // Ignore folding errors
                }
            }
        }
    }

    public class BraceFoldingStrategy
    {
        public void UpdateFoldings(FoldingManager manager, ICSharpCode.AvalonEdit.Document.TextDocument document)
        {
            // Simple brace matching for C# code
            int firstErrorOffset;
            var newFoldings = CreateNewFoldings(document, out firstErrorOffset);
            manager.UpdateFoldings(newFoldings, firstErrorOffset);
        }

        private IEnumerable<NewFolding> CreateNewFoldings(ICSharpCode.AvalonEdit.Document.TextDocument document, out int firstErrorOffset)
        {
            firstErrorOffset = -1;
            var newFoldings = new List<NewFolding>();

            try
            {
                var openBraces = new Stack<int>();
                var text = document.Text;

                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == '{')
                    {
                        openBraces.Push(i);
                    }
                    else if (text[i] == '}' && openBraces.Count > 0)
                    {
                        int startOffset = openBraces.Pop();
                        int endOffset = i + 1;

                        // Only create folding if it spans multiple lines
                        var startLine = document.GetLineByOffset(startOffset);
                        var endLine = document.GetLineByOffset(endOffset);

                        if (endLine.LineNumber > startLine.LineNumber)
                        {
                            newFoldings.Add(new NewFolding(startOffset, endOffset));
                        }
                    }
                }
            }
            catch
            {
                // If parsing fails, return empty foldings
            }

            newFoldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
            return newFoldings;
        }
    }
}