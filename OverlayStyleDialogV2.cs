// 功能：編輯清單或便籤的背景、字型、間距、圓角、對齊與內容分布樣式。
// 使用：從清單或便籤右鍵選「樣式…」；滑桿與右側數字框會即時預覽，只有按「確定」才保存。
// 設定：顏色與透明度也即時套用；各清單與每張便籤依顯示器配置獨立保存，Enter 不會關閉樣式視窗。

using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopListTool;

internal enum OverlayTextAlignment { Left, Center, Right }
internal enum OverlayContentDistribution { Packed, Fill }
internal enum OverlayGradientDirection { LeftToRight, TopToBottom, DiagonalDown, DiagonalUp }
internal enum OverlayGradientMode { Single, Sandwich, Edges }
internal enum OverlayResizeCorner { None, TopLeft, TopRight, BottomLeft, BottomRight }

internal sealed class OverlayStyleOptions
{
    public Color BackgroundColor { get; set; } = Color.Black;
    public byte BackgroundOpacity { get; set; }
    public bool FollowThemeColor { get; set; }
    public bool GradientEnabled { get; set; }
    public Color GradientColor { get; set; } = Color.FromArgb(37, 99, 235);
    public byte GradientOpacity { get; set; } = 255;
    public int GradientTransition { get; set; } = 50;
    public OverlayGradientDirection GradientDirection { get; set; }
    public OverlayGradientMode GradientMode { get; set; }
    public Color TextColor { get; set; } = Color.White;
    public string FontFamily { get; set; } = "Microsoft JhengHei UI";
    public float FontSize { get; set; } = 10F;
    public bool Bold { get; set; }
    public int LineHeight { get; set; } = 36;
    public int LineSpacing { get; set; }
    public int HorizontalSpacing { get; set; } = 16;
    public int CornerRadius { get; set; }
    public OverlayTextAlignment TextAlignment { get; set; }
    public OverlayContentDistribution Distribution { get; set; }
    public OverlayStyleOptions Clone() => (OverlayStyleOptions)MemberwiseClone();
}

internal static class OverlayStyleClipboard
{
    private static OverlayStyleOptions? _style;
    public static bool HasStyle => _style is not null;
    public static void Copy(OverlayStyleOptions style) => _style = style.Clone();
    public static OverlayStyleOptions? Paste() => _style?.Clone();
}

internal sealed class ButtonChoice<T> : FlowLayoutPanel where T : struct, Enum
{
    private readonly Dictionary<T, Button> _buttons = [];
    private T _value;
    public event EventHandler? ValueChanged;
    public ButtonChoice(params (T Value, string Text, string Tip)[] choices)
    {
        AutoSize = true; WrapContents = false; FlowDirection = FlowDirection.LeftToRight;
        var tips = new ToolTip();
        foreach (var choice in choices)
        {
            var button = new Button { Text = choice.Text, AutoSize = true, MinimumSize = new Size(54, 36), FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 6, 0), Tag = choice.Value };
            button.Click += (_, _) => Value = choice.Value; tips.SetToolTip(button, choice.Tip); _buttons[choice.Value] = button; Controls.Add(button);
        }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public T Value { get => _value; set { _value = value; UpdateButtons(); ValueChanged?.Invoke(this, EventArgs.Empty); } }
    public void SetVisible(T value, bool visible) { if (_buttons.TryGetValue(value, out var button)) button.Visible = visible; }
    internal bool IsOptionVisible(T value) => _buttons.TryGetValue(value, out var button) && button.Visible;
    private void UpdateButtons()
    {
        foreach (var pair in _buttons) { var selected = EqualityComparer<T>.Default.Equals(pair.Key, _value); pair.Value.BackColor = selected ? Color.FromArgb(37, 99, 235) : SystemColors.Control; pair.Value.ForeColor = selected ? Color.White : SystemColors.ControlText; pair.Value.FlatAppearance.BorderColor = selected ? Color.FromArgb(29, 78, 216) : Color.FromArgb(190, 198, 210); }
    }
}

internal sealed record FontChoice(string Name, bool SupportsChinese)
{
    public override string ToString() => Name;
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

internal sealed class ColorWithAlphaDialog : Form
{
    private Color _color;
    private readonly Button _colorButton = new() { Text = "選擇顏色…", AutoSize = true };
    private readonly SliderNumberControl _alpha;
    public Color SelectedColor => _color;
    public byte SelectedAlpha => (byte)Math.Round(_alpha.Value / 100M * 255M);
    public event EventHandler? PreviewChanged;

    public ColorWithAlphaDialog(Color color, byte alpha, bool colorEnabled = true)
    {
        _color = color;
        _alpha = new SliderNumberControl(0, 100, Math.Round(alpha / 255M * 100M));
        Text = "顏色與透明度"; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; ClientSize = new Size(560, 190); Font = new Font("Microsoft JhengHei UI", 10F);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 3 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.Controls.Add(new Label { Text = "顏色", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0); table.Controls.Add(_colorButton, 1, 0);
        table.Controls.Add(new Label { Text = "不透明度", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1); table.Controls.Add(_alpha, 1, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true }; var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok); table.Controls.Add(buttons, 0, 2); table.SetColumnSpan(buttons, 2); Controls.Add(table); AcceptButton = ok; CancelButton = cancel;
        _colorButton.Click += (_, _) => { using var picker = new ColorDialog { Color = _color, FullOpen = true }; if (picker.ShowDialog(this) == DialogResult.OK) { _color = picker.Color; RefreshColor(); } };
        _colorButton.Click += (_, _) => PreviewChanged?.Invoke(this, EventArgs.Empty);
        _alpha.ValueChanged += (_, _) => PreviewChanged?.Invoke(this, EventArgs.Empty);
        _colorButton.Enabled = colorEnabled;
        RefreshColor();
    }
    private void RefreshColor() { _colorButton.BackColor = _color; _colorButton.ForeColor = _color.GetBrightness() < .5F ? Color.White : Color.Black; }
    internal void SetAlphaForTest(int percent) { _alpha.Value = Math.Clamp(percent, 0, 100); PreviewChanged?.Invoke(this, EventArgs.Empty); }
}

internal sealed class OverlayStyleDialog : Form
{
    private readonly Button _color = new() { Text = "第一顏色…", AutoSize = true };
    private readonly CheckBox _gradient = new() { Text = "使用漸層背景", AutoSize = true };
    private readonly Button _gradientColor = new() { Text = "第二顏色…", AutoSize = true };
    private readonly SliderNumberControl _gradientTransition;
    private readonly ButtonChoice<OverlayGradientDirection> _gradientDirection = new((OverlayGradientDirection.LeftToRight, "→", "由左到右"), (OverlayGradientDirection.TopToBottom, "↓", "由上到下"), (OverlayGradientDirection.DiagonalDown, "↘", "左上到右下"), (OverlayGradientDirection.DiagonalUp, "↗", "左下到右上"));
    private readonly ButtonChoice<OverlayGradientMode> _gradientMode = new((OverlayGradientMode.Single, "單層", "單向漸層"), (OverlayGradientMode.Sandwich, "上下夾層", "上下為第一顏色、中央為第二顏色"), (OverlayGradientMode.Edges, "四周", "四周為第一顏色、中央為第二顏色"));
    private readonly Button _textColor = new() { Text = "選擇文字顏色…", AutoSize = true };
    private readonly CheckBox _systemColor = new() { Text = "使用 Windows 系統顏色", AutoSize = true };
    private readonly ComboBox _family = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 34, DropDownWidth = 640, MaxDropDownItems = 16 };
    private readonly ButtonChoice<FontStyle> _weight = new((FontStyle.Regular, "一般", "一般字重"), (FontStyle.Bold, "粗體", "粗體字重"));
    private readonly SliderNumberControl _fontSize;
    private readonly SliderNumberControl _lineHeight;
    private readonly SliderNumberControl _lineSpacing;
    private readonly SliderNumberControl _horizontalSpacing;
    private readonly SliderNumberControl _radius;
    private readonly ButtonChoice<OverlayTextAlignment> _alignment = new((OverlayTextAlignment.Left, "☰←", "靠左"), (OverlayTextAlignment.Center, "☰↔", "置中"), (OverlayTextAlignment.Right, "☰→", "靠右"));
    private readonly ButtonChoice<OverlayContentDistribution> _distribution = new((OverlayContentDistribution.Packed, "⇥ 集中", "左右集中排列"), (OverlayContentDistribution.Fill, "↔ 分散", "左右平均分散填滿"));
    private Color _selectedColor;
    private Color _selectedGradientColor;
    private Color _selectedTextColor;
    private byte _selectedOpacity;
    private byte _selectedGradientOpacity;
    public event EventHandler? PreviewChanged;

    public OverlayStyleDialog(OverlayStyleOptions style, string targetName)
    {
        _gradientTransition = new SliderNumberControl(0, 100, style.GradientTransition);
        _fontSize = new SliderNumberControl(6, 48, (decimal)style.FontSize, .5M, 1, 10M);
        _lineHeight = new SliderNumberControl(16, 160, style.LineHeight);
        _lineSpacing = new SliderNumberControl(0, 48, style.LineSpacing);
        _horizontalSpacing = new SliderNumberControl(0, 120, style.HorizontalSpacing);
        _radius = new SliderNumberControl(0, 80, style.CornerRadius);
        _selectedColor = style.BackgroundColor;
        _selectedGradientColor = style.GradientColor;
        _selectedTextColor = style.TextColor;
        _selectedOpacity = style.BackgroundOpacity;
        _selectedGradientOpacity = style.GradientOpacity;
        Text = $"{targetName}樣式"; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(720, 640); ClientSize = new Size(820, 790); AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false; BackColor = Color.FromArgb(246, 248, 252); Font = new Font("Microsoft JhengHei UI", 10F);

        foreach (var item in FontFamily.Families.Select(value => new FontChoice(value.Name, SupportsChinese(value.Name))).OrderByDescending(value => value.SupportsChinese).ThenBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase)) _family.Items.Add(item);
        _family.DrawItem += DrawFontItem;
        _systemColor.Checked = style.FollowThemeColor;
        _gradient.Checked = style.GradientEnabled;
        _family.SelectedItem = _family.Items.Cast<FontChoice>().FirstOrDefault(value => value.Name.Equals(style.FontFamily, StringComparison.OrdinalIgnoreCase));
        if (_family.SelectedIndex < 0 && _family.Items.Count > 0) _family.SelectedItem = _family.Items.Cast<FontChoice>().FirstOrDefault(value => value.Name == "Microsoft JhengHei UI");
        if (_family.SelectedIndex < 0 && _family.Items.Count > 0) _family.SelectedIndex = 0;
        _weight.Value = style.Bold ? FontStyle.Bold : FontStyle.Regular; _alignment.Value = style.TextAlignment; _distribution.Value = style.Distribution; _gradientDirection.Value = style.GradientDirection; _gradientMode.Value = style.GradientMode;
        UpdateWeightButtons();

        var isListStyle = targetName.Contains("清單", StringComparison.Ordinal);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22), ColumnCount = 2, RowCount = isListStyle ? 19 : 18 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < table.RowCount; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AddRow(table, 0, "背景顏色與透明度", _color); AddRow(table, 1, "系統顏色", _systemColor);
        AddRow(table, 2, "漸層", _gradient); AddRow(table, 3, "第二顏色與透明度", _gradientColor);
        var gradientActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var swap = new Button { Text = "⇅ 反轉顏色", AutoSize = true }; swap.Click += (_, _) => { (_selectedColor, _selectedGradientColor) = (_selectedGradientColor, _selectedColor); (_selectedOpacity, _selectedGradientOpacity) = (_selectedGradientOpacity, _selectedOpacity); _systemColor.Checked = false; UpdatePreview(); };
        gradientActions.Controls.Add(swap); gradientActions.Controls.Add(_gradientMode); AddRow(table, 4, "漸層樣式", gradientActions);
        AddRow(table, 5, "漸層過渡", _gradientTransition); AddRow(table, 6, "漸層方向", _gradientDirection);
        var clear = new Button { Text = "恢復完全透明", AutoSize = true }; clear.Click += (_, _) => { _selectedOpacity = 0; _selectedGradientOpacity = 0; UpdatePreview(); }; AddRow(table, 7, "背景重設", clear);
        var fontPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        fontPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); fontPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var resetFont = new Button { Text = "預設", AutoSize = true, Margin = new Padding(0, 0, 8, 0) }; resetFont.Click += (_, _) => _family.SelectedItem = _family.Items.Cast<FontChoice>().FirstOrDefault(value => value.Name == "Microsoft JhengHei UI");
        fontPanel.Controls.Add(resetFont, 0, 0); fontPanel.Controls.Add(_family, 1, 0); AddRow(table, 8, "字型", fontPanel);
        AddRow(table, 9, "字體顏色", _textColor); AddRow(table, 10, "字體大小", _fontSize); AddRow(table, 11, "字體粗細", _weight); AddRow(table, 12, "行高", _lineHeight);
        AddRow(table, 13, "行距／上下項目間距", _lineSpacing);
        var styleRow = 14;
        if (isListStyle) { AddRow(table, styleRow, "左右項目間距", _horizontalSpacing); styleRow++; }
        AddRow(table, styleRow++, "圓角大小（0 為直角）", _radius);
        AddRow(table, styleRow++, "文字對齊", _alignment); AddRow(table, styleRow, "內容排列（左右）", _distribution);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(100, 38) };
        var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, Size = new Size(100, 38) };
        buttons.Controls.AddRange([cancel, ok]); Controls.Add(table); Controls.Add(buttons); CancelButton = cancel;
        _color.Click += (_, _) => ChooseColorWithAlpha(true); _gradientColor.Click += (_, _) => ChooseColorWithAlpha(false); _textColor.Click += (_, _) => ChooseColor(ref _selectedTextColor, _textColor);
        _systemColor.CheckedChanged += (_, _) => UpdatePreview(); _gradient.CheckedChanged += (_, _) => UpdatePreview();
        foreach (var slider in new[] { _gradientTransition, _fontSize, _lineHeight, _lineSpacing, _horizontalSpacing, _radius }) slider.ValueChanged += (_, _) => UpdatePreview();
        _family.SelectedIndexChanged += (_, _) => { UpdateWeightButtons(); UpdatePreview(); };
        _weight.ValueChanged += (_, _) => UpdatePreview(); _alignment.ValueChanged += (_, _) => UpdatePreview();
        _distribution.ValueChanged += (_, _) => UpdatePreview(); _gradientDirection.ValueChanged += (_, _) => UpdatePreview(); _gradientMode.ValueChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    public OverlayStyleOptions SelectedStyle => new()
    {
        BackgroundColor = _systemColor.Checked ? OverlayAppearanceSettings.GetWindowsThemeColor() : _selectedColor,
        BackgroundOpacity = _selectedOpacity, FollowThemeColor = _systemColor.Checked,
        GradientEnabled = _gradient.Checked, GradientColor = _selectedGradientColor, GradientOpacity = _selectedGradientOpacity, GradientTransition = (int)_gradientTransition.Value, GradientDirection = _gradientDirection.Value, GradientMode = _gradientMode.Value,
        TextColor = _selectedTextColor, FontFamily = (_family.SelectedItem as FontChoice)?.Name ?? "Microsoft JhengHei UI", FontSize = (float)_fontSize.Value, Bold = _weight.Value == FontStyle.Bold,
        LineHeight = (int)_lineHeight.Value, LineSpacing = (int)_lineSpacing.Value, HorizontalSpacing = (int)_horizontalSpacing.Value, CornerRadius = (int)_radius.Value,
        TextAlignment = _alignment.Value, Distribution = _distribution.Value
    };

    private static void AddRow(TableLayoutPanel table, int row, string title, Control control)
    {
        table.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 12, 12, 12) }, 0, row);
        control.Margin = new Padding(3, 6, 3, 6); table.Controls.Add(control, 1, row);
    }
    private void ChooseColor(ref Color target, Button button, bool disableSystemColor = false)
    {
        using var picker = new ColorDialog { Color = target, FullOpen = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        target = picker.Color; if (disableSystemColor) _systemColor.Checked = false; UpdatePreview();
    }
    private void ChooseColorWithAlpha(bool primary)
    {
        var originalColor = primary ? _selectedColor : _selectedGradientColor;
        var originalAlpha = primary ? _selectedOpacity : _selectedGradientOpacity;
        var usingSystemColor = primary && _systemColor.Checked;
        using var dialog = new ColorWithAlphaDialog(usingSystemColor ? OverlayAppearanceSettings.GetWindowsThemeColor() : originalColor, originalAlpha, !usingSystemColor);
        void ApplyDialogValue()
        {
            if (primary) { if (!usingSystemColor) _selectedColor = dialog.SelectedColor; _selectedOpacity = dialog.SelectedAlpha; }
            else { _selectedGradientColor = dialog.SelectedColor; _selectedGradientOpacity = dialog.SelectedAlpha; }
            UpdatePreview();
        }
        dialog.PreviewChanged += (_, _) => ApplyDialogValue();
        var result = dialog.ShowDialog(this);
        if (result == DialogResult.OK) ApplyDialogValue();
        else
        {
            if (primary) { _selectedColor = originalColor; _selectedOpacity = originalAlpha; }
            else { _selectedGradientColor = originalColor; _selectedGradientOpacity = originalAlpha; }
            UpdatePreview();
        }
    }
    private void UpdatePreview()
    {
        _color.Enabled = true; SetColorButton(_color, _systemColor.Checked ? OverlayAppearanceSettings.GetWindowsThemeColor() : _selectedColor, _selectedOpacity); SetColorButton(_gradientColor, _selectedGradientColor, _selectedGradientOpacity); SetColorButton(_textColor, _selectedTextColor, 255);
        _gradientColor.Enabled = _gradient.Checked; _gradientTransition.Enabled = _gradient.Checked; _gradientDirection.Enabled = _gradient.Checked && _gradientMode.Value == OverlayGradientMode.Single; _gradientMode.Enabled = _gradient.Checked;
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }
    private void DrawFontItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground(); if (e.Index < 0 || e.Index >= _family.Items.Count) return;
        var choice = _family.Items[e.Index] as FontChoice ?? new FontChoice("Microsoft JhengHei UI", true); var name = choice.Name; var supported = choice.SupportsChinese;
        using var nameFont = CreateFont(new OverlayStyleOptions { FontFamily = name, FontSize = 10F }); using var warningFont = new Font(Font.FontFamily, 9F);
        using var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : ForeColor);
        e.Graphics.DrawString(name, nameFont, brush, e.Bounds.X + 3, e.Bounds.Y + 3);
        if (!supported) { var width = e.Graphics.MeasureString(name, nameFont).Width; e.Graphics.DrawString("（不支援中文）", warningFont, brush, e.Bounds.X + 5 + width, e.Bounds.Y + 4); }
        e.DrawFocusRectangle();
    }
    private static void SetColorButton(Button button, Color color, byte alpha) { button.BackColor = color; button.ForeColor = color.GetBrightness() < .5F ? Color.White : Color.Black; button.Text = button.Text.Split('（')[0].TrimEnd() + $"（{Math.Round(alpha / 255D * 100D)}%）"; }
    private void UpdateWeightButtons()
    {
        var family = (_family.SelectedItem as FontChoice)?.Name ?? "Microsoft JhengHei UI";
        try
        {
            using var value = new FontFamily(family); var regular = value.IsStyleAvailable(FontStyle.Regular); var bold = value.IsStyleAvailable(FontStyle.Bold);
            _weight.SetVisible(FontStyle.Regular, regular); _weight.SetVisible(FontStyle.Bold, bold);
            if (_weight.Value == FontStyle.Bold && !bold) _weight.Value = FontStyle.Regular;
            if (_weight.Value == FontStyle.Regular && !regular && bold) _weight.Value = FontStyle.Bold;
        }
        catch { _weight.SetVisible(FontStyle.Regular, true); _weight.SetVisible(FontStyle.Bold, false); _weight.Value = FontStyle.Regular; }
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

internal static class OverlayStyleRendering
{
    internal static float ResizeIndicatorThickness => Math.Clamp(SystemInformation.VerticalScrollBarWidth * .65F, 10F, 14F);

    public static OverlayResizeCorner GetResizeCorner(Point point, Size size, int range = 24)
    {
        var left = point.X >= -range && point.X <= range;
        var right = point.X >= size.Width - range && point.X <= size.Width + range;
        var top = point.Y >= -range && point.Y <= range;
        var bottom = point.Y >= size.Height - range && point.Y <= size.Height + range;
        if (left && top) return OverlayResizeCorner.TopLeft;
        if (right && top) return OverlayResizeCorner.TopRight;
        if (left && bottom) return OverlayResizeCorner.BottomLeft;
        if (right && bottom) return OverlayResizeCorner.BottomRight;
        return OverlayResizeCorner.None;
    }

    public static void DrawResizeIndicator(Graphics graphics, Rectangle frame, int cornerRadius, OverlayResizeCorner corner)
    {
        if (corner == OverlayResizeCorner.None) return;
        var color = Color.FromArgb(245, 96, 165, 250);
        if (cornerRadius <= 0)
        {
            using var brush = new SolidBrush(color); const int size = 16;
            var points = corner switch
            {
                OverlayResizeCorner.TopLeft => new[] { new Point(frame.Left, frame.Top), new Point(frame.Left + size, frame.Top), new Point(frame.Left, frame.Top + size) },
                OverlayResizeCorner.TopRight => new[] { new Point(frame.Right, frame.Top), new Point(frame.Right - size, frame.Top), new Point(frame.Right, frame.Top + size) },
                OverlayResizeCorner.BottomLeft => new[] { new Point(frame.Left, frame.Bottom), new Point(frame.Left + size, frame.Bottom), new Point(frame.Left, frame.Bottom - size) },
                _ => new[] { new Point(frame.Right, frame.Bottom), new Point(frame.Right - size, frame.Bottom), new Point(frame.Right, frame.Bottom - size) }
            };
            graphics.FillPolygon(brush, points); return;
        }
        var thickness = ResizeIndicatorThickness;
        var inset = (int)Math.Ceiling(thickness / 2F);
        var resizeFrame = Rectangle.Inflate(frame, -inset, -inset);
        var radius = Math.Min(cornerRadius, Math.Min(resizeFrame.Width, resizeFrame.Height) / 2); var diameter = radius * 2; var tail = Math.Min(34, Math.Max(14, radius));
        using var pen = new Pen(color, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        switch (corner)
        {
            case OverlayResizeCorner.TopLeft:
                graphics.DrawArc(pen, resizeFrame.Left, resizeFrame.Top, diameter, diameter, 180, 90); graphics.DrawLine(pen, resizeFrame.Left, resizeFrame.Top + radius, resizeFrame.Left, resizeFrame.Top + radius + tail); graphics.DrawLine(pen, resizeFrame.Left + radius, resizeFrame.Top, resizeFrame.Left + radius + tail, resizeFrame.Top); break;
            case OverlayResizeCorner.TopRight:
                graphics.DrawArc(pen, resizeFrame.Right - diameter, resizeFrame.Top, diameter, diameter, 270, 90); graphics.DrawLine(pen, resizeFrame.Right, resizeFrame.Top + radius, resizeFrame.Right, resizeFrame.Top + radius + tail); graphics.DrawLine(pen, resizeFrame.Right - radius, resizeFrame.Top, resizeFrame.Right - radius - tail, resizeFrame.Top); break;
            case OverlayResizeCorner.BottomLeft:
                graphics.DrawArc(pen, resizeFrame.Left, resizeFrame.Bottom - diameter, diameter, diameter, 90, 90); graphics.DrawLine(pen, resizeFrame.Left, resizeFrame.Bottom - radius, resizeFrame.Left, resizeFrame.Bottom - radius - tail); graphics.DrawLine(pen, resizeFrame.Left + radius, resizeFrame.Bottom, resizeFrame.Left + radius + tail, resizeFrame.Bottom); break;
            case OverlayResizeCorner.BottomRight:
                graphics.DrawArc(pen, resizeFrame.Right - diameter, resizeFrame.Bottom - diameter, diameter, diameter, 0, 90); graphics.DrawLine(pen, resizeFrame.Right, resizeFrame.Bottom - radius, resizeFrame.Right, resizeFrame.Bottom - radius - tail); graphics.DrawLine(pen, resizeFrame.Right - radius, resizeFrame.Bottom, resizeFrame.Right - radius - tail, resizeFrame.Bottom); break;
        }
    }

    public static SolidBrush CreateInputHitBrush(Color color) => new(Color.FromArgb(1, color.R, color.G, color.B));

    public static Brush CreateBackgroundBrush(Rectangle bounds, OverlayStyleOptions style, Color primary)
    {
        var first = Color.FromArgb(style.BackgroundOpacity, primary); var second = Color.FromArgb(style.GradientOpacity, style.GradientColor);
        if (!style.GradientEnabled || bounds.Width <= 1 || bounds.Height <= 1) return new SolidBrush(first);
        if (style.GradientMode == OverlayGradientMode.Edges)
        {
            var path = new GraphicsPath(); path.AddRectangle(bounds);
            var radial = new PathGradientBrush(path) { CenterColor = second, SurroundColors = [first] };
            var focus = Math.Clamp(style.GradientTransition / 100F, .02F, .98F); radial.FocusScales = new PointF(focus, focus);
            path.Dispose(); return radial;
        }
        if (style.GradientMode == OverlayGradientMode.Sandwich)
        {
            var vertical = new LinearGradientBrush(bounds, first, first, 90F);
            var center = Math.Clamp(style.GradientTransition / 100F, .05F, .95F);
            vertical.InterpolationColors = new ColorBlend { Colors = [first, second, first], Positions = [0F, center, 1F] };
            return vertical;
        }
        var angle = style.GradientDirection switch { OverlayGradientDirection.TopToBottom => 90F, OverlayGradientDirection.DiagonalDown => 45F, OverlayGradientDirection.DiagonalUp => 315F, _ => 0F };
        var brush = new LinearGradientBrush(bounds, first, second, angle);
        var position = Math.Clamp(style.GradientTransition / 100F, .02F, .98F);
        brush.InterpolationColors = new ColorBlend { Colors = [first, Blend(first, second, .5F), second], Positions = [0F, position, 1F] };
        return brush;
    }
    private static Color Blend(Color first, Color second, float amount) => Color.FromArgb((int)(first.A + (second.A - first.A) * amount), (int)(first.R + (second.R - first.R) * amount), (int)(first.G + (second.G - first.G) * amount), (int)(first.B + (second.B - first.B) * amount));
}

internal static class OverlayAppearanceSettings
{
    public static OverlayStyleOptions LoadStyle(string? identity, OverlayStyleOptions fallback)
    {
        var result = fallback.Clone();
        try
        {
            using var profileKey = Registry.CurrentUser.OpenSubKey(OverlaySettings.GetSettingsKey(identity));
            using var legacyKey = profileKey?.GetValue("BackgroundColor") is int ? null : Registry.CurrentUser.OpenSubKey(OverlaySettings.GetLegacySettingsKey(identity));
            var key = profileKey?.GetValue("BackgroundColor") is int ? profileKey : legacyKey;
            if (key is null) return result;
            if (key.GetValue("BackgroundColor") is int rgb) result.BackgroundColor = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            if (key.GetValue("BackgroundOpacity") is int opacity) result.BackgroundOpacity = (byte)Math.Clamp(opacity, 0, 255);
            if (key.GetValue("FollowThemeColor") is int follow) result.FollowThemeColor = follow != 0;
            if (key.GetValue("GradientEnabled") is int gradient) result.GradientEnabled = gradient != 0;
            if (key.GetValue("GradientColor") is int gradientRgb) result.GradientColor = Color.FromArgb((gradientRgb >> 16) & 255, (gradientRgb >> 8) & 255, gradientRgb & 255);
            result.GradientOpacity = key.GetValue("GradientOpacity") is int gradientOpacity ? (byte)Math.Clamp(gradientOpacity, 0, 255) : result.BackgroundOpacity;
            if (key.GetValue("GradientTransition") is int transition) result.GradientTransition = Math.Clamp(transition, 0, 100);
            if (key.GetValue("GradientDirection") is int direction && Enum.IsDefined(typeof(OverlayGradientDirection), direction)) result.GradientDirection = (OverlayGradientDirection)direction;
            if (key.GetValue("GradientMode") is int mode && Enum.IsDefined(typeof(OverlayGradientMode), mode)) result.GradientMode = (OverlayGradientMode)mode;
            if (key.GetValue("TextColor") is int textRgb) result.TextColor = Color.FromArgb((textRgb >> 16) & 255, (textRgb >> 8) & 255, textRgb & 255);
            if (key.GetValue("FontFamily") is string family && family.Length > 0) result.FontFamily = family;
            if (key.GetValue("FontSizeX10") is int size) result.FontSize = Math.Clamp(size / 10F, 6F, 48F);
            if (key.GetValue("FontBold") is int bold) result.Bold = bold != 0;
            if (key.GetValue("LineHeight") is int height) result.LineHeight = Math.Clamp(height, 16, 160);
            if (key.GetValue("LineSpacing") is int spacing) result.LineSpacing = Math.Clamp(spacing, 0, 48);
            if (key.GetValue("HorizontalSpacing") is int horizontalSpacing) result.HorizontalSpacing = Math.Clamp(horizontalSpacing, 0, 120);
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
            var gradient = style.GradientColor; var text = style.TextColor;
            key.SetValue("GradientEnabled", style.GradientEnabled ? 1 : 0, RegistryValueKind.DWord); key.SetValue("GradientColor", (gradient.R << 16) | (gradient.G << 8) | gradient.B, RegistryValueKind.DWord);
            key.SetValue("GradientOpacity", style.GradientOpacity, RegistryValueKind.DWord); key.SetValue("GradientTransition", style.GradientTransition, RegistryValueKind.DWord); key.SetValue("GradientDirection", (int)style.GradientDirection, RegistryValueKind.DWord); key.SetValue("GradientMode", (int)style.GradientMode, RegistryValueKind.DWord);
            key.SetValue("TextColor", (text.R << 16) | (text.G << 8) | text.B, RegistryValueKind.DWord);
            key.SetValue("FontFamily", style.FontFamily, RegistryValueKind.String); key.SetValue("FontSizeX10", (int)Math.Round(style.FontSize * 10), RegistryValueKind.DWord);
            key.SetValue("FontBold", style.Bold ? 1 : 0, RegistryValueKind.DWord); key.SetValue("LineHeight", style.LineHeight, RegistryValueKind.DWord);
            key.SetValue("LineSpacing", style.LineSpacing, RegistryValueKind.DWord); key.SetValue("HorizontalSpacing", style.HorizontalSpacing, RegistryValueKind.DWord); key.SetValue("CornerRadius", style.CornerRadius, RegistryValueKind.DWord);
            key.SetValue("TextAlignment", (int)style.TextAlignment, RegistryValueKind.DWord); key.SetValue("Distribution", (int)style.Distribution, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
    }
    public static Color GetWindowsThemeColor()
    {
        try
        {
            using var explorer = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            // StartColorMenu is the composed Start/taskbar surface color used by Explorer when taskbar accent color is enabled.
            if (explorer?.GetValue("StartColorMenu") is int start) return DecodeExplorerAccentColor(unchecked((uint)start));
            if (explorer?.GetValue("AccentColorMenu") is int accent) return DecodeExplorerAccentColor(unchecked((uint)accent));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("ColorizationColor") is int signed) { var value = unchecked((uint)signed); return Color.FromArgb((int)((value >> 16) & 255), (int)((value >> 8) & 255), (int)(value & 255)); }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        return SystemColors.Highlight;
    }
    internal static Color DecodeExplorerAccentColor(uint abgr) => Color.FromArgb((int)(abgr & 255), (int)((abgr >> 8) & 255), (int)((abgr >> 16) & 255));
}
