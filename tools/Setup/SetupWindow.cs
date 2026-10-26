using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PdfTranslator.Setup
{
    /// <summary>
    /// The installer window: who it is for, where it goes, and getting on with it.
    /// </summary>
    /// <remarks>
    /// One page, not a wizard. There are two decisions - who for, and where - and
    /// putting them behind Next buttons would only make them harder to see.
    ///
    /// Built in code rather than XAML for the same reason as the application: this
    /// machine has no .NET SDK, so a markup compile would be another toolchain
    /// dependency for no gain.
    /// </remarks>
    internal sealed class SetupWindow : Window
    {
        private readonly Installer _installer = new Installer();
        private readonly bool _uninstalling;

        private RadioButton _currentUser, _allUsers;
        private TextBox _directory;
        private CheckBox _desktop, _removeData, _launch, _accept;
        private ProgressBar _progress;
        private TextBlock _state, _headline, _detail;
        private Button _action, _cancel;
        private StackPanel _choices;

        /// <summary>
        /// What the checkbox said when Uninstall was pressed. Captured on the UI
        /// thread: the work runs on another one, and cannot ask a control anything.
        /// </summary>
        private bool _removingData;

        public SetupWindow(bool uninstalling, Scope scope, string directory)
        {
            _uninstalling = uninstalling;

            Title = (uninstalling ? "Uninstall " : "Install ") + Installer.ProductName;
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            _installer.Scope = scope;
            _installer.Directory = directory ?? Installer.DefaultDirectory(scope);

            LoadIcon();
            Content = Build();
            Describe();
        }

        private void LoadIcon()
        {
            try
            {
                using (Stream stream = Payload.Open("PdfTranslator.ico"))
                {
                    if (stream == null) return;

                    System.Windows.Media.Imaging.BitmapDecoder decoder =
                        new System.Windows.Media.Imaging.IconBitmapDecoder(
                            stream,
                            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                    if (decoder.Frames.Count > 0) Icon = decoder.Frames[0];
                }
            }
            catch (Exception)
            {
                // An icon is cosmetic; never let it stop the installer opening.
            }
        }

        // --- layout ------------------------------------------------------------

        private UIElement Build()
        {
            StackPanel panel = new StackPanel();
            panel.Margin = new Thickness(22, 18, 22, 18);

            _headline = Label(Installer.ProductName, 19, Brushes.Black);
            _headline.Margin = new Thickness(0, 0, 0, 2);
            panel.Children.Add(_headline);

            _detail = Label("", 12, Grey);
            _detail.TextWrapping = TextWrapping.Wrap;
            _detail.Margin = new Thickness(0, 0, 0, 16);
            panel.Children.Add(_detail);

            _choices = new StackPanel();
            panel.Children.Add(_choices);

            if (_uninstalling) BuildUninstallChoices();
            else BuildInstallChoices();

            _progress = new ProgressBar();
            _progress.Height = 6;
            _progress.Maximum = 1;
            _progress.Visibility = Visibility.Collapsed;
            _progress.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(_progress);

            _state = Label("", 11.5, Grey);
            _state.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(_state);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.Margin = new Thickness(0, 18, 0, 0);

            _action = Button(_uninstalling ? "Uninstall" : "Install");
            _action.IsDefault = true;
            // Without an agreement to accept there is nothing to wait for.
            _action.IsEnabled = _uninstalling || !Terms.Present;
            _action.Click += delegate { Go(); };
            buttons.Children.Add(_action);

            _cancel = Button("Cancel");
            _cancel.IsCancel = true;
            _cancel.Click += delegate { Close(); };
            buttons.Children.Add(_cancel);

            panel.Children.Add(buttons);
            SyncAction();
            return panel;
        }

        private void BuildInstallChoices()
        {
            _currentUser = new RadioButton();
            _currentUser.Content = Rich("Just me", "Installs under this account. No " +
                                        "administrator password needed.");
            _currentUser.IsChecked = _installer.Scope == Scope.CurrentUser;
            _currentUser.Checked += delegate { ScopeChanged(Scope.CurrentUser); };
            _choices.Children.Add(_currentUser);

            _allUsers = new RadioButton();
            _allUsers.Content = Rich("Anyone who uses this PC",
                                     "One copy for every account. Asks for an " +
                                     "administrator password.");
            _allUsers.Margin = new Thickness(0, 10, 0, 0);
            _allUsers.IsChecked = _installer.Scope == Scope.AllUsers;
            _allUsers.Checked += delegate { ScopeChanged(Scope.AllUsers); };
            _choices.Children.Add(_allUsers);

            _choices.Children.Add(SectionLabel("Install to"));

            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _directory = new TextBox();
            _directory.Text = _installer.Directory;
            _directory.Padding = new Thickness(6, 5, 6, 5);
            _directory.FontSize = 12;
            Grid.SetColumn(_directory, 0);
            row.Children.Add(_directory);

            Button browse = Button("Browse...");
            browse.Margin = new Thickness(8, 0, 0, 0);
            browse.MinWidth = 90;
            browse.Click += delegate { Browse(); };
            Grid.SetColumn(browse, 1);
            row.Children.Add(browse);

            _choices.Children.Add(row);

            _desktop = new CheckBox();
            _desktop.Content = Label("Put a shortcut on the desktop", 12, Brushes.Black);
            _desktop.IsChecked = true;
            _desktop.Margin = new Thickness(0, 14, 0, 0);
            _choices.Children.Add(_desktop);

            if (Terms.Present) BuildTerms();
        }

        /// <summary>
        /// The agreement, and the box that has to be ticked before Install does
        /// anything.
        /// </summary>
        /// <remarks>
        /// Shown in full rather than linked to. An agreement that has to be found
        /// before it can be read is one nobody has read, and "by installing you
        /// agree" on its own is a weaker thing to rely on than a box somebody ticked
        /// with the text in front of them. The Install button is disabled until it is
        /// ticked, so it cannot be missed by pressing Enter.
        /// </remarks>
        private void BuildTerms()
        {
            _choices.Children.Add(SectionLabel("Licence agreement"));

            TextBox terms = new TextBox();
            terms.Text = Terms.Text();
            terms.IsReadOnly = true;
            terms.FontFamily = new FontFamily("Consolas");
            terms.FontSize = 11.5;
            terms.Height = 150;
            terms.Padding = new Thickness(8, 6, 8, 6);
            terms.TextWrapping = TextWrapping.NoWrap;
            terms.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            terms.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _choices.Children.Add(terms);

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Margin = new Thickness(0, 10, 0, 0);

            _accept = new CheckBox();
            _accept.Content = Label("I accept the terms of the licence agreement", 12,
                                    Brushes.Black);
            _accept.Checked += delegate { SyncAction(); };
            _accept.Unchecked += delegate { SyncAction(); };
            row.Children.Add(_accept);

            Button print = Button("Save a copy...");
            print.Margin = new Thickness(16, 0, 0, 0);
            print.MinWidth = 110;
            print.Click += delegate { SaveTerms(); };
            row.Children.Add(print);

            _choices.Children.Add(row);

        }

        private void SyncAction()
        {
            if (_action != null && _accept != null)
                _action.IsEnabled = _accept.IsChecked == true;
        }

        private void SaveTerms()
        {
            Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog();
            dialog.Title = "Save the licence agreement";
            dialog.Filter = "Text (*.txt)|*.txt|All files (*.*)|*.*";
            dialog.FileName = "PdfTranslator-EULA.txt";

            if (dialog.ShowDialog() != true) return;

            try { File.WriteAllText(dialog.FileName, Terms.Text()); }
            catch (Exception ex) { Complain(ex.Message); }
        }

        private void BuildUninstallChoices()
        {
            _removeData = new CheckBox();
            _removeData.Content = Rich("Also remove settings and OCR models",
                                       "Service keys and choices, and the downloaded OCR language data. " +
                                       "Your PDFs are never removed.");
            _removeData.IsChecked = false;
            _choices.Children.Add(_removeData);
        }

        private void Describe()
        {
            if (_uninstalling)
            {
                _detail.Text = "This will remove " + Installer.ProductName + " from " +
                               _installer.Directory + ".";
                return;
            }

            string installed = Installer.InstalledVersion(_installer.Scope);

            _detail.Text = installed == null
                ? "Translates PDFs keeping their layout. Version " +
                  Installer.ProductVersion + ", " + Megabytes() + " MB."
                : "Version " + installed + " is installed here already, and will be " +
                  "replaced with " + Installer.ProductVersion + ".";
        }

        private string Megabytes()
        {
            return (Payload.TotalBytes() / 1024.0 / 1024.0).ToString("0.0");
        }

        // --- choices -----------------------------------------------------------

        private void ScopeChanged(Scope scope)
        {
            // Only move the folder when it is still the default for the other scope -
            // a path the user typed is theirs, not ours to overwrite.
            bool wasDefault = string.Equals(
                _directory.Text.Trim(),
                Installer.DefaultDirectory(_installer.Scope),
                StringComparison.OrdinalIgnoreCase);

            _installer.Scope = scope;
            if (wasDefault) _directory.Text = Installer.DefaultDirectory(scope);

            Describe();
        }

        private void Browse()
        {
            using (System.Windows.Forms.FolderBrowserDialog dialog =
                   new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Where should " + Installer.ProductName + " go?";
                dialog.SelectedPath = _directory.Text;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    _directory.Text = Path.Combine(dialog.SelectedPath, Installer.ProductName);
            }
        }

        // --- doing it ----------------------------------------------------------

        private void Go()
        {
            if (!_uninstalling)
            {
                _installer.Directory = _directory.Text.Trim();
                _installer.DesktopShortcut = _desktop.IsChecked == true;

                if (string.IsNullOrEmpty(_installer.Directory))
                {
                    Complain("Choose a folder to install into.");
                    return;
                }

                // Elevation is asked for at the last possible moment - after the
                // choices are made, and only for the scope that needs it.
                if (_installer.Scope == Scope.AllUsers && !Elevation.IsElevated)
                {
                    if (Elevation.Relaunch(false, _installer.Directory,
                                           _installer.DesktopShortcut))
                    {
                        Close();
                        return;
                    }

                    Complain("Installing for everyone needs an administrator password. " +
                             "Choose \"Just me\" to install without one.");
                    return;
                }
            }

            if (Installer.IsRunning(_installer.Directory))
            {
                Complain(Installer.ProductName + " is running from that folder. Close it " +
                         "and try again.");
                return;
            }

            // Read on this thread, before the work starts. A WPF control belongs to
            // the thread that created it, and reading a checkbox from the background
            // task threw "the calling thread cannot access this object" - which
            // surfaced as a failure to uninstall, because the read was the first
            // thing the task did.
            bool removeData = _removeData != null && _removeData.IsChecked == true;
            _removingData = removeData;

            Working(true);

            _installer.Progress = delegate (double fraction, string what)
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    _progress.Value = fraction;
                    _state.Text = what;
                });
            };

            System.Threading.Tasks.Task.Factory.StartNew(delegate
            {
                try
                {
                    if (_uninstalling) _installer.Uninstall(removeData);
                    else _installer.Install();

                    Dispatcher.BeginInvoke((Action)Finished);
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke((Action)delegate { Failed(ex); });
                }
            });
        }

        private void Working(bool busy)
        {
            _choices.IsEnabled = !busy;
            _action.IsEnabled = !busy && (_uninstalling || _accept == null ||
                                          _accept.IsChecked == true);
            _cancel.IsEnabled = !busy;
            _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Finished()
        {
            Working(false);
            _choices.Children.Clear();
            _progress.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;

            if (_uninstalling)
            {
                _headline.Text = Installer.ProductName + " has been removed";
                _detail.Text = _removingData
                    ? "Its settings have been removed as well."
                    : "Its settings have been kept, in case it is installed again.";

                _state.Text = "";
                _action.Content = Caption("Close");
                _action.Click += delegate { Close(); };
                _action.IsEnabled = true;
                return;
            }

            _headline.Text = Installer.ProductName + " is installed";
            _detail.Text = "It is in " + _installer.Directory +
                           ", and in the Start menu. Sample projects are in the samples folder.";

            _launch = new CheckBox();
            _launch.Content = Label("Start " + Installer.ProductName + " now", 12, Brushes.Black);
            _launch.IsChecked = true;
            _choices.Children.Add(_launch);

            _state.Text = "";
            _action.Content = Caption("Finish");
            _action.IsEnabled = true;
            _accept = null;
            _action.Click += delegate { LaunchAndClose(); };
        }

        private void LaunchAndClose()
        {
            if (_launch != null && _launch.IsChecked == true)
            {
                try
                {
                    Process.Start(Path.Combine(_installer.Directory, Installer.ExecutableName));
                }
                catch (Exception ex)
                {
                    Complain("Installed, but it would not start - " + ex.Message);
                }
            }

            Close();
        }

        private void Failed(Exception ex)
        {
            Working(false);
            _progress.Visibility = Visibility.Collapsed;
            _state.Text = "";

            Complain((_uninstalling ? "Could not finish removing it - " :
                                      "Could not finish installing - ") + ex.Message);
        }

        private void Complain(string message)
        {
            MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // --- small pieces -------------------------------------------------------

        private static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61));

        private static TextBlock Label(string text, double size, Brush colour)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = size;
            t.Foreground = colour;
            return t;
        }

        private static UIElement Rich(string heading, string note)
        {
            StackPanel stack = new StackPanel();
            stack.Margin = new Thickness(4, 0, 0, 0);
            stack.Children.Add(Label(heading, 13, Brushes.Black));

            TextBlock detail = Label(note, 11.5, Grey);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.MaxWidth = 440;
            stack.Children.Add(detail);
            return stack;
        }

        private static TextBlock SectionLabel(string text)
        {
            TextBlock t = Label(text, 12, Grey);
            t.Margin = new Thickness(0, 18, 0, 5);
            return t;
        }

        private static object Caption(string caption)
        {
            return Label(caption, 12.5, Brushes.Black);
        }

        private static Button Button(string caption)
        {
            Button b = new Button();
            b.Content = Caption(caption);
            b.Padding = new Thickness(16, 6, 16, 6);
            b.Margin = new Thickness(8, 0, 0, 0);
            b.MinWidth = 96;
            return b;
        }
    }
}
