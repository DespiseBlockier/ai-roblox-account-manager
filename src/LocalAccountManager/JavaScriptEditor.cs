using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace LocalAccountManager;

public sealed class JavaScriptEditor : TextEditor
{
    private const int InputLimit = 128000;
    public JavaScriptEditor()
    {
        FontFamily = new FontFamily("Consolas"); FontSize = 12; Padding = new Thickness(12);
        Background = Brush("#10151D"); Foreground = Brush("#E6EDF3");
        BorderBrush = Brush("#394452"); BorderThickness = new Thickness(1);
        LineNumbersForeground = Brush("#8E9BB0"); ShowLineNumbers = true;
        HorizontalScrollBarVisibility = VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        TextArea.Caret.CaretBrush = Foreground; TextArea.SelectionBrush = Brush("#355172");
        Options.EnableHyperlinks = Options.EnableEmailHyperlinks = false;
        Options.ConvertTabsToSpaces = true; Options.IndentationSize = 2;
        Document.UndoStack.SizeLimit = 0;
        using var reader = XmlReader.Create(new StringReader(Definition), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        PreviewTextInput += (_, e) => { if (Document.TextLength - SelectionLength + e.Text.Length > InputLimit) e.Handled = true; };
        DataObject.AddPastingHandler(this, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is string pasted && Document.TextLength - SelectionLength + pasted.Length > InputLimit) e.CancelCommand();
        });
        var menu = new ContextMenu();
        foreach (var command in new[] { ApplicationCommands.Cut, ApplicationCommands.Copy, ApplicationCommands.Paste, ApplicationCommands.SelectAll })
            menu.Items.Add(new MenuItem { Header = command.Text, Command = command, CommandTarget = this });
        ContextMenu = menu;
    }
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    private const string Definition = """
        <SyntaxDefinition name="JavaScript" extensions=".js" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="#8EAB82"/>
          <Color name="String" foreground="#E6B08A"/>
          <Color name="Escape" foreground="#E5D08E"/>
          <Color name="Keyword" foreground="#C9A1F0"/>
          <Color name="Literal" foreground="#8FC9F4"/>
          <Color name="Number" foreground="#A7D8B5"/>
          <Color name="Helper" foreground="#65D4C4"/>
          <Color name="Function" foreground="#E5D08E"/>
          <RuleSet>
            <Span color="Comment" begin="//" end="$"/>
            <Span color="Comment" multiline="true" begin="/\*" end="\*/"/>
            <Span color="String" begin="&quot;" end="&quot;"><RuleSet><Rule color="Escape">\\.</Rule></RuleSet></Span>
            <Span color="String" begin="'" end="'"><RuleSet><Rule color="Escape">\\.</Rule></RuleSet></Span>
            <Span color="String" multiline="true" begin="`" end="`"><RuleSet><Rule color="Escape">\\.</Rule></RuleSet></Span>
            <Keywords color="Keyword">
              <Word>async</Word><Word>await</Word><Word>const</Word><Word>let</Word><Word>var</Word><Word>function</Word><Word>return</Word>
              <Word>if</Word><Word>else</Word><Word>for</Word><Word>in</Word><Word>of</Word><Word>while</Word><Word>do</Word>
              <Word>switch</Word><Word>case</Word><Word>break</Word><Word>continue</Word><Word>try</Word><Word>catch</Word><Word>finally</Word>
              <Word>throw</Word><Word>new</Word><Word>class</Word><Word>extends</Word><Word>static</Word><Word>this</Word><Word>super</Word>
              <Word>import</Word><Word>export</Word><Word>default</Word><Word>typeof</Word><Word>instanceof</Word><Word>void</Word><Word>delete</Word><Word>yield</Word>
            </Keywords>
            <Keywords color="Literal"><Word>true</Word><Word>false</Word><Word>null</Word><Word>undefined</Word><Word>NaN</Word><Word>Infinity</Word></Keywords>
            <Rule color="Number">\b(?:0[xX][\da-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?n?)\b</Rule>
            <Rule color="Helper">\bram\b</Rule>
            <Rule color="Function">\b[A-Za-z_$][\w$]*(?=\s*\()</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;
}
