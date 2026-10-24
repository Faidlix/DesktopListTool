// 功能：編輯清單或便籤的完整樣式，包含背景、系統色、透明度、字型、行距、行高與圓角。
// 使用：在清單空白處或便籤上按右鍵選「樣式…」；所有控制項即時預覽，取消則由呼叫端還原。
// 設定：各清單與每張便籤獨立保存；「恢復完全透明」只把背景透明度設為 0%。

using Microsoft.Win32;

namespace DesktopListTool;

internal sealed class OverlayStyleOptions
{
    public Color BackgroundColor { get; set; } = Color.Black;
    public byte BackgroundOpacity { get; set; }
    public bool FollowThemeColor { get; set; }
    public string FontFamily { get; set; } = "Microsoft JhengHei UI";
    public float FontSize { get; set; } = 10F;
    public bool Bold { get; set; }
    public int LineHeight { get; set; } = 36;
    public int LineSpacing { get; set; }
    public int CornerRadius { get; set; }
    public OverlayStyleOptions Clone() => (OverlayStyleOptions)MemberwiseClone();
}

internal sealed class OverlayStyleDialog : Form
{
    private readonly Button _color = new() { Text = "選擇顏色…", AutoSize = true };
    private readonly CheckBox _systemColor = new() { Text = "使用 Windows 系統顏色", AutoSize = true };
    private readonly TrackBar _opacity = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Dock = DockStyle.Fill };
    private readonly Label _opacityText = new() { AutoSize = true };
    private readonly ComboBox _family = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _weight = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown _fontSize = Numeric(6, 48, 10, 1);
    private readonly NumericUpDown _lineHeight = Numeric(16, 160, 36);
    private readonly NumericUpDown _lineSpacing = Numeric(0, 48, 0);
    private readonly NumericUpDown _radius = Numeric(0, 80, 0);
    private readonly Panel _preview = new() { Height = 110, Dock = DockStyle.Fill };
    private Color _selectedColor;

    public event EventHandler? PreviewChanged;

    public OverlayStyleDialog(OverlayStyleOptions style, string targetName)
    {
        _selectedColor = style.BackgroundColor;
        Text = $"{targetName}樣式";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(620, 680);
        ClientSize = new Size(700, 760);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(246, 248, 252);
        Font = new Font("Microsoft JhengHei UI", 10F);

        foreach (var item in FontFamily.Families.OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase)) _family.Items.Add(item.Name);
        _weight.Items.AddRange(["一般", "粗體"]);
        _systemColor.Checked = style.FollowThemeColor;
        _opacity.Value = (int)Math.Round(style.BackgroundOpacity / 255d * 100);
        _family.SelectedItem = _family.Items.Cast<string>().FirstOrDefault(value => value.Equals(style.FontFamily, StringComparison.OrdinalIgnoreCase));
        if (_family.SelectedIndex < 0 && _family.Items.Count > 0) _family.SelectedIndex = 0;
        _weight.SelectedIndex = style.Bold ? 1 : 0;
        _fontSize.Value = Math.Clamp((decimal)style.FontSize, _fontSize.Minimum, _fontSize.Maximum);
        _lineHeight.Value = Math.Clamp(style.LineHeight, (int)_lineHeight.Minimum, (int)_lineHeight.Maximum);
        _lineSpacing.Value = Math.Clamp(style.LineSpacing, (int)_lineSpacing.Minimum, (int)_lineSpacing.Maximum);
        _radius.Value = Math.Clamp(style.CornerRadius, (int)_radius.Minimum, (int)_radius.Maximum);

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22), ColumnCount = 2, RowCount = 11 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 10; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        AddRow(table, 0, "背景顏色", _color);
        AddRow(table, 1, "系統顏色", _systemColor);
        var opacityPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        opacityPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        opacityPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        opacityPanel.Controls.Add(_opacity, 0, 0); opacityPanel.Controls.Add(_opacityText, 1, 0);
        AddRow(table, 2, "透明度", opacityPanel);
        var clear = new Button { Text = "恢復完全透明", AutoSize = true };
        clear.Click += (_, _) => _opacity.Value = 0;
        AddRow(table, 3, "背景重設", clear);
        AddRow(table, 4, "字型", _family);
        AddRow(table, 5, "字體大小", _fontSize);
        AddRow(table, 6, "字體粗細", _weight);
        AddRow(table, 7, "行高", _lineHeight);
        AddRow(table, 8, "行距／項目間距", _lineSpacing);
        AddRow(table, 9, "圓角大小（0 為直角）", _radius);
        table.Controls.Add(_preview, 0, 10); table.SetColumnSpan(_preview, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(100, 38) };
        var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, Size = new Size(100, 38) };
        buttons.Controls.AddRange([cancel, ok]);
        Controls.Add(table); Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;

        _color.Click += (_, _) => ChooseColor();
        foreach (var control in new Control[] { _systemColor, _opacity, _family, _weight, _fontSize, _lineHeight, _lineSpacing, _radius })
        {
            switch (control)
            {
                case CheckBox box: box.CheckedChanged += (_, _) => UpdatePreview(); break;
                case TrackBar track: track.ValueChanged += (_, _) => UpdatePreview(); break;
                case ComboBox combo: combo.SelectedIndexChanged += (_, _) => UpdatePreview(); break;
                case NumericUpDown number: number.ValueChanged += (_, _) => UpdatePreview(); break;
            }
        }
        _preview.Paint += DrawPreview;
        UpdatePreview();
    }

    public OverlayStyleOptions SelectedStyle => new()
    {
        BackgroundColor = _systemColor.Checked ? OverlayAppearanceSettings.GetWindowsThemeColor() : _selectedColor,
        BackgroundOpacity = (byte)Math.Round(_opacity.Value / 100d * 255),
        FollowThemeColor = _systemColor.Checked,
        FontFamily = _family.SelectedItem as string ?? "Microsoft JhengHei UI",
        FontSize = (float)_fontSize.Value,
        Bold = _weight.SelectedIndex == 1,
        LineHeight = (int)_lineHeight.Value,
        LineSpacing = (int)_lineSpacing.Value,
        CornerRadius = (int)_radius.Value
    };

    private static NumericUpDown Numeric(int min, int max, int value, int decimals = 0) => new()
    {
        Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals,
        Increment = decimals > 0 ? 0.5M : 1M, Width = 120
    };

    private static void AddRow(TableLayoutPanel table, int row, string title, Control control)
    {
        table.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 12, 12, 12) }, 0, row);
        control.Margin = new Padding(3, 8, 3, 8);
        table.Controls.Add(control, 1, row);
    }

    private void ChooseColor()
    {
        using var picker = new ColorDialog { Color = _selectedColor, FullOpen = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _selectedColor = picker.Color;
        _systemColor.Checked = false;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        _opacityText.Text = $"{_opacity.Value}%";
        _color.Enabled = !_systemColor.Checked;
        _color.BackColor = _selectedColor;
        _color.ForeColor = _selectedColor.GetBrightness() < .5F ? Color.White : Color.Black;
        _preview.Invalidate();
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DrawPreview(object? sender, PaintEventArgs e)
    {
        var style = SelectedStyle;
        var bounds = Rectangle.Inflate(_preview.ClientRectangle, -4, -4);
        using var background = new SolidBrush(Color.FromArgb(Math.Max(1, (int)style.BackgroundOpacity), style.BackgroundColor));
        if (style.CornerRadius > 0) e.Graphics.FillRoundedRectangle(background, bounds, Math.Min(style.CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2));
        else e.Graphics.FillRectangle(background, bounds);
        using var font = CreateFont(style);
        using var brush = new SolidBrush(Color.White);
        e.Graphics.DrawString("樣式即時預覽  Aa  中文", font, brush, bounds.X + 16, bounds.Y + 10);
        e.Graphics.DrawString("第二行文字與行距", font, brush, bounds.X + 16, bounds.Y + 10 + style.LineHeight + style.LineSpacing);
    }

    internal static Font CreateFont(OverlayStyleOptions style)
    {
        try { return new Font(style.FontFamily, style.FontSize, style.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point); }
        catch (ArgumentException) { return new Font("Microsoft JhengHei UI", style.FontSize, style.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point); }
    }
}

internal static class OverlayAppearanceSettings
{
    public static OverlayStyleOptions LoadStyle(string? identity, OverlayStyleOptions fallback)
    {
        var result = fallback.Clone();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(OverlaySettings.GetSettingsKey(identity));
            if (key is null) return result;
            if (key.GetValue("BackgroundColor") is int rgb) result.BackgroundColor = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            if (key.GetValue("BackgroundOpacity") is int opacity) result.BackgroundOpacity = (byte)Math.Clamp(opacity, 0, 255);
            if (key.GetValue("FollowThemeColor") is int follow) result.FollowThemeColor = follow != 0;
            if (key.GetValue("FontFamily") is string family && family.Length > 0) result.FontFamily = family;
            if (key.GetValue("FontSizeX10") is int size) result.FontSize = Math.Clamp(size / 10F, 6F, 48F);
            if (key.GetValue("FontBold") is int bold) result.Bold = bold != 0;
            if (key.GetValue("LineHeight") is int height) result.LineHeight = Math.Clamp(height, 16, 160);
            if (key.GetValue("LineSpacing") is int spacing) result.LineSpacing = Math.Clamp(spacing, 0, 48);
            if (key.GetValue("CornerRadius") is int radius) result.CornerRadius = Math.Clamp(radius, 0, 80);
            if (result.FollowThemeColor) result.BackgroundColor = GetWindowsThemeColor();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        return result;
    }

    public static void SaveStyle(string? identity, OverlayStyleOptions style)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(OverlaySettings.GetSettingsKey(identity));
            var color = style.BackgroundColor;
            key.SetValue("BackgroundColor", (color.R << 16) | (color.G << 8) | color.B, RegistryValueKind.DWord);
            key.SetValue("BackgroundOpacity", style.BackgroundOpacity, RegistryValueKind.DWord);
            key.SetValue("FollowThemeColor", style.FollowThemeColor ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("FontFamily", style.FontFamily, RegistryValueKind.String);
            key.SetValue("FontSizeX10", (int)Math.Round(style.FontSize * 10), RegistryValueKind.DWord);
            key.SetValue("FontBold", style.Bold ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("LineHeight", style.LineHeight, RegistryValueKind.DWord);
            key.SetValue("LineSpacing", style.LineSpacing, RegistryValueKind.DWord);
            key.SetValue("CornerRadius", style.CornerRadius, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
    }

    public static Color GetWindowsThemeColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("ColorizationColor") is int signed)
            {
                var value = unchecked((uint)signed);
                return Color.FromArgb((int)((value >> 16) & 255), (int)((value >> 8) & 255), (int)(value & 255));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        return SystemColors.Highlight;
    }
}
