using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PdfTranslator;

/// <summary>Starts the commands and links the app tells people about, so nobody has to retype them.</summary>
public static class Launch
{
    /// <summary>Opens a web page (or an ms-settings: page) in the default handler.</summary>
    public static void Url(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Could not open {url}:\n{ex.Message}", "PDF Translator"); }
    }

    /// <summary>
    /// Runs a command in a new console window that stays open, so its output (a token, a confirmation) can be read
    /// and copied. Claude session variables are removed so a CLI started from here acts as a normal terminal would.
    /// </summary>
    public static void Terminal(string exe, string arguments, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        // cmd /k "<"exe" args>" keeps the window open after the command finishes.
        psi.Arguments = $"/k \"\"{exe}\" {arguments}\"";
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("CLAUDE_CODE_") || k == "CLAUDECODE" || k.StartsWith("CLAUDE_AGENT_SDK")).ToList())
            psi.Environment.Remove(key);
        try { Process.Start(psi); }
        catch (Exception ex) { MessageBox.Show($"Could not start the command:\n{ex.Message}", "PDF Translator"); }
    }

    public static void Copy(string text)
    {
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }
}

/// <summary>A message with buttons that act on it (run a command, open a page, copy, open Settings).</summary>
public sealed class ActionDialog : Window
{
    public sealed record Action(string Label, System.Action Run, bool CloseAfter = false, string? ToolTip = null);

    readonly TextBlock _status;

    ActionDialog(string title, string message, IEnumerable<Action> actions)
    {
        Title = title;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/PdfTranslator.ico")); } catch { }

        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        root.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(LinkedText(message));

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        bool first = true;
        foreach (var a in actions)
        {
            var b = new Button { Content = a.Label, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6, 4, 0, 0), ToolTip = a.ToolTip };
            if (first) { b.FontWeight = FontWeights.SemiBold; first = false; }
            b.Click += (_, _) =>
            {
                a.Run();
                if (a.CloseAfter) Close();
                else _status!.Text = "Done: " + a.Label;
            };
            buttons.Children.Add(b);
        }
        var close = new Button { Content = "Close", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6, 4, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        _status = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0), FontSize = 11 };
        root.Children.Add(_status);
        Content = root;
    }

    static readonly System.Text.RegularExpressions.Regex LinkPattern =
        new(@"(https?://|ms-settings:)[^\s""“”<>]+");

    /// <summary>The message with every web address (and ms-settings: page) as a clickable link.</summary>
    static TextBlock LinkedText(string message)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20, Margin = new Thickness(0, 0, 0, 14) };
        int pos = 0;
        foreach (System.Text.RegularExpressions.Match m in LinkPattern.Matches(message))
        {
            string url = m.Value.TrimEnd('.', ',', ';', ':', ')', '’', '”');
            if (m.Index > pos) block.Inlines.Add(new System.Windows.Documents.Run(message[pos..m.Index]));
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(url)) { ToolTip = "Open " + url };
            link.Click += (_, _) => Launch.Url(url);
            block.Inlines.Add(link);
            pos = m.Index + url.Length;
        }
        if (pos < message.Length) block.Inlines.Add(new System.Windows.Documents.Run(message[pos..]));
        return block;
    }

    /// <summary>Set by the --snapshot-dialog command: dialogs are rendered to this PNG instead of shown.</summary>
    internal static string? SnapshotTarget;

    public static void Show(Window? owner, string title, string message, params Action[] actions)
    {
        var dlg = new ActionDialog(title, message, actions);
        if (SnapshotTarget != null)
        {
            Cli.SaveWindowImage(dlg, SnapshotTarget);
            return;
        }
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        dlg.ShowDialog();
    }
}
