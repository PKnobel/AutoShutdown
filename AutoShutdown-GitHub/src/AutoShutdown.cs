// Auto-Shutdown: fährt den Computer nach einer gewählten Zeit herunter.
// Nutzt intern "shutdown /s /t <Sekunden>" und "shutdown /a" zum Abbrechen.
// Oberfläche: WPF im Windows-11-Stil (Mica, Hell-/Dunkelmodus und Akzentfarbe vom System), Deutsch und Englisch.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("Auto-Shutdown")]
[assembly: AssemblyDescription("Fährt den Computer nach einer gewählten Zeit herunter")]
[assembly: AssemblyProduct("Auto-Shutdown")]
[assembly: AssemblyCopyright("Copyright © 2026 Pascal Knobel")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
// Native DLLs nur aus System32 laden, nie aus dem Programmordner (Schutz gegen DLL-Hijacking)
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace AutoShutdown
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            new Application().Run(new MainWindow());
        }
    }

    public class MainWindow : Window
    {
        const string Author = "Pascal Knobel";
        const double DesignWidth = 490;   // Breite der Oberfläche bei Mindestgrösse
        const int ERROR_NO_SHUTDOWN_IN_PROGRESS = 1116;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        const int DWMSBT_MAINWINDOW = 2; // Mica

        static readonly string AppVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoShutdown");

        // Merkt sich den geplanten Zeitpunkt, falls das Fenster geschlossen und wieder geöffnet wird.
        static readonly string StateFile = Path.Combine(DataDir, "geplant.txt");
        static readonly string LanguageFile = Path.Combine(DataDir, "sprache.txt");

        // Alle Texte: [0] = Deutsch, [1] = English. In XAML als {DynamicResource S.<Schlüssel>} verwendet.
        static readonly Dictionary<string, string[]> Strings = new Dictionary<string, string[]>
        {
            { "IdleTitle",        new[] { "Kein Herunterfahren geplant", "No shutdown scheduled" } },
            { "IdleText",         new[] { "Wähle unten eine Zeit, um den Timer zu starten.", "Choose a time below to start the timer." } },
            { "CountdownLabel",   new[] { "Herunterfahren in", "Shutting down in" } },
            { "Cancel",           new[] { "Herunterfahren abbrechen", "Cancel shutdown" } },
            { "QuickPick",        new[] { "Schnellwahl", "Quick pick" } },
            { "Minutes",          new[] { "Minuten", "minutes" } },
            { "Hour",             new[] { "Stunde", "hour" } },
            { "Hours",            new[] { "Stunden", "hours" } },
            { "CustomTime",       new[] { "Eigene Zeit", "Custom time" } },
            { "AfterDuration",    new[] { "Nach Dauer", "After a duration" } },
            { "DurationHint",     new[] { "Stunden : Minuten", "Hours : minutes" } },
            { "AtTime",           new[] { "Zur Uhrzeit", "At a set time" } },
            { "AtTimeHint",       new[] { "Heute oder morgen", "Today or tomorrow" } },
            { "Start",            new[] { "Start", "Start" } },
            { "Warning",          new[] { "Offene Programme werden ohne Nachfrage beendet.", "Open apps will be closed without asking." } },
            { "About",            new[] { "Info", "About" } },
            { "AboutDescription", new[] { "Fährt den Computer nach einer gewählten Zeit herunter.", "Shuts down your computer after a chosen time." } },
            { "Version",          new[] { "Version", "Version" } },
            { "CreatedBy",        new[] { "Erstellt von", "Created by" } },
            { "License",          new[] { "Lizenz", "License" } },
            { "Close",            new[] { "Schliessen", "Close" } },
            { "KeepOpen",         new[] { "Offen lassen", "Keep open" } },
            { "Today",            new[] { "Heute um {0} Uhr", "Today at {0}" } },
            { "Tomorrow",         new[] { "Morgen um {0} Uhr", "Tomorrow at {0}" } },
            { "OnDay",            new[] { "{0} um {1} Uhr", "{0} at {1}" } },
            { "DayFormat",        new[] { "dddd, d. MMMM", "dddd, MMMM d" } },
            { "ShuttingDown",     new[] { "Computer wird heruntergefahren …", "Shutting down …" } },
            { "NoTimeTitle",      new[] { "Keine Zeit eingestellt", "No time set" } },
            { "NoTimeText",       new[] { "Bitte gib eine Dauer von mindestens 1 Minute ein.", "Please enter a duration of at least 1 minute." } },
            { "ErrorTitle",       new[] { "Das hat nicht geklappt", "Something went wrong" } },
            { "ScheduleFailed",   new[] { "Das Herunterfahren konnte nicht geplant werden (Fehlercode {0}).", "The shutdown could not be scheduled (error code {0})." } },
            { "CancelFailed",     new[] { "Das Herunterfahren konnte nicht abgebrochen werden (Fehlercode {0}).", "The shutdown could not be cancelled (error code {0})." } },
            { "CloseTitle",       new[] { "Fenster schliessen?", "Close window?" } },
            { "CloseText",        new[] { "Der Computer wird trotzdem um {0} Uhr heruntergefahren, auch wenn dieses Fenster geschlossen ist.",
                                          "The computer will still shut down at {0}, even if this window is closed." } },
        };

        [StructLayout(LayoutKind.Sequential)]
        struct Margins { public int Left, Right, Top, Bottom; }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

        [DllImport("kernel32.dll")]
        static extern ulong GetTickCount64();

        readonly Grid host = new Grid();
        readonly ScaleTransform zoom = new ScaleTransform();
        readonly FrameworkElement root;
        readonly DispatcherTimer ticker = new DispatcherTimer();
        readonly int[] presetMinutes = { 15, 30, 60, 120 };
        readonly Button[] presetButtons;

        readonly FrameworkElement mainPanel, idlePanel, activePanel, progressTrack, progressFill;
        readonly FrameworkElement dialogLayer, dialogCard, dialogIcon, aboutDetails;
        readonly TextBlock countdownText, targetText, dialogTitle, dialogText;
        readonly Button dialogPrimary, dialogSecondary, langDe, langEn;
        readonly NumberBox durationHours, durationMinutes, clockHours, clockMinutes;

        DateTime? target;
        DateTime plannedAt;
        int preset;            // Minuten der gewählten Schnellwahl, 0 = eigene Zeit
        string lang;           // "de" oder "en"
        double designHeight;   // Höhe der Oberfläche bei Mindestgrösse, 0 = noch nicht gemessen
        bool micaActive;
        bool closeConfirmed;
        Action dialogAction;

        public MainWindow()
        {
            Title = "Auto-Shutdown";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 14;
            UseLayoutRounding = true;
            TaskbarItemInfo = new TaskbarItemInfo();
            SetResourceReference(ForegroundProperty, "TextPrimary");
            SetResourceReference(BackgroundProperty, "WindowBg");
            ApplyTheme();
            lang = LoadLanguage();
            ApplyLanguage();

            using (Stream xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("AutoShutdown.MainWindow.xaml"))
                root = (FrameworkElement)XamlReader.Load(xaml);

            // Die Oberfläche wird mit dem Fenster skaliert (siehe UpdateScale)
            root.Width = DesignWidth;
            root.LayoutTransform = zoom;
            host.Children.Add(root);
            Content = host;

            mainPanel = Find<FrameworkElement>("MainPanel");
            idlePanel = Find<FrameworkElement>("IdlePanel");
            activePanel = Find<FrameworkElement>("ActivePanel");
            progressTrack = Find<FrameworkElement>("ProgressTrack");
            progressFill = Find<FrameworkElement>("ProgressFill");
            countdownText = Find<TextBlock>("CountdownText");
            targetText = Find<TextBlock>("TargetText");
            dialogLayer = Find<FrameworkElement>("DialogLayer");
            dialogCard = Find<FrameworkElement>("DialogCard");
            dialogTitle = Find<TextBlock>("DialogTitle");
            dialogText = Find<TextBlock>("DialogText");
            dialogPrimary = Find<Button>("DialogPrimary");
            dialogSecondary = Find<Button>("DialogSecondary");
            dialogIcon = Find<FrameworkElement>("DialogIcon");
            aboutDetails = Find<FrameworkElement>("AboutDetails");
            langDe = Find<Button>("LangDe");
            langEn = Find<Button>("LangEn");
            durationHours = Find<NumberBox>("DurationHours");
            durationMinutes = Find<NumberBox>("DurationMinutes");
            clockHours = Find<NumberBox>("ClockHours");
            clockMinutes = Find<NumberBox>("ClockMinutes");

            presetButtons = new Button[presetMinutes.Length];
            for (int i = 0; i < presetMinutes.Length; i++)
            {
                int mins = presetMinutes[i];
                presetButtons[i] = Find<Button>("Preset" + mins);
                presetButtons[i].Click += delegate { Schedule(TimeSpan.FromMinutes(mins), mins); };
            }
            Find<Button>("StartDuration").Click += delegate { StartDuration(); };
            Find<Button>("StartClock").Click += delegate { StartAtClock(); };
            Find<Button>("CancelButton").Click += delegate { Cancel(); };
            dialogPrimary.Click += delegate
            {
                Action action = dialogAction;
                HideMessage();
                if (action != null) action();
            };
            dialogSecondary.Click += delegate { HideMessage(); };
            langDe.Click += delegate { SetLanguage("de"); };
            langEn.Click += delegate { SetLanguage("en"); };
            Find<Button>("AboutButton").Click += delegate { ShowAbout(); };
            Find<TextBlock>("AboutVersion").Text = AppVersion;
            Find<TextBlock>("AboutAuthor").Text = Author;

            clockHours.Value = DateTime.Now.AddHours(1).Hour;
            clockMinutes.Value = 0;

            SourceInitialized += delegate { ApplyBackdrop(); };
            ContentRendered += delegate { EnableScaling(); };
            Closing += OnClosing;
            Closed += delegate { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; };
            PreviewKeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape && dialogLayer.Visibility == Visibility.Visible)
                {
                    HideMessage();
                    e.Handled = true;
                }
            };
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            Application.Current.SessionEnding += delegate { closeConfirmed = true; };

            UpdateLanguageButtons();
            LoadState();
            UpdateMode();

            ticker.Interval = TimeSpan.FromMilliseconds(250);
            ticker.Tick += delegate { if (target != null) UpdateCountdown(); };
            ticker.Start();
        }

        T Find<T>(string name) where T : class
        {
            return (T)root.FindName(name);
        }

        // ---------- Aktionen ----------

        void StartDuration()
        {
            durationHours.Commit();
            durationMinutes.Commit();
            TimeSpan span = new TimeSpan(durationHours.Value, durationMinutes.Value, 0);
            if (span.TotalMinutes < 1)
            {
                ShowMessage(T("NoTimeTitle"), T("NoTimeText"), "OK", null, null);
                return;
            }
            Schedule(span, 0);
        }

        void StartAtClock()
        {
            clockHours.Commit();
            clockMinutes.Commit();
            DateTime now = DateTime.Now;
            DateTime at = now.Date + new TimeSpan(clockHours.Value, clockMinutes.Value, 0);
            if (at <= now) at = at.AddDays(1); // Uhrzeit schon vorbei -> morgen
            Schedule(at - now, 0);
        }

        void Schedule(TimeSpan delay, int presetMins)
        {
            int seconds = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds));

            // Einen evtl. bereits geplanten Shutdown ersetzen
            RunShutdown("/a");
            int code = RunShutdown("/s /t " + seconds.ToString(CultureInfo.InvariantCulture));
            if (code != 0)
            {
                target = null;
                SaveState();
                UpdateMode();
                ShowMessage(T("ErrorTitle"), string.Format(T("ScheduleFailed"), code), "OK", null, null);
                return;
            }

            plannedAt = DateTime.Now;
            target = plannedAt.AddSeconds(seconds);
            preset = presetMins;
            SaveState();
            UpdateMode();
        }

        void Cancel()
        {
            int code = RunShutdown("/a");
            target = null;
            SaveState();
            UpdateMode();

            if (code != 0 && code != ERROR_NO_SHUTDOWN_IN_PROGRESS)
                ShowMessage(T("ErrorTitle"), string.Format(T("CancelFailed"), code), "OK", null, null);
        }

        // Immer über den vollen Pfad in System32 (kein Suchpfad), Argumente nur aus festen Werten und Zahlen
        static int RunShutdown(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }

        void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (closeConfirmed || target == null || target.Value <= DateTime.Now) return;
            e.Cancel = true;
            ShowMessage(T("CloseTitle"),
                string.Format(T("CloseText"), target.Value.ToString("HH:mm", CultureInfo.InvariantCulture)),
                T("Close"), T("KeepOpen"),
                delegate { closeConfirmed = true; Close(); });
        }

        // ---------- Anzeige ----------

        void UpdateMode()
        {
            bool active = target != null;
            idlePanel.Visibility = active ? Visibility.Hidden : Visibility.Visible;
            activePanel.Visibility = active ? Visibility.Visible : Visibility.Hidden;
            for (int i = 0; i < presetButtons.Length; i++)
                presetButtons[i].SetResourceReference(StyleProperty,
                    active && preset == presetMinutes[i] ? "TileButtonActive" : "TileButton");

            if (active)
            {
                UpdateCountdown();
            }
            else
            {
                Title = "Auto-Shutdown";
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
            }
        }

        void UpdateCountdown()
        {
            DateTime now = DateTime.Now;
            int left = Math.Max(0, (int)Math.Ceiling((target.Value - now).TotalSeconds));
            string time = string.Format("{0:00}:{1:00}:{2:00}", left / 3600, left / 60 % 60, left % 60);

            countdownText.Text = time;
            countdownText.SetResourceReference(TextBlock.ForegroundProperty, left <= 60 ? "Critical" : "TextPrimary");
            targetText.Text = left == 0 ? T("ShuttingDown") : DescribeTarget(target.Value);
            Title = "Auto-Shutdown – " + time;

            double total = (target.Value - plannedAt).TotalSeconds;
            double done = total <= 0 ? 1 : Math.Min(1, Math.Max(0, (now - plannedAt).TotalSeconds / total));
            progressFill.Width = progressTrack.ActualWidth * done;
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
            TaskbarItemInfo.ProgressValue = done;
        }

        string DescribeTarget(DateTime t)
        {
            string clock = t.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (t.Date == DateTime.Today) return string.Format(T("Today"), clock);
            if (t.Date == DateTime.Today.AddDays(1)) return string.Format(T("Tomorrow"), clock);
            CultureInfo culture = new CultureInfo(lang == "en" ? "en-US" : "de-DE");
            return string.Format(T("OnDay"), t.ToString(T("DayFormat"), culture), clock);
        }

        void ShowAbout()
        {
            ShowMessage("Auto-Shutdown", null, T("Close"), null, null, true);
        }

        void ShowMessage(string title, string text, string primary, string secondary, Action onPrimary, bool about = false)
        {
            dialogTitle.Text = title;
            dialogText.Text = text;
            dialogText.Visibility = about ? Visibility.Collapsed : Visibility.Visible;
            dialogIcon.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
            aboutDetails.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
            dialogPrimary.Content = primary;
            dialogAction = onPrimary;
            dialogSecondary.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;
            dialogSecondary.Content = secondary;
            Grid.SetColumn(dialogPrimary, secondary == null ? 2 : 0);
            dialogPrimary.IsDefault = true;
            mainPanel.IsEnabled = false;
            dialogLayer.Visibility = Visibility.Visible;

            // Einblenden wie bei Windows-11-Dialogen
            Duration d = TimeSpan.FromMilliseconds(180);
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ScaleTransform scale = new ScaleTransform(1.05, 1.05);
            dialogCard.RenderTransform = scale;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.05, 1, d) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.05, 1, d) { EasingFunction = ease });
            dialogLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
            Dispatcher.BeginInvoke(new Action(() => dialogPrimary.Focus()), DispatcherPriority.Input);
        }

        void HideMessage()
        {
            dialogAction = null;
            dialogPrimary.IsDefault = false;
            dialogLayer.Visibility = Visibility.Collapsed;
            mainPanel.IsEnabled = true;
        }

        // ---------- Fenstergrösse: Startgrösse = Mindestgrösse, darüber wächst alles gleichmässig mit ----------

        void EnableScaling()
        {
            if (designHeight > 0) return;
            designHeight = root.ActualHeight;
            MinWidth = ActualWidth;
            MinHeight = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            host.SizeChanged += delegate { UpdateScale(); };
            UpdateScale();
        }

        void UpdateScale()
        {
            if (designHeight <= 0 || host.ActualWidth <= 0 || host.ActualHeight <= 0) return;
            double s = Math.Max(1, Math.Min(host.ActualWidth / DesignWidth, host.ActualHeight / designHeight));
            zoom.ScaleX = s;
            zoom.ScaleY = s;
            // Die freie Richtung wird mit Inhalt gefüllt (breitere Karten bzw. zentriert), nicht mit leerem Rand
            root.Width = host.ActualWidth / s;
            root.Height = host.ActualHeight / s;
        }

        // ---------- Sprache ----------

        string T(string key)
        {
            return Strings[key][lang == "en" ? 1 : 0];
        }

        void SetLanguage(string value)
        {
            if (value == lang) return;
            lang = value;
            ApplyLanguage();
            UpdateLanguageButtons();
            UpdateMode();
            SaveLanguage();
        }

        void ApplyLanguage()
        {
            foreach (KeyValuePair<string, string[]> s in Strings)
                Application.Current.Resources["S." + s.Key] = T(s.Key);
            Language = XmlLanguage.GetLanguage(lang == "en" ? "en-US" : "de-DE");
        }

        void UpdateLanguageButtons()
        {
            langDe.SetResourceReference(StyleProperty, lang == "de" ? "SegmentSelected" : "Segment");
            langEn.SetResourceReference(StyleProperty, lang == "en" ? "SegmentSelected" : "Segment");

            // Namen für Screenreader, z. B. "15 Minuten"
            for (int i = 0; i < presetMinutes.Length; i++)
            {
                int m = presetMinutes[i];
                string label = m < 60 ? m + " " + T("Minutes") : m / 60 + " " + T(m == 60 ? "Hour" : "Hours");
                AutomationProperties.SetName(presetButtons[i], label);
            }
        }

        // Nur "de" oder "en" werden akzeptiert, sonst gilt die Sprache von Windows
        static string LoadLanguage()
        {
            try
            {
                FileInfo file = new FileInfo(LanguageFile);
                if (file.Exists && file.Length <= 16)
                {
                    string value = File.ReadAllText(LanguageFile).Trim();
                    if (value == "de" || value == "en") return value;
                }
            }
            catch { }
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en";
        }

        void SaveLanguage()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(LanguageFile, lang);
            }
            catch { }
        }

        // ---------- Windows-11-Design ----------

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ApplyTheme));
        }

        static bool IsDarkTheme()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        // Akzentpalette von Windows: 0 = Light3, 1 = Light2, 2 = Light1, 3 = Basis, 4 = Dark1, 5 = Dark2, 6 = Dark3
        static Color AccentShade(int index, Color fallback)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    byte[] p = k == null ? null : k.GetValue("AccentPalette") as byte[];
                    if (p != null && p.Length >= 32)
                        return Color.FromRgb(p[index * 4], p[index * 4 + 1], p[index * 4 + 2]);
                }
            }
            catch { }
            return fallback;
        }

        void ApplyTheme()
        {
            bool dark = IsDarkTheme();
            Color accent = dark ? AccentShade(1, Color.FromRgb(0x60, 0xCD, 0xFF)) : AccentShade(4, Color.FromRgb(0x00, 0x5F, 0xB8));
            Color accentText = dark ? AccentShade(0, Color.FromRgb(0x99, 0xEB, 0xFF)) : AccentShade(5, Color.FromRgb(0x00, 0x3E, 0x92));

            // Farbwerte entsprechen den Fluent-Design-Tokens von Windows 11
            SetBrush("TextPrimary",      dark ? "#FFFFFFFF" : "#E4000000");
            SetBrush("TextSecondary",    dark ? "#C5FFFFFF" : "#9E000000");
            SetBrush("TextOnAccent",     dark ? "#FF000000" : "#FFFFFFFF");
            SetBrush("CardBg",           dark ? "#0DFFFFFF" : "#B3FFFFFF");
            SetBrush("CardStroke",       dark ? "#19000000" : "#0F000000");
            SetBrush("ControlBg",        dark ? "#0FFFFFFF" : "#B3FFFFFF");
            SetBrush("ControlBgHover",   dark ? "#15FFFFFF" : "#80F9F9F9");
            SetBrush("ControlBgPressed", dark ? "#08FFFFFF" : "#4DF9F9F9");
            SetBrush("ControlStroke",    dark ? "#17FFFFFF" : "#1C000000");
            SetBrush("InputBg",          dark ? "#0FFFFFFF" : "#B3FFFFFF");
            SetBrush("InputBgHover",     dark ? "#15FFFFFF" : "#80F9F9F9");
            SetBrush("InputBgFocus",     dark ? "#B31E1E1E" : "#FFFFFFFF");
            SetBrush("InputStroke",      dark ? "#17FFFFFF" : "#1C000000");
            SetBrush("InputUnderline",   dark ? "#8BFFFFFF" : "#72000000");
            SetBrush("SubtleHover",      dark ? "#0FFFFFFF" : "#09000000");
            SetBrush("SubtlePressed",    dark ? "#0AFFFFFF" : "#06000000");
            SetBrush("SegmentSelectedBg", dark ? "#26FFFFFF" : "#FFFFFFFF");
            SetBrush("Divider",          dark ? "#15FFFFFF" : "#0F000000");
            SetBrush("TrackBg",          dark ? "#29FFFFFF" : "#1A000000");
            SetBrush("Smoke",            "#4D000000");
            SetBrush("DialogBg",         dark ? "#FF2B2B2B" : "#FFFFFFFF");
            SetBrush("DialogFooterBg",   dark ? "#FF202020" : "#FFF3F3F3");
            SetBrush("DialogStroke",     "#66757575");
            SetBrush("WindowBg",         dark ? "#FF202020" : "#FFF3F3F3");
            SetBrush("Critical",         dark ? "#FFFF99A4" : "#FFC42B1C");

            SetBrush("Accent", accent);
            SetBrush("AccentStroke", accent);
            SetBrush("AccentHover", Color.FromArgb(0xE6, accent.R, accent.G, accent.B));
            SetBrush("AccentPressed", Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
            SetBrush("AccentSubtle", Color.FromArgb(dark ? (byte)0x33 : (byte)0x26, accent.R, accent.G, accent.B));
            SetBrush("AccentText", accentText);

            ApplyBackdrop();
        }

        void SetBrush(string key, string argb)
        {
            SetBrush(key, (Color)ColorConverter.ConvertFromString(argb));
        }

        void SetBrush(string key, Color color)
        {
            // App-weit, damit die Farben schon beim Laden der XAML-Oberfläche auflösbar sind
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }

        // Mica-Hintergrund und passende Titelleiste (Windows 11 22H2 und neuer)
        void ApplyBackdrop()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int dark = IsDarkTheme() ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            if (!micaActive && Environment.OSVersion.Version.Build >= 22621)
            {
                int backdrop = DWMSBT_MAINWINDOW;
                if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
                {
                    Margins m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    DwmExtendFrameIntoClientArea(hwnd, ref m);
                    HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                    micaActive = true;
                }
            }
            if (micaActive) Background = Brushes.Transparent;
        }

        // ---------- Gespeicherter Zustand ----------

        void SaveState()
        {
            try
            {
                if (target == null)
                {
                    if (File.Exists(StateFile)) File.Delete(StateFile);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
                File.WriteAllText(StateFile, string.Join(" ",
                    plannedAt.Ticks.ToString(CultureInfo.InvariantCulture),
                    target.Value.Ticks.ToString(CultureInfo.InvariantCulture),
                    preset.ToString(CultureInfo.InvariantCulture)));
            }
            catch { }
        }

        // Die Datei steuert nur die Anzeige, nie einen Shutdown. Trotzdem wird alles streng geprüft.
        void LoadState()
        {
            try
            {
                FileInfo file = new FileInfo(StateFile);
                if (!file.Exists) return;

                long plannedTicks = 0, targetTicks = 0;
                int presetMins = 0;
                string[] parts = file.Length <= 100 ? File.ReadAllText(StateFile).Trim().Split(' ') : new string[0];
                bool valid = parts.Length == 3
                    && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out plannedTicks)
                    && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out targetTicks)
                    && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out presetMins)
                    && plannedTicks <= DateTime.MaxValue.Ticks && targetTicks <= DateTime.MaxValue.Ticks
                    && (presetMins == 0 || Array.IndexOf(presetMinutes, presetMins) >= 0);

                if (valid)
                {
                    DateTime now = DateTime.Now;
                    DateTime planned = new DateTime(plannedTicks);
                    DateTime at = new DateTime(targetTicks);
                    DateTime bootTime = now.AddMilliseconds(-(double)GetTickCount64());

                    // Nur übernehmen, wenn seit der Planung nicht neu gestartet wurde, die Zeit in der Zukunft
                    // liegt und die Dauer zu dem passt, was das Programm selbst einstellen kann (max. 100 Std)
                    if (planned > bootTime && planned <= now && at > now && at - planned <= TimeSpan.FromHours(100))
                    {
                        plannedAt = planned;
                        target = at;
                        preset = presetMins;
                        return;
                    }
                }
                File.Delete(StateFile);
            }
            catch { }
        }
    }

    // Zahlenfeld im Stil der Windows-11-NumberBox (Eingabe, Pfeiltasten, Mausrad)
    public class NumberBox : Grid
    {
        readonly Border frame = new Border();
        readonly Border underline = new Border();
        readonly TextBox text = new TextBox();
        int value;

        public int Min { get; set; }
        public int Max { get; set; }
        public bool Wrap { get; set; }

        public int Value
        {
            get { return value; }
            set
            {
                this.value = Math.Min(Max, Math.Max(Min, value));
                text.Text = this.value.ToString("00");
            }
        }

        public NumberBox()
        {
            Width = 82;
            Height = 32;
            Max = 99;

            frame.CornerRadius = new CornerRadius(4);
            frame.BorderThickness = new Thickness(1);
            frame.SetResourceReference(Border.BorderBrushProperty, "InputStroke");
            underline.CornerRadius = new CornerRadius(4);
            underline.IsHitTestVisible = false;

            text.Background = Brushes.Transparent;
            text.BorderThickness = new Thickness(0);
            text.Padding = new Thickness(2, 0, 0, 1);
            text.VerticalContentAlignment = VerticalAlignment.Center;
            text.TextAlignment = TextAlignment.Center;
            text.MaxLength = 2;
            text.FocusVisualStyle = null;
            text.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
            text.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextPrimary");
            text.SetResourceReference(TextBoxBase.SelectionBrushProperty, "Accent");
            InputMethod.SetIsInputMethodEnabled(text, false);

            Grid inner = new Grid();
            inner.ColumnDefinitions.Add(new ColumnDefinition());
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.Children.Add(text);
            inner.Children.Add(MakeSpin("", -1, 1));
            RepeatButton up = MakeSpin("", +1, 2);
            up.Margin = new Thickness(0, 0, 3, 0);
            inner.Children.Add(up);

            Children.Add(frame);
            Children.Add(inner);
            Children.Add(underline);

            text.PreviewTextInput += delegate (object s, TextCompositionEventArgs e)
            {
                foreach (char c in e.Text)
                    if (!char.IsDigit(c)) e.Handled = true;
            };
            text.PreviewKeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Key == Key.Up) { Step(1); e.Handled = true; }
                else if (e.Key == Key.Down) { Step(-1); e.Handled = true; }
                else if (e.Key == Key.Enter) { Commit(); text.SelectAll(); }
            };
            text.GotKeyboardFocus += delegate { text.SelectAll(); };
            text.LostKeyboardFocus += delegate { Commit(); };
            text.PreviewMouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                if (!text.IsKeyboardFocusWithin) { text.Focus(); e.Handled = true; }
            };
            PreviewMouseWheel += delegate (object s, MouseWheelEventArgs e)
            {
                Step(e.Delta > 0 ? 1 : -1);
                e.Handled = true;
            };
            IsKeyboardFocusWithinChanged += delegate { UpdateLook(); };
            MouseEnter += delegate { UpdateLook(); };
            MouseLeave += delegate { UpdateLook(); };

            Value = 0;
            UpdateLook();
        }

        public void Commit()
        {
            int v;
            Value = int.TryParse(text.Text, out v) ? v : value;
        }

        void Step(int delta)
        {
            Commit();
            int v = value + delta;
            if (Wrap)
            {
                int range = Max - Min + 1;
                v = ((v - Min) % range + range) % range + Min;
            }
            Value = v;
            if (text.IsKeyboardFocusWithin) text.SelectAll();
        }

        RepeatButton MakeSpin(string glyph, int delta, int column)
        {
            RepeatButton b = new RepeatButton();
            b.Content = glyph;
            b.SetResourceReference(StyleProperty, "SpinButton");
            b.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(b, column);
            b.Click += delegate { Step(delta); };
            return b;
        }

        void UpdateLook()
        {
            bool focused = IsKeyboardFocusWithin;
            frame.SetResourceReference(Border.BackgroundProperty,
                focused ? "InputBgFocus" : IsMouseOver ? "InputBgHover" : "InputBg");
            underline.SetResourceReference(Border.BorderBrushProperty, focused ? "Accent" : "InputUnderline");
            underline.BorderThickness = new Thickness(0, 0, 0, focused ? 2 : 1);
        }
    }
}
