// 功能：編輯清單或便籤的背景、字型、間距、圓角、對齊與內容分布樣式。
// 使用：從清單或便籤右鍵選「樣式…」；滑桿與右側數字框會即時預覽，只有按「確定」才保存。
// 設定：各清單與每張便籤獨立保存；Enter 只套用目前輸入值，不會關閉視窗。

using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopListTool;

internal enum OverlayTextAlignment { Left, Center, Right }
internal enum OverlayContentDistribution { Packed, Fill }

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
    public OverlayTextAlignment TextAlignment { get; set; }
    public OverlayContentDistribution Distribution { get; set; }
    public OverlayStyleOptions Clone() => (OverlayStyleOptions)MemberwiseClone();
}

internal sealed class SliderNumberControl : UserControl
{
    private readonly TrackBar _track = new() { Dock = DockStyle.Fill, TickStyle = TickStyle.BottomRight };
    private readonly NumericUpDown _number = new() { Dock = DockStyle.Fill, TextAlign = HorizontalAlignment.Right };
    private readonly decimal _scale;
    private bool _syncing;
    public event EventHandler? ValueChanged;

    public SliderNumberControl(decimal minimum, decimal maximum, decimal value, decimal increment = 1M, int decimals = 0, decimal scale = 1M)
    {
        _scale = scale;
        Height = 48;
        Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        _track.Minimum = Decimal.ToInt32(minimum * scale);
        _track.Maximum = Decimal.ToInt32(maximum * scale);
        _track.TickFrequency = Math.Max(1, (_track.Maximum - _track.Minimum) / 10);
        _number.Minimum = minimum; _number.Maximum = maximum; _number.Increment = increment; _number.DecimalPlaces = decimals;
        layout.Controls.Add(_track, 0, 0); layout.Controls.Add(_number, 1, 0); Controls.Add(layout);
        _track.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true; _number.Value = Math.Clamp(_track.Value / _scale, _number.Minimum, _number.Maximum); _syncing = false;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        };
        _number.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true; _track.Value = Math.Clamp(Decimal.ToInt32(_number.Value * _scale), _track.Minimum, _track.Maximum); _syncing = false;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        };
        _number.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true; e.Handled = true; ValueChanged?.Invoke(this, EventArgs.Empty);
        };
        Value = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal Value
    {
        get => _number.Value;
        set
        {
            _syncing = true;
            _number.Value = Math.Clamp(value, _number.Minimum, _number.Maximum);
            _track.Value = Math.Clamp(Decimal.ToInt32(_number.Value * _scale), _track.Minimum, _track.Maximum);
            _syncing = false;
        }
    }
}

internal sealed class OverlayStyleDialog : Form
{
    private readonly Button _color = new() { Text = "選擇顏色…", AutoSize = true };
    private readonly CheckBox _systemColor = new() { Text = "使用 Windows 系統顏色", AutoSize = true };
    private readonly SliderNumberControl _opacity;
    private readonly ComboBox _family = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 30 };
    private readonly ComboBox _weight = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly SliderNumberControl _fontSize;
    private readonly SliderNumberControl _lineHeight;
    private readonly SliderNumberControl _lineSpacing;
    private readonly SliderNumberControl _radius;
    private readonly ComboBox _alignment = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _distribution = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Panel _preview = new() { Height = 130, Dock = DockStyle.Fill, BackColor = Color.FromArgb(225, 230, 238) };
    private Color _selectedColor;
    public event EventHandler? PreviewChanged;

    public OverlayStyleDialog(OverlayStyleOptions style, string targetName)
    {
        _opacity = new SliderNumberControl(0, 100, Math.Round(style.BackgroundOpacity / 255M * 100M));
        _fontSize = new SliderNumberControl(6, 48, (decimal)style.FontSize, .5M, 1, 10M);
        _lineHeight = new SliderNumberControl(16, 160, style.LineHeight);
        _lineSpacing = new SliderNumberControl(0, 48, style.LineSpacing);
        _radius = new SliderNumberControl(0, 80, style.CornerRadius);
        _selectedColor = style.BackgroundColor;
        Text = $"{targetName}樣式"; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(720, 720); ClientSize = new Size(820, 850); AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false; BackColor = Color.FromArgb(246, 248, 252); Font = new Font("Microsoft JhengHei UI", 10F);

        foreach (var item in FontFamily.Families.OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase)) _family.Items.Add(item.Name);
        _family.DrawItem += DrawFontItem;
        _weight.Items.AddRange(["一般", "粗體"]); _alignment.Items.AddRange(["靠左", "置中", "靠右"]); _distribution.Items.AddRange(["集中排列", "平均分散填滿"]);
        _systemColor.Checked = style.FollowThemeColor;
        _family.SelectedItem = _family.Items.Cast<string>().FirstOrDefault(value => value.Equals(style.FontFamily, StringComparison.OrdinalIgnoreCase));
        if (_family.SelectedIndex < 0 && _family.Items.Count > 0) _family.SelectedItem = "Microsoft JhengHei UI";
        if (_family.SelectedIndex < 0 && _family.Items.Count > 0) _family.SelectedIndex = 0;
        _weight.SelectedIndex = style.Bold ? 1 : 0; _alignment.SelectedIndex = (int)style.TextAlignment; _distribution.SelectedIndex = (int)style.Distribution;

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22), ColumnCount = 2, RowCount = 13 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 12; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); table.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        AddRow(table, 0, "背景顏色", _color); AddRow(table, 1, "系統顏色", _systemColor); AddRow(table, 2, "透明度", _opacity);
        var clear = new Button { Text = "恢復完全透明", AutoSize = true }; clear.Click += (_, _) => _opacity.Value = 0; AddRow(table, 3, "背景重設", clear);
        var fontPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        fontPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); fontPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var resetFont = new Button { Text = "預設", AutoSize = true, Margin = new Padding(0, 0, 8, 0) }; resetFont.Click += (_, _) => _family.SelectedItem = "Microsoft JhengHei UI";
        fontPanel.Controls.Add(resetFont, 0, 0); fontPanel.Controls.Add(_family, 1, 0); AddRow(table, 4, "字型", fontPanel);
        AddRow(table, 5, "字體大小", _fontSize); AddRow(table, 6, "字體粗細", _weight); AddRow(table, 7, "行高", _lineHeight);
        AddRow(table, 8, "行距／項目間距", _lineSpacing); AddRow(table, 9, "圓角大小（0 為直角）", _radius);
        AddRow(table, 10, "文字對齊", _alignment); AddRow(table, 11, "內容排列", _distribution);
        table.Controls.Add(_preview, 0, 12); table.SetColumnSpan(_preview, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(100, 38) };
        var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, Size = new Size(100, 38) };
        buttons.Controls.AddRange([cancel, ok]); Controls.Add(table); Controls.Add(buttons); CancelButton = cancel;
        _color.Click += (_, _) => ChooseColor(); _systemColor.CheckedChanged += (_, _) => UpdatePreview();
        foreach (var slider in new[] { _opacity, _fontSize, _lineHeight, _lineSpacing, _radius }) slider.ValueChanged += (_, _) => UpdatePreview();
        foreach (var combo in new[] { _family, _weight, _alignment, _distribution }) combo.SelectedIndexChanged += (_, _) => UpdatePreview();
        _preview.Paint += DrawPreview; UpdatePreview();
    }

    public OverlayStyleOptions SelectedStyle => new()
    {
        BackgroundColor = _systemColor.Checked ? OverlayAppearanceSettings.GetWindowsThemeColor() : _selectedColor,
        BackgroundOpacity = (byte)Math.Round(_opacity.Value / 100M * 255M), FollowThemeColor = _systemColor.Checked,
        FontFamily = _family.SelectedItem as string ?? "Microsoft JhengHei UI", FontSize = (float)_fontSize.Value, Bold = _weight.SelectedIndex == 1,
        LineHeight = (int)_lineHeight.Value, LineSpacing = (int)_lineSpacing.Value, CornerRadius = (int)_radius.Value,
        TextAlignment = (OverlayTextAlignment)Math.Max(0, _alignment.SelectedIndex), Distribution = (OverlayContentDistribution)Math.Max(0, _distribution.SelectedIndex)
    };

    private static void AddRow(TableLayoutPanel table, int row, string title, Control control)
    {
        table.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 12, 12, 12) }, 0, row);
        control.Margin = new Padding(3, 6, 3, 6); table.Controls.Add(control, 1, row);
    }
    private void ChooseColor()
    {
        using var picker = new ColorDialog { Color = _selectedColor, FullOpen = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _selectedColor = picker.Color; _systemColor.Checked = false; UpdatePreview();
    }
    private void UpdatePreview()
    {
        _color.Enabled = !_systemColor.Checked; _color.BackColor = _selectedColor; _color.ForeColor = _selectedColor.GetBrightness() < .5F ? Color.White : Color.Black;
        _preview.Invalidate(); PreviewChanged?.Invoke(this, EventArgs.Empty);
    }
    private void DrawPreview(object? sender, PaintEventArgs e)
    {
        e.Graphics.Clear(_preview.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var style = SelectedStyle; var bounds = Rectangle.Inflate(_preview.ClientRectangle, -4, -4);
        using var background = new SolidBrush(Color.FromArgb(Math.Max(1, (int)style.BackgroundOpacity), style.BackgroundColor));
        if (style.CornerRadius > 0) e.Graphics.FillRoundedRectangle(background, bounds, Math.Min(style.CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2)); else e.Graphics.FillRectangle(background, bounds);
        using var font = CreateFont(style); using var brush = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = style.TextAlignment switch { OverlayTextAlignment.Center => StringAlignment.Center, OverlayTextAlignment.Right => StringAlignment.Far, _ => StringAlignment.Near } };
        var inset = Math.Max(16, style.CornerRadius); var textBounds = RectangleF.FromLTRB(bounds.Left + inset, bounds.Top + 10, bounds.Right - inset, bounds.Bottom - 8);
        e.Graphics.DrawString("樣式即時預覽  Aa  中文\n第二行文字與行距", font, brush, textBounds, format);
    }
    private void DrawFontItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground(); if (e.Index < 0 || e.Index >= _family.Items.Count) return;
        var name = _family.Items[e.Index] as string ?? "Microsoft JhengHei UI"; var supported = SupportsChinese(name);
        using var nameFont = CreateFont(new OverlayStyleOptions { FontFamily = name, FontSize = 10F }); using var warningFont = new Font(Font.FontFamily, 9F);
        using var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : ForeColor);
        e.Graphics.DrawString(name, nameFont, brush, e.Bounds.X + 3, e.Bounds.Y + 3);
        if (!supported) { var width = e.Graphics.MeasureString(name, nameFont).Width; e.Graphics.DrawString("（不支援中文）", warningFont, brush, e.Bounds.X + 5 + width, e.Bounds.Y + 4); }
        e.DrawFocusRectangle();
    }
    internal static bool SupportsChinese(string family)
    {
        try
        {
            using var font = new Font(family, 10F); using var bitmap = new Bitmap(1, 1); using var graphics = Graphics.FromImage(bitmap);
            var dc = graphics.GetHdc(); var hfont = font.ToHfont(); var previous = SelectObject(dc, hfont);
            try { var glyphs = new ushort[2]; return GetGlyphIndices(dc, "中文", 2, glyphs, 1) != uint.MaxValue && glyphs.All(value => value != ushort.MaxValue); }
            finally { SelectObject(dc, previous); DeleteObject(hfont); graphics.ReleaseHdc(dc); }
        }
        catch { return false; }
    }
    internal static Font CreateFont(OverlayStyleOptions style)
    {
        try { return new Font(style.FontFamily, style.FontSize, style.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point); }
        catch (ArgumentException) { return new Font("Microsoft JhengHei UI", style.FontSize, style.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point); }
    }
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern uint GetGlyphIndices(IntPtr dc, string text, int count, [Out] ushort[] glyphs, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
}

internal static class OverlayAppearanceSettings
{
    public static OverlayStyleOptions LoadStyle(string? identity, OverlayStyleOptions fallback)
    {
        var result = fallback.Clone();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(OverlaySettings.GetSettingsKey(identity)); if (key is null) return result;
            if (key.GetValue("BackgroundColor") is int rgb) result.BackgroundColor = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            if (key.GetValue("BackgroundOpacity") is int opacity) result.BackgroundOpacity = (byte)Math.Clamp(opacity, 0, 255);
            if (key.GetValue("FollowThemeColor") is int follow) result.FollowThemeColor = follow != 0;
            if (key.GetValue("FontFamily") is string family && family.Length > 0) result.FontFamily = family;
            if (key.GetValue("FontSizeX10") is int size) result.FontSize = Math.Clamp(size / 10F, 6F, 48F);
            if (key.GetValue("FontBold") is int bold) result.Bold = bold != 0;
            if (key.GetValue("LineHeight") is int height) result.LineHeight = Math.Clamp(height, 16, 160);
            if (key.GetValue("LineSpacing") is int spacing) result.LineSpacing = Math.Clamp(spacing, 0, 48);
            if (key.GetValue("CornerRadius") is int radius) result.CornerRadius = Math.Clamp(radius, 0, 80);
            if (key.GetValue("TextAlignment") is int alignment && Enum.IsDefined(typeof(OverlayTextAlignment), alignment)) result.TextAlignment = (OverlayTextAlignment)alignment;
            if (key.GetValue("Distribution") is int distribution && Enum.IsDefined(typeof(OverlayContentDistribution), distribution)) result.Distribution = (OverlayContentDistribution)distribution;
            if (result.FollowThemeColor) result.BackgroundColor = GetWindowsThemeColor();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        return result;
    }
    public static void SaveStyle(string? identity, OverlayStyleOptions style)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(OverlaySettings.GetSettingsKey(identity)); var color = style.BackgroundColor;
            key.SetValue("BackgroundColor", (color.R << 16) | (color.G << 8) | color.B, RegistryValueKind.DWord);
            key.SetValue("BackgroundOpacity", style.BackgroundOpacity, RegistryValueKind.DWord); key.SetValue("FollowThemeColor", style.FollowThemeColor ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("FontFamily", style.FontFamily, RegistryValueKind.String); key.SetValue("FontSizeX10", (int)Math.Round(style.FontSize * 10), RegistryValueKind.DWord);
            key.SetValue("FontBold", style.Bold ? 1 : 0, RegistryValueKind.DWord); key.SetValue("LineHeight", style.LineHeight, RegistryValueKind.DWord);
            key.SetValue("LineSpacing", style.LineSpacing, RegistryValueKind.DWord); key.SetValue("CornerRadius", style.CornerRadius, RegistryValueKind.DWord);
            key.SetValue("TextAlignment", (int)style.TextAlignment, RegistryValueKind.DWord); key.SetValue("Distribution", (int)style.Distribution, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
    }
    public static Color GetWindowsThemeColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("ColorizationColor") is int signed) { var value = unchecked((uint)signed); return Color.FromArgb((int)((value >> 16) & 255), (int)((value >> 8) & 255), (int)(value & 255)); }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        return SystemColors.Highlight;
    }
}
