using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinScpGlass.ViewModels;

namespace WinScpGlass.Terminal;

/// <summary>
/// Renders a <see cref="TerminalEmulator"/> as a cyberpunk green-phosphor screen: every colour a program
/// asks for is drawn as a shade of neon green, over the black screen provided by the surrounding view.
/// </summary>
public sealed class TerminalView : FrameworkElement
{
    // ---- cyberpunk phosphor: black screen, every colour drawn as a shade of neon green.
    // Standard xterm colours only decide how bright each green is, so ls/git output stays readable.
    private static readonly Color[] StandardPalette =
    {
        C("#000000"), C("#CD0000"), C("#00CD00"), C("#CDCD00"), C("#0000EE"), C("#CD00CD"), C("#00CDCD"), C("#E5E5E5"),
        C("#7F7F7F"), C("#FF0000"), C("#00FF00"), C("#FFFF00"), C("#5C5CFF"), C("#FF00FF"), C("#00FFFF"), C("#FFFFFF"),
    };
    private static readonly Color Phosphor = C("#39FF7F");
    private static readonly Color ScreenBlack = C("#000000");

    /// <summary>Any colour → green of the same perceived brightness, never too dark to read on black.</summary>
    private static Color ToGreen(Color c)
    {
        double luma = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        double level = 0.4 + luma * 0.6;
        return Color.FromRgb((byte)(level * 110), (byte)(115 + level * 140), (byte)(60 + level * 95));
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private readonly Thickness _padding = new(14, 10, 14, 10);
    private readonly Dictionary<Color, SolidColorBrush> _brushes = new();
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
    private bool _blinkOn = true;

    private TerminalViewModel? _vm;
    private Typeface _regular = null!, _bold = null!, _italic = null!, _boldItalic = null!;
    private double _fontSize = 13.5, _cellWidth, _cellHeight, _baseline, _pixelsPerDip = 1;

    private int _scrollOffset;
    private long _lastPushed;

    private (int Line, int Column)? _selectionStart, _selectionEnd;
    private bool _selecting;

    public TerminalView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        SnapsToDevicePixels = true;
        _blink.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            InvalidateVisual();
        };
        DataContextChanged += (_, e) => Attach(e.NewValue as TerminalViewModel);
        Loaded += (_, _) => UpdateMetrics();
        Unloaded += (_, _) => _blink.Stop();
        ContextMenu = BuildContextMenu();
    }

    private TerminalEmulator? Emulator => _vm?.Emulator;

    private void Attach(TerminalViewModel? vm)
    {
        if (_vm != null) _vm.Updated -= OnUpdated;
        _vm = vm;
        if (_vm != null)
        {
            _vm.Updated += OnUpdated;
            _lastPushed = _vm.Emulator.ScrollbackPushed;
            ResizeTerminal();
        }
        InvalidateVisual();
    }

    private void OnUpdated()
    {
        var emulator = Emulator;
        if (emulator == null) return;
        // Keep a scrolled-back view still while new output arrives below it.
        long pushed = emulator.ScrollbackPushed;
        if (_scrollOffset > 0) _scrollOffset = Math.Min(emulator.HistoryLength, _scrollOffset + (int)(pushed - _lastPushed));
        _lastPushed = pushed;
        _blinkOn = true;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ metrics & size

    private void UpdateMetrics()
    {
        var family = new FontFamily("Cascadia Mono, Cascadia Code, Consolas");
        _regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _bold = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _italic = new Typeface(family, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        _boldItalic = new Typeface(family, FontStyles.Italic, FontWeights.SemiBold, FontStretches.Normal);
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var sample = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _regular, _fontSize, Brushes.White, _pixelsPerDip);
        _cellWidth = sample.WidthIncludingTrailingWhitespace / 10;
        _cellHeight = Math.Ceiling(family.LineSpacing * _fontSize * 1.08);
        _baseline = (_cellHeight - sample.Height) / 2;
        ResizeTerminal();
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ResizeTerminal();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateMetrics();
    }

    private void ResizeTerminal()
    {
        if (_vm == null || _cellWidth <= 0 || ActualWidth <= 0) return;
        int columns = (int)((ActualWidth - _padding.Left - _padding.Right) / _cellWidth);
        int rows = (int)((ActualHeight - _padding.Top - _padding.Bottom) / _cellHeight);
        if (columns < 10 || rows < 3) return;
        if (columns != _vm.Emulator.Columns || rows != _vm.Emulator.Rows) _vm.Resize(columns, rows);
    }

    // ------------------------------------------------------------------ colours

    private static Color Foreground => Phosphor;

    private static Color Accent => Phosphor;

    private static Color Resolve(int color, bool foreground)
    {
        if (color == TermColor.Default) return foreground ? Foreground : Colors.Transparent;
        return ToGreen(Standard(color));
    }

    private static Color Standard(int color)
    {
        if (TermColor.IsRgb(color)) return Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);
        if (color < 16) return StandardPalette[color];
        if (color < 232)
        {
            int index = color - 16;
            static byte Level(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
            return Color.FromRgb(Level(index / 36), Level(index / 6 % 6), Level(index % 6));
        }
        byte gray = (byte)(8 + (color - 232) * 10);
        return Color.FromRgb(gray, gray, gray);
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private SolidColorBrush Brush(Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            brush.Freeze();
            _brushes[color] = brush;
        }
        return brush;
    }

    private readonly record struct RunStyle(Color Foreground, Color Background, bool Bold, bool Italic, bool Underline, bool Strike);

    private RunStyle StyleOf(in TermCell cell, bool selected)
    {
        var style = cell.Style;
        Color fg = Resolve(cell.Fg, true);
        Color bg = cell.Bg == TermColor.Default ? Colors.Transparent : WithAlpha(Resolve(cell.Bg, false), 0x5C);

        if ((style & CellStyle.Inverse) != 0)
        {
            // Inverse video: classic green block with black text.
            var highlight = cell.Fg == TermColor.Default ? Accent : fg;
            fg = cell.Bg == TermColor.Default ? ScreenBlack : Resolve(cell.Bg, false);
            bg = WithAlpha(highlight, 0xE6);
        }
        if ((style & CellStyle.Dim) != 0) fg = WithAlpha(fg, 0x99);
        if ((style & CellStyle.Hidden) != 0) fg = Colors.Transparent;
        if (selected) bg = WithAlpha(Accent, 0x55);

        return new RunStyle(fg, bg, (style & CellStyle.Bold) != 0, (style & CellStyle.Italic) != 0,
            (style & CellStyle.Underline) != 0, (style & CellStyle.Strike) != 0);
    }

    // ------------------------------------------------------------------ rendering

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill keeps the whole surface clickable without hiding the glass.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var emulator = Emulator;
        if (emulator == null || _cellWidth <= 0) return;

        int top = emulator.HistoryLength - _scrollOffset;
        var (selStart, selEnd) = OrderedSelection();
        var text = new System.Text.StringBuilder();

        for (int row = 0; row < emulator.Rows; row++)
        {
            int absolute = top + row;
            var cells = emulator.LineAt(absolute).Cells;
            double y = _padding.Top + row * _cellHeight;

            int x = 0;
            while (x < cells.Length && x < emulator.Columns)
            {
                var style = StyleOf(cells[x], IsSelected(absolute, x, selStart, selEnd));
                int start = x;
                text.Clear();
                while (x < cells.Length && x < emulator.Columns && StyleOf(cells[x], IsSelected(absolute, x, selStart, selEnd)) == style)
                {
                    text.Append(cells[x].Ch == '\0' ? ' ' : cells[x].Ch);
                    x++;
                }
                DrawRun(dc, text.ToString(), style, _padding.Left + start * _cellWidth, y);
            }
        }

        DrawCursor(dc, emulator, top);
    }

    private void DrawRun(DrawingContext dc, string run, RunStyle style, double x, double y)
    {
        if (style.Background.A > 0)
        {
            dc.DrawRectangle(Brush(style.Background), null, new Rect(x, y, run.Length * _cellWidth, _cellHeight));
        }
        if (style.Foreground.A == 0 || string.IsNullOrWhiteSpace(run) && !style.Underline && !style.Strike) return;

        var typeface = style.Bold ? (style.Italic ? _boldItalic : _bold) : (style.Italic ? _italic : _regular);
        var formatted = new FormattedText(run, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, _fontSize, Brush(style.Foreground), _pixelsPerDip);
        if (style.Underline) formatted.SetTextDecorations(TextDecorations.Underline);
        if (style.Strike) formatted.SetTextDecorations(TextDecorations.Strikethrough);
        dc.DrawText(formatted, new Point(x, y + _baseline));
    }

    private void DrawCursor(DrawingContext dc, TerminalEmulator emulator, int top)
    {
        if (_vm is not { IsConnected: true } || !emulator.CursorVisible || _scrollOffset != 0) return;
        int screenTop = emulator.HistoryLength - top; // 0 when not scrolled
        double x = _padding.Left + emulator.CursorX * _cellWidth;
        double y = _padding.Top + (emulator.CursorY + screenTop) * _cellHeight;
        var rect = new Rect(x, y + 1, _cellWidth, _cellHeight - 2);
        var accent = Accent;

        if (!IsKeyboardFocused)
        {
            dc.DrawRoundedRectangle(null, new Pen(Brush(WithAlpha(accent, 0xCC)), 1.2), rect, 2, 2);
            return;
        }
        if (!_blinkOn) return;

        dc.DrawRoundedRectangle(Brush(WithAlpha(accent, 0xE6)), null, rect, 2, 2);
        var cell = emulator.ScreenLine(emulator.CursorY).Cells[Math.Min(emulator.CursorX, emulator.Columns - 1)];
        if (cell.Ch != ' ' && cell.Ch != '\0')
        {
            var formatted = new FormattedText(cell.Ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _regular, _fontSize, Brush(ScreenBlack), _pixelsPerDip);
            dc.DrawText(formatted, new Point(x, y + _baseline));
        }
    }

    // ------------------------------------------------------------------ selection

    private ((int Line, int Column) Start, (int Line, int Column) End) OrderedSelection()
    {
        if (_selectionStart is not { } a || _selectionEnd is not { } b) return ((-1, -1), (-1, -1));
        return (a.Line < b.Line || a.Line == b.Line && a.Column <= b.Column) ? (a, b) : (b, a);
    }

    private static bool IsSelected(int line, int column, (int Line, int Column) start, (int Line, int Column) end)
    {
        if (start.Line < 0) return false;
        if (line < start.Line || line > end.Line) return false;
        if (line == start.Line && column < start.Column) return false;
        if (line == end.Line && column > end.Column) return false;
        return true;
    }

    private bool HasSelection => _selectionStart != null && _selectionEnd != null && _selectionStart != _selectionEnd;

    private (int Line, int Column) HitCell(Point point)
    {
        var emulator = Emulator!;
        int column = Math.Clamp((int)((point.X - _padding.Left) / _cellWidth), 0, emulator.Columns - 1);
        int row = Math.Clamp((int)((point.Y - _padding.Top) / _cellHeight), 0, emulator.Rows - 1);
        return (emulator.HistoryLength - _scrollOffset + row, column);
    }

    private void ClearSelection()
    {
        if (_selectionStart == null) return;
        _selectionStart = _selectionEnd = null;
        InvalidateVisual();
    }

    private void CopySelection()
    {
        if (!HasSelection || Emulator == null) return;
        var (start, end) = OrderedSelection();
        var text = Emulator.GetText(start.Line, start.Column, end.Line, end.Column);
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard briefly locked by another app.
        }
        ClearSelection();
    }

    private void Paste()
    {
        if (_vm == null || !Clipboard.ContainsText()) return;
        var text = Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r');
        if (_vm.Emulator.BracketedPaste) text = "\x1b[200~" + text + "\x1b[201~";
        SendInput(text);
    }

    private void SelectWord((int Line, int Column) at)
    {
        var cells = Emulator!.LineAt(at.Line).Cells;
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || "-_./~:@".Contains(c);
        if (at.Column >= cells.Length || !IsWord(cells[at.Column].Ch)) return;
        int start = at.Column, end = at.Column;
        while (start > 0 && IsWord(cells[start - 1].Ch)) start--;
        while (end < cells.Length - 1 && IsWord(cells[end + 1].Ch)) end++;
        _selectionStart = (at.Line, start);
        _selectionEnd = (at.Line, end);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (Emulator == null) return;
        var cell = HitCell(e.GetPosition(this));
        if (e.ClickCount == 2)
        {
            SelectWord(cell);
            e.Handled = true;
            return;
        }
        _selectionStart = _selectionEnd = cell;
        _selecting = true;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting || Emulator == null) return;
        _selectionEnd = HitCell(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_selecting) return;
        _selecting = false;
        ReleaseMouseCapture();
        if (!HasSelection) ClearSelection();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var emulator = Emulator;
        if (emulator == null) return;
        int lines = Math.Max(1, Math.Abs(e.Delta) / 40);
        if (emulator.IsAlternateScreen)
        {
            // Full-screen apps (less, man, vim) scroll with arrow keys.
            string arrow = e.Delta > 0 ? "A" : "B";
            string sequence = (emulator.ApplicationCursorKeys ? "\x1bO" : "\x1b[") + arrow;
            _vm?.Send(string.Concat(Enumerable.Repeat(sequence, lines)));
        }
        else
        {
            _scrollOffset = Math.Clamp(_scrollOffset + (e.Delta > 0 ? lines : -lines), 0, emulator.HistoryLength);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ keyboard

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _blinkOn = true;
        _blink.Start();
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _blink.Stop();
        InvalidateVisual();
    }

    private void SendInput(string text)
    {
        if (_vm == null) return;
        _scrollOffset = 0;
        _blinkOn = true;
        ClearSelection();
        _vm.Send(text);
        InvalidateVisual();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_vm == null) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;

        // Clipboard: Ctrl+C copies only when something is selected, otherwise it interrupts.
        if ((ctrl && shift && key == Key.C) || (ctrl && !shift && key == Key.C && HasSelection))
        {
            CopySelection();
            e.Handled = true;
            return;
        }
        if ((ctrl && key == Key.V) || (shift && key == Key.Insert))
        {
            Paste();
            e.Handled = true;
            return;
        }
        if (shift && key is Key.PageUp or Key.PageDown && !Emulator!.IsAlternateScreen)
        {
            int page = Math.Max(1, Emulator.Rows - 2);
            _scrollOffset = Math.Clamp(_scrollOffset + (key == Key.PageUp ? page : -page), 0, Emulator.HistoryLength);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        string? sequence = Translate(key, ctrl, shift, alt);
        if (sequence != null)
        {
            SendInput(sequence);
            e.Handled = true;
        }
    }

    private string? Translate(Key key, bool ctrl, bool shift, bool alt)
    {
        bool app = Emulator!.ApplicationCursorKeys;
        // xterm modifier parameter: 2 shift, 3 alt, 5 ctrl …
        int modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

        string Cursor(char final) => modifier > 1 ? $"\x1b[1;{modifier}{final}" : (app ? $"\x1bO{final}" : $"\x1b[{final}");
        string Tilde(int code) => modifier > 1 ? $"\x1b[{code};{modifier}~" : $"\x1b[{code}~";

        switch (key)
        {
            case Key.Enter: return "\r";
            case Key.Back: return ctrl ? "\x08" : (alt ? "\x1b\x7f" : "\x7f");
            case Key.Tab: return shift ? "\x1b[Z" : "\t";
            case Key.Escape: return "\x1b";
            case Key.Up: return Cursor('A');
            case Key.Down: return Cursor('B');
            case Key.Right: return Cursor('C');
            case Key.Left: return Cursor('D');
            case Key.Home: return Cursor('H');
            case Key.End: return Cursor('F');
            case Key.Insert: return Tilde(2);
            case Key.Delete: return Tilde(3);
            case Key.PageUp: return Tilde(5);
            case Key.PageDown: return Tilde(6);
            case Key.F1: return "\x1bOP";
            case Key.F2: return "\x1bOQ";
            case Key.F3: return "\x1bOR";
            case Key.F4: return "\x1bOS";
            case Key.F5: return Tilde(15);
            case Key.F6: return Tilde(17);
            case Key.F7: return Tilde(18);
            case Key.F8: return Tilde(19);
            case Key.F9: return Tilde(20);
            case Key.F10: return Tilde(21);
            case Key.F11: return Tilde(23);
            case Key.F12: return Tilde(24);
        }

        // Ctrl+letter → control character (Ctrl+Alt is AltGr on many layouts, so it is left to text input).
        if (ctrl && !alt)
        {
            if (key >= Key.A && key <= Key.Z) return ((char)(key - Key.A + 1)).ToString();
            switch (key)
            {
                case Key.Space:
                case Key.D2: return "\0";
                case Key.Oem4: return "\x1b";      // Ctrl+[
                case Key.Oem5: return "\x1c";      // Ctrl+\
                case Key.Oem6: return "\x1d";      // Ctrl+]
                case Key.D6: return "\x1e";
                case Key.OemMinus: return "\x1f";
            }
        }

        // Alt+key → ESC prefix (readline word movement etc.)
        if (alt && !ctrl)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                char letter = (char)('a' + (key - Key.A));
                return "\x1b" + (shift ? char.ToUpperInvariant(letter) : letter);
            }
            if (key >= Key.D0 && key <= Key.D9) return "\x1b" + (char)('0' + (key - Key.D0));
            if (key == Key.OemPeriod) return "\x1b.";
        }
        return null;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (_vm == null || string.IsNullOrEmpty(e.Text)) return;
        // Control characters (Enter, Tab, Ctrl+…) are sent from key handling above.
        var printable = new string(e.Text.Where(c => c >= ' ' && c != '\x7f').ToArray());
        if (printable.Length == 0) return;
        SendInput(printable);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ context menu

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+Shift+C" };
        Controls.UiProps.SetIcon(copy, "");
        copy.Click += (_, _) => CopySelection();
        var paste = new MenuItem { Header = "Paste", InputGestureText = "Ctrl+V" };
        Controls.UiProps.SetIcon(paste, "");
        paste.Click += (_, _) => Paste();
        var clear = new MenuItem { Header = "Clear" };
        Controls.UiProps.SetIcon(clear, "");
        clear.Click += (_, _) => _vm?.ClearCommand.Execute(null);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(clear);
        menu.Opened += (_, _) => copy.IsEnabled = HasSelection;
        return menu;
    }
}
