using CommPanel.Audio;
using CommPanel.Core;

namespace CommPanel.Ui;

/// <summary>
/// Settings dialog: which programs pop the panel open, which devices appear on it, and how
/// CommPanel behaves in the background. Deliberately plain - the panel is the show piece,
/// this is the maintenance hatch behind it.
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly Color Background = Color.FromArgb(0x26, 0x24, 0x21);
    private static readonly Color Surface = Color.FromArgb(0x33, 0x30, 0x2B);
    private static readonly Color Ink = Color.FromArgb(0xE6, 0xDF, 0xCD);
    private static readonly Color InkDim = Color.FromArgb(0x9C, 0x93, 0x84);

    private readonly AppSettings _settings;

    private readonly ListBox _watchList = new();
    private readonly TextBox _newProcess = new();
    private readonly CheckedListBox _deviceList = new();

    private readonly ThemedCheckBox _watchEnabled = new();
    private readonly ThemedCheckBox _linkComms = new();
    private readonly ThemedCheckBox _autoFallback = new();
    private readonly ThemedCheckBox _showMeters = new();
    private readonly ThemedCheckBox _meterMic = new();
    private readonly ThemedCheckBox _watchHeadset = new();
    private readonly ThemedCheckBox _queryHeadset = new();
    private readonly ThemedCheckBox _returnToHeadset = new();
    private readonly ThemedCheckBox _startInTray = new();
    private readonly ThemedCheckBox _autoHide = new();
    private readonly ThemedCheckBox _hotkey = new();
    private readonly ThemedCheckBox _startWithWindows = new();

    private readonly List<AudioDevice> _allDevices = new();
    private readonly List<AudioDevice> _outputDevices = new();
    private readonly Label _headsetStatus = new();

    private readonly VolumeFader _bloomFader = new();
    private readonly VolumeFader _sizeFader = new();
    private readonly LevelMeter _bloomPreview = new();

    /// <summary>Applies a size while the user drags, so the choice is made by eye.</summary>
    public Action<float>? PreviewScale { get; set; }

    /// <summary>Slider position 0..1 maps to a panel scale of 0.8x to 2.0x.</summary>
    private static float ScaleFromSlider(float v) => 0.8f + Math.Clamp(v, 0f, 1f) * 1.2f;

    private static float SliderFromScale(float s) => Math.Clamp((s - 0.8f) / 1.2f, 0f, 1f);

    /// <summary>Restores the live bloom setting if the dialog is cancelled.</summary>
    private readonly float _bloomOnEntry = PanelTheme.Bloom;

    /// <summary>Working copy: only written back to settings when the user saves.</summary>
    private List<HeadsetProfile> _headsetProfiles = new();

    public SettingsForm(AppSettings settings, List<AudioDevice> outputs, List<AudioDevice> inputs)
    {
        _settings = settings;
        _allDevices.AddRange(outputs);
        _allDevices.AddRange(inputs);
        _outputDevices.AddRange(outputs);
        _headsetProfiles = settings.HeadsetProfiles.Select(Clone).ToList();

        Text = "CommPanel Settings";
        Icon = AppIcon.Load(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Background;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(560, 610);

        BuildLayout();
        LoadValues(outputs, inputs);
    }

    private void BuildLayout()
    {
        int margin = 16;
        int width = ClientSize.Width - margin * 2;
        int y = margin;

        Controls.Add(SectionLabel("PROGRAMS THAT OPEN THE PANEL", margin, y, width));
        y += 22;

        Controls.Add(Hint("When one of these comes to the foreground, CommPanel appears on top " +
                          "without stealing focus, so you can re-route while it loads.",
                          margin, y, width, 34));
        y += 38;

        _watchList.SetBounds(margin, y, width, 120);
        _watchList.BackColor = Surface;
        _watchList.ForeColor = Ink;
        _watchList.BorderStyle = BorderStyle.FixedSingle;
        _watchList.IntegralHeight = false;
        Controls.Add(_watchList);
        y += 126;

        _newProcess.SetBounds(margin, y, width - 250, 24);
        _newProcess.BackColor = Surface;
        _newProcess.ForeColor = Ink;
        _newProcess.BorderStyle = BorderStyle.FixedSingle;
        _newProcess.PlaceholderText = "game.exe";
        _newProcess.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                AddWatchEntry(_newProcess.Text);
                e.SuppressKeyPress = true;
            }
        };
        Controls.Add(_newProcess);

        var addButton = DialogButton("Add", margin + width - 246, y, 74);
        addButton.Click += (_, _) => AddWatchEntry(_newProcess.Text);
        Controls.Add(addButton);

        var browseButton = DialogButton("Browse…", margin + width - 166, y, 80);
        browseButton.Click += (_, _) => BrowseForProgram();
        Controls.Add(browseButton);

        var removeButton = DialogButton("Remove", margin + width - 80, y, 80);
        removeButton.Click += (_, _) =>
        {
            if (_watchList.SelectedIndex >= 0) _watchList.Items.RemoveAt(_watchList.SelectedIndex);
        };
        Controls.Add(removeButton);
        y += 36;

        _watchEnabled.SetBounds(margin, y, width, 22);
        _watchEnabled.Text = "Watch for these programs while running in the background";
        StyleCheckBox(_watchEnabled);
        Controls.Add(_watchEnabled);
        y += 32;

        Controls.Add(SectionLabel("DEVICES SHOWN ON THE PANEL", margin, y, width));
        y += 24;

        _deviceList.SetBounds(margin, y, width, 130);
        _deviceList.BackColor = Surface;
        _deviceList.ForeColor = Ink;
        _deviceList.BorderStyle = BorderStyle.FixedSingle;
        _deviceList.CheckOnClick = true;
        _deviceList.IntegralHeight = false;
        Controls.Add(_deviceList);
        y += 140;

        Controls.Add(SectionLabel("BEHAVIOUR", margin, y, width));
        y += 24;

        foreach (var (box, text) in new[]
        {
            (_showMeters, "Show level meters and volume faders on the panel"),
            (_meterMic, "Meter the microphone — opens the mic while the panel is visible"),
            (_linkComms, "Switch the communications device along with the default device"),
            (_autoFallback, "Switch to another device when the current one goes offline"),
            (_watchHeadset, "Detect a wireless headset being powered off"),
            (_queryHeadset, "Ask the headset its state at launch — sends one request to it"),
            (_returnToHeadset, "Switch back to the headset when it is powered on again"),
            (_hotkey, "Global hotkey  Ctrl + Alt + C  shows or hides the panel"),
            (_autoHide, "Hide the panel immediately after a device is selected"),
            (_startInTray, "Start minimised to the notification area"),
            (_startWithWindows, "Start CommPanel when Windows starts")
        })
        {
            box.SetBounds(margin, y, width, 22);
            box.Text = text;
            StyleCheckBox(box);
            Controls.Add(box);
            y += 24;
        }

        y += 10;

        Controls.Add(SectionLabel("PANEL SIZE", margin, y, width));
        y += 22;

        _sizeFader.SetBounds(margin, y, 210, 24);
        _sizeFader.BackColor = Background;
        _sizeFader.ReadoutFont = new Font("Consolas", 8.25f, FontStyle.Bold);
        _sizeFader.ReadoutText = v => ((int)MathF.Round(ScaleFromSlider(v) * 100f)) + "%";
        _sizeFader.ValueChanged += v => PreviewScale?.Invoke(ScaleFromSlider(v));
        Controls.Add(_sizeFader);

        Controls.Add(Hint("Scales the text and the whole panel with it, so nothing is clipped. "
                          + "The window resizes as you drag.",
                          margin + 222, y - 4, width - 222, 34));
        y += 34;

        Controls.Add(SectionLabel("LAMP BLOOM", margin, y, width));
        y += 22;

        // The fader and meter from the panel itself, so the preview is the real renderer
        // rather than an approximation of it.
        _bloomFader.SetBounds(margin, y, 210, 24);
        _bloomFader.BackColor = Background;
        _bloomFader.ReadoutFont = new Font("Consolas", 8.25f, FontStyle.Bold);
        _bloomFader.ValueChanged += value =>
        {
            PanelTheme.Bloom = Math.Clamp(value, 0f, 1f) * 2f;
            RefreshBloomPreview();
        };
        Controls.Add(_bloomFader);

        _bloomPreview.SetBounds(margin + 222, y, width - 222, 26);
        _bloomPreview.BackColor = Background;
        _bloomPreview.Caption = "DEMO";
        _bloomPreview.CaptionFont = new Font("Consolas", 8.25f, FontStyle.Bold);
        Controls.Add(_bloomPreview);
        y += 34;

        var learn = DialogButton("Learn my headset…", margin, y, 150);
        learn.Click += (_, _) => LearnHeadset();
        Controls.Add(learn);

        _headsetStatus.SetBounds(margin + 160, y, width - 160, 26);
        _headsetStatus.ForeColor = InkDim;
        _headsetStatus.Font = new Font("Consolas", 8.25f);
        _headsetStatus.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(_headsetStatus);
        y += 36;

        var ok = DialogButton("Save", ClientSize.Width - margin - 170, y, 80);
        ok.Click += (_, _) => Commit();
        Controls.Add(ok);

        var cancel = DialogButton("Cancel", ClientSize.Width - margin - 80, y, 80);
        cancel.Click += (_, _) =>
        {
            PanelTheme.Bloom = _bloomOnEntry; // discard live bloom changes on cancel
            DialogResult = DialogResult.Cancel;
            Close();
        };
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;

        ClientSize = new Size(ClientSize.Width, y + 34 + margin);
    }

    private void LoadValues(List<AudioDevice> outputs, List<AudioDevice> inputs)
    {
        foreach (string process in _settings.WatchedProcesses)
            _watchList.Items.Add(process);

        foreach (var device in _allDevices)
        {
            string label = string.Format("{0}  —  {1}",
                device.Flow == EDataFlow.Render ? "OUT" : "IN ", device.FullName);
            int index = _deviceList.Items.Add(label);
            _deviceList.SetItemChecked(index, !_settings.HiddenDeviceIds.Contains(device.Id));
        }

        _watchEnabled.Checked = _settings.WatchProcesses;
        _linkComms.Checked = _settings.LinkCommunications;
        _autoFallback.Checked = _settings.AutoFallback;
        _showMeters.Checked = _settings.ShowMeters;
        _bloomFader.Value = _settings.BloomIntensity;
        _sizeFader.Value = SliderFromScale(_settings.SafeFontScale);
        RefreshBloomPreview();
        _meterMic.Checked = _settings.MeterMicrophone;
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
        _settings.WatchedProcesses = _watchList.Items.Cast<object>()
            .Select(item => item.ToString() ?? string.Empty)
            .Where(text => text.Length > 0)
            .ToList();

        _settings.HiddenDeviceIds = _allDevices
            .Where((_, index) => !_deviceList.GetItemChecked(index))
            .Select(device => device.Id)
            .ToList();

        _settings.WatchProcesses = _watchEnabled.Checked;
        _settings.LinkCommunications = _linkComms.Checked;
        _settings.AutoFallback = _autoFallback.Checked;
        _settings.ShowMeters = _showMeters.Checked;
        _settings.BloomIntensity = _bloomFader.Value;
        _settings.FontScale = ScaleFromSlider(_sizeFader.Value);
        _settings.MeterMicrophone = _meterMic.Checked;
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

        foreach (object item in _watchList.Items)
        {
            if (string.Equals(item.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                _newProcess.Clear();
                return;
            }
        }

        _watchList.Items.Add(name);
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
        using var wizard = new LearnHeadsetForm(_outputDevices);
        if (wizard.ShowDialog(this) != DialogResult.OK || wizard.Result is null) return;

        var learned = wizard.Result;

        _headsetProfiles.RemoveAll(p =>
            p.VendorId == learned.VendorId &&
            p.ProductId == learned.ProductId &&
            p.UsagePage == learned.UsagePage);

        _headsetProfiles.Add(learned);

        // Learning a headset is a clear statement of intent that the feature should be on.
        _watchHeadset.Checked = true;
        _watchHeadset.Invalidate();

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

            _headsetStatus.Text = matched.Count > 0
                ? "detected: " + string.Join(", ", matched)
                : "no supported base station detected — use Learn my headset";
        }
        catch
        {
            _headsetStatus.Text = string.Empty;
        }
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

    /// <summary>
    /// The themed checkbox glyph is near-invisible against a dark form, so the box is drawn
    /// flat with colours chosen here: a checked box fills green, which reads at a glance.
    /// </summary>
    private static void StyleCheckBox(ThemedCheckBox box)
    {
        box.FlatStyle = FlatStyle.Flat;
        box.ForeColor = Ink;
        box.BackColor = Background;
        box.FlatAppearance.BorderColor = Color.FromArgb(0x6A, 0x64, 0x59);
        box.FlatAppearance.CheckedBackColor = Color.FromArgb(0x3F, 0x6E, 0x42);
        box.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x3A, 0x37, 0x31);
    }

    private static Label SectionLabel(string text, int x, int y, int width) => new()
    {
        Text = text,
        Bounds = new Rectangle(x, y, width, 20),
        ForeColor = Color.FromArgb(0xC9, 0xBF, 0xA8),
        Font = new Font("Consolas", 8.25f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static Label Hint(string text, int x, int y, int width, int height) => new()
    {
        Text = text,
        Bounds = new Rectangle(x, y, width, height),
        ForeColor = InkDim,
        Font = new Font("Segoe UI", 8.25f)
    };

    private static Button DialogButton(string text, int x, int y, int width)
    {
        var button = new Button
        {
            Text = text,
            Bounds = new Rectangle(x, y, width, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = Ink,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(0x55, 0x50, 0x48);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x45, 0x41, 0x39);
        return button;
    }
}
