using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SimRigMode.Models;
using SimRigMode.Services;
using SimRigMode.Services.Apps;
using SimRigMode.Services.Display;
using SimRigMode.UI;
using Windows.Storage.Pickers;
using VirtualKey = Windows.System.VirtualKey;

namespace SimRigMode;

public sealed partial class SettingsWindow : Window
{
    private readonly App _host;
    private readonly AppWindow _appWindow;
    private readonly List<UsbTopologyRow> _usbRows = [];
    private readonly Dictionary<string, bool> _hubDisableOverrides = new(StringComparer.OrdinalIgnoreCase);
    private UsbTopology? _usbTopology;
    private UsbTopologyRow? _selectedUsbRow;
    private IReadOnlyList<AudioEndpointInfo> _audioEndpoints = [];
    private DisplaySnapshot? _liveDisplays;
    private DisplayPathRecord? _selectedDisplay;
    private WorkspaceMode _displayEditingMode;
    private bool _initialized;
    private bool _loadingValues;

    private AppConfig Config => _host.Configuration.Current;

    public SettingsWindow(App host)
    {
        _host = host;
        InitializeComponent();
        Title = Product.Name;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        var dpi = GetDpiForWindow(hwnd) / 96d;
        _appWindow.Resize(new Windows.Graphics.SizeInt32((int)(1160 * dpi), (int)(800 * dpi)));
        CenterWindow();
        _appWindow.Closing += (_, args) => { args.Cancel = true; Hide(); };
        Navigation.SelectedItem = Navigation.MenuItems[0];
        _host.Log.EntryWritten += OnLogEntry;
        _host.Switcher.StepCompleted += OnStepCompleted;
        OverviewSwitch.HoldStarted += _ => OverviewCaption.Text = "Keep holding";
        OverviewSwitch.HoldCancelled += () => { if (!_transitioning) OverviewCaption.Text = HoldHint(); };
        OverviewSwitch.Confirmed += target => _ = _host.SwitchModeAsync(target);
    }

    public void ShowAndActivate()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96d;
        _appWindow.Resize(new Windows.Graphics.SizeInt32((int)(1160 * scale), (int)(800 * scale)));
        CenterWindow();
        ShowWindow(hwnd, 5);
        Activate();
        SetForegroundWindow(hwnd);
        if (!_initialized) _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _loadingValues = true;
        LoadConfigIntoControls();
        DiagnosticsText.Text = _host.Log.ReadRecent();
        _loadingValues = false;
        await RefreshDisplaysAsync();
        await RefreshUsbAsync();
        await RefreshAudioAsync();
        _initialized = true;
        SetOverviewStatus(OverviewState.Active, Config.CurrentMode == WorkspaceMode.Unknown ? "Not switched yet" : "Active");
        UpdateSummaries();
    }

    private void LoadConfigIntoControls()
    {
        StartupToggle.IsOn = _host.StartWithWindows.IsEnabled;
        UsbArmedToggle.IsOn = Config.General.UsbSwitchingArmed;
        DryRunToggle.IsOn = Config.General.DryRunTransitions;
        HotkeysToggle.IsOn = Config.General.HotkeysEnabled;
        HotkeysExpander.IsExpanded = Config.General.HotkeysEnabled;
        DesktopHotkeyText.Text = Config.General.DesktopHotkey;
        RigHotkeyText.Text = Config.General.SimRigHotkey;
        RigPowerOnDelay.Value = Config.General.HardwarePowerOnWaitSeconds;
        UsbEnumerationDelay.Value = Config.General.UsbEnumerationWaitSeconds;
        DesktopPowerOffDelay.Value = Config.General.HardwarePowerOffDelaySeconds;
        LoadIntegration();
        VersionText.Text = $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}  •  Windows App SDK 2.4  •  x64";
        UpdatePowerOffList();
        RenderLinkedApps();
    }

    // ===== Apps linked to a mode =======================================================

    private Task<IReadOnlyList<DiscoveredApp>>? _discovery;

    private void RenderLinkedApps()
    {
        RenderAppList(SimRigAppsPanel, Config.SimRig, "Sim rig");
        RenderAppList(DesktopAppsPanel, Config.Desktop, "Desktop");
    }

    private void RenderAppList(StackPanel panel, ModeConfig mode, string modeName)
    {
        panel.Children.Clear();
        if (mode.LinkedApps.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"No apps linked to {modeName} mode.",
                Margin = new Thickness(4, 0, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            return;
        }

        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        foreach (var app in mode.LinkedApps.ToList())
        {
            // One expandable card per app, the way Windows Settings lays out an item with options:
            // the name, the path and the remove button on one line, the options inside. With the
            // options on the card's right-hand side, a narrow window squeezed the name and path
            // down to a sliver, one character per line.
            var start = new ToggleSwitch { IsOn = app.StartWithMode };
            var close = new ToggleSwitch { IsOn = app.CloseWhenLeaving };
            var force = new ToggleSwitch { IsOn = app.ForceClose, IsEnabled = app.CloseWhenLeaving };
            start.Toggled += async (_, _) => { app.StartWithMode = start.IsOn; await SaveAppsAsync(); };
            close.Toggled += async (_, _) => { app.CloseWhenLeaving = close.IsOn; force.IsEnabled = app.CloseWhenLeaving; await SaveAppsAsync(); };
            force.Toggled += async (_, _) => { app.ForceClose = force.IsOn; await SaveAppsAsync(); };

            var remove = new Button { Content = new SymbolIcon(Symbol.Delete) };
            ToolTipService.SetToolTip(remove, "Remove from " + modeName + " mode");
            AutomationProperties.SetName(remove, $"Remove {app.Name} from {modeName} mode");
            remove.Click += async (_, _) => { mode.LinkedApps.Remove(app); await SaveAppsAsync(); RenderLinkedApps(); };

            var where = File.Exists(app.Path) ? app.Path : app.Path + "  •  Not found";
            var path = new TextBlock
            {
                Text = where,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = secondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            };
            ToolTipService.SetToolTip(path, where);

            var card = new CommunityToolkit.WinUI.Controls.SettingsExpander
            {
                Header = app.Name,
                Description = path,
                HeaderIcon = new FontIcon { Glyph = "\uE8FC" },
                Content = remove
            };
            card.Items.Add(new CommunityToolkit.WinUI.Controls.SettingsCard { Header = "Start with the mode", Description = "Started when the mode begins, unless it is already running", Content = start });
            card.Items.Add(new CommunityToolkit.WinUI.Controls.SettingsCard { Header = "Close when leaving", Description = "Asked to close when you switch to the other mode", Content = close });
            card.Items.Add(new CommunityToolkit.WinUI.Controls.SettingsCard { Header = "End it if it won't close", Description = "Many sim tools hide in the tray instead of closing; this ends them a few seconds later", Content = force });
            panel.Children.Add(card);
        }
    }

    private async Task SaveAppsAsync()
    {
        try { await _host.Configuration.SaveAsync(); UpdateSummaries(); }
        catch (Exception ex) { Report(AppsInfoBar, "The app settings could not be saved", ex); }
    }

    private async void AddApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var mode = tag == "desktop" ? Config.Desktop : Config.SimRig;
        var modeName = tag == "desktop" ? "Desktop" : "Sim rig";
        try
        {
            _discovery ??= Task.Run(() => new AppDiscoveryService().Discover());
            var picked = await PickAppsAsync(modeName, mode);
            if (picked.Count == 0) return;

            var added = new List<string>();
            var skipped = new List<string>();
            foreach (var app in picked)
            {
                if (mode.LinkedApps.Any(linked => linked.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase)) ||
                    mode.LinkedApps.Count >= LinkedAppSanitizer.MaxAppsPerMode)
                {
                    skipped.Add(app.Name);
                    continue;
                }
                mode.LinkedApps.Add(new LinkedApp { Name = app.Name, Path = app.Path, Arguments = app.Arguments });
                added.Add(app.Name);
            }
            if (added.Count > 0) { await _host.Configuration.SaveAsync(); RenderLinkedApps(); UpdateSummaries(); }

            if (added.Count == 0)
                ShowInfo(AppsInfoBar, "Nothing new to link", $"{string.Join(", ", skipped)} {(skipped.Count == 1 ? "is" : "are")} already linked to {modeName} mode.", InfoBarSeverity.Informational);
            else
                ShowInfo(AppsInfoBar, $"Linked {added.Count} app{(added.Count == 1 ? "" : "s")} to {modeName} mode",
                    $"{string.Join(", ", added)}: started when {modeName} mode begins, closed when you switch to the other mode." +
                    (skipped.Count > 0 ? $" Skipped {string.Join(", ", skipped)}, already linked or the list is full." : ""),
                    InfoBarSeverity.Success);
        }
        catch (Exception ex) { Report(AppsInfoBar, "The apps could not be added", ex); }
    }

    /// <summary>
    /// The app picker: every app found in the Start menu and among running programs, sim racing
    /// tools first. Tick as many as you like. Ticks survive searching, and Browse adds programs to
    /// the ticked set without closing the picker. Apps already linked to this mode are shown but
    /// cannot be ticked again.
    /// </summary>
    private async Task<IReadOnlyList<DiscoveredApp>> PickAppsAsync(string modeName, ModeConfig mode)
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var search = new AutoSuggestBox { PlaceholderText = "Search apps", QueryIcon = new SymbolIcon(Symbol.Find) };
        var status = new TextBlock { Text = "Looking for installed apps…", Foreground = secondary };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, Height = 380 };
        var content = new StackPanel { Spacing = 10, Width = 540 };
        content.Children.Add(search); content.Children.Add(status); content.Children.Add(list);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot, Title = $"Link apps to {modeName} mode", Content = content,
            PrimaryButtonText = "Add", SecondaryButtonText = "Browse for .exe", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false
        };

        var apps = new List<DiscoveredApp>();
        var ticked = new Dictionary<string, DiscoveredApp>(StringComparer.OrdinalIgnoreCase);
        var linked = new HashSet<string>(mode.LinkedApps.Select(app => app.Path), StringComparer.OrdinalIgnoreCase);
        var loaded = false;
        var filling = false;

        void Refresh()
        {
            var query = search.Text?.Trim() ?? "";
            status.Text = !loaded && apps.Count == 0 ? "Looking for installed apps…"
                : apps.Count == 0 ? "No apps were found. Use Browse to pick programs."
                : $"{list.Items.Count} {(query.Length > 0 ? "matching apps" : "apps")}, {ticked.Count} ticked. Sim racing tools are listed first.";
            dialog.IsPrimaryButtonEnabled = ticked.Count > 0;
            dialog.PrimaryButtonText = ticked.Count <= 1 ? "Add" : $"Add {ticked.Count} apps";
        }

        void Fill()
        {
            // Rebuilding the list raises SelectionChanged for every row it removes; those are not
            // the user unticking anything, so they must not touch the ticked set.
            filling = true;
            try
            {
                var query = search.Text?.Trim() ?? "";
                list.Items.Clear();
                var reticked = new List<ListViewItem>();
                foreach (var app in apps.Where(app => query.Length == 0 ||
                             app.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             app.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(400))
                {
                    var alreadyLinked = linked.Contains(app.Path);
                    var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    title.Children.Add(new TextBlock { Text = app.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
                    if (app.IsSimRacing)
                        title.Children.Add(new Border
                        {
                            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center,
                            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                            Child = new TextBlock { Text = "Sim racing", FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] }
                        });
                    if (alreadyLinked)
                        title.Children.Add(new TextBlock { Text = "Already linked", FontSize = 12, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center });
                    var row = new StackPanel { Spacing = 2, Padding = new Thickness(0, 4, 0, 4) };
                    row.Children.Add(title);
                    row.Children.Add(new TextBlock { Text = app.Path, FontSize = 12, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis });
                    var item = new ListViewItem { Content = row, Tag = app, IsEnabled = !alreadyLinked };
                    list.Items.Add(item);
                    if (ticked.ContainsKey(app.Path)) reticked.Add(item);
                }
                foreach (var item in reticked) list.SelectedItems.Add(item);
            }
            finally { filling = false; }
            Refresh();
        }

        search.TextChanged += (_, _) => Fill();
        list.SelectionChanged += (_, args) =>
        {
            if (filling) return;
            foreach (var app in args.AddedItems.OfType<ListViewItem>().Select(item => item.Tag).OfType<DiscoveredApp>()) ticked[app.Path] = app;
            foreach (var app in args.RemovedItems.OfType<ListViewItem>().Select(item => item.Tag).OfType<DiscoveredApp>()) ticked.Remove(app.Path);
            Refresh();
        };

        // Browse adds to the ticked set and keeps the picker open, so found and browsed apps can
        // be added in one go.
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe"); InitializePicker(picker);
                var files = await picker.PickMultipleFilesAsync();
                foreach (var file in files)
                {
                    var app = apps.FirstOrDefault(existing => existing.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
                    if (app is null)
                    {
                        var name = AppDiscoveryService.FriendlyName(file.Path, System.IO.Path.GetFileNameWithoutExtension(file.Path));
                        app = new DiscoveredApp(name, file.Path, "", AppCatalog.IsSimRacing(name, file.Path));
                        apps.Insert(0, app);
                    }
                    if (!linked.Contains(app.Path)) ticked[app.Path] = app;
                }
                if (files.Count > 0) { search.Text = ""; Fill(); }
            }
            catch (Exception ex) { _host.Log.Error("Browsing for a program failed", ex); }
            finally { deferral.Complete(); }
        };

        _ = LoadAsync();
        async Task LoadAsync()
        {
            IReadOnlyList<DiscoveredApp> found;
            try { found = await _discovery!; } catch { found = []; }
            foreach (var app in found)
                if (!apps.Any(existing => existing.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase))) apps.Add(app);
            loaded = true;
            Fill();
        }

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return [];
        // In list order: browsed programs first, then sim racing tools, then the rest.
        return apps.Where(app => ticked.ContainsKey(app.Path) && !linked.Contains(app.Path)).ToList();
    }

    /// <summary>The Safety card listing monitors that are parked when a mode leaves them out.
    /// It lives there as well as on the Displays page because a monitor that is out of the layout
    /// does not appear on the Displays page at all.</summary>
    private void UpdatePowerOffList()
    {
        var entries = Config.General.SwitchOffWhenUnused;
        PowerOffListCard.Description = entries.Count == 0
            ? "None. A monitor is added when you turn this on for it on the Displays page, or when Pitlane sees it keep dropping out."
            : string.Join(", ", entries.Select(entry =>
                entry.FriendlyName + (DisplayPowerPolicy.IsParked(Config.General, entry.MonitorDevicePath) ? ", parked now" : "") +
                (entry.Reason == "watchdog" ? ", seen dropping out" : "")));
        ClearPowerOffListButton.IsEnabled = entries.Count > 0;
    }

    private async void ClearPowerOffList_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Config.General.SwitchOffWhenUnused.Clear();
            Config.General.ParkedDisplays.Clear();
            await _host.Configuration.SaveAsync(); UpdatePowerOffList();
            if (_selectedDisplay is not null) SelectDisplay(_selectedDisplay);
            ShowInfo(SettingsInfoBar, "List cleared", "Mode switches will detach monitors they leave out. If one starts dropping out, Pitlane tells you and puts it back on this list.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { Report(SettingsInfoBar, "The list could not be saved", ex); }
    }

    private async void DisplaySwitchedToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingValues || _selectedDisplay is null) return;
        var display = _selectedDisplay;
        var on = DisplaySwitchedToggle.IsOn;
        try
        {
            DisplayPowerPolicy.SetSwitchedWithModes(Config.General, display.MonitorDevicePath, on);
            await _host.Configuration.SaveAsync();
            SelectDisplay(display);
            ShowInfo(DisplayInfoBar,
                on ? $"{display.FriendlyName} switches with the modes" : $"{display.FriendlyName} is left as it is",
                on ? "It is on in the modes that use it, and off in the others."
                   : "Pitlane leaves it on or off as it is when switching modes.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _loadingValues = true; DisplaySwitchedToggle.IsOn = !on; _loadingValues = false;
            Report(DisplayInfoBar, "The setting could not be saved", ex);
        }
    }

    private async void DisplayPowerOffToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingValues || _selectedDisplay is null) return;
        var display = _selectedDisplay;
        var on = DisplayPowerOffToggle.IsOn;
        try
        {
            if (on) DisplayPowerPolicy.Remember(Config.General, display.MonitorDevicePath, display.FriendlyName, "user");
            else DisplayPowerPolicy.Forget(Config.General, display.MonitorDevicePath);
            await _host.Configuration.SaveAsync(); UpdatePowerOffList();
            ShowInfo(DisplayInfoBar,
                on ? $"{display.FriendlyName} will be parked" : $"{display.FriendlyName} will be detached",
                on ? "When a mode leaves it out, Pitlane keeps it attached in a corner instead of detaching it. Switch its screen off with its own power button."
                   : "When a mode leaves it out, Pitlane detaches it. If it starts dropping out, Pitlane tells you.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _loadingValues = true; DisplayPowerOffToggle.IsOn = !on; _loadingValues = false;
            Report(DisplayInfoBar, "The setting could not be saved", ex);
        }
    }

    /// <summary>Raised by the display watchdog when a monitor keeps dropping out.</summary>
    public void ShowDisplayWatchdogWarning(string title, string message) => Enqueue(() =>
    {
        ShowInfo(OverviewInfoBar, title, message, InfoBarSeverity.Warning);
        UpdatePowerOffList();
        if (_selectedDisplay is not null) SelectDisplay(_selectedDisplay);
    });

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        SetPage(OverviewPage, tag == "overview"); SetPage(DesktopPage, tag == "desktop"); SetPage(SimRigPage, tag == "simrig");
        SetPage(DisplaysPage, tag == "displays"); SetPage(UsbPage, tag == "usb"); SetPage(AudioPage, tag == "audio");
        SetPage(AppsPage, tag == "apps"); SetPage(IntegrationsPage, tag == "integrations"); SetPage(SettingsPage, tag == "settings");
        if (tag == "integrations") _ = CheckIntegrationAsync();
        if (tag == "apps")
        {
            // Start looking for installed apps now, so the picker is ready when it opens.
            _discovery ??= Task.Run(() => new AppDiscoveryService().Discover());
            RenderLinkedApps();
        }
    }
    private void PageHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var paneWidth = Navigation.DisplayMode == NavigationViewDisplayMode.Expanded ? Navigation.OpenPaneLength : 0;
        var width = Math.Min(1050, Math.Max(420, e.NewSize.Width - paneWidth - 64));
        foreach (var viewer in new[] { OverviewPage, DesktopPage, SimRigPage, DisplaysPage, AudioPage, AppsPage, IntegrationsPage, SettingsPage })
            if (viewer.Content is StackPanel panel) panel.Width = width;
    }
    private static void SetPage(UIElement page, bool visible) => page.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    private void NavigateButton_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string tag }) SelectNavigation(tag); }
    private void SelectNavigation(string tag)
    {
        foreach (var item in Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>())
            if (item.Tag as string == tag) { Navigation.SelectedItem = item; return; }
    }

    // ===== Overview ======================================================================

    private bool _transitioning;
    private enum OverviewState { Active, Switching, Warning, Failed }

    private WorkspaceMode ActiveMode => Config.CurrentMode == WorkspaceMode.SimRig ? WorkspaceMode.SimRig : WorkspaceMode.Desktop;
    private static WorkspaceMode OtherMode(WorkspaceMode mode) => mode == WorkspaceMode.SimRig ? WorkspaceMode.Desktop : WorkspaceMode.SimRig;
    private static string ModeName(WorkspaceMode mode) => mode == WorkspaceMode.SimRig ? "Sim Rig" : "Desktop";
    private string HoldHint() => $"Hold for 3 seconds to switch to {ModeName(OtherMode(ActiveMode))}";

    // The "Apply this workspace" buttons on the two mode pages: a deliberate click, no hold.
    private async void DesktopSwitch_Click(object sender, RoutedEventArgs e) => await _host.SwitchModeAsync(WorkspaceMode.Desktop);
    private async void RigSwitch_Click(object sender, RoutedEventArgs e) => await _host.SwitchModeAsync(WorkspaceMode.SimRig);

    private void CancelTransition_Click(object sender, RoutedEventArgs e) { _host.CancelTransition(); OverviewCancel.IsEnabled = false; OverviewCaption.Text = "Cancelling at the next safe point…"; }

    public void BeginTransition(WorkspaceMode target) => Enqueue(() =>
    {
        _transitioning = true;
        OverviewInfoBar.IsOpen = false;
        // Started from the tray, a shortcut or the flyout: slide over now, like a hold would.
        if (OverviewSwitch.Mode != target) OverviewSwitch.SetMode(target);
        OverviewSwitch.IsBusy = true;
        OverviewMode.Text = ModeName(target);
        SetOverviewStatus(OverviewState.Switching, "Switching…");
        OverviewCaption.Text = $"Switching to {ModeName(target)}";
        OverviewCancel.IsEnabled = true;
        OverviewCancel.Visibility = Visibility.Visible;
        OverviewSteps.Children.Clear();
        OverviewSteps.Visibility = Visibility.Visible;
    });

    public void EndTransition(WorkspaceMode mode, IReadOnlyList<TransitionStep> results) => Enqueue(() =>
    {
        _transitioning = false;
        OverviewSwitch.IsBusy = false;
        OverviewCancel.Visibility = Visibility.Collapsed;
        var active = ActiveMode;
        OverviewSwitch.SetMode(active);
        var warnings = results.Count(step => !step.Success);
        if (Config.General.DryRunTransitions)
        {
            SetOverviewStatus(OverviewState.Active, "Active");
            OverviewCaption.Text = "Dry run finished. Nothing was changed.";
        }
        else if (warnings > 0)
        {
            SetOverviewStatus(OverviewState.Warning, $"Active, {warnings} warning{(warnings == 1 ? "" : "s")}");
            OverviewCaption.Text = $"Switched to {ModeName(active)}, but not every step worked.";
        }
        else
        {
            SetOverviewStatus(OverviewState.Active, "Active");
            OverviewCaption.Text = $"Switched to {ModeName(active)}";
        }
        UpdateSummaries();
    });

    public void TransitionFailed(string message) => Enqueue(() =>
    {
        _transitioning = false;
        OverviewSwitch.IsBusy = false;
        OverviewCancel.Visibility = Visibility.Collapsed;
        OverviewSwitch.SetMode(ActiveMode);
        OverviewMode.Text = ModeName(ActiveMode);
        SetOverviewStatus(OverviewState.Failed, "Switch stopped");
        OverviewCaption.Text = message;
    });

    /// <summary>Test switches run without the overview; their steps are reported in Settings.</summary>
    private void OnStepCompleted(TransitionStep step) => Enqueue(() => { if (_transitioning) OverviewSteps.Children.Add(StepRow(step)); });

    private UIElement StepRow(TransitionStep step)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon
        {
            Glyph = step.Success ? "\uE73E" : "\uE7BA",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (Brush)Application.Current.Resources[step.Success ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"]
        });
        var text = new TextBlock
        {
            Text = step.Success ? step.Name : $"{step.Name}: {step.Detail}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private void SetOverviewStatus(OverviewState state, string text)
    {
        var color = state switch
        {
            OverviewState.Switching or OverviewState.Warning => Windows.UI.Color.FromArgb(255, 251, 191, 36),
            OverviewState.Failed => Windows.UI.Color.FromArgb(255, 248, 113, 113),
            _ => Windows.UI.Color.FromArgb(255, 52, 211, 153)
        };
        OverviewDot.Fill = new SolidColorBrush(color);
        OverviewHalo.Fill = new SolidColorBrush(color);
        OverviewStatus.Text = text;

        // The halo round the dot swells and fades, faster while switching.
        var halo = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(OverviewHalo);
        var compositor = halo.Compositor;
        halo.CenterPoint = new System.Numerics.Vector3(5, 5, 0);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new System.Numerics.Vector3(0.6f, 0.6f, 1));
        scale.InsertKeyFrame(1, new System.Numerics.Vector3(1.6f, 1.6f, 1));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0.55f);
        fade.InsertKeyFrame(1, 0);
        foreach (var animation in new Microsoft.UI.Composition.KeyFrameAnimation[] { scale, fade })
        {
            animation.Duration = TimeSpan.FromMilliseconds(state == OverviewState.Switching ? 1100 : 2600);
            animation.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        }
        halo.StartAnimation("Scale", scale);
        halo.StartAnimation("Opacity", fade);
    }

    /// <summary>The mode, the switch at rest, and what each mode does when you switch to it.</summary>
    private void RenderOverview()
    {
        var active = ActiveMode;
        if (!_transitioning)
        {
            OverviewMode.Text = ModeName(active);
            if (OverviewSwitch.Mode != active) OverviewSwitch.SetMode(active, animate: false);
            if (string.IsNullOrEmpty(OverviewCaption.Text)) OverviewCaption.Text = HoldHint();
        }
        OverviewDryRun.Visibility = Config.General.DryRunTransitions || Product.IsStandbyTest ? Visibility.Visible : Visibility.Collapsed;
        OverviewDryRunText.Text = Config.General.DryRunTransitions ? "Dry run" : "Standby test";

        // The active mode's card is the only one with a colour: its title, in that mode's colour.
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        DesktopSummaryTitle.Foreground = active == WorkspaceMode.Desktop ? new SolidColorBrush(HoldSwitch.AccentOf(WorkspaceMode.Desktop)) : secondary;
        RigSummaryTitle.Foreground = active == WorkspaceMode.SimRig ? new SolidColorBrush(HoldSwitch.AccentOf(WorkspaceMode.SimRig)) : secondary;
        FillSummary(DesktopSummaryRows, WorkspaceMode.Desktop);
        FillSummary(RigSummaryRows, WorkspaceMode.SimRig);
    }

    private void FillSummary(StackPanel rows, WorkspaceMode mode)
    {
        var profile = mode == WorkspaceMode.SimRig ? Config.SimRig : Config.Desktop;
        rows.Children.Clear();
        rows.Children.Add(SummaryRow("\uE7F4", "Displays", DisplaysValue(profile)));
        rows.Children.Add(SummaryRow("\uE71D", "Apps", AppsValue(profile)));
        rows.Children.Add(SummaryRow("\uE767", "Audio", AudioValue(profile.Audio)));
        var rules = Config.UsbRules.Count(rule => (mode == WorkspaceMode.Desktop ? rule.Desktop : rule.SimRig) != DeviceModeAction.DoNotChange);
        if (rules > 0)
            rows.Children.Add(SummaryRow("\uECF0", "USB", $"{rules} rule{(rules == 1 ? "" : "s")}{(Config.General.UsbSwitchingArmed ? "" : ", disarmed")}"));
        if (Config.Integration.IsActive)
            rows.Children.Add(SummaryRow("\uE945", "Rig power", mode == WorkspaceMode.SimRig ? "Turns on" : "Turns off"));
    }

    private static UIElement SummaryRow(string glyph, string label, string value)
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock { Text = label, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = value, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 0, 0) };
        ToolTipService.SetToolTip(text, value);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(text, 2);
        row.Children.Add(name);
        row.Children.Add(text);
        return row;
    }

    private static string DisplaysValue(ModeConfig profile)
    {
        if (profile.Displays is null) return "Not captured";
        var paths = profile.Displays.Paths;
        // EDID names come padded, and some end in a dash: "27G2WG3-".
        var names = paths.Select(path => path.FriendlyName.Trim().TrimEnd('-').Trim()).Where(name => name.Length > 0).ToList();
        if (names.Count == paths.Count && names.Count is 1 or 2) return string.Join(", ", names);
        return paths.Count == 1 ? "1 display" : $"{paths.Count} displays";
    }

    private static string AppsValue(ModeConfig profile)
    {
        var apps = profile.LinkedApps.Where(app => app.StartWithMode).Select(app => app.Name).ToList();
        return apps.Count switch { 0 => "None", 1 or 2 => string.Join(", ", apps), _ => $"{apps[0]}, {apps[1]} +{apps.Count - 2}" };
    }

    private string AudioValue(AudioSelection audio)
    {
        if (!string.IsNullOrWhiteSpace(audio.PlaybackDefaultId))
            return _audioEndpoints.FirstOrDefault(endpoint => endpoint.Id.Equals(audio.PlaybackDefaultId, StringComparison.OrdinalIgnoreCase))?.Name ?? "Set";
        return new[] { audio.PlaybackCommunicationsId, audio.RecordingDefaultId, audio.RecordingCommunicationsId }.Any(id => !string.IsNullOrWhiteSpace(id)) ? "Set" : "Unchanged";
    }

    /// <summary>The two cards sit side by side, and stack when there is no room for both.</summary>
    private void ModeCards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 560;
        Grid.SetRow(RigSummary, stacked ? 1 : 0);
        Grid.SetColumn(RigSummary, stacked ? 0 : 1);
        ModeCards.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ModeCards.ColumnSpacing = stacked ? 0 : 16;
    }

    private void OnLogEntry(string line) => Enqueue(() => { if (DiagnosticsText.Text.Length > 40000) DiagnosticsText.Text = DiagnosticsText.Text[^30000..]; DiagnosticsText.Text += line + Environment.NewLine; });

    private async void CaptureDesktop_Click(object sender, RoutedEventArgs e) => await CaptureAsync(Config.Desktop, DesktopInfoBar);
    private async void CaptureRig_Click(object sender, RoutedEventArgs e) => await CaptureAsync(Config.SimRig, RigInfoBar);
    private void ChooseDisplays_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _displayEditingMode = tag == "desktop" ? WorkspaceMode.Desktop : WorkspaceMode.SimRig;
        // Both workspace switches are live on this page regardless of which button opened it,
        // so the banner must not claim otherwise - it used to say "Editing Desktop mode" while
        // editing either, and the field it set was never read again.
        DisplayEditorInfoBar.Title = "Choose which displays belong to each workspace";
        DisplayEditorInfoBar.Message = $"Select a monitor tile, then use its Desktop and Sim rig switches. You came here from {(_displayEditingMode == WorkspaceMode.Desktop ? "Desktop" : "Sim rig")} mode.";
        DisplayEditorInfoBar.IsOpen = true; SelectNavigation("displays");
    }
    private async Task CaptureAsync(ModeConfig mode, InfoBar info)
    {
        try
        {
            // Off the UI thread: QueryDisplayConfig plus a DisplayConfigGetDeviceInfo per path per
            // name is synchronous native work, and RefreshDisplaysAsync already offloaded it.
            var captured = await Task.Run(_host.Displays.CaptureCurrent);
            _liveDisplays = captured;
            // A parked monitor is attached only so that it does not drop out. Its parking spot is
            // not part of any layout, so it is left out of what is saved.
            var leftOut = new List<string>();
            foreach (var parked in captured.Paths.Where(path => DisplayPowerPolicy.IsParked(Config.General, path.MonitorDevicePath)).ToList())
            {
                if (captured.Paths.Count <= 1) break;
                captured = _host.Displays.SetDisplayIncluded(captured, captured, parked, false);
                leftOut.Add(parked.FriendlyName);
            }
            mode.Displays = captured; await _host.Configuration.SaveAsync();
            RenderDisplays(); UpdateSummaries();
            ShowInfo(info, "Display profile captured",
                $"Saved {captured.Paths.Count} active displays with exact topology and timing." +
                (leftOut.Count > 0 ? $" Left out {string.Join(", ", leftOut)}, which is parked." : ""), InfoBarSeverity.Success);
        }
        catch (Exception ex) { Report(info, "Display capture failed", ex); }
    }

    private async void RefreshDisplays_Click(object sender, RoutedEventArgs e) => await RefreshDisplaysAsync(true);
    private async Task RefreshDisplaysAsync(bool announce = false)
    {
        try
        {
            _liveDisplays = await Task.Run(_host.Displays.CaptureCurrent); RenderDisplays();
            if (announce) ShowInfo(DisplayInfoBar, "Displays refreshed", $"Found {_liveDisplays.Paths.Count} active displays.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { Report(DisplayInfoBar, "Display refresh failed", ex); }
    }
    private void MonitorCanvas_SizeChanged(object sender, SizeChangedEventArgs e) { if (_liveDisplays is not null) RenderDisplays(); }
    private void RenderDisplays()
    {
        MonitorCanvas.Children.Clear();
        if (_liveDisplays is null) return;
        var views = _liveDisplays.Paths.Select((path, index) => (path, index, mode: GetSourceMode(_liveDisplays, path))).Where(x => x.mode is not null).ToList();
        if (views.Count == 0) return;
        var minX = views.Min(v => v.mode!.PositionX); var minY = views.Min(v => v.mode!.PositionY);
        var maxX = views.Max(v => v.mode!.PositionX + (int)v.mode.Width); var maxY = views.Max(v => v.mode!.PositionY + (int)v.mode.Height);
        var topologyWidth = Math.Max(1, maxX - minX); var topologyHeight = Math.Max(1, maxY - minY);
        var availableWidth = Math.Max(1, MonitorCanvas.ActualWidth); var availableHeight = Math.Max(1, MonitorCanvas.ActualHeight);
        var scale = Math.Min(availableWidth / topologyWidth, availableHeight / topologyHeight);
        var offsetX = (availableWidth - topologyWidth * scale) / 2; var offsetY = (availableHeight - topologyHeight * scale) / 2;
        var selectedPath = _selectedDisplay is null ? null : views.Select(v => v.path).FirstOrDefault(path => IsSameDisplay(path, _selectedDisplay));
        _selectedDisplay = selectedPath ?? views[0].path;
        foreach (var view in views)
        {
            var mode = view.mode!;
            var isSelected = IsSameDisplay(_selectedDisplay, view.path);
            var number = DisplayNumber(view.path, view.index + 1);
            var tile = new Button
            {
                Tag = view.path,
                Content = new TextBlock { Text = number, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                Width = Math.Max(1, mode.Width * scale), Height = Math.Max(1, mode.Height * scale),
                MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(isSelected ? 2 : 0),
                BorderBrush = (Brush)Application.Current.Resources[isSelected ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources[isSelected ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush"]
            };
            tile.Click += DisplayTile_Click;
            ToolTipService.SetToolTip(tile, $"Display {number}: {view.path.FriendlyName}\n{mode.Width} × {mode.Height} • {RefreshText(view.path)}");
            Canvas.SetLeft(tile, offsetX + (mode.PositionX - minX) * scale); Canvas.SetTop(tile, offsetY + (mode.PositionY - minY) * scale);
            Canvas.SetZIndex(tile, isSelected ? 1 : 0); MonitorCanvas.Children.Add(tile);
        }
        SelectDisplay(_selectedDisplay);
    }
    private void DisplayTile_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: DisplayPathRecord path }) { SelectDisplay(path); RenderDisplays(); } }
    private void SelectDisplay(DisplayPathRecord path)
    {
        _selectedDisplay = path; var mode = _liveDisplays is null ? null : GetSourceMode(_liveDisplays, path);
        var number = DisplayNumber(path, 1); var primary = mode is { PositionX: 0, PositionY: 0 } ? " • Primary display" : "";
        SelectedDisplayCard.Header = $"Display {number} • {path.FriendlyName}";
        SelectedDisplayCard.Description = mode is null ? "Currently unavailable" : $"{mode.Width} × {mode.Height} • {RefreshText(path)}{primary}";
        if (DisplayPowerPolicy.IsParked(Config.General, path.MonitorDevicePath)) SelectedDisplayCard.Description += "  •  Parked";
        DisplayIdentityText.Text = $"{path.SourceName}\n{path.MonitorDevicePath}".Trim();
        var switchedWithModes = DisplayPowerPolicy.IsSwitchedWithModes(Config.General, path.MonitorDevicePath);
        if (path.IsIndirect && !switchedWithModes)
        {
            // Explain why this display does not follow the mode switch, and how to change that.
            SelectedDisplayCard.Description += "  \u2022  Left as it is";
            DisplayIdentityText.Text += "\n\n" + path.IndirectReason +
                "\n\nPitlane leaves this display on or off as it is when switching modes. Turn on Switch with modes to change that.";
        }
        _loadingValues = true;
        DisplayDesktopEnabled.IsOn = ContainsDisplay(Config.Desktop.Displays, path); DisplayRigEnabled.IsOn = ContainsDisplay(Config.SimRig.Displays, path);
        // A mode never leaves an indirect display out, so there is nothing to park.
        DisplayPowerOffToggle.IsEnabled = !path.IsIndirect && DisplayPowerPolicy.IsPlausibleMonitorPath(path.MonitorDevicePath);
        DisplayPowerOffToggle.IsOn = DisplayPowerPolicy.IsListed(Config.General, path.MonitorDevicePath);
        DisplaySwitchedCard.Visibility = path.IsIndirect ? Visibility.Visible : Visibility.Collapsed;
        DisplaySwitchedToggle.IsOn = switchedWithModes;
        _loadingValues = false;
        DisplayResolutionCombo.Items.Clear(); DisplayRefreshCombo.Items.Clear(); DisplayOrientationCombo.Items.Clear();
        DisplayResolutionCombo.Items.Add(mode is null ? "Unavailable" : $"{mode.Width} × {mode.Height}"); DisplayResolutionCombo.SelectedIndex = 0;
        DisplayRefreshCombo.Items.Add(RefreshText(path)); DisplayRefreshCombo.SelectedIndex = 0;
        var orientation = path.Rotation switch { 2 => "Portrait (90°)", 3 => "Landscape (flipped)", 4 => "Portrait (flipped)", _ => "Landscape" };
        var primaryText = mode is { PositionX: 0, PositionY: 0 } ? "  •  Primary" : ""; DisplayOrientationCombo.Items.Add(orientation + primaryText); DisplayOrientationCombo.SelectedIndex = 0;
    }
    private async void DisplayModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingValues || _selectedDisplay is null || _liveDisplays is null || sender is not ToggleSwitch toggle) return;
        var desktop = ReferenceEquals(toggle, DisplayDesktopEnabled); var mode = desktop ? Config.Desktop : Config.SimRig;
        var modeName = desktop ? "Desktop" : "Sim rig"; var currentlyIncluded = ContainsDisplay(mode.Displays, _selectedDisplay);
        if (!toggle.IsOn && currentlyIncluded && mode.Displays?.Paths.Count == 1)
        {
            _loadingValues = true; toggle.IsOn = true; _loadingValues = false;
            ShowInfo(DisplayInfoBar, "At least one display is required", $"{modeName} mode must keep one active display.", InfoBarSeverity.Warning); return;
        }
        try
        {
            mode.Displays = _host.Displays.SetDisplayIncluded(mode.Displays, _liveDisplays, _selectedDisplay, toggle.IsOn);
            await _host.Configuration.SaveAsync(); UpdateSummaries();
            ShowInfo(DisplayInfoBar, $"{modeName} mode updated", $"Display {DisplayNumber(_selectedDisplay, 1)} is now {(toggle.IsOn ? "included in" : "excluded from")} {modeName} mode.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _loadingValues = true; toggle.IsOn = currentlyIncluded; _loadingValues = false; Report(DisplayInfoBar, "Display profile update failed", ex);
        }
    }
    private static bool IsSameDisplay(DisplayPathRecord left, DisplayPathRecord right) =>
        (!string.IsNullOrWhiteSpace(left.MonitorDevicePath) && left.MonitorDevicePath.Equals(right.MonitorDevicePath, StringComparison.OrdinalIgnoreCase)) ||
        left.TargetAdapter == right.TargetAdapter && left.TargetId == right.TargetId;
    private string DisplayNumber(DisplayPathRecord path, int fallback)
    {
        if (_liveDisplays is not null)
        {
            var ordered = _liveDisplays.Paths.OrderBy(p => SourceDisplayOrdinal(p.SourceName)).ToList();
            var index = ordered.FindIndex(candidate => IsSameDisplay(candidate, path));
            if (index >= 0) return (index + 1).ToString();
        }
        return fallback.ToString();
    }
    private static int SourceDisplayOrdinal(string sourceName)
    {
        const string marker = "DISPLAY"; var index = sourceName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 && int.TryParse(sourceName[(index + marker.Length)..], out var number) ? number : int.MaxValue;
    }
    private static DisplayModeRecord? GetSourceMode(DisplaySnapshot snapshot, DisplayPathRecord path) => path.SourceModeInfoIdx < snapshot.Modes.Count ? snapshot.Modes[(int)path.SourceModeInfoIdx] : snapshot.Modes.FirstOrDefault(m => m.InfoType == 1 && m.Adapter == path.SourceAdapter && m.Id == path.SourceId);
    private static bool ContainsDisplay(DisplaySnapshot? snapshot, DisplayPathRecord path) => snapshot?.Paths.Any(p => (!string.IsNullOrWhiteSpace(path.MonitorDevicePath) && p.MonitorDevicePath.Equals(path.MonitorDevicePath, StringComparison.OrdinalIgnoreCase)) || p.TargetAdapter == path.TargetAdapter && p.TargetId == path.TargetId) == true;
    private static string RefreshText(DisplayPathRecord path) => path.RefreshDenominator == 0 ? "Unknown Hz" : $"{(double)path.RefreshNumerator / path.RefreshDenominator:0.##} Hz";

    private async void RefreshUsb_Click(object sender, RoutedEventArgs e) => await RefreshUsbAsync(true);

    /// <summary>
    /// Opt-in deep scan. This opens every hub and issues several hundred synchronous IOCTLs
    /// across the bus, including control transfers that wake devices out of selective suspend.
    /// It used to happen on every refresh; it is now something the user asks for.
    /// </summary>
    private async void DeepScanUsb_Click(object sender, RoutedEventArgs e) => await RefreshUsbAsync(true, deepScan: true);

    private async Task RefreshUsbAsync(bool announce = false, bool deepScan = false)
    {
        try
        {
            _usbTopology = await Task.Run(() => new UsbTopologyService(_host.Usb).Enumerate(deepScan)); _usbRows.Clear(); _hubDisableOverrides.Clear();
            AddUnavailableUsbRules(_usbTopology);
            foreach (var node in _usbTopology.Flatten())
            {
                var rule = UsbTopologyService.MatchRule(Config.UsbRules, node);
                var row = new UsbTopologyRow { Node = node, DesktopIndex = (int)(rule?.Desktop ?? DeviceModeAction.DoNotChange), SimRigIndex = (int)(rule?.SimRig ?? DeviceModeAction.DoNotChange), TechnicalVisibility = UsbTechnicalToggle.IsOn ? Visibility.Visible : Visibility.Collapsed };
                row.PolicyChanged += _ => UpdateUsbEffectivePolicies(); _usbRows.Add(row); if (rule?.DisableHubItself == true) _hubDisableOverrides[node.IdentityKey] = true;
            }
            RebuildUsbTree(); UpdateUsbEffectivePolicies();
            var physical = _usbTopology.Flatten().Count(node => node.Kind is UsbTopologyNodeKind.Hub or UsbTopologyNodeKind.Device);
            UsbCountText.Text = $"{physical} physical devices across {_usbTopology.Flatten().Count(node => node.Kind == UsbTopologyNodeKind.HostController)} host controllers";
            if (announce) ShowInfo(UsbInfoBar, deepScan ? "Deep scan complete" : "Topology refreshed",
                deepScan
                    ? $"Queried {physical} physical USB devices for names, speeds and port numbers."
                    : $"Mapped {physical} physical USB devices. Use Deep scan for port numbers, speeds and product names.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex) { Report(UsbInfoBar, "USB refresh failed", ex); }
    }
    private void UsbSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => RebuildUsbTree();
    private void AddUnavailableUsbRules(UsbTopology topology)
    {
        var missingRules = Config.UsbRules.Where(rule => !topology.Flatten().Any(node => ReferenceEquals(UsbTopologyService.MatchRule([rule], node), rule))).ToList();
        if (missingRules.Count == 0 || topology.Roots.Count == 0) return;
        var computer = topology.Roots[0];
        var group = new UsbTopologyNode { Kind = UsbTopologyNodeKind.Interfaces, IdentityKey = "saved-unavailable", Name = "Saved branches currently unavailable", IsPresent = true, IsEnabled = true, Parent = computer };
        computer.Children.Add(group);
        foreach (var rule in missingRules)
        {
            var node = new UsbTopologyNode { Kind = rule.IsSubtree ? UsbTopologyNodeKind.Hub : UsbTopologyNodeKind.Missing, IdentityKey = rule.IdentityKey, Name = rule.FriendlyName + " (unavailable)", InstanceId = rule.InstanceId, ContainerId = rule.ContainerId, Vid = rule.Vid, Pid = rule.Pid, Serial = rule.Serial, LocationPath = rule.LocationPath, PortChain = rule.PortChain, IsPresent = false, IsEnabled = false, Parent = group };
            group.Children.Add(node);
        }
    }
    private void UsbTechnicalToggle_Toggled(object sender, RoutedEventArgs e) { foreach (var row in _usbRows) row.TechnicalVisibility = UsbTechnicalToggle.IsOn ? Visibility.Visible : Visibility.Collapsed; }
    private void RebuildUsbTree()
    {
        UsbTree.RootNodes.Clear(); if (_usbTopology is null) return; var query = UsbSearch.Text?.Trim() ?? "";
        foreach (var root in _usbTopology.Roots) { var tree = BuildUsbTreeNode(root, query); if (tree is not null) { UsbTree.RootNodes.Add(tree); tree.IsExpanded = true; } }
    }
    private TreeViewNode? BuildUsbTreeNode(UsbTopologyNode node, string query)
    {
        var children = node.Children.Select(child => BuildUsbTreeNode(child, query)).Where(child => child is not null).Cast<TreeViewNode>().ToList();
        var matches = string.IsNullOrWhiteSpace(query) || new[] { node.Name, node.Manufacturer, node.InstanceId, node.Vid, node.Pid, node.PortChain }.Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (!matches && children.Count == 0) return null; var row = _usbRows.First(item => ReferenceEquals(item.Node, node)); var result = new TreeViewNode { Content = row, IsExpanded = !string.IsNullOrWhiteSpace(query) };
        foreach (var child in children) result.Children.Add(child); return result;
    }
    private void ExpandUsb_Click(object sender, RoutedEventArgs e) { foreach (var root in UsbTree.RootNodes) SetUsbExpanded(root, true); }
    private void CollapseUsb_Click(object sender, RoutedEventArgs e) { foreach (var root in UsbTree.RootNodes) SetUsbExpanded(root, false); }
    private static void SetUsbExpanded(TreeViewNode node, bool expanded) { node.IsExpanded = expanded; foreach (var child in node.Children) SetUsbExpanded(child, expanded); }
    private void UsbTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        var row = args.InvokedItem as UsbTopologyRow ?? (args.InvokedItem as TreeViewNode)?.Content as UsbTopologyRow;
        if (row is not null) { _selectedUsbRow = row; ShowUsbDetails(row); }
    }
    private void ShowUsbDetails(UsbTopologyRow row)
    {
        var node = row.Node; UsbDetailsExpander.Header = node.Name; UsbDetailsExpander.Description = row.Detail;
        UsbDetailsText.Text = $"Name: {node.Name}\nManufacturer: {node.Manufacturer}\nVID / PID: {node.Vid} / {node.Pid}\nSerial: {node.Serial}\nInstance ID: {node.InstanceId}\nContainer ID: {node.ContainerId}\nParent: {node.Parent?.Name}\nPhysical port chain: {node.PortChain}\nUSB speed: {node.Speed}\nLocation: {node.LocationPath}\nClass: {node.DeviceClass}\nStatus: {(node.IsPresent ? node.IsEnabled ? "Connected and enabled" : "Present but disabled" : "Missing")}";
        // Disabling a hub disconnects its whole branch, and the resolver deliberately does NOT
        // operate the children in that case - so a keyboard or mouse downstream is collateral
        // rather than protected. If the branch holds anything protected the toggle is disabled
        // outright and the description says what is in the way, rather than merely warning.
        var protectedNames = node.CriticalDescendantNames;
        var externalHub = node.Kind == UsbTopologyNodeKind.Hub && !node.IsSelfCritical && !node.HasCriticalDescendant;
        _loadingValues = true;
        DisableHubItselfToggle.IsEnabled = externalHub;
        DisableHubItselfToggle.IsOn = externalHub && _hubDisableOverrides.GetValueOrDefault(node.IdentityKey);
        _loadingValues = false;
        if (node.Kind == UsbTopologyNodeKind.Hub && !externalHub && protectedNames.Count > 0)
            UsbDetailsExpander.Description = "Cannot disable this hub itself: it carries " + string.Join(", ", protectedNames.Take(4));
        var affected = node.Flatten().Where(child => child.Kind is UsbTopologyNodeKind.Hub or UsbTopologyNodeKind.Device).ToList(); UsbAffectedText.Text = $"{affected.Count} physical device{(affected.Count == 1 ? "" : "s")}: " + string.Join(", ", affected.Take(8).Select(child => child.Name)) + (affected.Count > 8 ? $" and {affected.Count - 8} more" : "");
    }
    private void DisableHubItselfToggle_Toggled(object sender, RoutedEventArgs e) { if (_loadingValues || _selectedUsbRow is null) return; _hubDisableOverrides[_selectedUsbRow.Node.IdentityKey] = DisableHubItselfToggle.IsOn; }
    private void UpdateUsbEffectivePolicies()
    {
        foreach (var row in _usbRows) { var desktop = EffectiveUsbAction(row.Node, true); var sim = EffectiveUsbAction(row.Node, false); row.EffectivePolicy = desktop == DeviceModeAction.DoNotChange && sim == DeviceModeAction.DoNotChange ? "" : $"Desktop: {EffectiveLabel(row.Desktop, desktop)} · Sim rig: {EffectiveLabel(row.SimRig, sim)}"; }
    }
    private DeviceModeAction EffectiveUsbAction(UsbTopologyNode node, bool desktop)
    {
        for (var current = node; current is not null; current = current.Parent) { var row = _usbRows.FirstOrDefault(item => ReferenceEquals(item.Node, current)); var action = row is null ? DeviceModeAction.DoNotChange : desktop ? row.Desktop : row.SimRig; if (action != DeviceModeAction.DoNotChange) return action; } return DeviceModeAction.DoNotChange;
    }
    private static string EffectiveLabel(DeviceModeAction explicitAction, DeviceModeAction effective) => explicitAction == DeviceModeAction.DoNotChange ? effective == DeviceModeAction.DoNotChange ? "Inherit" : $"Inherited {effective}" : effective.ToString();
    private async void SaveUsb_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await SaveUsbRulesWithSafetyPromptAsync()) return; await _host.Configuration.SaveAsync(); UpdateSummaries(); ShowInfo(UsbInfoBar, "Topology policies saved", "Subtree policies are ready for both workspaces.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { Report(UsbInfoBar, "Device rules could not be saved", ex); }
    }
    private async Task<bool> SaveUsbRulesWithSafetyPromptAsync()
    {
        foreach (var row in _usbRows.Where(r => r.Node.IsCritical && (r.Desktop == DeviceModeAction.Disabled || r.SimRig == DeviceModeAction.Disabled)))
        {
            var affected = row.Node.CriticalDescendantNames;
            var detail = affected.Count > 0
                ? "It carries: " + string.Join(", ", affected.Take(6)) + "."
                : "This device is itself protected.";
            var result = await ConfirmAsync("Protected USB branch",
                $"{row.Name} is protected because disabling it can disconnect system input. {detail}\n\n" +
                "Pitlane will still refuse to disable the protected devices themselves, but the rest of the branch will go.",
                "Allow rule");
            // Declining used to abandon the whole save with no message, silently discarding every
            // unsaved edit on the page.
            if (!result)
            {
                ShowInfo(UsbInfoBar, "Nothing was saved", $"The rule for {row.Name} was not confirmed, so no policy changes were saved. Your edits are still on screen.", InfoBarSeverity.Informational);
                return false;
            }
        }
        Config.UsbRules = _usbRows.Where(r => r.CanConfigure && (r.Desktop != DeviceModeAction.DoNotChange || r.SimRig != DeviceModeAction.DoNotChange)).Select(r =>
        {
            var n = r.Node;
            var existing = UsbTopologyService.MatchRule(Config.UsbRules, n);
            var affected = n.Flatten().Where(child => child.Kind == UsbTopologyNodeKind.Device && !child.IsCritical).ToList();
            var targets = affected.Count > 0
                ? affected.Select(child => new UsbRuleTarget { InstanceId = child.InstanceId, FriendlyName = child.Name, IsCritical = child.IsCritical }).ToList()
                : existing?.LastKnownTargets ?? [];
            return new UsbRule { IdentityKey = n.IdentityKey, InstanceId = n.InstanceId, FriendlyName = n.Name.Replace(" (unavailable)", "", StringComparison.Ordinal), ContainerId = n.ContainerId, Vid = n.Vid, Pid = n.Pid, Serial = n.Serial, LocationPath = n.LocationPath, PortChain = n.PortChain, IsSubtree = n.Kind == UsbTopologyNodeKind.Hub || existing?.IsSubtree == true, DisableHubItself = _hubDisableOverrides.GetValueOrDefault(n.IdentityKey), Desktop = r.Desktop, SimRig = r.SimRig, // Recorded only when the device is protected AND the dialog above was confirmed - it
                // reached this point, so it was. Previously this was set to the NEGATION of
                // criticality, computed without reference to the dialog, and never read by anything.
                CriticalDeviceAcknowledged = n.IsCritical, LastKnownTargets = targets };
        }).ToList();
        return true;
    }

    private async void RefreshAudio_Click(object sender, RoutedEventArgs e) => await RefreshAudioAsync(true);
    private async Task RefreshAudioAsync(bool announce = false)
    {
        try
        {
            _audioEndpoints = await Task.Run(_host.Audio.Enumerate); var playback = _audioEndpoints.Where(x => x.IsPlayback).ToList(); var recording = _audioEndpoints.Where(x => !x.IsPlayback).ToList();
            ConfigureAudioCombo(DesktopPlayback, playback, Config.Desktop.Audio.PlaybackDefaultId); ConfigureAudioCombo(DesktopCommPlayback, playback, Config.Desktop.Audio.PlaybackCommunicationsId);
            ConfigureAudioCombo(DesktopRecording, recording, Config.Desktop.Audio.RecordingDefaultId); ConfigureAudioCombo(DesktopCommRecording, recording, Config.Desktop.Audio.RecordingCommunicationsId);
            ConfigureAudioCombo(RigPlayback, playback, Config.SimRig.Audio.PlaybackDefaultId); ConfigureAudioCombo(RigCommPlayback, playback, Config.SimRig.Audio.PlaybackCommunicationsId);
            ConfigureAudioCombo(RigRecording, recording, Config.SimRig.Audio.RecordingDefaultId); ConfigureAudioCombo(RigCommRecording, recording, Config.SimRig.Audio.RecordingCommunicationsId);
            AudioInventoryText.Text = $"{Math.Max(0, playback.Count - 1)} playback endpoints and {Math.Max(0, recording.Count - 1)} recording endpoints are available. Raw endpoint IDs remain hidden from the normal workflow.";
            if (announce) ShowInfo(AudioInfoBar, "Audio refreshed", AudioInventoryText.Text, InfoBarSeverity.Success);
        }
        catch (Exception ex) { Report(AudioInfoBar, "Audio refresh failed", ex); }
    }
    private static void ConfigureAudioCombo(ComboBox combo, IReadOnlyList<AudioEndpointInfo> endpoints, string selected)
    {
        var options = endpoints.ToList();
        // A saved endpoint whose device is currently absent used to fall back to index 0 ("Do not
        // change"), and the next save then wrote that empty id back - silently erasing the
        // user's choice because a device happened to be unplugged. Keep it as a placeholder
        // entry instead, so it round-trips.
        if (!string.IsNullOrWhiteSpace(selected) && !options.Any(option => option.Id == selected))
            options.Add(new AudioEndpointInfo { Id = selected, Name = "(saved device, not currently available)" });

        combo.ItemsSource = options; combo.DisplayMemberPath = nameof(AudioEndpointInfo.Name); combo.SelectedValuePath = nameof(AudioEndpointInfo.Id); combo.SelectedValue = selected; if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
    }
    private async void SaveAudio_Click(object sender, RoutedEventArgs e)
    {
        Config.Desktop.Audio.PlaybackDefaultId = AudioId(DesktopPlayback); Config.Desktop.Audio.PlaybackCommunicationsId = AudioId(DesktopCommPlayback); Config.Desktop.Audio.RecordingDefaultId = AudioId(DesktopRecording); Config.Desktop.Audio.RecordingCommunicationsId = AudioId(DesktopCommRecording);
        Config.SimRig.Audio.PlaybackDefaultId = AudioId(RigPlayback); Config.SimRig.Audio.PlaybackCommunicationsId = AudioId(RigCommPlayback); Config.SimRig.Audio.RecordingDefaultId = AudioId(RigRecording); Config.SimRig.Audio.RecordingCommunicationsId = AudioId(RigCommRecording);
        try { await _host.Configuration.SaveAsync(); UpdateSummaries(); ShowInfo(AudioInfoBar, "Audio settings saved", "Endpoint choices will be applied during the next transition.", InfoBarSeverity.Success); } catch (Exception ex) { Report(AudioInfoBar, "Audio settings could not be saved", ex); }
    }
    private static string AudioId(ComboBox combo) => (combo.SelectedItem as AudioEndpointInfo)?.Id ?? "";

    // ===== Home Assistant ==============================================================

    private void LoadIntegration()
    {
        var loading = _loadingValues;
        _loadingValues = true;
        IntegrationToggle.IsOn = Config.Integration.Enabled;
        _loadingValues = loading;
        HaHostText.Text = Config.Integration.Host;
        HaEntityText.Text = Config.Integration.Entity;
        HaTokenBox.Password = "";
        HaTokenBox.PlaceholderText = string.IsNullOrEmpty(Config.Integration.ProtectedToken) ? "Paste the token" : "Saved. Paste a new one to replace it";
    }

    private async void IntegrationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingValues) return;
        try
        {
            Config.Integration.Enabled = IntegrationToggle.IsOn;
            await _host.Configuration.SaveAsync();
            UpdateSummaries();
            await CheckIntegrationAsync();
        }
        catch (Exception ex) { Report(IntegrationInfoBar, "Home Assistant settings could not be saved", ex); }
    }

    private async void Integration_LostFocus(object sender, RoutedEventArgs e) => await CommitIntegrationAsync();

    private async void Integration_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await CommitIntegrationAsync();
    }

    /// <summary>Saves the three fields as they are and checks the connection. There is no save
    /// button; the switch above them decides whether a mode switch uses them.</summary>
    private async Task CommitIntegrationAsync()
    {
        try
        {
            var integration = Config.Integration;
            var host = HaHostText.Text.Trim();
            var entity = HaEntityText.Text.Trim().ToLowerInvariant();
            var token = HaTokenBox.Password.Trim();
            if (host == integration.Host && entity == integration.Entity && token.Length == 0) return;
            integration.Host = host;
            integration.Entity = entity;
            if (token.Length > 0) integration.ProtectedToken = HomeAssistantService.Protect(token);
            await _host.Configuration.SaveAsync();
            LoadIntegration();
            UpdateSummaries();
            await CheckIntegrationAsync();
        }
        catch (Exception ex) { Report(IntegrationInfoBar, "Home Assistant settings could not be saved", ex); }
    }

    private int _integrationCheck;

    /// <summary>Reads the entity from Home Assistant - never switches it - and says what it found.</summary>
    private async Task CheckIntegrationAsync()
    {
        var integration = Config.Integration;
        var check = ++_integrationCheck;
        if (!integration.Enabled)
        {
            SetIntegrationStatus(null, "Off. Switching leaves the rig's power alone.");
            return;
        }
        if (!integration.IsSetUp)
        {
            var missing = new[] { (integration.Host, "host"), (integration.ProtectedToken, "access token"), (integration.Entity, "entity") }
                .Where(field => string.IsNullOrWhiteSpace(field.Item1)).Select(field => field.Item2).ToList();
            SetIntegrationStatus(null, missing.Count == 3
                ? "Not set up yet. Until it is, switching leaves the rig's power alone."
                : $"Add the {(missing.Count == 1 ? missing[0] : string.Join(", ", missing.Take(missing.Count - 1)) + " and " + missing[^1])} to finish setting it up.");
            return;
        }
        SetIntegrationStatus(null, "Checking…");
        var status = await _host.HomeAssistant.CheckAsync(integration, CancellationToken.None);
        if (check != _integrationCheck) return;   // the fields changed again meanwhile
        SetIntegrationStatus(status.Connected, status.Message);
    }

    private void SetIntegrationStatus(bool? connected, string message)
    {
        IntegrationStatusIcon.Glyph = connected switch { true => "\uE73E", false => "\uE7BA", null => "" };
        IntegrationStatusIcon.Foreground = (Brush)Application.Current.Resources[connected switch
        {
            true => "SystemFillColorSuccessBrush",
            false => "SystemFillColorCriticalBrush",
            null => "TextFillColorSecondaryBrush"
        }];
        IntegrationStatusIcon.Visibility = connected is null ? Visibility.Collapsed : Visibility.Visible;
        IntegrationStatusText.Text = message;
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e) { if (_loadingValues) return; try { _host.SetStartWithWindows(StartupToggle.IsOn); ShowInfo(SettingsInfoBar, "Startup setting updated", StartupToggle.IsOn ? "Pitlane will launch when you sign in." : "Pitlane will not launch automatically.", InfoBarSeverity.Success); } catch (Exception ex) { Report(SettingsInfoBar, "Startup setting failed", ex); } }
    private void HotkeysToggle_Toggled(object sender, RoutedEventArgs e) { HotkeysExpander.IsExpanded = HotkeysToggle.IsOn; }
    private async void Delay_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_initialized || _loadingValues) return;
        ReadGeneralControls();
        // Every NumberBox tick used to write the whole config through one fixed temp path, so two
        // overlapping saves could throw IOException into an async void. Serialise them.
        try { await _saveGate.WaitAsync(); try { await _host.Configuration.SaveAsync(); } finally { _saveGate.Release(); } }
        catch (Exception ex) { Report(SettingsInfoBar, "Setting could not be saved", ex); }
    }

    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private async void UsbArmedToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingValues) return;
        try
        {
            if (UsbArmedToggle.IsOn)
            {
                var rules = Config.UsbRules.Count(rule => rule.Desktop != DeviceModeAction.DoNotChange || rule.SimRig != DeviceModeAction.DoNotChange);
                var confirmed = await ConfirmAsync("Arm USB device switching",
                    $"Mode switches will start enabling and disabling real devices. {rules} rule(s) are currently configured.\n\n" +
                    "Each change requires Windows administrator approval, can be refused by a driver, and may need a reboot to undo. " +
                    "Review the USB page first if you are not sure what these rules cover.",
                    "Arm");
                if (!confirmed) { _loadingValues = true; UsbArmedToggle.IsOn = false; _loadingValues = false; return; }
            }
            Config.General.UsbSwitchingArmed = UsbArmedToggle.IsOn;
            await _host.Configuration.SaveAsync();
            ShowInfo(SettingsInfoBar, UsbArmedToggle.IsOn ? "USB switching armed" : "USB switching disarmed",
                UsbArmedToggle.IsOn ? "Mode switches will now apply USB device rules." : "USB rules are kept but no device will be enabled or disabled.",
                UsbArmedToggle.IsOn ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception ex) { Report(SettingsInfoBar, "Setting could not be saved", ex); }
    }

    private async void DryRunToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingValues) return;
        try
        {
            Config.General.DryRunTransitions = DryRunToggle.IsOn;
            await _host.Configuration.SaveAsync();
            ShowInfo(SettingsInfoBar, DryRunToggle.IsOn ? "Dry run enabled" : "Dry run disabled",
                DryRunToggle.IsOn ? "Transitions will validate and log what they would do, and change nothing." : "Transitions will apply changes normally.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex) { Report(SettingsInfoBar, "Setting could not be saved", ex); }
    }

    // ===== Advanced ======================================================================

    /// <summary>The mode the last test ran, so Toggle alternates. Tests do not change the mode on
    /// record, so that cannot be what Toggle goes by after the first one.</summary>
    private WorkspaceMode? _lastTest;

    /// <summary>
    /// A real switch minus the display step, for trying linked apps, Home Assistant, audio and
    /// USB rules without the monitors rearranging. The mode Pitlane shows stays the one that is
    /// really active. Toggle alternates, starting from the mode that is not active.
    /// </summary>
    private async void TestSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var from = _lastTest ?? (Config.CurrentMode == WorkspaceMode.SimRig ? WorkspaceMode.SimRig : WorkspaceMode.Desktop);
        var target = tag switch
        {
            "simrig" => WorkspaceMode.SimRig,
            "desktop" => WorkspaceMode.Desktop,
            _ => from == WorkspaceMode.SimRig ? WorkspaceMode.Desktop : WorkspaceMode.SimRig
        };
        var name = target == WorkspaceMode.SimRig ? "Sim Rig" : "Desktop";
        Button[] buttons = [TestToggleButton, TestRigButton, TestDesktopButton];
        foreach (var button in buttons) button.IsEnabled = false;
        TestSwitchStatus.Visibility = Visibility.Visible;
        TestSwitchStatus.Text = $"Running the {name} steps without the displays…";
        var clock = Stopwatch.StartNew();
        try
        {
            var outcome = await _host.SwitchModeAsync(target, displays: false);
            if (outcome.Error is null) _lastTest = target;
            TestSwitchStatus.Text = TestSwitchReport(name, outcome, clock.Elapsed);
        }
        catch (Exception ex) { TestSwitchStatus.Text = $"The test switch stopped: {ex.Message}"; }
        finally { foreach (var button in buttons) button.IsEnabled = true; }
    }

    private string TestSwitchReport(string name, App.SwitchOutcome outcome, TimeSpan elapsed)
    {
        if (outcome.Error is not null) return $"The {name} test stopped: {outcome.Error}";
        var steps = outcome.Steps.Where(step => step.Name != ModeSwitchService.DisplaysLeftAlone).ToList();
        var problems = steps.Where(step => !step.Success).Select(step => $"{step.Name}: {step.Detail}").ToList();
        var took = $"{elapsed.TotalSeconds:0.0} s";
        if (Config.General.DryRunTransitions) return $"Dry run is on, so the {name} test changed nothing ({took}).";
        if (problems.Count > 0) return $"Ran the {name} steps in {took}, displays untouched, with problems. {string.Join(" ", problems)}";
        if (steps.Count == 0) return $"Ran the {name} steps in {took}, displays untouched. Nothing else is set up for {name}: no apps, Home Assistant, audio or USB rules.";
        return $"Ran the {name} steps in {took}, displays untouched. Done: {string.Join(", ", steps.Select(step => step.Name))}.";
    }

    private async void SaveGeneral_Click(object sender, RoutedEventArgs e)
    {
        try { ReadGeneralControls(); await _host.Configuration.SaveAsync(); _host.ConfigureHotkeys(); ShowInfo(SettingsInfoBar, "Settings saved", "General settings and global shortcuts are active.", InfoBarSeverity.Success); } catch (Exception ex) { Report(SettingsInfoBar, "Settings could not be saved", ex); }
    }
    private void ReadGeneralControls()
    {
        Config.General.HotkeysEnabled = HotkeysToggle.IsOn; Config.General.DesktopHotkey = DesktopHotkeyText.Text.Trim(); Config.General.SimRigHotkey = RigHotkeyText.Text.Trim();
        Config.General.HardwarePowerOnWaitSeconds = Number(RigPowerOnDelay); Config.General.UsbEnumerationWaitSeconds = Number(UsbEnumerationDelay); Config.General.HardwarePowerOffDelaySeconds = Number(DesktopPowerOffDelay);
    }
    private static int Number(NumberBox box) => double.IsNaN(box.Value) ? 0 : (int)Math.Clamp(box.Value, 0, 300);

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "Pitlane-config" }; picker.FileTypeChoices.Add("JSON configuration", [".json"]); InitializePicker(picker);
            var file = await picker.PickSaveFileAsync(); if (file is not null) { await _host.Configuration.ExportAsync(file.Path); ShowInfo(SettingsInfoBar, "Configuration exported", file.Path, InfoBarSeverity.Success); }
        }
        catch (Exception ex) { Report(SettingsInfoBar, "Export failed", ex); }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); InitializePicker(picker); var file = await picker.PickSingleFileAsync(); if (file is null) return;
            var findings = await _host.Configuration.ImportAsync(file.Path);
            _loadingValues = true; LoadConfigIntoControls(); _loadingValues = false;
            await RefreshUsbAsync(); await RefreshAudioAsync(); await RefreshDisplaysAsync(); UpdateSummaries();
            // An imported file never arrives armed, and the sanitizer may have dropped or neutered
            // rules. Say so rather than presenting the import as a clean success.
            if (findings.Count == 0)
                ShowInfo(SettingsInfoBar, "Configuration imported", "The imported workspace settings are active. USB switching is disarmed until you arm it.", InfoBarSeverity.Success);
            else
                ShowInfo(SettingsInfoBar, $"Imported with {findings.Count} change(s)",
                    string.Join("  •  ", findings.Select(finding => finding.Detail)) + "  USB switching is disarmed until you arm it.",
                    InfoBarSeverity.Warning);
        }
        catch (Exception ex) { Report(SettingsInfoBar, "Import failed", ex); }
    }
    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Reset Pitlane?", "This removes captured display profiles, device rules, audio choices, integration settings, and shortcuts.", "Reset")) return;
        try { await _host.Configuration.ResetAsync(); _loadingValues = true; LoadConfigIntoControls(); _loadingValues = false; await RefreshUsbAsync(); await RefreshAudioAsync(); UpdateSummaries(); ShowInfo(SettingsInfoBar, "Pitlane reset", "Default settings have been restored.", InfoBarSeverity.Success); } catch (Exception ex) { Report(SettingsInfoBar, "Reset failed", ex); }
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(_host.Log.LogDirectory) { UseShellExecute = true });
    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

    private void UpdateSummaries()
    {
        DesktopDisplayCard.Description = SnapshotDescription(Config.Desktop.Displays); RigDisplayCard.Description = SnapshotDescription(Config.SimRig.Displays);
        RenderOverview();
        RigPowerCard.Description = Config.Integration.IsActive ? $"Home Assistant turns {Config.Integration.Entity} on and off" : !Config.Integration.Enabled ? "Home Assistant control is off" : "Not set up. Integrations has the three fields it needs";
        var desktopRules = Config.UsbRules.Count(r => r.Desktop != DeviceModeAction.DoNotChange); var rigRules = Config.UsbRules.Count(r => r.SimRig != DeviceModeAction.DoNotChange);
        DesktopUsbCard.Description = desktopRules == 0 ? "No device rules" : $"{desktopRules} device rules configured"; RigUsbCard.Description = rigRules == 0 ? "No device rules" : $"{rigRules} device rules configured";
        DesktopAudioCard.Description = AudioDescription(Config.Desktop.Audio); RigAudioCard.Description = AudioDescription(Config.SimRig.Audio);
        DesktopAppsCard.Description = AppsDescription(Config.Desktop); RigAppsCard.Description = AppsDescription(Config.SimRig);
    }
    private static string SnapshotDescription(DisplaySnapshot? snapshot) => snapshot is null ? "No profile captured" : $"{snapshot.Paths.Count} displays  •  Captured {snapshot.CapturedUtc.ToLocalTime():g}";
    private static string AppsDescription(ModeConfig mode) =>
        mode.LinkedApps.Count == 0 ? "No apps linked" : string.Join(", ", mode.LinkedApps.Select(app => app.StartWithMode ? app.Name : app.Name + " (not started)"));
    private static string AudioDescription(AudioSelection audio) => new[] { audio.PlaybackDefaultId, audio.PlaybackCommunicationsId, audio.RecordingDefaultId, audio.RecordingCommunicationsId }.Any(x => !string.IsNullOrWhiteSpace(x)) ? "Custom endpoints configured" : "Do not change";

    private async Task<bool> ConfirmAsync(string title, string message, string confirm)
    {
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = title, Content = message, PrimaryButtonText = confirm, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    private void Report(InfoBar bar, string title, Exception ex) { _host.Log.Error(title, ex); ShowInfo(bar, title, ex.Message, InfoBarSeverity.Error); }
    private static void ShowInfo(InfoBar bar, string title, string message, InfoBarSeverity severity) { bar.Title = title; bar.Message = message; bar.Severity = severity; bar.IsOpen = true; }
    private void Enqueue(Action action) => RootGrid.DispatcherQueue.TryEnqueue(() => action());
    private void CenterWindow() { var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary); if (area is null) return; var x = area.WorkArea.X + (area.WorkArea.Width - _appWindow.Size.Width) / 2; var y = area.WorkArea.Y + (area.WorkArea.Height - _appWindow.Size.Height) / 2; _appWindow.Move(new Windows.Graphics.PointInt32(Math.Max(area.WorkArea.X, x), Math.Max(area.WorkArea.Y, y))); }
    private void Hide() => ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), 0);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
