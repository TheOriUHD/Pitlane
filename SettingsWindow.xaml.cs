using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SimRigMode.Models;
using SimRigMode.Services;
using SimRigMode.UI;
using Windows.Storage.Pickers;

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
        Title = "SimRigMode Settings";
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

    internal void InitializeHidden()
    {
        _appWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
        Activate();
        RootGrid.DispatcherQueue.TryEnqueue(() => { Hide(); CenterWindow(); });
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
        UpdateSummaries();
    }

    private void LoadConfigIntoControls()
    {
        StartupToggle.IsOn = _host.StartWithWindows.IsEnabled;
        HotkeysToggle.IsOn = Config.General.HotkeysEnabled;
        HotkeysExpander.IsExpanded = Config.General.HotkeysEnabled;
        DesktopHotkeyText.Text = Config.General.DesktopHotkey;
        RigHotkeyText.Text = Config.General.SimRigHotkey;
        RigPowerOnDelay.Value = Config.General.HardwarePowerOnWaitSeconds;
        UsbEnumerationDelay.Value = Config.General.UsbEnumerationWaitSeconds;
        DesktopPowerOffDelay.Value = Config.General.HardwarePowerOffDelaySeconds;
        IntegrationToggle.IsOn = Config.Integration.Enabled;
        IntegrationExpander.Visibility = Config.Integration.Enabled ? Visibility.Visible : Visibility.Collapsed;
        IntegrationActions.Visibility = Config.Integration.Enabled ? Visibility.Visible : Visibility.Collapsed;
        OnWebhookText.Text = Config.Integration.OnWebhookUrl;
        OffWebhookText.Text = Config.Integration.OffWebhookUrl;
        HaBaseUrlText.Text = Config.Integration.BaseUrl;
        HaEntityText.Text = Config.Integration.EntityId;
        VersionText.Text = $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}  •  Windows App SDK 2.4  •  x64";
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        SetPage(OverviewPage, tag == "overview"); SetPage(DesktopPage, tag == "desktop"); SetPage(SimRigPage, tag == "simrig");
        SetPage(DisplaysPage, tag == "displays"); SetPage(UsbPage, tag == "usb"); SetPage(AudioPage, tag == "audio");
        SetPage(IntegrationsPage, tag == "integrations"); SetPage(SettingsPage, tag == "settings");
    }
    private void PageHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var paneWidth = Navigation.DisplayMode == NavigationViewDisplayMode.Expanded ? Navigation.OpenPaneLength : 0;
        var width = Math.Min(1050, Math.Max(420, e.NewSize.Width - paneWidth - 64));
        foreach (var viewer in new[] { OverviewPage, DesktopPage, SimRigPage, DisplaysPage, AudioPage, IntegrationsPage, SettingsPage })
            if (viewer.Content is StackPanel panel) panel.Width = width;
    }
    private static void SetPage(UIElement page, bool visible) => page.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    private void NavigateButton_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string tag }) SelectNavigation(tag); }
    private void SelectNavigation(string tag)
    {
        foreach (var item in Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>())
            if (item.Tag as string == tag) { Navigation.SelectedItem = item; return; }
    }

    private async void DesktopSwitch_Click(object sender, RoutedEventArgs e) => await _host.SwitchModeAsync(WorkspaceMode.Desktop);
    private async void RigSwitch_Click(object sender, RoutedEventArgs e) => await _host.SwitchModeAsync(WorkspaceMode.SimRig);
    private void CancelTransition_Click(object sender, RoutedEventArgs e) { _host.CancelTransition(); CancelTransitionButton.IsEnabled = false; TransitionTitle.Text = "Cancelling at the next safe point…"; }

    public void BeginTransition(WorkspaceMode target) => Enqueue(() =>
    {
        TransitionPanel.Visibility = Visibility.Visible; TransitionSteps.Items.Clear(); TransitionProgress.IsIndeterminate = true;
        TransitionTitle.Text = $"Switching to {(target == WorkspaceMode.Desktop ? "Desktop" : "Sim rig")} mode";
        CancelTransitionButton.IsEnabled = true; DesktopSwitchButton.IsEnabled = RigSwitchButton.IsEnabled = false;
        OverviewInfoBar.IsOpen = false;
    });
    public void EndTransition(WorkspaceMode mode, IReadOnlyList<TransitionStep> results) => Enqueue(() =>
    {
        TransitionProgress.IsIndeterminate = false; TransitionProgress.Value = 100; CancelTransitionButton.IsEnabled = false;
        DesktopSwitchButton.IsEnabled = RigSwitchButton.IsEnabled = true;
        var warnings = results.Where(x => !x.Success).ToList();
        ShowInfo(OverviewInfoBar, warnings.Count == 0 ? "Workspace switched" : "Workspace switched with warnings", warnings.Count == 0 ? $"{(mode == WorkspaceMode.Desktop ? "Desktop" : "Sim rig")} mode is active." : string.Join("\n", warnings.Select(x => $"{x.Name}: {x.Detail}")), warnings.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        UpdateSummaries();
    });
    public void TransitionFailed(string message) => Enqueue(() =>
    {
        TransitionProgress.IsIndeterminate = false; TransitionPanel.Visibility = Visibility.Collapsed; DesktopSwitchButton.IsEnabled = RigSwitchButton.IsEnabled = true;
        ShowInfo(OverviewInfoBar, "Mode switch stopped", message, InfoBarSeverity.Error);
    });
    private void OnStepCompleted(TransitionStep step) => Enqueue(() => TransitionSteps.Items.Add($"{(step.Success ? "✓" : "⚠")}  {step.Name}{(step.Success ? "" : " — " + step.Detail)}"));
    private void OnLogEntry(string line) => Enqueue(() => { if (DiagnosticsText.Text.Length > 40000) DiagnosticsText.Text = DiagnosticsText.Text[^30000..]; DiagnosticsText.Text += line + Environment.NewLine; });

    private async void CaptureDesktop_Click(object sender, RoutedEventArgs e) => await CaptureAsync(Config.Desktop, DesktopInfoBar);
    private async void CaptureRig_Click(object sender, RoutedEventArgs e) => await CaptureAsync(Config.SimRig, RigInfoBar);
    private void ChooseDisplays_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _displayEditingMode = tag == "desktop" ? WorkspaceMode.Desktop : WorkspaceMode.SimRig;
        DisplayEditorInfoBar.Title = $"Editing {(_displayEditingMode == WorkspaceMode.Desktop ? "Desktop" : "Sim rig")} mode";
        DisplayEditorInfoBar.Message = "Select a monitor tile, then turn its workspace switch on or off.";
        DisplayEditorInfoBar.IsOpen = true; SelectNavigation("displays");
    }
    private async Task CaptureAsync(ModeConfig mode, InfoBar info)
    {
        try
        {
            mode.Displays = _host.Displays.CaptureCurrent(); await _host.Configuration.SaveAsync(); _liveDisplays = mode.Displays;
            RenderDisplays(); UpdateSummaries(); ShowInfo(info, "Display profile captured", $"Saved {mode.Displays.Paths.Count} active displays with exact topology and timing.", InfoBarSeverity.Success);
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
        DisplayIdentityText.Text = $"{path.SourceName}\n{path.MonitorDevicePath}".Trim();
        _loadingValues = true;
        DisplayDesktopEnabled.IsOn = ContainsDisplay(Config.Desktop.Displays, path); DisplayRigEnabled.IsOn = ContainsDisplay(Config.SimRig.Displays, path);
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
    private async Task RefreshUsbAsync(bool announce = false)
    {
        try
        {
            _usbTopology = await Task.Run(() => new UsbTopologyService(_host.Usb).Enumerate()); _usbRows.Clear(); _hubDisableOverrides.Clear();
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
            if (announce) ShowInfo(UsbInfoBar, "Topology refreshed", $"Mapped {physical} physical USB devices through their real hub and port chains.", InfoBarSeverity.Success);
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
        var externalHub = node.Kind == UsbTopologyNodeKind.Hub && !node.IsCritical; _loadingValues = true; DisableHubItselfToggle.IsEnabled = externalHub; DisableHubItselfToggle.IsOn = _hubDisableOverrides.GetValueOrDefault(node.IdentityKey); _loadingValues = false;
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
        foreach (var row in _usbRows.Where(r => r.Node.Flatten().Any(node => node.IsCritical) && (r.Desktop == DeviceModeAction.Disabled || r.SimRig == DeviceModeAction.Disabled)))
        {
            var result = await ConfirmAsync("Protected USB branch", $"{row.Name} contains or is a protected controller, root hub, keyboard, or mouse. Disabling this branch can disconnect system input.", "Allow rule"); if (!result) return false;
        }
        Config.UsbRules = _usbRows.Where(r => r.CanConfigure && (r.Desktop != DeviceModeAction.DoNotChange || r.SimRig != DeviceModeAction.DoNotChange)).Select(r =>
        {
            var n = r.Node;
            var existing = UsbTopologyService.MatchRule(Config.UsbRules, n);
            var affected = n.Flatten().Where(child => child.Kind == UsbTopologyNodeKind.Device && !child.IsCritical).ToList();
            var targets = affected.Count > 0
                ? affected.Select(child => new UsbRuleTarget { InstanceId = child.InstanceId, FriendlyName = child.Name, IsCritical = child.IsCritical }).ToList()
                : existing?.LastKnownTargets ?? [];
            return new UsbRule { IdentityKey = n.IdentityKey, InstanceId = n.InstanceId, FriendlyName = n.Name.Replace(" (unavailable)", "", StringComparison.Ordinal), ContainerId = n.ContainerId, Vid = n.Vid, Pid = n.Pid, Serial = n.Serial, LocationPath = n.LocationPath, PortChain = n.PortChain, IsSubtree = n.Kind == UsbTopologyNodeKind.Hub || existing?.IsSubtree == true, DisableHubItself = _hubDisableOverrides.GetValueOrDefault(n.IdentityKey), Desktop = r.Desktop, SimRig = r.SimRig, CriticalDeviceAcknowledged = !n.IsCritical, LastKnownTargets = targets };
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
        combo.ItemsSource = endpoints; combo.DisplayMemberPath = nameof(AudioEndpointInfo.Name); combo.SelectedValuePath = nameof(AudioEndpointInfo.Id); combo.SelectedValue = selected; if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
    }
    private async void SaveAudio_Click(object sender, RoutedEventArgs e)
    {
        Config.Desktop.Audio.PlaybackDefaultId = AudioId(DesktopPlayback); Config.Desktop.Audio.PlaybackCommunicationsId = AudioId(DesktopCommPlayback); Config.Desktop.Audio.RecordingDefaultId = AudioId(DesktopRecording); Config.Desktop.Audio.RecordingCommunicationsId = AudioId(DesktopCommRecording);
        Config.SimRig.Audio.PlaybackDefaultId = AudioId(RigPlayback); Config.SimRig.Audio.PlaybackCommunicationsId = AudioId(RigCommPlayback); Config.SimRig.Audio.RecordingDefaultId = AudioId(RigRecording); Config.SimRig.Audio.RecordingCommunicationsId = AudioId(RigCommRecording);
        try { await _host.Configuration.SaveAsync(); UpdateSummaries(); ShowInfo(AudioInfoBar, "Audio settings saved", "Endpoint choices will be applied during the next transition.", InfoBarSeverity.Success); } catch (Exception ex) { Report(AudioInfoBar, "Audio settings could not be saved", ex); }
    }
    private static string AudioId(ComboBox combo) => (combo.SelectedItem as AudioEndpointInfo)?.Id ?? "";

    private void IntegrationToggle_Toggled(object sender, RoutedEventArgs e) { IntegrationExpander.Visibility = IntegrationToggle.IsOn ? Visibility.Visible : Visibility.Collapsed; IntegrationActions.Visibility = IntegrationToggle.IsOn ? Visibility.Visible : Visibility.Collapsed; }
    private async void SaveIntegration_Click(object sender, RoutedEventArgs e) => await SaveIntegrationAsync(true);
    private async Task SaveIntegrationAsync(bool announce)
    {
        Config.Integration.Enabled = IntegrationToggle.IsOn; Config.Integration.OnWebhookUrl = OnWebhookText.Text.Trim(); Config.Integration.OffWebhookUrl = OffWebhookText.Text.Trim(); Config.Integration.BaseUrl = HaBaseUrlText.Text.Trim(); Config.Integration.EntityId = HaEntityText.Text.Trim();
        if (!string.IsNullOrWhiteSpace(HaTokenBox.Password)) { Config.Integration.ProtectedToken = HomeAssistantService.Protect(HaTokenBox.Password); HaTokenBox.Password = ""; }
        await _host.Configuration.SaveAsync(); UpdateSummaries(); if (announce) ShowInfo(IntegrationInfoBar, "Integration saved", IntegrationToggle.IsOn ? "Home Assistant power control is enabled." : "Power integration is disabled.", InfoBarSeverity.Success);
    }
    private async void TestOn_Click(object sender, RoutedEventArgs e) => await TestIntegrationAsync(true);
    private async void TestOff_Click(object sender, RoutedEventArgs e) => await TestIntegrationAsync(false);
    private async Task TestIntegrationAsync(bool on)
    {
        try { await SaveIntegrationAsync(false); await new HomeAssistantService().ExecuteAsync(Config.Integration, on, CancellationToken.None); ShowInfo(IntegrationInfoBar, $"Test {(on ? "ON" : "OFF")} succeeded", "Home Assistant accepted the request.", InfoBarSeverity.Success); }
        catch (Exception ex) { Report(IntegrationInfoBar, "Integration test failed", ex); }
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e) { if (_loadingValues) return; try { _host.SetStartWithWindows(StartupToggle.IsOn); ShowInfo(SettingsInfoBar, "Startup setting updated", StartupToggle.IsOn ? "SimRigMode will launch when you sign in." : "SimRigMode will not launch automatically.", InfoBarSeverity.Success); } catch (Exception ex) { Report(SettingsInfoBar, "Startup setting failed", ex); } }
    private void HotkeysToggle_Toggled(object sender, RoutedEventArgs e) { HotkeysExpander.IsExpanded = HotkeysToggle.IsOn; }
    private async void Delay_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) { if (!_initialized || _loadingValues) return; ReadGeneralControls(); await _host.Configuration.SaveAsync(); }
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
            var picker = new FileSavePicker { SuggestedFileName = "SimRigMode-config" }; picker.FileTypeChoices.Add("JSON configuration", [".json"]); InitializePicker(picker);
            var file = await picker.PickSaveFileAsync(); if (file is not null) { await _host.Configuration.ExportAsync(file.Path); ShowInfo(SettingsInfoBar, "Configuration exported", file.Path, InfoBarSeverity.Success); }
        }
        catch (Exception ex) { Report(SettingsInfoBar, "Export failed", ex); }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); InitializePicker(picker); var file = await picker.PickSingleFileAsync(); if (file is null) return;
            await _host.Configuration.ImportAsync(file.Path); _loadingValues = true; LoadConfigIntoControls(); _loadingValues = false; await RefreshUsbAsync(); await RefreshAudioAsync(); UpdateSummaries(); ShowInfo(SettingsInfoBar, "Configuration imported", "The imported workspace settings are active.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { Report(SettingsInfoBar, "Import failed", ex); }
    }
    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Reset SimRigMode?", "This removes captured display profiles, device rules, audio choices, integration settings, and shortcuts.", "Reset")) return;
        try { await _host.Configuration.ResetAsync(); _loadingValues = true; LoadConfigIntoControls(); _loadingValues = false; await RefreshUsbAsync(); await RefreshAudioAsync(); UpdateSummaries(); ShowInfo(SettingsInfoBar, "SimRigMode reset", "Default settings have been restored.", InfoBarSeverity.Success); } catch (Exception ex) { Report(SettingsInfoBar, "Reset failed", ex); }
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(_host.Log.LogDirectory) { UseShellExecute = true });
    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

    private void UpdateSummaries()
    {
        var current = Config.CurrentMode == WorkspaceMode.Desktop ? "Desktop mode" : Config.CurrentMode == WorkspaceMode.SimRig ? "Sim rig mode" : "Mode not selected";
        CurrentModeCard.Header = current; CurrentModeCard.Description = Config.CurrentMode == WorkspaceMode.Unknown ? "Choose a workspace to begin." : $"{current} is currently active.";
        DesktopWorkspaceCard.Description = WorkspaceDescription(Config.Desktop, WorkspaceMode.Desktop); RigWorkspaceCard.Description = WorkspaceDescription(Config.SimRig, WorkspaceMode.SimRig);
        DesktopDisplayCard.Description = SnapshotDescription(Config.Desktop.Displays); RigDisplayCard.Description = SnapshotDescription(Config.SimRig.Displays);
        DesktopReadinessCard.Description = Config.Desktop.Displays is null ? "Not captured" : $"Captured with {Config.Desktop.Displays.Paths.Count} displays"; RigReadinessCard.Description = Config.SimRig.Displays is null ? "Not captured" : $"Captured with {Config.SimRig.Displays.Paths.Count} displays";
        IntegrationReadinessCard.Description = Config.Integration.Enabled ? "Enabled" : "Disabled"; RigPowerCard.Description = Config.Integration.Enabled ? "Enabled for workspace transitions" : "Home Assistant integration disabled";
        var desktopRules = Config.UsbRules.Count(r => r.Desktop != DeviceModeAction.DoNotChange); var rigRules = Config.UsbRules.Count(r => r.SimRig != DeviceModeAction.DoNotChange);
        DesktopUsbCard.Description = desktopRules == 0 ? "No device rules" : $"{desktopRules} device rules configured"; RigUsbCard.Description = rigRules == 0 ? "No device rules" : $"{rigRules} device rules configured";
        DesktopAudioCard.Description = AudioDescription(Config.Desktop.Audio); RigAudioCard.Description = AudioDescription(Config.SimRig.Audio);
        DesktopSwitchButton.Style = Config.CurrentMode == WorkspaceMode.Desktop ? null : (Style)Application.Current.Resources["AccentButtonStyle"];
        RigSwitchButton.Style = Config.CurrentMode == WorkspaceMode.SimRig ? null : (Style)Application.Current.Resources["AccentButtonStyle"];
    }
    private string WorkspaceDescription(ModeConfig mode, WorkspaceMode workspace) { var displays = mode.Displays?.Paths.Count ?? 0; var rules = Config.UsbRules.Count(r => (workspace == WorkspaceMode.Desktop ? r.Desktop : r.SimRig) != DeviceModeAction.DoNotChange); return $"{displays} display{(displays == 1 ? "" : "s")}  •  {(rules == 0 ? "No USB rules" : $"{rules} USB rules")}"; }
    private static string SnapshotDescription(DisplaySnapshot? snapshot) => snapshot is null ? "No profile captured" : $"{snapshot.Paths.Count} displays  •  Captured {snapshot.CapturedUtc.ToLocalTime():g}";
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
