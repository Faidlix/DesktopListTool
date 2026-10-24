// 功能：編輯桌面清單背景顏色與透明度，並將結果交由透明清單即時套用。
// 使用：在清單空白處按右鍵，選擇「背景顏色與透明度…」。
// 設定：透明度 0–100%；可選手動顏色或跟隨 Windows 主題顏色，所有變更皆即時預覽。

using Microsoft.Win32;

namespace DesktopListTool;

internal sealed class LegacyOverlayAppearanceDialog : Form
{
    private readonly Button _colorButton = new();
    private readonly TrackBar _opacityTrack = new();
    private readonly Label _opacityValue = new();
    private readonly Panel _preview = new();
    private readonly CheckBox _followTheme = new();
    private Color _selectedColor;

    public event EventHandler? PreviewChanged;

    public LegacyOverlayAppearanceDialog(Color color, byte opacity, bool followTheme)
    {
        _selectedColor = color;
        Text = "清單背景設定";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(500, 390);
        BackColor = Color.FromArgb(246, 248, 252);
        Font = new Font("Microsoft JhengHei UI", 10F);

        var colorLabel = new Label { Text = "背景顏色", AutoSize = true, Location = new Point(24, 24) };
        _colorButton.Text = "選擇顏色…";
        _colorButton.Location = new Point(24, 50);
        _colorButton.Size = new Size(130, 36);
        _colorButton.Click += (_, _) => ChooseColor();

        var opacityLabel = new Label { Text = "透明度", AutoSize = true, Location = new Point(24, 108) };
        _opacityTrack.Minimum = 0;
        _opacityTrack.Maximum = 100;
        _opacityTrack.TickFrequency = 10;
        _opacityTrack.Value = (int)Math.Round(opacity / 255d * 100);
        _opacityTrack.Location = new Point(20, 132);
        _opacityTrack.Size = new Size(365, 45);
        _opacityTrack.ValueChanged += (_, _) => UpdatePreview();
        _opacityValue.AutoSize = true;
        _opacityValue.Location = new Point(404, 140);

        _preview.Location = new Point(174, 24);
        _preview.Size = new Size(296, 72);
        _preview.BorderStyle = BorderStyle.FixedSingle;
        var previewText = new Label
        {
            Text = "清單背景預覽",
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(70, 24)
        };
        _preview.Controls.Add(previewText);

        _followTheme.Text = "跟隨 Windows 主題顏色";
        _followTheme.AutoSize = false;
        _followTheme.Size = new Size(430, 36);
        _followTheme.ForeColor = Color.FromArgb(20, 28, 40);
        _followTheme.UseCompatibleTextRendering = true;
        _followTheme.Checked = followTheme;
        _followTheme.Location = new Point(24, 194);
        _followTheme.CheckedChanged += (_, _) =>
        {
            _colorButton.Enabled = !_followTheme.Checked;
            UpdatePreview();
        };

        var okButton = new Button { Text = "確定", DialogResult = DialogResult.OK, Size = new Size(92, 34), Location = new Point(278, 326) };
        var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(92, 34), Location = new Point(378, 326) };
        AcceptButton = okButton;
        CancelButton = cancelButton;
        Controls.AddRange([colorLabel, _colorButton, opacityLabel, _opacityTrack, _opacityValue, _preview, _followTheme, okButton, cancelButton]);
        _colorButton.Enabled = !followTheme;
        UpdatePreview();
    }

    public Color SelectedColor => _followTheme.Checked ? OverlayAppearanceSettings.GetWindowsThemeColor() : _selectedColor;
    public byte SelectedOpacity => (byte)Math.Round(_opacityTrack.Value / 100d * 255);
    public bool FollowThemeColor => _followTheme.Checked;

    private void ChooseColor()
    {
        using var dialog = new ColorDialog { Color = _selectedColor, FullOpen = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _selectedColor = dialog.Color;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        _opacityValue.Text = $"{_opacityTrack.Value}%";
        _colorButton.BackColor = _selectedColor;
        _colorButton.ForeColor = _selectedColor.GetBrightness() < 0.5F ? Color.White : Color.Black;
        var alpha = SelectedOpacity;
        var blended = Blend(SelectedColor, BackColor, alpha / 255F);
        _preview.BackColor = blended;
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Color Blend(Color foreground, Color background, float amount) => Color.FromArgb(
        (int)Math.Round(foreground.R * amount + background.R * (1 - amount)),
        (int)Math.Round(foreground.G * amount + background.G * (1 - amount)),
        (int)Math.Round(foreground.B * amount + background.B * (1 - amount)));
}

internal static class LegacyOverlayAppearanceSettings
{
    private const string SettingsKey = @"Software\DesktopListTool";

    public static (Color Color, byte Opacity, bool FollowTheme) Load() => Load(null);

    public static (Color Color, byte Opacity, bool FollowTheme) Load(string? folderPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(OverlaySettings.GetSettingsKey(folderPath));
            var rgb = key?.GetValue("BackgroundColor") is int colorValue ? colorValue : 0;
            var opacity = key?.GetValue("BackgroundOpacity") is int opacityValue ? Math.Clamp(opacityValue, 0, 255) : 0;
            var followTheme = key?.GetValue("FollowThemeColor") is int followValue && followValue != 0;
            var color = followTheme ? GetWindowsThemeColor() : Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            return (color, (byte)opacity, followTheme);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return (Color.Black, 0, false);
        }
    }

    public static void Save(Color color, byte opacity, bool followTheme) => Save(null, color, opacity, followTheme);

    public static void Save(string? folderPath, Color color, byte opacity, bool followTheme)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(OverlaySettings.GetSettingsKey(folderPath));
            var rgb = (color.R << 16) | (color.G << 8) | color.B;
            key.SetValue("BackgroundColor", rgb, RegistryValueKind.DWord);
            key.SetValue("BackgroundOpacity", opacity, RegistryValueKind.DWord);
            key.SetValue("FollowThemeColor", followTheme ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
    }

    public static Color GetWindowsThemeColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("ColorizationColor") is int signedValue)
            {
                var value = unchecked((uint)signedValue);
                return Color.FromArgb((int)((value >> 16) & 0xFF), (int)((value >> 8) & 0xFF), (int)(value & 0xFF));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
        return SystemColors.Highlight;
    }
}
