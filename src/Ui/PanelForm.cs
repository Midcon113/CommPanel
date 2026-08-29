using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using CommPanel.Audio;
using CommPanel.Core;

namespace CommPanel.Ui;

/// <summary>
/// The control panel itself: one bank of lamps for outputs, one for inputs. Click a lamp,
/// Windows switches its default endpoint immediately - no Sound control panel, no in-game
/// audio menu.
/// </summary>
internal sealed class PanelForm : Form
{
    // Layout constants, in logical (96 dpi) units.
    private const int EdgeMargin = 16;
    private const int HeaderHeight = 56;
    private const int SectionHeaderHeight = 24;
    private const int ButtonHeight = 54;
    private const int ButtonGap = 8;
    private const int ColumnWidth = 322;
    private const int ColumnGap = 14;
    private const int BankPadding = 10;
    private const int FooterHeight = 44;
    private const int EmptyBankHeight = 54;

    /// <summary>Cap on mixer rows: beyond this the panel would be taller than most screens.</summary>
    private const int MaxMixerRows = 8;

    private const int HotkeyId = 0xC0DE;

    private readonly AudioEndpointService _audio;
    private readonly AppSettings _settings;
    private readonly ForegroundWatcher _watcher = new();
    private readonly HeadsetWatcher _headsetWatcher = new();
    private readonly System.Windows.Forms.Timer _refreshDebounce = new();

    private readonly List<LampButton> _outputButtons = new();
    private readonly List<LampButton> _inputButtons = new();

    private readonly PlateButton _minimizeButton = new();
    private readonly PlateButton _closeButton = new();
    private readonly PlateButton _linkCommsButton = new();
    private readonly PlateButton _watchListButton = new();
    private readonly PlateButton _refreshButton = new();

    private readonly LevelMeter _outputMeter = new();
    private readonly LevelMeter _inputMeter = new();
    private readonly VolumeFader _outputFader = new();
    private readonly VolumeFader _inputFader = new();
    private readonly PlateButton _outputMute = new();
    private readonly PlateButton _inputMute = new();

    /// <summary>
    /// Metering has no notification API, so it has to be polled. The timer runs only while
    /// the panel is on screen and is stopped the moment it hides, which is what keeps a
    /// backgrounded CommPanel at zero CPU while a game is running.
    /// </summary>
    private readonly System.Windows.Forms.Timer _meterTimer = new();

    private EndpointControls? _outputControls;
    private EndpointControls? _inputControls;
    private int _meterTick;

    /// <summary>
    /// Held open only while the panel is visible: a microphone reads flat zero unless
    /// something is capturing from it, so metering one means opening it. Closing this the
    /// moment the panel hides is what makes the Windows microphone indicator track the
    /// panel exactly.
    /// </summary>
    private CaptureMeter? _inputCapture;
    private string? _captureMeterErrorFor;

    // Per-application mixer. Only enumerated while the section is expanded and on screen.
    private readonly PlateButton _mixerToggle = new();
    private readonly List<MixerRow> _mixerRows = new();
    private SessionMixer? _mixer;
    private int _sessionRefreshTick;
    private int _deviceRecheckTick;

    /// <summary>Devices we have already prompted about, so the offer is made once per outage.</summary>
    private readonly HashSet<string> _offlinePrompts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One application's row: name, level, fader, mute.</summary>
    private sealed class MixerRow
    {
        public required PlateLabel Name { get; init; }
        public required LevelMeter Meter { get; init; }
        public required VolumeFader Fader { get; init; }
        public required PlateButton Mute { get; init; }
        public AudioSessionHandle? Session { get; set; }

        public IEnumerable<Control> Controls()
        {
            yield return Name;
            yield return Meter;
            yield return Fader;
            yield return Mute;
        }

        public void SetVisible(bool visible)
        {
            foreach (var control in Controls()) control.Visible = visible;
        }
    }

    private Font _titleFont = PanelTheme.TitleFont(1f);
    private Font _labelFont = PanelTheme.LabelFont(1f);
    private Font _smallFont = PanelTheme.SmallFont(1f);
    private Font _stencilFont = PanelTheme.StencilFont(1f);

    private NotifyIcon? _tray;
    private Bitmap? _chassis;

    private Rectangle _outputBank;
    private Rectangle _inputBank;
    private Rectangle _outputHeader;
    private Rectangle _inputHeader;
    private Rectangle _outputConsole;
    private Rectangle _inputConsole;
    private Rectangle _mixerPlate;
    private Rectangle _mixerHeader;
    private Rectangle _footerRect;
    private Rectangle _statusRect;

    private string _statusText = "READY";
    private Color _statusColor = PanelTheme.TextSecondary;

    private List<AudioDevice> _outputs = new();
    private List<AudioDevice> _inputs = new();

    private bool _exiting;
    private bool _hotkeyRegistered;
    private uint _showMessage;

    // The endpoints last seen as default, used to notice one going offline.
    private string? _activeOutputId;
    private string? _activeInputId;
    private bool _handlingFallback;

    // Endpoints we switched away from because a headset powered down, so that turning it
    // back on can undo exactly that and nothing else.
    private string? _returnOutputId;
    private string? _returnInputId;

    public PanelForm(AudioEndpointService audio, AppSettings settings)
    {
        _audio = audio;
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "CommPanel";
        Icon = AppIcon.Load(32);
        BackColor = PanelTheme.ChassisBottom;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);

        _showMessage = NativeMethods.RegisterWindowMessage("CommPanel.ShowPanel.6F2A");

        BuildChrome();

        _refreshDebounce.Interval = 200;
        _refreshDebounce.Tick += (_, _) =>
        {
            _refreshDebounce.Stop();
            RefreshDevices();
        };

        _meterTimer.Interval = 33; // ~30 Hz, smooth enough to read, cheap enough to ignore
        _meterTimer.Tick += OnMeterTick;

        _audio.EndpointsChanged += OnEndpointsChangedFromAudioService;

        _watcher.ShouldTrigger = exe => _settings.WatchProcesses && _settings.IsWatched(exe);
        _watcher.WatchedProgramActivated += OnWatchedProgramActivated;

        // Force the handle so hotkeys, cross-instance messages and marshalled COM callbacks
        // all work even when the app starts hidden in the tray.
        _ = Handle;

        // Subscribe before anything can raise these. RefreshDevices below opens the headset
        // watcher and sends the first status query, and a reply that arrives while nobody is
        // listening is lost for good: the watcher records the state, and every later reply is
        // then suppressed as an unchanged repeat.
        _headsetWatcher.HeadsetPoweredOff += OnHeadsetPoweredOff;
        _headsetWatcher.HeadsetPoweredOn += OnHeadsetPoweredOn;
        _headsetWatcher.SetProfiles(_settings.HeadsetProfiles);

        RefreshDevices();
        ApplyScale();
        BuildTrayIcon();
        ApplyRoundedCorners();

        if (_settings.WatchHeadsetPower)
        {
            _headsetWatcher.Rescan();
            if (_settings.QueryHeadsetStatus) QueryHeadsetStatus();
        }

        if (_settings.WatchProcesses) _watcher.Start();
        if (_settings.HotkeyEnabled) RegisterHotkey();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW - a borderless panel needs its own shadow.
            return cp;
        }
    }

    // ---------------------------------------------------------------- chrome

    private void BuildChrome()
    {
        _minimizeButton.Text = "—";
        _minimizeButton.Font = _labelFont;
        _minimizeButton.Click += (_, _) => HidePanel();

        _closeButton.Text = "✕";
        _closeButton.Font = _labelFont;
        _closeButton.Destructive = true;
        _closeButton.Click += (_, _) => HidePanel();

        _linkCommsButton.Font = _stencilFont;
        _linkCommsButton.Text = "LINK COMMS";
        _linkCommsButton.ShowLamp = true;
        _linkCommsButton.LampColor = PanelTheme.LampBlue;
        _linkCommsButton.IsOn = _settings.LinkCommunications;
        _linkCommsButton.Click += (_, _) =>
        {
            _settings.LinkCommunications = !_settings.LinkCommunications;
            _linkCommsButton.IsOn = _settings.LinkCommunications;
            _linkCommsButton.Invalidate();
            _settings.Save();
            SetStatus(_settings.LinkCommunications
                ? "COMMS ROLE FOLLOWS SELECTION"
                : "COMMS HELD INDEPENDENT — RIGHT-CLICK A DEVICE TO ASSIGN IT", PanelTheme.LampBlue);
        };

        _watchListButton.Font = _stencilFont;
        _watchListButton.Text = "SETTINGS";
        _watchListButton.Click += (_, _) => ShowSettings();

        _refreshButton.Font = _stencilFont;
        _refreshButton.Text = "RESCAN";
        _refreshButton.Click += (_, _) =>
        {
            RefreshDevices();
            SetStatus("DEVICE SCAN COMPLETE", PanelTheme.LampGreen);
        };

        BuildConsoleStrip(_outputMeter, _outputFader, _outputMute, "OUT",
            () => _outputControls, PanelTheme.LampGreen);
        BuildConsoleStrip(_inputMeter, _inputFader, _inputMute, "IN",
            () => _inputControls, PanelTheme.LampGreen);

        _mixerToggle.Font = _stencilFont;
        _mixerToggle.ShowLamp = true;
        _mixerToggle.LampColor = PanelTheme.LampGreen;
        _mixerToggle.IsOn = _settings.MixerExpanded;
        _mixerToggle.Text = MixerToggleCaption;
        _mixerToggle.Click += (_, _) => ToggleMixer();

        Controls.AddRange(new Control[]
        {
            _minimizeButton, _closeButton, _linkCommsButton, _watchListButton, _refreshButton,
            _outputMeter, _inputMeter, _outputFader, _inputFader, _outputMute, _inputMute,
            _mixerToggle
        });
    }

    /// <summary>A logical measurement scaled for both DPI and the user's chosen panel size.</summary>
    private int Scaled(int logical) =>
        LogicalToDeviceUnits((int)MathF.Round(logical * _settings.SafeFontScale));

    /// <summary>
    /// Rebuilds every font at the current size setting, pushes them into the controls, and
    /// re-lays-out the panel around them.
    ///
    /// Text and layout scale together deliberately. Enlarging only the font would spill it
    /// out of keys that stayed the same size; enlarging the whole panel keeps the proportions
    /// and simply gives the larger text more room, and the window resizes to suit.
    /// </summary>
    public void ApplyScale()
    {
        float scale = _settings.SafeFontScale;

        var oldFonts = new[] { _titleFont, _labelFont, _smallFont, _stencilFont };

        _titleFont = PanelTheme.TitleFont(scale);
        _labelFont = PanelTheme.LabelFont(scale);
        _smallFont = PanelTheme.SmallFont(scale);
        _stencilFont = PanelTheme.StencilFont(scale);

        foreach (Control control in Controls)
            if (control is ChassisControl chassisControl) chassisControl.UiScale = scale;

        _minimizeButton.Font = _labelFont;
        _closeButton.Font = _labelFont;
        _linkCommsButton.Font = _stencilFont;
        _watchListButton.Font = _stencilFont;
        _refreshButton.Font = _stencilFont;
        _mixerToggle.Font = _stencilFont;

        _outputMeter.CaptionFont = _stencilFont;
        _inputMeter.CaptionFont = _stencilFont;
        _outputFader.ReadoutFont = _stencilFont;
        _inputFader.ReadoutFont = _stencilFont;
        _outputMute.Font = _stencilFont;
        _inputMute.Font = _stencilFont;

        foreach (var button in _outputButtons.Concat(_inputButtons))
        {
            button.PrimaryFont = _labelFont;
            button.SecondaryFont = _smallFont;
            button.StencilFont = _stencilFont;
        }

        foreach (var row in _mixerRows)
        {
            row.Name.Font = _smallFont;
            row.Meter.CaptionFont = _stencilFont;
            row.Fader.ReadoutFont = _stencilFont;
            row.Mute.Font = _stencilFont;
        }

        LayoutPanel();
        ClampToScreen();

        // Disposed only after nothing refers to them any more.
        foreach (var font in oldFonts) font.Dispose();
    }

    private string MixerToggleCaption => _settings.MixerExpanded ? "APP MIXER  ▲" : "APP MIXER  ▼";

    private void ToggleMixer()
    {
        _settings.MixerExpanded = !_settings.MixerExpanded;
        _settings.Save();

        _mixerToggle.IsOn = _settings.MixerExpanded;
        _mixerToggle.Text = MixerToggleCaption;

        if (_settings.MixerExpanded)
        {
            EnsureSessionMixer();
            RefreshSessions();
        }
        else
        {
            CloseSessionMixer();
        }

        LayoutPanel();
        ClampToScreen();

        SetStatus(_settings.MixerExpanded ? "APP MIXER OPEN" : "APP MIXER CLOSED", PanelTheme.LampGreen);
    }

    /// <summary>
    /// Keeps the window on a monitor after it grows or shrinks. Expanding the mixer near the
    /// bottom of the screen would otherwise push the new rows off the edge.
    /// </summary>
    private void ClampToScreen()
    {
        var area = Screen.FromControl(this).WorkingArea;

        int x = Math.Min(Math.Max(Left, area.Left), Math.Max(area.Left, area.Right - Width));
        int y = Math.Min(Math.Max(Top, area.Top), Math.Max(area.Top, area.Bottom - Height));

        if (x != Left || y != Top) Location = new Point(x, y);
    }

    private void BuildConsoleStrip(LevelMeter meter, VolumeFader fader, PlateButton mute,
                                   string caption, Func<EndpointControls?> controls, Color accent)
    {
        meter.Caption = caption;
        meter.CaptionFont = _stencilFont;

        fader.ReadoutFont = _stencilFont;
        fader.ValueChanged += value => controls()?.WriteVolume(value);

        mute.Text = "MUTE";
        mute.Font = _stencilFont;
        mute.ShowLamp = true;
        mute.LampColor = PanelTheme.LampRed;
        mute.Click += (_, _) =>
        {
            var target = controls();
            if (target is null) return;

            bool muted = !(target.ReadMute() ?? false);
            target.WriteMute(muted);
            RefreshVolumeUi();

            SetStatus(caption + (muted ? " MUTED" : " UNMUTED"),
                      muted ? PanelTheme.LampRed : accent);
        };
    }

    private void ApplyRoundedCorners()
    {
        try
        {
            // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2. Silently ignored pre-Win11.
            int preference = 2;
            DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
        }
        catch
        {
            // Square corners are a perfectly good fallback.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void BuildTrayIcon()
    {
        _tray = new NotifyIcon
        {
            Icon = AppIcon.LoadTray() ?? SystemIcons.Application,
            Text = "CommPanel",
            Visible = true
        };

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Opening += (_, _) => BuildTrayMenu(menu);
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePanel();
        };
    }

    /// <summary>
    /// The tray menu is rebuilt each time it opens: device lists change, and holding menu
    /// items for devices that no longer exist would be both stale and wasteful.
    /// </summary>
    private void BuildTrayMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var open = new ToolStripMenuItem("Open CommPanel", null, (_, _) => ShowPanel(activate: true))
        {
            Font = new Font(menu.Font, FontStyle.Bold)
        };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());

        AddDeviceMenuSection(menu, "Output", _outputs);
        AddDeviceMenuSection(menu, "Input", _inputs);

        menu.Items.Add(new ToolStripSeparator());

        var linkComms = new ToolStripMenuItem("Switch communications device too", null, (_, _) =>
        {
            _settings.LinkCommunications = !_settings.LinkCommunications;
            _linkCommsButton.IsOn = _settings.LinkCommunications;
            _linkCommsButton.Invalidate();
            _settings.Save();
        })
        { Checked = _settings.LinkCommunications, CheckOnClick = false };
        menu.Items.Add(linkComms);

        var startWithWindows = new ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            bool enable = !StartupRegistration.IsEnabled;
            if (!StartupRegistration.SetEnabled(enable))
                ShowError("Could not update the Windows startup entry.");
        })
        { Checked = StartupRegistration.IsEnabled };
        menu.Items.Add(startWithWindows);

        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));
    }

    private void AddDeviceMenuSection(ContextMenuStrip menu, string caption, List<AudioDevice> devices)
    {
        var root = new ToolStripMenuItem(caption);
        if (devices.Count == 0)
        {
            root.DropDownItems.Add(new ToolStripMenuItem("No devices") { Enabled = false });
        }
        else
        {
            foreach (var device in devices)
            {
                var captured = device;
                var item = new ToolStripMenuItem(device.FullName, null, (_, _) => SwitchTo(captured))
                {
                    Checked = device.IsDefault
                };
                root.DropDownItems.Add(item);
            }
        }
        menu.Items.Add(root);
    }

    // ---------------------------------------------------------------- layout

    private void LayoutPanel()
    {
        int margin = Scaled(EdgeMargin);
        int headerHeight = Scaled(HeaderHeight);
        int sectionHeader = Scaled(SectionHeaderHeight);
        int buttonHeight = Scaled(ButtonHeight);
        int buttonGap = Scaled(ButtonGap);
        int columnWidth = Scaled(ColumnWidth);
        int columnGap = Scaled(ColumnGap);
        int bankPadding = Scaled(BankPadding);
        int footerHeight = Scaled(FooterHeight);
        int emptyBank = Scaled(EmptyBankHeight);

        int BankHeight(int count) => count == 0
            ? emptyBank
            : bankPadding * 2 + count * buttonHeight + (count - 1) * buttonGap;

        int outputBankHeight = BankHeight(_outputButtons.Count);
        int inputBankHeight = BankHeight(_inputButtons.Count);
        int bankHeight = Math.Max(outputBankHeight, inputBankHeight);

        int consolePadding = Scaled(8);
        int meterHeight = Scaled(26);
        int faderHeight = Scaled(22);
        int consoleHeight = _settings.ShowMeters
            ? consolePadding * 2 + meterHeight + Scaled(6) + faderHeight
            : 0;

        int mixerRowHeight = Scaled(26);
        int mixerRowGap = Scaled(6);
        int mixerPadding = Scaled(9);
        int mixerRows = _settings.MixerExpanded ? Math.Max(1, CountVisibleRows()) : 0;
        int mixerHeight = _settings.MixerExpanded
            ? mixerPadding * 2 + mixerRows * mixerRowHeight + (mixerRows - 1) * mixerRowGap
            : 0;

        int width = margin * 2 + columnWidth * 2 + columnGap;
        int bodyTop = headerHeight + Scaled(4);
        int bankBottom = bodyTop + sectionHeader + bankHeight;
        int consoleTop = bankBottom + (consoleHeight > 0 ? Scaled(8) : 0);

        int mixerHeaderTop = consoleTop + consoleHeight + Scaled(10);
        int mixerHeaderHeight = Scaled(26);
        int mixerTop = mixerHeaderTop + mixerHeaderHeight + Scaled(6);

        int footerTop = mixerTop + mixerHeight + Scaled(10);
        int height = footerTop + footerHeight + Scaled(4);

        ClientSize = new Size(width, height);

        int leftX = margin;
        int rightX = margin + columnWidth + columnGap;

        _outputHeader = new Rectangle(leftX, bodyTop, columnWidth, sectionHeader);
        _inputHeader = new Rectangle(rightX, bodyTop, columnWidth, sectionHeader);
        _outputBank = new Rectangle(leftX, bodyTop + sectionHeader, columnWidth, bankHeight);
        _inputBank = new Rectangle(rightX, bodyTop + sectionHeader, columnWidth, bankHeight);
        _outputConsole = new Rectangle(leftX, consoleTop, columnWidth, consoleHeight);
        _inputConsole = new Rectangle(rightX, consoleTop, columnWidth, consoleHeight);
        _footerRect = new Rectangle(0, footerTop, width, footerHeight);

        PlaceBank(_outputButtons, _outputBank, bankPadding, buttonHeight, buttonGap);
        PlaceBank(_inputButtons, _inputBank, bankPadding, buttonHeight, buttonGap);

        PlaceConsole(_outputConsole, _outputMeter, _outputFader, _outputMute,
                     consolePadding, meterHeight, faderHeight);
        PlaceConsole(_inputConsole, _inputMeter, _inputFader, _inputMute,
                     consolePadding, meterHeight, faderHeight);

        _mixerPlate = new Rectangle(margin, mixerTop, width - margin * 2, mixerHeight);
        _mixerToggle.Bounds = new Rectangle(margin, mixerHeaderTop,
                                            Scaled(126), mixerHeaderHeight);
        _mixerHeader = new Rectangle(_mixerToggle.Right + Scaled(12), mixerHeaderTop,
                                     width - margin - _mixerToggle.Right - Scaled(12),
                                     mixerHeaderHeight);

        PlaceMixer(_mixerPlate, mixerPadding, mixerRowHeight, mixerRowGap);

        // Title-bar controls.
        int chromeButtonWidth = Scaled(34);
        int chromeButtonHeight = Scaled(24);
        int chromeTop = Scaled(10);
        _closeButton.Bounds = new Rectangle(width - margin - chromeButtonWidth, chromeTop,
                                            chromeButtonWidth, chromeButtonHeight);
        _minimizeButton.Bounds = new Rectangle(_closeButton.Left - chromeButtonWidth - Scaled(6),
                                               chromeTop, chromeButtonWidth, chromeButtonHeight);

        // Footer controls, laid out right to left.
        int footerButtonHeight = Scaled(26);
        int footerButtonTop = footerTop + (footerHeight - footerButtonHeight) / 2;
        int gap = Scaled(8);

        int refreshWidth = Scaled(76);
        int watchWidth = Scaled(86);
        int linkWidth = Scaled(124);

        _refreshButton.Bounds = new Rectangle(width - margin - refreshWidth, footerButtonTop, refreshWidth, footerButtonHeight);
        _watchListButton.Bounds = new Rectangle(_refreshButton.Left - gap - watchWidth, footerButtonTop, watchWidth, footerButtonHeight);
        _linkCommsButton.Bounds = new Rectangle(_watchListButton.Left - gap - linkWidth, footerButtonTop, linkWidth, footerButtonHeight);

        _statusRect = new Rectangle(margin, footerTop, _linkCommsButton.Left - margin - gap, footerHeight);

        RebuildChassis();
    }

    /// <summary>
    /// Opens the session mixer for the current output device, if the section is expanded.
    /// </summary>
    private void EnsureSessionMixer()
    {
        string? outputId = _outputControls?.DeviceId;

        if (!_settings.MixerExpanded || outputId is null)
        {
            CloseSessionMixer();
            return;
        }

        if (string.Equals(_mixer?.DeviceId, outputId, StringComparison.OrdinalIgnoreCase)) return;

        CloseSessionMixer();
        _mixer = _audio.OpenSessionMixer(outputId);
    }

    private void CloseSessionMixer()
    {
        _mixer?.Dispose();
        _mixer = null;

        foreach (var row in _mixerRows) row.Session = null;
    }

    /// <summary>
    /// Re-enumerates the applications and binds them to rows. Rows are created once and
    /// rebound rather than rebuilt, so a fader being dragged is not yanked away when an
    /// unrelated application starts or stops playing.
    /// </summary>
    private void RefreshSessions()
    {
        if (_mixer is null || !_settings.MixerExpanded) return;

        var sessions = _mixer.Refresh();
        if (sessions.Count > MaxMixerRows) sessions = sessions.Take(MaxMixerRows).ToList();

        bool countChanged = CountVisibleRows() != sessions.Count;

        while (_mixerRows.Count < sessions.Count) AddMixerRow();

        for (int i = 0; i < _mixerRows.Count; i++)
        {
            var row = _mixerRows[i];

            if (i >= sessions.Count)
            {
                row.Session = null;
                row.SetVisible(false);
                continue;
            }

            var session = sessions[i];
            bool rebound = !ReferenceEquals(row.Session, session);
            row.Session = session;

            if (rebound)
            {
                row.Name.Text = session.DisplayName;
                row.Meter.Reset();
                row.Name.Invalidate();
            }

            row.Name.IsLit = session.State == AudioSessionState.Active;

            if (!row.Fader.IsDragging)
            {
                float? volume = session.ReadVolume();
                if (volume is not null) row.Fader.Value = volume.Value;
            }

            bool muted = session.ReadMute();
            if (row.Mute.IsOn != muted)
            {
                row.Mute.IsOn = muted;
                row.Mute.Invalidate();
            }

            row.Meter.IsInactive = muted;
            row.SetVisible(true);
        }

        if (countChanged) LayoutPanel();
    }

    private int CountVisibleRows() => _mixerRows.Count(r => r.Session is not null);

    private void AddMixerRow()
    {
        var row = new MixerRow
        {
            Name = new PlateLabel { Font = _smallFont, ShowLamp = true, TextColor = PanelTheme.TextPrimary, UiScale = _settings.SafeFontScale },
            Meter = new LevelMeter { CaptionFont = _stencilFont, UiScale = _settings.SafeFontScale },
            Fader = new VolumeFader { ReadoutFont = _stencilFont, UiScale = _settings.SafeFontScale },
            Mute = new PlateButton
            {
                Text = "MUTE",
                Font = _stencilFont,
                ShowLamp = true,
                LampColor = PanelTheme.LampRed,
                UiScale = _settings.SafeFontScale
            }
        };

        var captured = row;
        row.Fader.ValueChanged += value => captured.Session?.WriteVolume(value);
        row.Mute.Click += (_, _) =>
        {
            var session = captured.Session;
            if (session is null) return;

            bool muted = !session.ReadMute();
            session.WriteMute(muted);
            captured.Mute.IsOn = muted;
            captured.Mute.Invalidate();
            captured.Meter.IsInactive = muted;

            SetStatus(captured.Name.Text.ToUpperInvariant() + (muted ? " MUTED" : " UNMUTED"),
                      muted ? PanelTheme.LampRed : PanelTheme.LampGreen);
        };

        _mixerRows.Add(row);
        foreach (var control in row.Controls()) Controls.Add(control);
    }

    /// <summary>Lays out the application rows inside the mixer plate.</summary>
    private void PlaceMixer(Rectangle plate, int padding, int rowHeight, int rowGap)
    {
        int visible = CountVisibleRows();

        for (int i = 0; i < _mixerRows.Count; i++)
        {
            var row = _mixerRows[i];

            if (i >= visible || plate.Height <= 0)
            {
                row.SetVisible(false);
                continue;
            }

            int top = plate.Y + padding + i * (rowHeight + rowGap);
            int left = plate.X + padding;
            int available = plate.Width - padding * 2;

            int nameWidth = Scaled(150);
            int faderWidth = Scaled(180);
            int muteWidth = Scaled(74);
            int gap = Scaled(10);
            int meterWidth = Math.Max(Scaled(60),
                available - nameWidth - faderWidth - muteWidth - gap * 3);

            row.Name.Bounds = new Rectangle(left, top, nameWidth, rowHeight);
            row.Meter.Bounds = new Rectangle(left + nameWidth + gap, top, meterWidth, rowHeight);
            row.Fader.Bounds = new Rectangle(left + nameWidth + gap + meterWidth + gap, top,
                                             faderWidth, rowHeight);
            row.Mute.Bounds = new Rectangle(plate.Right - padding - muteWidth,
                                            top + (rowHeight - Scaled(22)) / 2,
                                            muteWidth, Scaled(22));
            row.SetVisible(true);
        }
    }

    /// <summary>Lays out one channel strip: meter above, fader and mute below.</summary>
    private void PlaceConsole(Rectangle console, LevelMeter meter, VolumeFader fader, PlateButton mute,
                              int padding, int meterHeight, int faderHeight)
    {
        bool visible = console.Height > 0;
        meter.Visible = visible;
        fader.Visible = visible;
        mute.Visible = visible;
        if (!visible) return;

        int inner = console.Width - padding * 2;
        int muteWidth = Scaled(74);
        int gap = Scaled(8);

        meter.Bounds = new Rectangle(console.X + padding, console.Y + padding, inner, meterHeight);

        int rowTop = console.Y + padding + meterHeight + Scaled(6);
        fader.Bounds = new Rectangle(console.X + padding, rowTop,
                                     Math.Max(40, inner - muteWidth - gap), faderHeight);
        mute.Bounds = new Rectangle(console.Right - padding - muteWidth, rowTop, muteWidth, faderHeight);
    }

    private void PlaceBank(List<LampButton> buttons, Rectangle bank, int padding, int buttonHeight, int gap)
    {
        int x = bank.X + padding;
        int y = bank.Y + padding;
        int w = bank.Width - padding * 2;

        foreach (var button in buttons)
        {
            button.Bounds = new Rectangle(x, y, w, buttonHeight);
            y += buttonHeight + gap;
        }
    }

    /// <summary>
    /// Paints the static parts of the panel - texture, recessed banks, engraved section
    /// titles, screws - into a bitmap. Every later repaint is a blit rather than a redraw,
    /// which is what keeps the panel cheap to show over a running game.
    /// </summary>
    private void RebuildChassis()
    {
        _chassis?.Dispose();
        _chassis = PanelTheme.CreateChassis(ClientSize.Width, ClientSize.Height);

        using (var g = Graphics.FromImage(_chassis))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float radius = Scaled(6);
            PanelTheme.DrawRecess(g, _outputBank, radius);
            PanelTheme.DrawRecess(g, _inputBank, radius);
            if (_outputConsole.Height > 0) PanelTheme.DrawRecess(g, _outputConsole, radius);
            if (_inputConsole.Height > 0) PanelTheme.DrawRecess(g, _inputConsole, radius);
            if (_mixerPlate.Height > 0) PanelTheme.DrawRecess(g, _mixerPlate, radius);

            if (_mixerHeader.Width > 0)
            {
                TextRenderer.DrawText(g, _settings.MixerExpanded ? "PLAYING NOW" : "PER-APPLICATION VOLUME",
                    _stencilFont, _mixerHeader, Color.FromArgb(120, PanelTheme.TextSecondary),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }

            DrawSectionHeader(g, _outputHeader, "OUTPUT", "PLAYBACK DEVICE", PanelTheme.LampGreen, _outputButtons.Count);
            DrawSectionHeader(g, _inputHeader, "INPUT", "RECORDING DEVICE", PanelTheme.LampGreen, _inputButtons.Count);

            if (_outputButtons.Count == 0) DrawEmptyBank(g, _outputBank);
            if (_inputButtons.Count == 0) DrawEmptyBank(g, _inputBank);

            // Header and footer separators.
            int headerY = Scaled(HeaderHeight);
            using (var dark = new Pen(Color.FromArgb(150, PanelTheme.EdgeShadow)))
                g.DrawLine(dark, 0, headerY, ClientSize.Width, headerY);
            using (var light = new Pen(Color.FromArgb(40, PanelTheme.EdgeHighlight)))
                g.DrawLine(light, 0, headerY + 1, ClientSize.Width, headerY + 1);

            using (var dark = new Pen(Color.FromArgb(150, PanelTheme.EdgeShadow)))
                g.DrawLine(dark, 0, _footerRect.Top, ClientSize.Width, _footerRect.Top);
            using (var light = new Pen(Color.FromArgb(40, PanelTheme.EdgeHighlight)))
                g.DrawLine(light, 0, _footerRect.Top + 1, ClientSize.Width, _footerRect.Top + 1);

            float screw = Scaled(9);
            float inset = Scaled(9);
            PanelTheme.DrawScrew(g, inset, inset, screw);
            PanelTheme.DrawScrew(g, ClientSize.Width - inset, inset, screw);
            PanelTheme.DrawScrew(g, inset, ClientSize.Height - inset, screw);
            PanelTheme.DrawScrew(g, ClientSize.Width - inset, ClientSize.Height - inset, screw);
        }

        foreach (Control control in Controls)
        {
            if (control is ChassisControl chassisControl)
            {
                chassisControl.Backdrop = _chassis;
                chassisControl.Invalidate();
            }
        }

        Invalidate();
    }

    private void DrawSectionHeader(Graphics g, Rectangle rect, string title, string subtitle, Color accent, int count)
    {
        int lampSize = Scaled(9);
        var lampRect = new RectangleF(rect.X + Scaled(2),
                                      rect.Y + (rect.Height - lampSize) / 2f,
                                      lampSize, lampSize);
        PanelTheme.DrawLamp(g, lampRect, accent, count > 0, 0.55f);

        int textLeft = rect.X + Scaled(16);
        var titleRect = new Rectangle(textLeft, rect.Y, rect.Width - Scaled(16), rect.Height);
        PanelTheme.DrawEngraved(g, title, _stencilFont, titleRect,
            PanelTheme.Blend(accent, PanelTheme.TextPrimary, 0.45f),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        var subtitleRect = new Rectangle(rect.X, rect.Y, rect.Width - Scaled(2), rect.Height);
        TextRenderer.DrawText(g, subtitle, _stencilFont, subtitleRect,
            Color.FromArgb(120, PanelTheme.TextSecondary),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    private void DrawEmptyBank(Graphics g, Rectangle bank)
    {
        TextRenderer.DrawText(g, "NO ACTIVE DEVICES", _stencilFont, bank,
            Color.FromArgb(150, PanelTheme.TextSecondary),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        if (_chassis is not null) g.DrawImageUnscaled(_chassis, 0, 0);
        else g.Clear(PanelTheme.ChassisBottom);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        int margin = Scaled(EdgeMargin);
        int headerHeight = Scaled(HeaderHeight);

        // Master lamp - lit whenever the panel is live.
        int lampSize = Scaled(18);
        var lampRect = new RectangleF(margin, (headerHeight - lampSize) / 2f, lampSize, lampSize);
        PanelTheme.DrawLamp(g, lampRect, PanelTheme.LampGreen, true, 0.85f);

        int titleLeft = margin + lampSize + Scaled(12);
        int titleHeight = TextRenderer.MeasureText(g, "Ag", _titleFont).Height;
        int subtitleHeight = TextRenderer.MeasureText(g, "Ag", _stencilFont).Height;
        int block = titleHeight + subtitleHeight;
        int top = (headerHeight - block) / 2;

        PanelTheme.DrawEngraved(g, "COMM PANEL", _titleFont,
            new Rectangle(titleLeft, top, ClientSize.Width - titleLeft, titleHeight),
            PanelTheme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        TextRenderer.DrawText(g, "AUDIO ROUTING CONTROL", _stencilFont,
            new Rectangle(titleLeft, top + titleHeight, ClientSize.Width - titleLeft, subtitleHeight),
            Color.FromArgb(160, PanelTheme.TextSecondary),
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        // Footer status readout.
        TextRenderer.DrawText(g, _statusText, _stencilFont, _statusRect, _statusColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    // ----------------------------------------------------------- device data

    private void OnEndpointsChangedFromAudioService()
    {
        // Arrives on an MTA thread from the audio service; hop to the UI thread and coalesce,
        // because Windows fires several of these for a single device change.
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _refreshDebounce.Stop();
                _refreshDebounce.Start();
            }));
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
        catch (InvalidOperationException)
        {
            // Handle went away between the check and the call.
        }
    }

    public void RefreshDevices()
    {
        // The unfiltered lists decide whether a device still exists; the filtered ones drive
        // the panel and supply failover candidates, so hiding a device also excludes it.
        var allOutputs = _audio.GetDevices(EDataFlow.Render);
        var allInputs = _audio.GetDevices(EDataFlow.Capture);

        MarkOfflineDevices(allOutputs);
        MarkOfflineDevices(allInputs);

        _outputs = VisibleDevices(allOutputs);
        _inputs = VisibleDevices(allInputs);

        bool layoutChanged = SyncBank(_outputButtons, _outputs, PanelTheme.LampGreen);
        layoutChanged |= SyncBank(_inputButtons, _inputs, PanelTheme.LampGreen);

        if (layoutChanged)
        {
            LayoutPanel();

            // A base station that was plugged in after startup shows up as an endpoint
            // change, so its HID interface can be picked up without any polling.
            _headsetWatcher.SetProfiles(_settings.HeadsetProfiles);
        if (_settings.WatchHeadsetPower)
        {
            _headsetWatcher.Rescan();
            if (_settings.QueryHeadsetStatus) QueryHeadsetStatus();
        }
        }

        UpdateTrayText();
        SyncControls();

        string? lostOutput = TakeLostDevice(ref _activeOutputId, allOutputs);
        string? lostInput = TakeLostDevice(ref _activeInputId, allInputs);

        if (_settings.AutoFallback && !_handlingFallback)
        {
            if (lostOutput is not null) FailOver(EDataFlow.Render, _outputs, lostOutput, isOutput: true);
            if (lostInput is not null) FailOver(EDataFlow.Capture, _inputs, lostInput, isOutput: false);
        }
    }

    /// <summary>
    /// A wireless headset powered down. Windows still believes its endpoint is alive, so the
    /// loss is injected here by adapter name and handed to the ordinary failover path.
    /// </summary>
    private void OnHeadsetPoweredOff(string adapterName) => MarshalToUi(() =>
    {
        // Mark the device offline whether or not it was the one in use. A headset that times
        // out while unselected is exactly the case that used to go unnoticed: the panel went
        // on offering it, and picking it produced silence.
        RefreshWithoutFallback();
        SetStatus(adapterName.ToUpperInvariant() + " POWERED OFF", PanelTheme.LampAmber);

        if (!_settings.AutoFallback) return;

        _returnOutputId = FailOverAwayFrom(EDataFlow.Render, _outputs, adapterName, isOutput: true);
        _returnInputId = FailOverAwayFrom(EDataFlow.Capture, _inputs, adapterName, isOutput: false);
    });

    /// <summary>
    /// The headset came back. This only undoes a switch we made ourselves, and only while
    /// the fallback device is still the one in use - a manual choice in the meantime wins.
    /// </summary>
    private void OnHeadsetPoweredOn(string adapterName) => MarshalToUi(() =>
    {
        string? returnOutput = _returnOutputId;
        string? returnInput = _returnInputId;
        _returnOutputId = null;
        _returnInputId = null;

        // The headset is back, so a future outage deserves a fresh prompt.
        _offlinePrompts.Clear();

        // Clears the offline mark on the headset's devices.
        RefreshWithoutFallback();

        if (!_settings.ReturnToHeadset)
        {
            SetStatus(adapterName.ToUpperInvariant() + " BACK ON", PanelTheme.LampGreen);
            return;
        }

        // The endpoint list is unchanged as far as Windows is concerned, so no rescan is
        // needed - the headset's endpoints were never removed in the first place.
        bool restored = ReturnTo(EDataFlow.Render, _outputs, returnOutput);
        restored |= ReturnTo(EDataFlow.Capture, _inputs, returnInput);

        if (restored) SetStatus(adapterName.ToUpperInvariant() + " BACK ON — RESTORED", PanelTheme.LampGreen);
    });

    /// <summary>
    /// Switches away from every endpoint belonging to <paramref name="adapterName"/>, and
    /// returns the endpoint id that was in use so it can be restored later.
    /// </summary>
    private string? FailOverAwayFrom(EDataFlow flow, List<AudioDevice> available, string adapterName, bool isOutput)
    {
        var current = available.FirstOrDefault(d => d.IsDefault);
        if (current is null) return null;
        if (!current.Adapter.Contains(adapterName, StringComparison.OrdinalIgnoreCase)) return null;

        // Candidates exclude everything on the headset that just died, not merely the one
        // endpoint in use - its other endpoints are equally deaf.
        var candidates = available
            .Where(d => !d.Adapter.Contains(adapterName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var replacement = FallbackPolicy.ChooseReplacement(candidates, _settings.PriorityFor(isOutput), current.Id);
        if (replacement is null)
        {
            OfferHiddenFallback(flow, current, isOutput,
                d => d.Adapter.Contains(adapterName, StringComparison.OrdinalIgnoreCase));
            return null;
        }

        if (!_audio.SetDefault(replacement.Id, flow, _settings.LinkCommunications, out string? error))
        {
            SetStatus("FAILOVER FAILED — " + (error ?? "unknown error"), PanelTheme.LampRed);
            return null;
        }

        RefreshWithoutFallback();
        return current.Id;
    }

    /// <summary>
    /// Last resort when a device dies and the panel holds no replacement: ask whether to use
    /// a hidden one.
    ///
    /// CommPanel never switches to a hidden device by itself - hiding something means "do not
    /// offer me this", and quietly overriding that would make the setting untrustworthy. But
    /// leaving the user on a dead device with a working one sitting hidden is worse still, so
    /// this asks and lets them decide.
    /// </summary>
    private void OfferHiddenFallback(EDataFlow flow, AudioDevice? lost, bool isOutput,
                                     Func<AudioDevice, bool> isDead)
    {
        if (_settings.HiddenDeviceIds.Count == 0) return;

        string promptKey = flow + "|" + (lost?.Id ?? "unknown");
        if (!_offlinePrompts.Add(promptKey)) return; // already asked about this one

        var hidden = _audio.GetDevices(flow)
            .Where(d => _settings.HiddenDeviceIds.Contains(d.Id))
            .Where(d => !isDead(d))
            .ToList();

        MarkOfflineDevices(hidden);
        hidden = hidden.Where(d => !d.IsOffline).ToList();
        if (hidden.Count == 0) return;

        // Best candidate first, using the same ranking as automatic failover.
        var ranked = new List<AudioDevice>();
        var pool = hidden.ToList();
        while (pool.Count > 0)
        {
            var next = FallbackPolicy.ChooseReplacement(pool, _settings.PriorityFor(isOutput), null);
            if (next is null) break;
            ranked.Add(next);
            pool.Remove(next);
        }
        if (ranked.Count == 0) ranked = hidden;

        string lostName = lost?.ShortName ?? (isOutput ? "The output device" : "The input device");

        using var dialog = new OfflineFallbackDialog(lostName, isOutput, ranked);
        bool wasTopMost = TopMost;
        TopMost = false;

        var result = dialog.ShowDialog(Visible ? this : null);
        TopMost = wasTopMost;

        if (result != DialogResult.OK || dialog.Chosen is null) return;

        var chosen = dialog.Chosen;

        if (dialog.ShouldUnhide)
        {
            _settings.HiddenDeviceIds.RemoveAll(id => string.Equals(id, chosen.Id, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
        }

        if (_audio.SetDefault(chosen.Id, flow, _settings.LinkCommunications, out string? error))
        {
            _settings.RememberChoice(isOutput, chosen.Id);
            _settings.Save();
            SetStatus((isOutput ? "OUTPUT" : "INPUT") + " → " + chosen.ShortName.ToUpperInvariant(),
                      PanelTheme.LampGreen);
            RefreshWithoutFallback();
        }
        else
        {
            SetStatus("SWITCH FAILED — " + (error ?? "unknown error"), PanelTheme.LampRed);
        }
    }

    private bool ReturnTo(EDataFlow flow, List<AudioDevice> available, string? deviceId)
    {
        if (deviceId is null) return false;

        var target = available.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.IsDefault) return false;

        if (!_audio.SetDefault(target.Id, flow, _settings.LinkCommunications, out _)) return false;

        RefreshWithoutFallback();
        return true;
    }

    /// <summary>Re-reads devices without letting the pass be treated as another device loss.</summary>
    private void RefreshWithoutFallback()
    {
        _handlingFallback = true;
        try { RefreshDevices(); }
        finally { _handlingFallback = false; }
    }

    /// <summary>
    /// Asks the headset for its state, then re-reads the device list a moment later.
    ///
    /// The refresh is not redundant. The watcher raises an event only on a *change*, so a
    /// reply confirming a state it already holds is silent by design - correct for
    /// transitions, useless for establishing the state at startup. Reading the watcher's
    /// current answer directly makes the result independent of whether an event fired.
    /// </summary>
    private void QueryHeadsetStatus()
    {
        _headsetWatcher.Query();

        var settle = new System.Windows.Forms.Timer { Interval = 400 };
        settle.Tick += (sender, _) =>
        {
            settle.Stop();
            settle.Dispose();
            if (!IsDisposed) RefreshWithoutFallback();
        };
        settle.Start();
    }

    /// <summary>Hops a background-thread callback onto the UI thread, safely during shutdown.</summary>
    private void MarshalToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Updates the tracked default for one direction and reports the endpoint id that has
    /// just vanished, if any. Returns null on the ordinary case where the device is still
    /// there, and on the first pass, when there is no previous state to compare against.
    /// </summary>
    private static string? TakeLostDevice(ref string? trackedId, List<AudioDevice> allDevices)
    {
        string? previous = trackedId;
        trackedId = allDevices.FirstOrDefault(d => d.IsDefault)?.Id;

        if (previous is null) return null;
        if (allDevices.Any(d => string.Equals(d.Id, previous, StringComparison.OrdinalIgnoreCase)))
            return null;

        return previous;
    }

    /// <summary>
    /// Moves to the best remaining device after the one in use disappeared. Windows has
    /// usually already chosen a replacement of its own by this point; this overrides that
    /// choice unless it happens to agree.
    /// </summary>
    private void FailOver(EDataFlow flow, List<AudioDevice> available, string lostDeviceId, bool isOutput)
    {
        string direction = isOutput ? "OUTPUT" : "INPUT";

        var replacement = FallbackPolicy.ChooseReplacement(available, _settings.PriorityFor(isOutput), lostDeviceId);
        if (replacement is null)
        {
            SetStatus(direction + " DEVICE OFFLINE — NO REPLACEMENT AVAILABLE", PanelTheme.LampRed);
            OfferHiddenFallback(flow, null, isOutput, d => string.Equals(d.Id, lostDeviceId, StringComparison.OrdinalIgnoreCase));
            return;
        }

        if (replacement.IsDefault)
        {
            // Windows landed on the same device we would have picked; nothing to do.
            SetStatus(direction + " DEVICE OFFLINE → " + replacement.ShortName.ToUpperInvariant(),
                      PanelTheme.LampAmber);
            return;
        }

        if (!_audio.SetDefault(replacement.Id, flow, _settings.LinkCommunications, out string? error))
        {
            SetStatus(direction + " FAILOVER FAILED — " + (error ?? "unknown error"), PanelTheme.LampRed);
            return;
        }

        SetStatus(direction + " DEVICE OFFLINE → " + replacement.ShortName.ToUpperInvariant(),
                  PanelTheme.LampAmber);

        // Re-read so the lamps follow immediately. The guard stops this pass from being
        // treated as another device loss and recursing.
        _handlingFallback = true;
        try { RefreshDevices(); }
        finally { _handlingFallback = false; }
    }

    /// <summary>
    /// Flags endpoints whose hardware is known to be powered down. Windows still lists these
    /// as active because the base station is plugged in, so without this the panel offers a
    /// headset that is sitting on the desk switched off.
    /// </summary>
    private void MarkOfflineDevices(List<AudioDevice> devices)
    {
        var poweredOff = _settings.WatchHeadsetPower
            ? _headsetWatcher.PoweredOffAdapters()
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            device.IsOffline = poweredOff.Count > 0 &&
                               !string.IsNullOrEmpty(device.Adapter) &&
                               poweredOff.Any(name => device.Adapter.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private List<AudioDevice> VisibleDevices(List<AudioDevice> devices) =>
        _settings.HiddenDeviceIds.Count == 0
            ? devices
            : devices.Where(d => !_settings.HiddenDeviceIds.Contains(d.Id)).ToList();

    /// <summary>
    /// Reconciles a bank of buttons with the current device list. Returns true when the set
    /// of devices changed and the panel therefore has to be re-laid-out; a mere change of
    /// which device is default only repaints.
    /// </summary>
    private bool SyncBank(List<LampButton> buttons, List<AudioDevice> devices, Color lampColor)
    {
        bool sameSet = buttons.Count == devices.Count;
        if (sameSet)
        {
            for (int i = 0; i < devices.Count; i++)
            {
                if (!string.Equals(buttons[i].Device?.Id, devices[i].Id, StringComparison.OrdinalIgnoreCase))
                {
                    sameSet = false;
                    break;
                }
            }
        }

        if (sameSet)
        {
            for (int i = 0; i < devices.Count; i++)
            {
                var button = buttons[i];
                bool changed = button.IsActive != devices[i].IsDefault ||
                               button.IsCommunications != devices[i].IsDefaultCommunications ||
                               !string.Equals(button.Device?.FullName, devices[i].FullName, StringComparison.Ordinal);
                button.Device = devices[i];
                button.IsActive = devices[i].IsDefault;
                button.IsCommunications = devices[i].IsDefaultCommunications;
                if (changed) button.Invalidate();
            }
            return false;
        }

        foreach (var button in buttons)
        {
            Controls.Remove(button);
            button.Dispose();
        }
        buttons.Clear();

        foreach (var device in devices)
        {
            var captured = device;
            var button = new LampButton
            {
                Device = device,
                LampColor = lampColor,
                IsActive = device.IsDefault,
                IsCommunications = device.IsDefaultCommunications,
                PrimaryFont = _labelFont,
                SecondaryFont = _smallFont,
                StencilFont = _stencilFont,
                Backdrop = _chassis,
                UiScale = _settings.SafeFontScale
            };
            button.Click += (_, _) => SwitchTo(captured);
            button.CommsRequested += (_, _) => AssignCommunications(captured);
            buttons.Add(button);
            Controls.Add(button);
        }

        return true;
    }

    /// <summary>
    /// Assigns the Communications role only, leaving the normal default alone. This is what
    /// makes "game on the speakers, voice chat in the headset" possible: Windows keeps two
    /// separate defaults, and applications that identify themselves as communication apps
    /// ask for the second one.
    /// </summary>
    private void AssignCommunications(AudioDevice device)
    {
        if (!_audio.SetDefaultCommunications(device.Id, out string? error))
        {
            SetStatus("COMMS SWITCH FAILED — " + (error ?? "unknown error"), PanelTheme.LampRed);
            return;
        }

        // With LINK COMMS lit, the next ordinary switch will move this again. Say so rather
        // than letting the assignment quietly evaporate later.
        SetStatus(_settings.LinkCommunications
                ? "COMMS → " + device.ShortName.ToUpperInvariant() + " (UNTIL NEXT SWITCH — UNLINK TO KEEP)"
                : "COMMS → " + device.ShortName.ToUpperInvariant(),
            PanelTheme.LampBlue);

        RefreshWithoutFallback();
    }

    private void SwitchTo(AudioDevice device)
    {
        bool includeComms = _settings.LinkCommunications;

        if (!_audio.SetDefault(device.Id, device.Flow, includeComms, out string? error))
        {
            SetStatus("SWITCH FAILED — " + (error ?? "unknown error"), PanelTheme.LampRed);
            return;
        }

        string direction = device.Flow == EDataFlow.Render ? "OUTPUT" : "INPUT";

        // Selecting an offline device is allowed - you may be about to switch the headset on
        // - but it should not look like it worked when no sound will come out.
        if (device.IsOffline)
        {
            SetStatus(direction + " → " + device.ShortName.ToUpperInvariant() + " (POWERED OFF)",
                      PanelTheme.LampAmber);
        }
        else
        {
            SetStatus(direction + " → " + device.ShortName.ToUpperInvariant(), PanelTheme.LampGreen);
        }

        // A deliberate choice cancels any pending auto-return and shapes the failover order.
        if (device.Flow == EDataFlow.Render) _returnOutputId = null; else _returnInputId = null;

        _settings.RememberChoice(device.Flow == EDataFlow.Render, device.Id);
        _settings.Save();

        // Repaint immediately rather than waiting for the audio service callback, so the
        // lamp moves on the same frame as the click.
        RefreshDevices();

        if (_settings.AutoHideAfterSwitch) HidePanel();
    }

    private void SetStatus(string text, Color color)
    {
        _statusText = text;
        _statusColor = color;
        Invalidate(_statusRect);
    }

    /// <summary>
    /// Points the meter and volume controls at whichever device is now default. The COM
    /// objects are held open between ticks rather than re-activated each time, so a meter
    /// tick is two calls and nothing else.
    /// </summary>
    private void SyncControls()
    {
        string? outputId = _outputs.FirstOrDefault(d => d.IsDefault)?.Id;
        string? inputId = _inputs.FirstOrDefault(d => d.IsDefault)?.Id;

        if (!string.Equals(_outputControls?.DeviceId, outputId, StringComparison.OrdinalIgnoreCase))
        {
            // The session mixer belongs to the output device, so it follows this change.
            // The capture meter does not, and is left alone.
            CloseSessionMixer();
            _outputControls?.Dispose();
            _outputControls = outputId is null ? null : _audio.OpenControls(outputId);
            _outputMeter.Reset();
        }

        if (!string.Equals(_inputControls?.DeviceId, inputId, StringComparison.OrdinalIgnoreCase))
        {
            _inputControls?.Dispose();
            _inputControls = inputId is null ? null : _audio.OpenControls(inputId);
            _inputMeter.Reset();
        }

        RefreshVolumeUi();
        EnsureCaptureMeter();
        EnsureSessionMixer();
        RefreshSessions();
    }

    /// <summary>
    /// Pulls volume and mute back from Windows. Also runs periodically while the panel is
    /// open, so the fader follows the volume keys and the tray slider.
    /// </summary>
    private void RefreshVolumeUi()
    {
        RefreshOne(_outputControls, _outputFader, _outputMute, _outputMeter);
        RefreshOne(_inputControls, _inputFader, _inputMute, _inputMeter);

        static void RefreshOne(EndpointControls? controls, VolumeFader fader, PlateButton mute, LevelMeter meter)
        {
            bool muted = controls?.ReadMute() ?? false;
            float? volume = controls?.ReadVolume();

            // Never fight the user's hand: a drag in progress owns the value.
            if (!fader.IsDragging && volume is not null) fader.Value = volume.Value;

            bool inactive = controls is null || volume is null;
            if (fader.IsInactive != inactive)
            {
                fader.IsInactive = inactive;
                fader.Invalidate();
            }

            if (mute.IsOn != muted)
            {
                mute.IsOn = muted;
                mute.Invalidate();
            }

            bool meterInactive = controls is null || muted;
            if (meter.IsInactive != meterInactive)
            {
                meter.IsInactive = meterInactive;
                meter.Invalidate();
            }
        }
    }

    /// <summary>
    /// Reads both meters. Runs only while the panel is on screen - see <see cref="_meterTimer"/>.
    /// </summary>
    private void OnMeterTick(object? sender, EventArgs e)
    {
        if (!Visible)
        {
            StopMetering();
            return;
        }

        // Playback endpoints meter themselves. A microphone only reports a level while
        // something is capturing from it, so it is read from our own capture stream when we
        // have one, and otherwise from the endpoint meter - which still shows a level when
        // another application happens to be recording.
        float inputPeak = _inputCapture is not null
            ? _inputCapture.ReadPeak()
            : _inputControls?.ReadPeak() ?? 0f;

        if (_outputMeter.Feed(_outputControls?.ReadPeak() ?? 0f)) _outputMeter.Invalidate();
        if (_inputMeter.Feed(inputPeak)) _inputMeter.Invalidate();

        if (_settings.MixerExpanded)
        {
            foreach (var row in _mixerRows)
            {
                if (row.Session is null) continue;
                if (row.Meter.Feed(row.Session.ReadPeak())) row.Meter.Invalidate();
            }

            // Applications come and go far more slowly than levels change, so the list is
            // re-enumerated about once a second rather than on every tick.
            if (++_sessionRefreshTick >= 30)
            {
                _sessionRefreshTick = 0;
                RefreshSessions();
            }
        }

        // Volume can be changed from the tray, a keyboard key or another app; picking that
        // up a few times a second is plenty and costs two more calls.
        if (++_meterTick >= 6)
        {
            _meterTick = 0;
            RefreshVolumeUi();
        }

        // A periodic re-check of the device list. Windows does notify us of endpoint changes
        // and that remains the primary path - this is a safety net for a notification missed
        // while the panel was hidden, and it refreshes the offline marks. Visible only, so it
        // costs nothing in the tray.
        if (++_deviceRecheckTick >= 150)
        {
            _deviceRecheckTick = 0;
            RefreshDevices();
        }
    }

    private void StartMetering()
    {
        if (!_settings.ShowMeters || _meterTimer.Enabled) return;
        _meterTick = 0;
        _meterTimer.Start();
        EnsureCaptureMeter();
        EnsureSessionMixer();
        RefreshSessions();
    }

    private void StopMetering()
    {
        if (!_meterTimer.Enabled) return;
        _meterTimer.Stop();
        CloseCaptureMeter();
        CloseSessionMixer();
        _outputMeter.Reset();
        _inputMeter.Reset();
    }

    /// <summary>
    /// Opens a capture stream on the current microphone, or closes one that is no longer
    /// wanted. Only ever open while the panel is visible and metering is switched on.
    /// </summary>
    private void EnsureCaptureMeter()
    {
        string? inputId = _inputControls?.DeviceId;

        bool wanted = _settings.ShowMeters &&
                      _settings.MeterMicrophone &&
                      _meterTimer.Enabled &&
                      inputId is not null;

        if (!wanted)
        {
            CloseCaptureMeter();
            return;
        }

        if (string.Equals(_inputCapture?.DeviceId, inputId, StringComparison.OrdinalIgnoreCase)) return;

        CloseCaptureMeter();
        _inputCapture = _audio.OpenCaptureMeter(inputId!, out string? error);

        // Report a refusal once per device rather than on every retry.
        if (_inputCapture is null && error is not null &&
            !string.Equals(_captureMeterErrorFor, inputId, StringComparison.OrdinalIgnoreCase))
        {
            _captureMeterErrorFor = inputId;
            SetStatus("MIC METER — " + error.ToUpperInvariant(), PanelTheme.LampAmber);
        }
        else if (_inputCapture is not null)
        {
            _captureMeterErrorFor = null;
        }
    }

    private void CloseCaptureMeter()
    {
        _inputCapture?.Dispose();
        _inputCapture = null;
    }

    private void UpdateTrayText()
    {
        if (_tray is null) return;

        string output = _outputs.FirstOrDefault(d => d.IsDefault)?.ShortName ?? "none";
        string input = _inputs.FirstOrDefault(d => d.IsDefault)?.ShortName ?? "none";
        string text = "CommPanel\nOut: " + output + "\nIn: " + input;

        // NotifyIcon.Text throws above 63 characters.
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    // ------------------------------------------------------------ visibility

    /// <summary>
    /// Windows must never steal focus from a game that is still loading, so the form always
    /// shows without activation and focus is taken afterwards, only when the user asked for it.
    /// </summary>
    protected override bool ShowWithoutActivation => true;

    public void ShowPanel(bool activate)
    {
        PositionWindow();

        // Auto-triggered pop-ups sit above a fullscreen game; a deliberate open does not pin.
        TopMost = !activate;

        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;

        StartMetering();

        // Ask the headset where it stands each time the panel opens, since it may have
        // switched itself off while we were in the tray.
        if (_settings.WatchHeadsetPower && _settings.QueryHeadsetStatus) QueryHeadsetStatus();

        if (activate)
        {
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOW);
            Activate();
            NativeMethods.SetForegroundWindow(Handle);
        }
        else
        {
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    public void HidePanel()
    {
        StopMetering();
        SaveWindowPosition();
        TopMost = false;
        Hide();
        NativeMethods.TrimWorkingSet();
    }

    public void TogglePanel()
    {
        if (Visible && !NativeMethods.IsIconic(Handle)) HidePanel();
        else ShowPanel(activate: true);
    }

    private void PositionWindow()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;

        int x, y;
        if (_settings.WindowX != int.MinValue && _settings.WindowY != int.MinValue)
        {
            x = _settings.WindowX;
            y = _settings.WindowY;
        }
        else
        {
            x = area.Right - Width - 24;
            y = area.Bottom - Height - 24;
        }

        // Keep the panel on a screen that actually exists - monitors get unplugged.
        var target = new Rectangle(x, y, Width, Height);
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(target)))
        {
            x = area.Right - Width - 24;
            y = area.Bottom - Height - 24;
        }

        Location = new Point(x, y);
    }

    private void SaveWindowPosition()
    {
        if (!Visible) return;
        if (_settings.WindowX == Left && _settings.WindowY == Top) return;
        _settings.WindowX = Left;
        _settings.WindowY = Top;
        _settings.Save();
    }

    private void OnWatchedProgramActivated(string exeName)
    {
        SetStatus(exeName.ToUpperInvariant() + " DETECTED — SELECT ROUTING", PanelTheme.LampAmber);
        RefreshDevices();
        ShowPanel(activate: false);
    }

    // --------------------------------------------------------------- settings

    private void ShowSettings()
    {
        // Pass the unfiltered lists so devices the user previously hid can be brought back.
        using var dialog = new SettingsForm(
            _settings,
            _audio.GetDevices(EDataFlow.Render),
            _audio.GetDevices(EDataFlow.Capture));
        bool wasTopMost = TopMost;
        TopMost = false;

        // Panel size is judged by eye, so it applies as the slider moves and is put back if
        // the dialog is cancelled.
        float scaleOnEntry = _settings.FontScale;
        dialog.PreviewScale = scale =>
        {
            _settings.FontScale = scale;
            ApplyScale();
        };

        if (dialog.ShowDialog(Visible ? this : null) == DialogResult.OK)
        {
            _settings.Save();

            _linkCommsButton.IsOn = _settings.LinkCommunications;
            _linkCommsButton.Invalidate();

            _headsetWatcher.SetProfiles(_settings.HeadsetProfiles);
        if (_settings.WatchHeadsetPower)
        {
            _headsetWatcher.Rescan();
            if (_settings.QueryHeadsetStatus) QueryHeadsetStatus();
        }
            else _headsetWatcher.Stop();

            if (_settings.WatchProcesses && !_watcher.IsRunning) _watcher.Start();
            else if (!_settings.WatchProcesses && _watcher.IsRunning) _watcher.Stop();

            if (_settings.HotkeyEnabled) RegisterHotkey();
            else UnregisterHotkey();

            RefreshDevices();
            PanelTheme.Bloom = _settings.BloomMultiplier;
            ApplyScale();
            if (_settings.ShowMeters) StartMetering(); else StopMetering();
            SetStatus("SETTINGS SAVED", PanelTheme.LampGreen);
        }
        else if (Math.Abs(_settings.FontScale - scaleOnEntry) > 0.001f)
        {
            // Cancelled after dragging the size slider: put the panel back.
            _settings.FontScale = scaleOnEntry;
            ApplyScale();
        }

        TopMost = wasTopMost;
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "CommPanel", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // --------------------------------------------------------------- hotkeys

    private void RegisterHotkey()
    {
        if (_hotkeyRegistered || !IsHandleCreated) return;
        _hotkeyRegistered = NativeMethods.RegisterHotKey(
            Handle, HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            (uint)Keys.C);
    }

    private void UnregisterHotkey()
    {
        if (!_hotkeyRegistered) return;
        NativeMethods.UnregisterHotKey(Handle, HotkeyId);
        _hotkeyRegistered = false;
    }

    // ----------------------------------------------------------- window plumbing

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTCAPTION = 2;
        const int HTCLIENT = 1;

        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            TogglePanel();
            return;
        }

        if (_showMessage != 0 && m.Msg == (int)_showMessage)
        {
            ShowPanel(activate: true);
            return;
        }

        base.WndProc(ref m);

        // Dragging: the header strip behaves as the title bar of a borderless window.
        if (m.Msg == WM_NCHITTEST && m.Result.ToInt32() == HTCLIENT)
        {
            long lParam = m.LParam.ToInt64();
            var screenPoint = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
            var client = PointToClient(screenPoint);
            if (client.Y >= 0 && client.Y < Scaled(HeaderHeight))
                m.Result = new IntPtr(HTCAPTION);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            HidePanel();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        if (Visible) SaveWindowPosition();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutPanel();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The window is a panel, not the app: closing it parks CommPanel in the tray.
        if (!_exiting && e.CloseReason is CloseReason.UserClosing or CloseReason.None)
        {
            e.Cancel = true;
            HidePanel();
            return;
        }
        base.OnFormClosing(e);
    }

    public void ExitApplication()
    {
        _exiting = true;
        StopMetering();
        SaveWindowPosition();
        Close();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UnregisterHotkey();

            _audio.EndpointsChanged -= OnEndpointsChangedFromAudioService;
            StopMetering();
            _meterTimer.Dispose();
            CloseCaptureMeter();
            CloseSessionMixer();
            _outputControls?.Dispose();
            _inputControls?.Dispose();

            _watcher.Dispose();
            _headsetWatcher.HeadsetPoweredOff -= OnHeadsetPoweredOff;
            _headsetWatcher.HeadsetPoweredOn -= OnHeadsetPoweredOn;
            _headsetWatcher.Dispose();
            _refreshDebounce.Dispose();

            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.ContextMenuStrip?.Dispose();
                _tray.Dispose();
                _tray = null;
            }

            _chassis?.Dispose();
            _titleFont.Dispose();
            _labelFont.Dispose();
            _smallFont.Dispose();
            _stencilFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
