using CommPanel.Audio;
using CommPanel.Core;

namespace CommPanel.Ui;

/// <summary>
/// Asked when a device goes offline and nothing on the panel can take over, but a hidden
/// device could.
///
/// Hidden means hidden: CommPanel never switches to a hidden device on its own. This is the
/// one case where it would otherwise leave the user on a dead device with a working one
/// sitting right there, so it asks instead of either guessing or staying silent.
/// </summary>
internal sealed class OfflineFallbackDialog : PanelDialog
{
    private const int LogicalHeight = 330;

    private readonly List<AudioDevice> _candidates;
    private readonly PlateList _deviceList = new();
    private readonly PlateCheck _unhide;

    private Rectangle _headingRect;
    private Rectangle _bodyRect;
    private Rectangle _listCaption;
    private Rectangle _listRecess;

    private readonly string _lostDeviceName;
    private readonly bool _isOutput;

    public OfflineFallbackDialog(AppSettings settings, string lostDeviceName, bool isOutput,
                                 List<AudioDevice> candidates)
        : base(settings, "DEVICE OFFLINE", "NOTHING ON THE PANEL CAN TAKE OVER", LogicalHeight)
    {
        _candidates = candidates;
        _lostDeviceName = lostDeviceName;
        _isOutput = isOutput;

        // The panel may well be in the tray with a game in front; this has to be seen, and it
        // cannot assume there is a parent window on screen to centre on.
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;

        _unhide = BuildLayout();
    }

    /// <summary>The device the user chose, or null if they declined.</summary>
    public AudioDevice? Chosen { get; private set; }

    /// <summary>True when the user asked for the chosen device to be shown from now on.</summary>
    public bool ShouldUnhide => _unhide.Checked;

    private PlateCheck BuildLayout()
    {
        int margin = EdgeMargin;
        int width = Scaled(470) - margin * 2;
        int y = BodyTop;

        _headingRect = new Rectangle(margin, y, width, Scaled(22));
        y += Scaled(26);

        _bodyRect = new Rectangle(margin, y, width, Scaled(42));
        y += Scaled(48);

        _listCaption = new Rectangle(margin, y, width, Scaled(16));
        y += Scaled(20);

        _listRecess = new Rectangle(margin, y, width, Scaled(84));
        _deviceList.Bounds = Inside(_listRecess);
        StyleList(_deviceList);
        foreach (var device in _candidates) _deviceList.Add(device.FullName);
        if (_deviceList.Items.Count > 0) _deviceList.SelectedIndex = 0;
        Controls.Add(_deviceList);
        y += _listRecess.Height + Scaled(12);

        var unhide = Toggle("Also show this device on the panel from now on",
                            new Rectangle(margin, y, width, Scaled(20)));
        y += Scaled(20) + Scaled(14);

        int keyHeight = Scaled(30);
        int switchWidth = Scaled(124);
        int stayWidth = Scaled(96);

        var switchKey = Key("SWITCH TO IT",
            new Rectangle(margin + width - switchWidth, y, switchWidth, keyHeight),
            (_, _) =>
            {
                int index = _deviceList.SelectedIndex;
                if (index >= 0 && index < _candidates.Count) Chosen = _candidates[index];
                DialogResult = DialogResult.OK;
                Close();
            });
        switchKey.ShowLamp = true;
        switchKey.IsOn = true;
        switchKey.LampColor = PanelTheme.LampGreen;

        Key("STAY PUT",
            new Rectangle(switchKey.Left - Scaled(8) - stayWidth, y, stayWidth, keyHeight),
            (_, _) => { DialogResult = DialogResult.Cancel; Close(); });

        y += keyHeight + margin;

        ClientSize = new Size(Scaled(470), y);
        PlaceHeader();
        RebuildChassis();

        return unhide;
    }

    protected override void DrawChassis(Graphics g)
    {
        PanelTheme.DrawRecess(g, _listRecess, Scaled(5));

        PanelTheme.DrawEngraved(g, _lostDeviceName + " has powered off", LabelFont, _headingRect,
            PanelTheme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis);

        string kind = _isOutput ? "output" : "input";

        DrawBody(g, "No " + kind + " device on your panel can take over, so CommPanel has left it "
                  + "selected. These devices are hidden, but one of them could be used:", _bodyRect);

        DrawCaption(g, "HIDDEN DEVICES", _listCaption);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
