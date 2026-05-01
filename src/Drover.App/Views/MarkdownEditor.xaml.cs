using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using CommunityToolkit.Mvvm.Input;
using Drover.App.Services;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.Extensions.DependencyInjection;

namespace Drover.App.Views;

/// <summary>
/// AvalonEdit-based markdown editor exposing TwoWay-bindable Text and a
/// selectable color theme. Rebuilds the XSHD per theme so syntax colors
/// stay coherent with the chrome (background, line numbers, selection).
/// </summary>
public partial class MarkdownEditor : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarkdownEditor),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

    public static readonly DependencyProperty ThemeNameProperty = DependencyProperty.Register(
        nameof(ThemeName), typeof(string), typeof(MarkdownEditor),
        new FrameworkPropertyMetadata(DefaultThemeName, OnThemeChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string ThemeName
    {
        get => (string)GetValue(ThemeNameProperty);
        set => SetValue(ThemeNameProperty, value);
    }

    private const string DefaultThemeName = MarkdownThemes.DefaultName;
    private bool _suppressEcho;
    private SettingsStore? _settings;

    public static IReadOnlyList<string> AvailableThemes => MarkdownThemes.All.Keys.ToList();

    public MarkdownEditor()
    {
        InitializeComponent();
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.TextChanged += (_, _) =>
        {
            if (_suppressEcho) return;
            _suppressEcho = true;
            try { SetCurrentValue(TextProperty, Editor.Text); }
            finally { _suppressEcho = false; }
        };
        // Catch Ctrl+T at the UserControl level via AddHandler(handledEventsToo=true),
        // so we receive the tunneling PreviewKeyDown even if AvalonEdit's TextArea or any
        // intermediate element marks the event handled. Earlier attempts via instance
        // event subscriptions (Editor.PreviewKeyDown, Editor.TextArea.PreviewKeyDown) and
        // InputBindings both failed to fire — this is the most reliable mechanism WPF offers.
        AddHandler(PreviewKeyDownEvent,
            new KeyEventHandler(OnTunnelKeyDown),
            handledEventsToo: true);

        // Auto-focus the editor whenever it becomes visible, so users can press Ctrl+T
        // immediately after toggling into edit mode without having to click first.
        // Focus() before the layout pass is a no-op, so we marshal it via Dispatcher.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Editor.TextArea.Focus();
                System.Windows.Input.Keyboard.Focus(Editor.TextArea);
            }), System.Windows.Threading.DispatcherPriority.Input);
        };

        BuildThemeMenu();
        ApplyTheme(ThemeName);

        Loaded += OnLoaded;
    }

    // Matches the first `[ ]` / `[x]` / `[X]` anywhere on the line — works for
    // standard list items (`- [ ] foo`), headings (`### [ ] foo`), bare
    // checkboxes, and indented variants. Group 1 is the inner char.
    private static readonly Regex TaskCheckboxRx = new(
        @"\[( |x|X)\]",
        RegexOptions.Compiled);

    private void OnTunnelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.T) return;
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) == 0) return;
        if ((mods & ModifierKeys.Alt) != 0) return;
        if (TryToggleCheckboxOnCurrentLine())
            e.Handled = true;
    }

    private bool TryToggleCheckboxOnCurrentLine()
    {
        var doc = Editor.Document;
        if (doc is null) return false;
        var line = doc.GetLineByOffset(Editor.CaretOffset);
        var lineText = doc.GetText(line.Offset, line.Length);
        var m = TaskCheckboxRx.Match(lineText);
        if (!m.Success) return false;
        var inner = m.Groups[1];
        var replacement = inner.Value == " " ? "x" : " ";
        doc.Replace(line.Offset + inner.Index, 1, replacement);
        return true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings is not null) return;

        // Resolve SettingsStore opportunistically. In hosts without DI (designer,
        // unit tests), the editor still works — it just won't persist.
        var sp = (Application.Current as App)?.Services;
        _settings = sp?.GetService<SettingsStore>();
        if (_settings is null) return;

        var stored = _settings.Current.MarkdownTheme;
        if (!string.IsNullOrWhiteSpace(stored) && MarkdownThemes.All.ContainsKey(stored) && stored != ThemeName)
            SetCurrentValue(ThemeNameProperty, stored);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var me = (MarkdownEditor)d;
        if (me._suppressEcho) return;
        me._suppressEcho = true;
        try { me.Editor.Text = e.NewValue as string ?? string.Empty; }
        finally { me._suppressEcho = false; }
    }

    private static void OnThemeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var me = (MarkdownEditor)d;
        var name = e.NewValue as string ?? DefaultThemeName;
        me.ApplyTheme(name);
        if (me._settings is not null && me._settings.Current.MarkdownTheme != name)
            me._settings.Update(me._settings.Current with { MarkdownTheme = name });
    }

    private void BuildThemeMenu()
    {
        if (ThemeMenu is null) return;
        ThemeMenu.Items.Clear();
        foreach (var name in MarkdownThemes.All.Keys)
        {
            var captured = name;
            var mi = new MenuItem { Header = captured, IsCheckable = true };
            mi.Click += (_, _) => ThemeName = captured;
            ThemeMenu.Items.Add(mi);
        }
        ThemeMenu.SubmenuOpened += (_, _) =>
        {
            foreach (MenuItem child in ThemeMenu.Items)
                child.IsChecked = (child.Header as string) == ThemeName;
        };
    }

    private void ApplyTheme(string name)
    {
        var t = MarkdownThemes.Get(name);
        Editor.Background = Brush(t.Background);
        Editor.Foreground = Brush(t.Foreground);
        Editor.LineNumbersForeground = Brush(t.LineNumbers);
        Editor.TextArea.SelectionBrush = Brush(t.Selection);
        Editor.TextArea.SelectionForeground = Brush(t.Foreground);
        Editor.TextArea.SelectionBorder = null;

        try
        {
            using var sr = new StringReader(BuildXshd(t));
            using var xr = XmlReader.Create(sr);
            Editor.SyntaxHighlighting = HighlightingLoader.Load(xr, HighlightingManager.Instance);
        }
        catch
        {
            Editor.SyntaxHighlighting = null;
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex)!;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static string BuildXshd(MarkdownTheme t)
    {
        // Order matters: spans (code blocks) before per-line rules; longer markers
        // before shorter (bold-italic before bold before italic). Anything not
        // matched by a rule falls through to the editor Foreground.
        return $@"<?xml version=""1.0""?>
<SyntaxDefinition name=""Markdown"" extensions="".md""
                  xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
  <Color name=""H1""         foreground=""{t.H1}"" fontWeight=""bold""/>
  <Color name=""H2""         foreground=""{t.H2}"" fontWeight=""bold""/>
  <Color name=""H3""         foreground=""{t.H3}"" fontWeight=""bold""/>
  <Color name=""H456""       foreground=""{t.H456}"" fontWeight=""bold""/>
  <Color name=""HeadingText"" foreground=""#FFFFFF"" fontWeight=""bold""/>
  <Color name=""Bold""       foreground=""{t.Bold}"" fontWeight=""bold""/>
  <Color name=""Italic""     foreground=""{t.Italic}"" fontStyle=""italic""/>
  <Color name=""BoldItalic"" foreground=""{t.BoldItalic}"" fontWeight=""bold"" fontStyle=""italic""/>
  <Color name=""Strike""     foreground=""{t.Strike}""/>
  <Color name=""InlineCode"" foreground=""{t.InlineCodeFg}"" background=""{t.InlineCodeBg}""/>
  <Color name=""CodeBlock""  foreground=""{t.CodeBlockFg}"" background=""{t.CodeBlockBg}""/>
  <Color name=""Link""       foreground=""{t.Link}""/>
  <Color name=""Url""        foreground=""{t.Url}""/>
  <Color name=""Image""      foreground=""{t.Image}""/>
  <Color name=""ListMark""   foreground=""{t.ListMark}"" fontWeight=""bold""/>
  <Color name=""OrderedListMark"" foreground=""{t.OrderedListMark ?? t.ListMark}"" fontWeight=""bold""/>
  <Color name=""Quote""      foreground=""{t.Quote}"" fontStyle=""italic""/>
  <Color name=""HR""         foreground=""{t.HR}""/>
  <Color name=""Table""      foreground=""{t.Table}""/>
  <Color name=""TaskOpen""   foreground=""{t.TaskOpen}"" fontWeight=""bold""/>
  <Color name=""TaskDone""   foreground=""{t.TaskDone}"" fontWeight=""bold""/>
  <Color name=""Html""       foreground=""{t.Html}""/>
  <Color name=""Escape""     foreground=""{t.Escape}""/>
  <RuleSet ignoreCase=""false"">
    <Span color=""CodeBlock"" multiline=""true"">
      <Begin>^\s*```[^\n]*$</Begin>
      <End>^\s*```\s*$</End>
    </Span>
    <Span color=""CodeBlock"" multiline=""true"">
      <Begin>^\s*~~~[^\n]*$</Begin>
      <End>^\s*~~~\s*$</End>
    </Span>
    <Span color=""InlineCode"">
      <Begin>``</Begin>
      <End>``</End>
    </Span>
    <Span color=""InlineCode"">
      <Begin>`</Begin>
      <End>`</End>
    </Span>

    <Rule color=""Escape"">\\[\\`*_{{}}\[\]()#+\-.!~&gt;|]</Rule>

    <Rule color=""H1"">^\#{{1}}\s.*$</Rule>
    {(t.HeadingMarkerOnly
        ? @"<Rule color=""H2"">^\#{2}\s</Rule>
    <Rule color=""H3"">^\#{3}\s</Rule>
    <Rule color=""HeadingText"">(?&lt;=^\#{2,3}\s).*$</Rule>"
        : @"<Rule color=""H2"">^\#{2}\s.*$</Rule>
    <Rule color=""H3"">^\#{3}\s.*$</Rule>")}
    <Rule color=""H456"">^\#{{4,6}}\s.*$</Rule>

    <Rule color=""HR"">^\s*-{{3,}}\s*$</Rule>
    <Rule color=""HR"">^\s*\*{{3,}}\s*$</Rule>
    <Rule color=""HR"">^\s*_{{3,}}\s*$</Rule>
    <Rule color=""Quote"">^\s*&gt;.*$</Rule>
    <Rule color=""Table"">^\s*\|.*\|\s*$</Rule>
    <Rule color=""Table"">^\s*\|?\s*:?-{{2,}}:?(\s*\|\s*:?-{{2,}}:?)+\s*\|?\s*$</Rule>

    <Rule color=""TaskDone"">(?&lt;=^\s*([-*+]|\d+\.)\s)\[[xX]\]</Rule>
    <Rule color=""TaskOpen"">(?&lt;=^\s*([-*+]|\d+\.)\s)\[\s\]</Rule>
    <Rule color=""OrderedListMark"">^\s*\d+\.\s</Rule>
    <Rule color=""ListMark"">^\s*[-*+]\s</Rule>

    <Rule color=""Image"">!\[[^\]\n]*\]\([^\)\n]+\)</Rule>
    <Rule color=""Link"">\[[^\]\n]+\]\([^\)\n]+\)</Rule>
    <Rule color=""Link"">\[[^\]\n]+\]\[[^\]\n]*\]</Rule>
    <Rule color=""Url"">&lt;https?://[^&gt;\s]+&gt;</Rule>
    <Rule color=""Url"">https?://[^\s)]+(?=[\s)]|$)</Rule>

    <Rule color=""Strike"">~~[^~\n]+~~</Rule>
    <Rule color=""BoldItalic"">\*\*\*[^*\n]+\*\*\*</Rule>
    <Rule color=""BoldItalic"">___[^_\n]+___</Rule>
    <Rule color=""Bold"">\*\*[^*\n]+\*\*</Rule>
    <Rule color=""Bold"">__[^_\n]+__</Rule>
    <Rule color=""Italic"">(?&lt;![\*\w])\*[^\*\n]+\*(?!\*)</Rule>
    <Rule color=""Italic"">(?&lt;![_\w])_[^_\n]+_(?![_\w])</Rule>

    <Rule color=""Html"">&lt;/?[a-zA-Z][a-zA-Z0-9]*(\s[^&gt;\n]*)?/?&gt;</Rule>
  </RuleSet>
</SyntaxDefinition>";
    }
}
