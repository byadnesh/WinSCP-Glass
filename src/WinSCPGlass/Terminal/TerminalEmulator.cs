using System.Text;

namespace WinScpGlass.Terminal;

[Flags]
public enum CellStyle : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Hidden = 32,
    Strike = 64,
}

/// <summary>Colour of a cell: -1 = theme default, 0–255 = xterm palette, or 24-bit RGB tagged with <see cref="RgbFlag"/>.</summary>
public static class TermColor
{
    public const int Default = -1;
    public const int RgbFlag = 0x1000000;

    public static int Rgb(int r, int g, int b) => RgbFlag | (r << 16) | (g << 8) | b;
    public static bool IsRgb(int color) => color >= RgbFlag;
}

public struct TermCell
{
    public char Ch;
    public int Fg;
    public int Bg;
    public CellStyle Style;

    public static TermCell Blank(int bg = TermColor.Default) =>
        new() { Ch = ' ', Fg = TermColor.Default, Bg = bg };
}

public sealed class TermLine
{
    public TermCell[] Cells;
    /// <summary>The text continues on the next line (soft wrap), so copying joins them.</summary>
    public bool Wrapped;

    public TermLine(int columns, int bg = TermColor.Default)
    {
        Cells = new TermCell[columns];
        Array.Fill(Cells, TermCell.Blank(bg));
    }

    public void Resize(int columns)
    {
        if (columns == Cells.Length) return;
        int old = Cells.Length;
        Array.Resize(ref Cells, columns);
        for (int i = old; i < columns; i++) Cells[i] = TermCell.Blank();
    }
}

/// <summary>
/// A compact xterm-compatible terminal emulator: parses the byte stream from the server
/// (text, control characters, CSI/OSC escape sequences) into a grid of styled cells.
/// Supports colours (16, 256 and true colour), cursor addressing, scroll regions,
/// the alternate screen used by full-screen apps (vim, htop, nano, less), line drawing and scrollback.
/// </summary>
public sealed class TerminalEmulator
{
    public const int MaxScrollback = 5000;

    private enum ParseState { Ground, Escape, Csi, Osc, Charset, IgnoreString, EscapeHash }

    private struct SavedCursor
    {
        public int X, Y, Fg, Bg;
        public CellStyle Style;
        public bool LineDrawing, OriginMode;
    }

    // DEC special graphics (ESC ( 0) for 0x5F–0x7E: box drawing used by htop, mc, tmux, dialog…
    private const string DecGraphics = " ◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    private TermLine[] _main;
    private TermLine[] _alt;
    private TermLine[] _screen;

    private int _top, _bottom;
    private int _fg = TermColor.Default, _bg = TermColor.Default;
    private CellStyle _style;
    private bool _pendingWrap, _autoWrap = true, _originMode, _insertMode, _lineDrawing;
    private SavedCursor _savedMain, _savedAlt;
    private char _lastPrinted = ' ';
    private char _highSurrogate;

    private ParseState _state = ParseState.Ground;
    private readonly StringBuilder _params = new();
    private readonly StringBuilder _intermediate = new();
    private readonly StringBuilder _osc = new();
    private char _privateMarker;
    private bool _stringEscape;
    private char _charsetTarget;

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool ApplicationCursorKeys { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool IsAlternateScreen => _screen == _alt;
    public List<TermLine> Scrollback { get; } = new();
    /// <summary>Total lines ever pushed into scrollback; lets the view keep a scrolled-back position stable.</summary>
    public long ScrollbackPushed { get; private set; }

    /// <summary>Text the terminal must send back to the server (cursor position and device reports).</summary>
    public event Action<string>? Reply;
    public event Action<string>? TitleChanged;

    public TerminalEmulator(int columns, int rows)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(2, rows);
        _main = NewScreen();
        _alt = NewScreen();
        _screen = _main;
        _bottom = Rows - 1;
    }

    private TermLine[] NewScreen()
    {
        var screen = new TermLine[Rows];
        for (int i = 0; i < Rows; i++) screen[i] = new TermLine(Columns);
        return screen;
    }

    public TermLine ScreenLine(int row) => _screen[row];

    /// <summary>Lines of the combined history (scrollback, then the screen); alternate screen has no history.</summary>
    public int HistoryLength => IsAlternateScreen ? 0 : Scrollback.Count;

    public TermLine LineAt(int absolute) =>
        absolute < HistoryLength ? Scrollback[absolute] : _screen[Math.Clamp(absolute - HistoryLength, 0, Rows - 1)];

    // ------------------------------------------------------------------ input

    public void Feed(string text)
    {
        foreach (char c in text) Process(c);
    }

    private void Process(char c)
    {
        switch (_state)
        {
            case ParseState.Ground:
                Ground(c);
                break;
            case ParseState.Escape:
                Escape(c);
                break;
            case ParseState.Csi:
                Csi(c);
                break;
            case ParseState.Osc:
                Osc(c);
                break;
            case ParseState.Charset:
                if (_charsetTarget == '(') _lineDrawing = c == '0';
                _state = ParseState.Ground;
                break;
            case ParseState.EscapeHash:
                _state = ParseState.Ground;
                break;
            case ParseState.IgnoreString:
                if (c == '\a' || (_stringEscape && c == '\\')) _state = ParseState.Ground;
                _stringEscape = c == '\x1b';
                break;
        }
    }

    private void Ground(char c)
    {
        switch (c)
        {
            case '\x1b':
                _state = ParseState.Escape;
                _intermediate.Clear();
                return;
            case '\r':
                CursorX = 0;
                _pendingWrap = false;
                return;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                return;
            case '\b':
                if (CursorX > 0) CursorX--;
                _pendingWrap = false;
                return;
            case '\t':
                CursorX = Math.Min(Columns - 1, (CursorX / 8 + 1) * 8);
                _pendingWrap = false;
                return;
            case '\a':
            case '\x0e':
            case '\x0f':
                return;
        }
        if (c < ' ' || c == '\x7f') return;

        if (char.IsHighSurrogate(c))
        {
            _highSurrogate = c;
            return;
        }
        if (char.IsLowSurrogate(c))
        {
            // Characters outside the BMP (emoji) don't fit one UTF-16 cell; show a placeholder.
            _highSurrogate = '\0';
            Print('□');
            return;
        }
        // Combining marks would shift the grid; drop them.
        if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) return;
        Print(c);
    }

    private void Print(char c)
    {
        if (_lineDrawing && c >= '_' && c <= '~') c = DecGraphics[c - '_'];

        if (_pendingWrap)
        {
            if (_autoWrap)
            {
                _screen[CursorY].Wrapped = true;
                CursorX = 0;
                LineFeed();
            }
            _pendingWrap = false;
        }

        var cells = _screen[CursorY].Cells;
        if (_insertMode && CursorX < Columns - 1)
        {
            Array.Copy(cells, CursorX, cells, CursorX + 1, Columns - CursorX - 1);
        }
        cells[CursorX] = new TermCell { Ch = c, Fg = _fg, Bg = _bg, Style = _style };
        _lastPrinted = c;

        if (CursorX == Columns - 1) _pendingWrap = true;
        else CursorX++;
    }

    private void Escape(char c)
    {
        _state = ParseState.Ground;
        switch (c)
        {
            case '[':
                _params.Clear();
                _intermediate.Clear();
                _privateMarker = '\0';
                _state = ParseState.Csi;
                break;
            case ']':
                _osc.Clear();
                _stringEscape = false;
                _state = ParseState.Osc;
                break;
            case 'P':
            case 'X':
            case '^':
            case '_':
                _stringEscape = false;
                _state = ParseState.IgnoreString;
                break;
            case '(':
            case ')':
            case '*':
            case '+':
                _charsetTarget = c;
                _state = ParseState.Charset;
                break;
            case '#':
                _state = ParseState.EscapeHash;
                break;
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                CursorX = 0;
                LineFeed();
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'c':
                FullReset();
                break;
            // '=' '>' (keypad modes) and anything else: nothing to do.
        }
    }

    private void Osc(char c)
    {
        if (c == '\a' || (_stringEscape && c == '\\'))
        {
            _state = ParseState.Ground;
            var text = _osc.ToString();
            int semi = text.IndexOf(';');
            if (semi > 0 && (text[..semi] == "0" || text[..semi] == "2")) TitleChanged?.Invoke(text[(semi + 1)..]);
            return;
        }
        if (c == '\x1b')
        {
            _stringEscape = true;
            return;
        }
        _stringEscape = false;
        if (_osc.Length < 4096) _osc.Append(c);
    }

    private void Csi(char c)
    {
        if (c >= '0' && c <= ';' || c == ':')
        {
            _params.Append(c);
            return;
        }
        if (c is '?' or '>' or '<' or '=' && _params.Length == 0)
        {
            _privateMarker = c;
            return;
        }
        if (c >= ' ' && c <= '/')
        {
            _intermediate.Append(c);
            return;
        }
        _state = ParseState.Ground;
        if (c < '@' || c > '~') return;
        ExecuteCsi(c);
    }

    // ------------------------------------------------------------------ CSI

    private int[] ParseParams()
    {
        if (_params.Length == 0) return Array.Empty<int>();
        var parts = _params.ToString().Split(';');
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            int colon = part.IndexOf(':');
            if (colon >= 0) part = part[..colon];
            result[i] = int.TryParse(part, out int v) ? v : 0;
        }
        return result;
    }

    private static int Arg(int[] p, int index, int fallback) =>
        index < p.Length && p[index] > 0 ? p[index] : fallback;

    private void ExecuteCsi(char final)
    {
        var p = ParseParams();
        string intermediate = _intermediate.ToString();

        if (_privateMarker == '?')
        {
            if (final is 'h' or 'l') SetPrivateModes(p, final == 'h');
            return;
        }
        if (_privateMarker == '>')
        {
            if (final == 'c') Reply?.Invoke("\x1b[>0;0;0c");
            return;
        }
        if (intermediate.Length > 0)
        {
            if (intermediate == "!" && final == 'p') SoftReset();
            return; // cursor style (SP q) and other rarely-needed sequences
        }

        int n = Arg(p, 0, 1);
        switch (final)
        {
            case '@': InsertChars(n); break;
            case 'A': MoveCursor(CursorX, Math.Max(CursorY >= _top ? _top : 0, CursorY - n)); break;
            case 'B':
            case 'e': MoveCursor(CursorX, Math.Min(CursorY <= _bottom ? _bottom : Rows - 1, CursorY + n)); break;
            case 'C':
            case 'a': MoveCursor(Math.Min(Columns - 1, CursorX + n), CursorY); break;
            case 'D': MoveCursor(Math.Max(0, CursorX - n), CursorY); break;
            case 'E': MoveCursor(0, Math.Min(_bottom, CursorY + n)); break;
            case 'F': MoveCursor(0, Math.Max(_top, CursorY - n)); break;
            case 'G':
            case '`': MoveCursor(Math.Clamp(n - 1, 0, Columns - 1), CursorY); break;
            case 'H':
            case 'f': SetCursorPosition(Arg(p, 0, 1), Arg(p, 1, 1)); break;
            case 'd': SetCursorPosition(n, CursorX + 1); break;
            case 'I': for (int i = 0; i < n; i++) Ground('\t'); break;
            case 'Z': MoveCursor(Math.Max(0, ((CursorX - 1) / 8 - (n - 1)) * 8), CursorY); break;
            case 'J': EraseDisplay(p.Length > 0 ? p[0] : 0); break;
            case 'K': EraseLine(p.Length > 0 ? p[0] : 0); break;
            case 'L': InsertLines(n); break;
            case 'M': DeleteLines(n); break;
            case 'P': DeleteChars(n); break;
            case 'X': EraseChars(n); break;
            case 'S': ScrollUp(n); break;
            case 'T': ScrollDown(n); break;
            case 'b': for (int i = 0; i < Math.Min(n, Columns * Rows); i++) Print(_lastPrinted); break;
            case 'm': SelectGraphicRendition(); break;
            case 'r': SetScrollRegion(Arg(p, 0, 1), Arg(p, 1, Rows)); break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 'h': if (p.Contains(4)) _insertMode = true; break;
            case 'l': if (p.Contains(4)) _insertMode = false; break;
            case 'n':
                if (n == 5) Reply?.Invoke("\x1b[0n");
                else if (n == 6) Reply?.Invoke($"\x1b[{CursorY + 1 - (_originMode ? _top : 0)};{CursorX + 1}R");
                break;
            case 'c': Reply?.Invoke("\x1b[?1;2c"); break;
        }
    }

    private void SetPrivateModes(int[] modes, bool on)
    {
        foreach (int mode in modes)
        {
            switch (mode)
            {
                case 1: ApplicationCursorKeys = on; break;
                case 6:
                    _originMode = on;
                    MoveCursor(0, on ? _top : 0);
                    break;
                case 7: _autoWrap = on; break;
                case 25: CursorVisible = on; break;
                case 2004: BracketedPaste = on; break;
                case 47:
                case 1047:
                    if (on) EnterAlternateScreen(clear: mode == 1047);
                    else LeaveAlternateScreen();
                    break;
                case 1049:
                    if (on)
                    {
                        _savedMain = Snapshot();
                        EnterAlternateScreen(clear: true);
                    }
                    else
                    {
                        LeaveAlternateScreen();
                        Restore(_savedMain);
                    }
                    break;
            }
        }
    }

    private void SelectGraphicRendition()
    {
        var groups = _params.Length == 0 ? new[] { "0" } : _params.ToString().Split(';');
        for (int i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            if (group.Contains(':'))
            {
                // Colon form, e.g. 38:2::r:g:b or 38:5:n or 4:3 (curly underline)
                var sub = group.Split(':');
                int head = int.TryParse(sub[0], out int h) ? h : 0;
                if ((head == 38 || head == 48 || head == 58) && sub.Length >= 3)
                {
                    int color = TermColor.Default;
                    if (sub[1] == "5" && int.TryParse(sub[2], out int index)) color = Math.Clamp(index, 0, 255);
                    else if (sub[1] == "2" && sub.Length >= 5)
                    {
                        int.TryParse(sub[^3], out int r);
                        int.TryParse(sub[^2], out int g);
                        int.TryParse(sub[^1], out int b);
                        color = TermColor.Rgb(r & 255, g & 255, b & 255);
                    }
                    if (head == 38) _fg = color;
                    else if (head == 48) _bg = color;
                }
                else if (head == 4)
                {
                    if (sub.Length > 1 && sub[1] == "0") _style &= ~CellStyle.Underline;
                    else _style |= CellStyle.Underline;
                }
                continue;
            }

            int code = int.TryParse(group, out int v) ? v : 0;
            switch (code)
            {
                case 0:
                    _fg = _bg = TermColor.Default;
                    _style = CellStyle.None;
                    break;
                case 1: _style |= CellStyle.Bold; break;
                case 2: _style |= CellStyle.Dim; break;
                case 3: _style |= CellStyle.Italic; break;
                case 4:
                case 21: _style |= CellStyle.Underline; break;
                case 7: _style |= CellStyle.Inverse; break;
                case 8: _style |= CellStyle.Hidden; break;
                case 9: _style |= CellStyle.Strike; break;
                case 22: _style &= ~(CellStyle.Bold | CellStyle.Dim); break;
                case 23: _style &= ~CellStyle.Italic; break;
                case 24: _style &= ~CellStyle.Underline; break;
                case 27: _style &= ~CellStyle.Inverse; break;
                case 28: _style &= ~CellStyle.Hidden; break;
                case 29: _style &= ~CellStyle.Strike; break;
                case >= 30 and <= 37: _fg = code - 30; break;
                case 39: _fg = TermColor.Default; break;
                case >= 40 and <= 47: _bg = code - 40; break;
                case 49: _bg = TermColor.Default; break;
                case >= 90 and <= 97: _fg = code - 90 + 8; break;
                case >= 100 and <= 107: _bg = code - 100 + 8; break;
                case 38:
                case 48:
                case 58:
                    int color = TermColor.Default;
                    if (i + 2 < groups.Length && groups[i + 1] == "5")
                    {
                        color = int.TryParse(groups[i + 2], out int index) ? Math.Clamp(index, 0, 255) : TermColor.Default;
                        i += 2;
                    }
                    else if (i + 4 < groups.Length && groups[i + 1] == "2")
                    {
                        int.TryParse(groups[i + 2], out int r);
                        int.TryParse(groups[i + 3], out int g);
                        int.TryParse(groups[i + 4], out int b);
                        color = TermColor.Rgb(r & 255, g & 255, b & 255);
                        i += 4;
                    }
                    if (code == 38) _fg = color;
                    else if (code == 48) _bg = color;
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ cursor & screen operations

    private void MoveCursor(int x, int y)
    {
        CursorX = Math.Clamp(x, 0, Columns - 1);
        CursorY = Math.Clamp(y, 0, Rows - 1);
        _pendingWrap = false;
    }

    private void SetCursorPosition(int row, int column)
    {
        int y = row - 1 + (_originMode ? _top : 0);
        MoveCursor(column - 1, _originMode ? Math.Clamp(y, _top, _bottom) : y);
    }

    private void LineFeed()
    {
        _pendingWrap = false;
        if (CursorY == _bottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ReverseIndex()
    {
        _pendingWrap = false;
        if (CursorY == _top) ScrollDown(1);
        else if (CursorY > 0) CursorY--;
    }

    private void ScrollUp(int count)
    {
        count = Math.Clamp(count, 1, _bottom - _top + 1);
        for (int i = 0; i < count; i++)
        {
            var removed = _screen[_top];
            Array.Copy(_screen, _top + 1, _screen, _top, _bottom - _top);
            _screen[_bottom] = new TermLine(Columns, _bg);
            if (_screen == _main && _top == 0) PushScrollback(removed);
        }
    }

    private void ScrollDown(int count)
    {
        count = Math.Clamp(count, 1, _bottom - _top + 1);
        for (int i = 0; i < count; i++)
        {
            Array.Copy(_screen, _top, _screen, _top + 1, _bottom - _top);
            _screen[_top] = new TermLine(Columns, _bg);
        }
    }

    private void PushScrollback(TermLine line)
    {
        Scrollback.Add(line);
        ScrollbackPushed++;
        if (Scrollback.Count > MaxScrollback + 500) Scrollback.RemoveRange(0, 500);
    }

    private void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top - 1, 0, Rows - 1);
        bottom = Math.Clamp(bottom - 1, 0, Rows - 1);
        if (top >= bottom) return;
        _top = top;
        _bottom = bottom;
        MoveCursor(0, _originMode ? _top : 0);
    }

    private void EraseCells(TermLine line, int from, int to)
    {
        from = Math.Max(0, from);
        to = Math.Min(Columns, to);
        for (int x = from; x < to; x++) line.Cells[x] = TermCell.Blank(_bg);
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseCells(_screen[CursorY], CursorX, Columns);
                for (int y = CursorY + 1; y < Rows; y++) _screen[y] = new TermLine(Columns, _bg);
                break;
            case 1:
                for (int y = 0; y < CursorY; y++) _screen[y] = new TermLine(Columns, _bg);
                EraseCells(_screen[CursorY], 0, CursorX + 1);
                break;
            case 2:
            case 3:
                for (int y = 0; y < Rows; y++) _screen[y] = new TermLine(Columns, _bg);
                if (mode == 3 && _screen == _main) Scrollback.Clear();
                break;
        }
    }

    private void EraseLine(int mode)
    {
        var line = _screen[CursorY];
        switch (mode)
        {
            case 0: EraseCells(line, CursorX, Columns); break;
            case 1: EraseCells(line, 0, CursorX + 1); break;
            case 2: EraseCells(line, 0, Columns); break;
        }
        line.Wrapped = false;
    }

    private void InsertChars(int count)
    {
        var cells = _screen[CursorY].Cells;
        count = Math.Min(count, Columns - CursorX);
        Array.Copy(cells, CursorX, cells, CursorX + count, Columns - CursorX - count);
        for (int x = CursorX; x < CursorX + count; x++) cells[x] = TermCell.Blank(_bg);
    }

    private void DeleteChars(int count)
    {
        var cells = _screen[CursorY].Cells;
        count = Math.Min(count, Columns - CursorX);
        Array.Copy(cells, CursorX + count, cells, CursorX, Columns - CursorX - count);
        for (int x = Columns - count; x < Columns; x++) cells[x] = TermCell.Blank(_bg);
    }

    private void EraseChars(int count) => EraseCells(_screen[CursorY], CursorX, CursorX + count);

    private void InsertLines(int count)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        count = Math.Min(count, _bottom - CursorY + 1);
        for (int i = 0; i < count; i++)
        {
            Array.Copy(_screen, CursorY, _screen, CursorY + 1, _bottom - CursorY);
            _screen[CursorY] = new TermLine(Columns, _bg);
        }
        CursorX = 0;
    }

    private void DeleteLines(int count)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        count = Math.Min(count, _bottom - CursorY + 1);
        for (int i = 0; i < count; i++)
        {
            Array.Copy(_screen, CursorY + 1, _screen, CursorY, _bottom - CursorY);
            _screen[_bottom] = new TermLine(Columns, _bg);
        }
        CursorX = 0;
    }

    private SavedCursor Snapshot() => new()
    {
        X = CursorX, Y = CursorY, Fg = _fg, Bg = _bg, Style = _style,
        LineDrawing = _lineDrawing, OriginMode = _originMode,
    };

    private void Restore(SavedCursor saved)
    {
        _fg = saved.Fg;
        _bg = saved.Bg;
        _style = saved.Style;
        _lineDrawing = saved.LineDrawing;
        _originMode = saved.OriginMode;
        MoveCursor(saved.X, saved.Y);
    }

    private void SaveCursor()
    {
        if (IsAlternateScreen) _savedAlt = Snapshot();
        else _savedMain = Snapshot();
    }

    private void RestoreCursor() => Restore(IsAlternateScreen ? _savedAlt : _savedMain);

    private void EnterAlternateScreen(bool clear)
    {
        if (IsAlternateScreen) return;
        if (clear) _alt = NewScreen();
        _screen = _alt;
        _top = 0;
        _bottom = Rows - 1;
    }

    private void LeaveAlternateScreen()
    {
        if (!IsAlternateScreen) return;
        _screen = _main;
        _top = 0;
        _bottom = Rows - 1;
    }

    private void SoftReset()
    {
        _fg = _bg = TermColor.Default;
        _style = CellStyle.None;
        _top = 0;
        _bottom = Rows - 1;
        _originMode = _insertMode = _lineDrawing = false;
        _autoWrap = true;
        CursorVisible = true;
        ApplicationCursorKeys = false;
    }

    private void FullReset()
    {
        SoftReset();
        BracketedPaste = false;
        _main = NewScreen();
        _alt = NewScreen();
        _screen = _main;
        Scrollback.Clear();
        MoveCursor(0, 0);
    }

    /// <summary>Clears the screen and history, keeping the current line (the prompt) at the top.</summary>
    public void Clear()
    {
        var current = _screen[CursorY];
        Scrollback.Clear();
        for (int y = 0; y < Rows; y++) _screen[y] = new TermLine(Columns);
        _screen[0] = current;
        CursorY = 0;
        _pendingWrap = false;
    }

    // ------------------------------------------------------------------ resize & text

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(2, rows);
        if (columns == Columns && rows == Rows) return;

        // Keep the cursor line on screen: lines that no longer fit move into history.
        int shift = Math.Max(0, CursorY + 1 - rows);
        _main = ResizeScreen(_main, columns, rows, shift, toScrollback: true);
        _alt = ResizeScreen(_alt, columns, rows, IsAlternateScreen ? shift : 0, toScrollback: false);
        _screen = IsAlternateScreen ? _alt : _main;

        Columns = columns;
        Rows = rows;
        _top = 0;
        _bottom = rows - 1;
        MoveCursor(CursorX, CursorY - shift);
        _savedMain.X = Math.Min(_savedMain.X, columns - 1);
        _savedMain.Y = Math.Min(_savedMain.Y, rows - 1);
        _savedAlt.X = Math.Min(_savedAlt.X, columns - 1);
        _savedAlt.Y = Math.Min(_savedAlt.Y, rows - 1);
    }

    private TermLine[] ResizeScreen(TermLine[] screen, int columns, int rows, int shift, bool toScrollback)
    {
        if (toScrollback && !IsAlternateScreen)
        {
            for (int i = 0; i < shift; i++) PushScrollback(screen[i]);
        }
        var result = new TermLine[rows];
        for (int y = 0; y < rows; y++)
        {
            int source = y + (toScrollback && !IsAlternateScreen ? shift : (toScrollback ? 0 : shift));
            var line = source < screen.Length ? screen[source] : new TermLine(columns);
            line.Resize(columns);
            result[y] = line;
        }
        return result;
    }

    /// <summary>Plain text between two absolute positions (inclusive), joining soft-wrapped lines.</summary>
    public string GetText(int startLine, int startColumn, int endLine, int endColumn)
    {
        var text = new StringBuilder();
        for (int line = startLine; line <= endLine; line++)
        {
            var cells = LineAt(line).Cells;
            int from = line == startLine ? startColumn : 0;
            int to = line == endLine ? Math.Min(endColumn, cells.Length - 1) : cells.Length - 1;
            var row = new StringBuilder();
            for (int x = from; x <= to && x < cells.Length; x++) row.Append(cells[x].Ch == '\0' ? ' ' : cells[x].Ch);
            bool wrapped = LineAt(line).Wrapped && line != endLine;
            text.Append(wrapped ? row.ToString() : row.ToString().TrimEnd());
            if (line != endLine && !wrapped) text.Append(Environment.NewLine);
        }
        return text.ToString();
    }
}
