using CommPanel.Audio;
using CommPanel.Core;

namespace CommPanel.Ui;

/// <summary>
/// Settings dialog: which programs pop the panel open, which devices appear on it, and how
/// CommPanel behaves in the background.
///
/// Laid out in two columns rather than one long strip. A single column of everything here
/// stands about 640 logical units tall, which at a large panel size would run off the bottom
/// of the screen with the Save key on it.
/// </summary>
internal sealed class SettingsForm : PanelDialog
{
    /// <summary>Roughly how tall this gets, so the base can shrink it to fit the screen.</summary>
    private const int LogicalHeight = 620;

    private readonly AppSettings _settings;

    private readonly PlateList _watchList = new();
    private readonly TextBox _newProcess = new();
    private readonly PlateList _deviceList = new();
    private readonly TextBox _voicePort = new();

    private PlateCheck _watchEnabled = null!;
    private PlateCheck _linkComms = null!;
    private PlateCheck _autoFallback = null!;
    private PlateCheck _showMeters = null!;
    private PlateCheck _meterMic = null!;
    private PlateCheck _watchHeadset = null!;
    private PlateCheck _queryHeadset = null!;
    private PlateCheck _returnToHeadset = null!;
    private PlateCheck _startInTray = null!;
    private PlateCheck _autoHide = null!;
    private PlateCheck _hotkey = null!;
    private PlateCheck _startWithWindows = null!;

    private readonly List<AudioDevice> _allDevices = new();
    private readonly List<AudioDevice> _outputDevices = new();

    private readonly VolumeFader _bloomFader = new();
    private readonly VolumeFader _sizeFader = new();
    private readonly LevelMeter _bloomPreview = new();

    // Engraved regions, drawn into the chassis once.
    private readonly List<(string Text, Rectangle Bounds)> _captions = new();
    private readonly List<(string Text, Rectangle Bounds)> _hints = new();
    private readonly List<Rectangle> _recesses = new();

    private Rectangle _headsetStatusRect;
    private string _headsetStatus = string.Empty;

    /// <summary>Applies a size while the user drags, so the choice is made by eye.</summary>
    public Action<float>? PreviewScale { get; set; }

    /// <summary>Slider position 0..1 maps to a panel scale of 0.8x to 2.0x.</summary>
    private static float ScaleFromSlider(float v) => 0.8f + Math.Clamp(v, 0f, 1f) * 1.2f;

    private static float SliderFromScale(float s) => Math.Clamp((s - 0.8f) / 1.2f, 0f, 1f);

    /// <summary>Restores the live bloom setting if the dialog is cancelled.</summary>
    private readonly float _bloomOnEntry = PanelTheme.Bloom;

    /// <summary>Working copy: only written back to settings when the user saves.</summary>
    private List<HeadsetProfile> _headsetProfiles;

    public SettingsForm(AppSettings settings, List<AudioDevice> outputs, List<AudioDevice> inputs)
        : base(settings, "SETTINGS", "COMMPANEL CONFIGURATION", LogicalHeight)
    {
        _settings = settings;
        _allDevices.AddRange(outputs);
        _allDevices.AddRange(inputs);
        _outputDevices.AddRange(outputs);
        _headsetProfiles = settings.HeadsetProfiles.Select(Clone).ToList();

        BuildLayout();
        LoadValues();
    }

    private void BuildLayout()
    {
        int margin = EdgeMargin;
        int total = Scaled(880);
        int gutter = Scaled(18);
        int column = (total - margin * 2 - gutter) / 2;
        int leftX = margin;
        int rightX = margin + column + gutter;

        int bottom = Math.Max(BuildLeftColumn(leftX, column), BuildRightColumn(rightX, column));

        int keyHeight = Scaled(30);
        int keyTop = bottom + Scaled(14);
        int saveWidth = Scaled(96);
        int cancelWidth = Scaled(96);

        var save = Key("SAVE", new Rectangle(total - margin - saveWidth, keyTop, saveWidth, keyHeight),
                       (_, _) => Commit());
        save.ShowLamp = true;
        save.IsOn = true;
        save.LampColor = PanelTheme.LampGreen;

        Key("CANCEL", new Rectangle(save.Left - Scaled(8) - cancelWidth, keyTop, cancelWidth, keyHeight),
            (_, _) =>
            {
                PanelTheme.Bloom = _bloomOnEntry; // discard live bloom changes on cancel
                DialogResult = DialogResult.Cancel;
                Close();
            });

        ClientSize = new Size(total, keyTop + keyHeight + margin);
        PlaceHeader();
        RebuildChassis();
    }

    private int BuildLeftColumn(int x, int width)
    {
        int y = BodyTop;

        y = Caption("PROGRAMS THAT OPEN THE PANEL", x, y, width);
        y = Hint("When one of these comes to the foreground, CommPanel appears on top without "
               + "stealing focus, so you can re-route while it loads.", x, y, width, 34);

        var listRecess = new Rectangle(x, y, width, Scaled(104));
        _recesses.Add(listRecess);
        _watchList.Bounds = Inside(listRecess);
        StyleList(_watchList);
        Controls.Add(_watchList);
        y += listRecess.Height + Scaled(10);

        var fieldRecess = new Rectangle(x, y, width, Scaled(30));
        _recesses.Add(fieldRecess);
        _newProcess.Bounds = Inside(fieldRecess);
        StyleField(_newProcess);
        _newProcess.PlaceholderText = "game.exe";
        _newProcess.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            AddWatchEntry(_newProcess.Text);
            e.SuppressKeyPress = true;
            e.Handled = true;
        };
        Controls.Add(_newProcess);
        y += fieldRecess.Height + Scaled(8);

        int keyHeight = Scaled(28);
        int third = (width - Scaled(16)) / 3;

        Key("ADD", new Rectangle(x, y, third, keyHeight), (_, _) => AddWatchEntry(_newProcess.Text));
        Key("BROWSE…", new Rectangle(x + third + Scaled(8), y, third, keyHeight), (_, _) => BrowseForProgram());
        Key("REMOVE", new Rectangle(x + (third + Scaled(8)) * 2, y, third, keyHeight), (_, _) =>
        {
            if (_watchList.SelectedIndex >= 0) _watchList.RemoveAt(_watchList.SelectedIndex);
        });
        y += keyHeight + Scaled(12);

        _watchEnabled = Toggle("Watch for these programs in the background",
                               new Rectangle(x, y, width, Scaled(20)));
        y += Scaled(20) + Scaled(16);

        y = Caption("DEVICES SHOWN ON THE PANEL", x, y, width);
        y = Hint("Click a lamp to show or hide that device. Hidden devices are never switched "
               + "to automatically.", x, y, width, 30);

        var deviceRecess = new Rectangle(x, y, width, Scaled(148));
        _recesses.Add(deviceRecess);
        _deviceList.Bounds = Inside(deviceRecess);
        _deviceList.ShowLamps = true;
        StyleList(_deviceList);
        Controls.Add(_deviceList);

        return deviceRecess.Bottom;
    }

    private int BuildRightColumn(int x, int width)
    {
        int y = BodyTop;

        y = Caption("BEHAVIOUR", x, y, width);

        int rowHeight = Scaled(20);
        int rowStep = Scaled(23);

        PlateCheck Row(string text)
        {
            var check = Toggle(text, new Rectangle(x, y, width, rowHeight));
            y += rowStep;
            return check;
        }

        _showMeters = Row("Show level meters and volume faders");
        _meterMic = Row("Meter the microphone while the panel is visible");
        _linkComms = Row("Switch the communications device too");
        _autoFallback = Row("Switch away from a device that goes offline");
        _watchHeadset = Row("Detect a wireless headset being powered off");
        _queryHeadset = Row("Ask the headset its state at launch");
        _returnToHeadset = Row("Switch back when the headset powers on again");
        _hotkey = Row("Global hotkey  Ctrl + Alt + C");
        _autoHide = Row("Hide the panel after a device is selected");
        _startInTray = Row("Start minimised to the notification area");
        _startWithWindows = Row("Start CommPanel when Windows starts");

        y += Scaled(12);

        y = Caption("PANEL SIZE", x, y, width);

        int faderWidth = Scaled(200);
        _sizeFader.Bounds = new Rectangle(x, y, faderWidth, Scaled(24));
        _sizeFader.ReadoutFont = StencilFont;
        _sizeFader.ReadoutText = v => ((int)MathF.Round(ScaleFromSlider(v) * 100f)) + "%";
        _sizeFader.ValueChanged += v => PreviewScale?.Invoke(ScaleFromSlider(v));
        Controls.Add(_sizeFader);

        // The preview applies to the panel behind this dialog, not to the dialog itself:
        // a window that resized under the slider you were dragging would be unusable.
        _hints.Add(("Scales the panel and its text together. The panel resizes as you drag.",
                    new Rectangle(x + faderWidth + Scaled(12), y - Scaled(2),
                                  width - faderWidth - Scaled(12), Scaled(34))));
        y += Scaled(34);

        y = Caption("LAMP BLOOM", x, y, width);

        // The fader and meter from the panel itself, so the preview is the real renderer
        // rather than an approximation of it.
        _bloomFader.Bounds = new Rectangle(x, y, faderWidth, Scaled(24));
        _bloomFader.ReadoutFont = StencilFont;
        _bloomFader.ValueChanged += value =>
        {
            PanelTheme.Bloom = Math.Clamp(value, 0f, 1f) * 2f;
            RefreshBloomPreview();
        };
        Controls.Add(_bloomFader);

        _bloomPreview.Bounds = new Rectangle(x + faderWidth + Scaled(12), y,
                                             width - faderWidth - Scaled(12), Scaled(24));
        _bloomPreview.Caption = "DEMO";
        _bloomPreview.CaptionFont = StencilFont;
        Controls.Add(_bloomPreview);
        y += Scaled(34);

        y = Caption("VOICE LINK PORT", x, y, width);

        var portRecess = new Rectangle(x, y, Scaled(96), Scaled(30));
        _recesses.Add(portRecess);
        _voicePort.Bounds = Inside(portRecess);
        StyleField(_voicePort);
        _voicePort.TextAlign = HorizontalAlignment.Center;
        _voicePort.MaxLength = 5;
        _voicePort.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
        };
        Controls.Add(_voicePort);

        _hints.Add(("The UDP port a call listens on. Change it only if something else uses "
                  + "this one — your link code changes with it.",
                    new Rectangle(x + portRecess.Width + Scaled(12), y - Scaled(2),
                                  width - portRecess.Width - Scaled(12), Scaled(34))));
        y += portRecess.Height + Scaled(12);

        int keyHeight = Scaled(28);
        int learnWidth = Scaled(168);
        Key("LEARN MY HEADSET…", new Rectangle(x, y, learnWidth, keyHeight), (_, _) => LearnHeadset());

        _headsetStatusRect = new Rectangle(x, y + keyHeight + Scaled(6), width, Scaled(18));

        return _headsetStatusRect.Bottom;
    }

    /// <summary>Records a stencil caption for the chassis and returns the y below it.</summary>
    private int Caption(string text, int x, int y, int width)
    {
        _captions.Add((text, new Rectangle(x, y, width, Scaled(18))));
        return y + Scaled(22);
    }

    /// <summary>Records a block of explanatory text for the chassis and returns the y below it.</summary>
    private int Hint(string text, int x, int y, int width, int logicalHeight)
    {
        _hints.Add((text, new Rectangle(x, y, width, Scaled(logicalHeight))));
        return y + Scaled(logicalHeight) + Scaled(4);
    }

    protected override void DrawChassis(Graphics g)
    {
        float radius = Scaled(5);
        foreach (var recess in _recesses) PanelTheme.DrawRecess(g, recess, radius);
        foreach (var (text, bounds) in _captions) DrawCaption(g, text, bounds);
        foreach (var (text, bounds) in _hints) DrawBody(g, text, bounds);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        // Changes when a headset is learned, so it is painted rather than engraved.
        TextRenderer.DrawText(e.Graphics, _headsetStatus, StencilFont, _headsetStatusRect,
            Color.FromArgb(170, PanelTheme.TextSecondary),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    private void LoadValues()
    {
        foreach (string process in _settings.WatchedProcesses)
            _watchList.Add(process);

        for (int i = 0; i < _allDevices.Count; i++)
        {
            var device = _allDevices[i];
            _deviceList.Add(string.Format("{0}  {1}",
                device.Flow == EDataFlow.Render ? "OUT" : "IN ", device.FullName));
            _deviceList.SetLit(i, !_settings.HiddenDeviceIds.Contains(device.Id));
        }

        _watchEnabled.Checked = _settings.WatchProcesses;
        _linkComms.Checked = _settings.LinkCommunications;
        _autoFallback.Checked = _settings.AutoFallback;
        _showMeters.Checked = _settings.ShowMeters;
        _bloomFader.Value = _settings.BloomIntensity;
        _sizeFader.Value = SliderFromScale(_settings.SafeFontScale);
        RefreshBloomPreview();
        _meterMic.Checked = _settings.MeterMicrophone;
        _voicePort.Text = _settings.SafeVoicePort.ToString();
        _watchHeadset.Checked = _settings.WatchHeadsetPower;
        _queryHeadset.Checked = _settings.QueryHeadsetStatus;
        _returnToHeadset.Checked = _settings.ReturnToHeadset;
        _startInTray.Checked = _settings.StartInTray;
        _autoHide.Checked = _settings.AutoHideAfterSwitch;
        _hotkey.Checked = _settings.HotkeyEnabled;
        _startWithWindows.Checked = StartupRegistration.IsEnabled;
        UpdateHeadsetStatus();
    }

    private void Commit()
    {
        _settings.WatchedProcesses = _watchList.Items
            .Where(text => text.Length > 0)
            .ToList();

        _settings.HiddenDeviceIds = _allDevices
            .Where((_, index) => !_deviceList.IsLit(index))
            .Select(device => device.Id)
            .ToList();

        _settings.WatchProcesses = _watchEnabled.Checked;
        _settings.LinkCommunications = _linkComms.Checked;
        _settings.AutoFallback = _autoFallback.Checked;
        _settings.ShowMeters = _showMeters.Checked;
        _settings.BloomIntensity = _bloomFader.Value;
        _settings.FontScale = ScaleFromSlider(_sizeFader.Value);
        _settings.MeterMicrophone = _meterMic.Checked;

        // A typed port is clamped rather than rejected: an out-of-range value here is a slip,
        // and refusing to save the whole dialog over it would be the wrong trade.
        _settings.VoicePort = int.TryParse(_voicePort.Text, out int port) && port is >= 1024 and <= 65535
            ? port
            : 47821;

        _settings.WatchHeadsetPower = _watchHeadset.Checked;
        _settings.QueryHeadsetStatus = _queryHeadset.Checked;
        _settings.ReturnToHeadset = _returnToHeadset.Checked;
        _settings.HeadsetProfiles = _headsetProfiles;
        _settings.StartInTray = _startInTray.Checked;
        _settings.AutoHideAfterSwitch = _autoHide.Checked;
        _settings.HotkeyEnabled = _hotkey.Checked;

        if (_startWithWindows.Checked != StartupRegistration.IsEnabled)
        {
            if (!StartupRegistration.SetEnabled(_startWithWindows.Checked))
            {
                MessageBox.Show(this, "Could not update the Windows startup entry.",
                    "CommPanel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void AddWatchEntry(string? raw)
    {
        string name = (raw ?? string.Empty).Trim();
        if (name.Length == 0) return;

        // Accept a full path or a bare name; only the file name is ever matched.
        int slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";

        foreach (string item in _watchList.Items)
        {
            if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
            {
                _newProcess.Clear();
                return;
            }
        }

        _watchList.Add(name);
        _newProcess.Clear();
    }

    private void BrowseForProgram()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select a program to watch for",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            AddWatchEntry(Path.GetFileName(dialog.FileName));
    }

    /// <summary>
    /// Pushes a representative level through the preview meter so the bloom is shown on the
    /// real renderer. Two thirds lights green through amber, which is where the glow reads.
    /// </summary>
    private void RefreshBloomPreview()
    {
        _bloomPreview.Feed(0.62f);
        _bloomPreview.Invalidate();
    }

    /// <summary>
    /// Runs the learn wizard and keeps the result. A profile for the same interface replaces
    /// the previous one, so re-learning after a firmware change simply overwrites it.
    /// </summary>
    private void LearnHeadset()
    {
        using var wizard = new LearnHeadsetForm(_settings, _outputDevices);
        if (wizard.ShowDialog(this) != DialogResult.OK || wizard.Result is null) return;

        var learned = wizard.Result;

        _headsetProfiles.RemoveAll(p =>
            p.VendorId == learned.VendorId &&
            p.ProductId == learned.ProductId &&
            p.UsagePage == learned.UsagePage);

        _headsetProfiles.Add(learned);

        // Learning a headset is a clear statement of intent that the feature should be on.
        _watchHeadset.Checked = true;

        UpdateHeadsetStatus();
    }

    /// <summary>
    /// Says plainly whether a base station is actually present. Without this the option
    /// looks active on hardware it cannot possibly detect, which is how the feature
    /// previously failed: silently.
    /// </summary>
    private void UpdateHeadsetStatus()
    {
        try
        {
            var profiles = HeadsetProfile.Resolve(_headsetProfiles);
            var devices = HidDevices.Enumerate();

            var matched = profiles
                .Where(profile => devices.Any(profile.Matches))
                .Select(profile => profile.Name)
                .Distinct()
                .ToList();

            _headsetStatus = matched.Count > 0
                ? "DETECTED: " + string.Join(", ", matched).ToUpperInvariant()
                : "NO SUPPORTED BASE STATION DETECTED — USE LEARN MY HEADSET";
        }
        catch
        {
            _headsetStatus = string.Empty;
        }

        Invalidate(_headsetStatusRect);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            PanelTheme.Bloom = _bloomOnEntry;
            DialogResult = DialogResult.Cancel;
            Close();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private static HeadsetProfile Clone(HeadsetProfile source) => new()
    {
        Name = source.Name,
        AdapterMatch = source.AdapterMatch,
        VendorId = source.VendorId,
        ProductId = source.ProductId,
        UsagePage = source.UsagePage,
        ReportId = source.ReportId,
        ReportTag = source.ReportTag,
        StatusOffset = source.StatusOffset,
        PoweredOnValue = source.PoweredOnValue,
        PoweredOffValue = source.PoweredOffValue,
        IsBuiltIn = source.IsBuiltIn
    };
}
