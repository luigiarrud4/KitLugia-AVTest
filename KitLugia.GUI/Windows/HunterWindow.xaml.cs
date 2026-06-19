using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;
using KitLugia.Core;

namespace KitLugia.GUI.Windows
{
    public partial class HunterWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(int x, int y);
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPhysicalPoint(int x, int y);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);
        [DllImport("gdi32.dll")]
        private static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);
        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private const uint GA_ROOT = 2;
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int GW_HWNDPREV = 3;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_CAPTION = 0x00C00000;
        private const uint WS_SYSMENU = 0x00080000;
        private const uint WS_THICKFRAME = 0x00040000;
        private const uint WS_MINIMIZEBOX = 0x00020000;
        private const uint WS_MAXIMIZEBOX = 0x00010000;
        private const uint WS_OVERLAPPEDWINDOW = WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
        private const uint WS_CHILD = 0x40000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_DISABLED = 0x08000000;
        private const uint WS_CLIPSIBLINGS = 0x04000000;
        private const uint WS_BORDER = 0x00800000;
        private const uint WS_DLGFRAME = 0x00400000;
        private const uint WS_VSCROLL = 0x00200000;
        private const uint WS_HSCROLL = 0x00100000;
        private const uint WS_TABSTOP = 0x00010000;
        private const uint WS_GROUP = 0x00020000;
        private const uint WS_SIZEBOX = 0x00040000;

        private const int WS_EX_DLGMODALFRAME = 0x00000001;
        private const int WS_EX_NOPARENTNOTIFY = 0x00000004;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_ACCEPTFILES = 0x00000010;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_MDICHILD = 0x00000040;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_WINDOWEDGE = 0x00000100;
        private const int WS_EX_CLIENTEDGE = 0x00000200;
        private const int WS_EX_OVERLAPPEDWINDOW = WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE;
        private const int WS_EX_PALETTEWINDOW = WS_EX_WINDOWEDGE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const int RGN_DIFF = 4;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        private static bool _isOpen;

        private bool _isHunting;
        private IntPtr _currentHwnd;
        private int _selfPid;
        private DispatcherTimer? _huntTimer;
        private DispatcherTimer? _autoTimer;
        private System.Windows.Forms.Form? _overlayForm;
        private System.Windows.Forms.Form? _crosshairForm;
        private System.Windows.Forms.Form? _infoForm;
        private System.Windows.Forms.Form? _contextForm;

        public HunterWindow()
        {
            if (_isOpen) { Close(); return; }
            _isOpen = true;
            InitializeComponent();
            _selfPid = Process.GetCurrentProcess().Id;
        }

        private void DragBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                DragMove();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _isOpen = false;
            _huntTimer?.Stop();
            _autoTimer?.Stop();
            DestroyCrosshair();
            DestroyInfo();
            DestroyContextMenu();
            DestroyOverlay();
            base.OnClosing(e);
        }

        private void BtnClose_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                e.Handled = true;
                Close();
            }
        }

        private void Finder_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isHunting) return;
            _isHunting = true;
            _currentHwnd = IntPtr.Zero;
            FinderButton.Background = System.Windows.Media.Brushes.Orange;
            TxtStatus.Text = "Caçando...";
            TxtStatus.Foreground = System.Windows.Media.Brushes.Orange;

            GetCursorPos(out var p);
            ShowCrosshair(p.X, p.Y);

            _huntTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _huntTimer.Tick += HuntTimer_Tick;
            _huntTimer.Start();
        }

        private void HuntTimer_Tick(object? sender, EventArgs e)
        {
            GetCursorPos(out var pos);

            ShowCrosshair(pos.X, pos.Y);

            var hWnd = WindowFromPointEx(pos.X, pos.Y);
            string name = "", path = "";
            if (hWnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != 0 && pid != _selfPid)
                {
                    if (hWnd != _currentHwnd)
                    {
                        _currentHwnd = hWnd;
                        UpdateInfo(hWnd);
                    }
                    try { var p = Process.GetProcessById((int)pid); name = p.ProcessName; try { path = p.MainModule?.FileName ?? ""; } catch { } } catch { }
                }
            }
            ShowInfo(pos.X, pos.Y, name, path);

            if ((GetAsyncKeyState(0x02) & 0x8000) != 0)
                StopHunting(showMenu: false);
            else if ((GetAsyncKeyState(0x01) & 0x8000) == 0)
                StopHunting(showMenu: true);
        }

        private void StopHunting(bool showMenu)
        {
            _huntTimer?.Stop();
            _huntTimer = null;
            _isHunting = false;
            DestroyCrosshair();
            DestroyInfo();

            if (showMenu && _currentHwnd != IntPtr.Zero)
            {
                CaptureCurrentInfo();
                GetCursorPos(out var p);
                ShowContextMenu(p.X, p.Y);
                return;
            }

            DestroyOverlay();
            FinderButton.Background = System.Windows.Media.Brushes.DodgerBlue;
            TxtStatus.Text = _currentHwnd != IntPtr.Zero ? "OK" : "Nenhuma janela";
            TxtStatus.Foreground = _currentHwnd != IntPtr.Zero
                ? System.Windows.Media.Brushes.Gold
                : System.Windows.Media.Brushes.Red;
            EnableActions(_currentHwnd != IntPtr.Zero);
        }

        private void EnableActions(bool enabled)
        {
            BtnUninstall.IsEnabled = enabled;
            BtnKill.IsEnabled = enabled;
            BtnKillDel.IsEnabled = enabled;
            BtnOpenFolder.IsEnabled = enabled;
            BtnProperties.IsEnabled = enabled;
        }

        private void UpdateOverlay(RECT r)
        {
            DestroyOverlay();

            int w = r.Right - r.Left + 6;
            int h = r.Bottom - r.Top + 6;
            if (w <= 0 || h <= 0) return;

            _overlayForm = new System.Windows.Forms.Form
            {
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                ShowInTaskbar = false,
                TopMost = true,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point(r.Left - 3, r.Top - 3),
                Size = new System.Drawing.Size(w, h),
                BackColor = System.Drawing.Color.Gold,
                Opacity = 0.78,
            };

            _overlayForm.Load += (_, _) =>
            {
                IntPtr hwnd = _overlayForm!.Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT);

                IntPtr outerRgn = CreateRectRgn(0, 0, w, h);
                IntPtr innerRgn = CreateRectRgn(3, 3, w - 6, h - 6);
                CombineRgn(outerRgn, outerRgn, innerRgn, RGN_DIFF);
                DeleteObject(innerRgn);
                SetWindowRgn(hwnd, outerRgn, true);
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            };

            _overlayForm.Show();
        }

        private void DestroyOverlay()
        {
            if (_overlayForm == null) return;
            _overlayForm.Close();
            _overlayForm.Dispose();
            _overlayForm = null;
        }

        private void ShowCrosshair(int x, int y)
        {
            if (_crosshairForm != null)
            {
                _crosshairForm.Location = new System.Drawing.Point(x - 20, y - 20);
                return;
            }

            _crosshairForm = new System.Windows.Forms.Form
            {
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                ShowInTaskbar = false,
                TopMost = true,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point(x - 20, y - 20),
                Size = new System.Drawing.Size(40, 40),
                BackColor = System.Drawing.Color.Fuchsia,
                TransparencyKey = System.Drawing.Color.Fuchsia,
            };

            _crosshairForm.Paint += (_, e) =>
            {
                using var p = new System.Drawing.Pen(System.Drawing.Color.DodgerBlue, 2f);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int cx = 20, cy = 20;
                e.Graphics.DrawLine(p, cx, cy - 12, cx, cy - 3);
                e.Graphics.DrawLine(p, cx, cy + 3, cx, cy + 12);
                e.Graphics.DrawLine(p, cx - 12, cy, cx - 3, cy);
                e.Graphics.DrawLine(p, cx + 3, cy, cx + 12, cy);
                e.Graphics.DrawEllipse(p, cx - 3, cy - 3, 6, 6);
            };

            _crosshairForm.Load += (_, _) =>
            {
                IntPtr hwnd = _crosshairForm!.Handle;
                SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT);
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            };

            _crosshairForm.Show();
        }

        private void DestroyCrosshair()
        {
            if (_crosshairForm == null) return;
            _crosshairForm.Close();
            _crosshairForm.Dispose();
            _crosshairForm = null;
        }

        private static readonly System.Drawing.Font _infoFont = new("Segoe UI", 10f);
        private string _infoText = "";
        private int _arrowDir;
        private int _arrowCx;
        private int _arrowCy;
        private bool _infoNeedsSetup = true;

        private void ShowInfo(int x, int y, string name, string path)
        {
            if (string.IsNullOrEmpty(name))
            {
                DestroyInfo();
                return;
            }

            string text = $"{name}\n{TruncatePath(path)}";

            if (_infoForm == null)
            {
                _infoForm = new System.Windows.Forms.Form
                {
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                    ShowInTaskbar = false,
                    TopMost = true,
                    StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    BackColor = System.Drawing.Color.FromArgb(0x1A, 0x1A, 0x1A),
                };

                _infoForm.Paint += InfoForm_Paint;

                _infoForm.Load += (_, _) =>
                {
                    IntPtr hwnd = _infoForm!.Handle;
                    SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT);
                    SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                };

                _infoNeedsSetup = true;
            }

            _infoText = text;

            const int arrowSize = 12;
            const int pad = 10;

            // measure natural width
            using var measureG = Graphics.FromHwnd(_infoForm!.Handle);
            var lines = text.Split('\n');
            float natW = 0;
            foreach (var ln in lines)
            {
                var sz = measureG.MeasureString(ln, _infoFont, int.MaxValue);
                natW = Math.Max(natW, sz.Width);
            }
            int formW = Math.Clamp((int)Math.Ceiling(natW) + pad * 2, 180, 500);

            // measure height at chosen width
            float totalH = pad * 2f;
            foreach (var ln in lines)
            {
                var sz = measureG.MeasureString(ln, _infoFont, formW - pad * 2);
                totalH += sz.Height;
            }
            int formH = Math.Max((int)Math.Ceiling(totalH) + arrowSize + 2, 40);

            var scr = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;

            _arrowDir = 0;
            int locX = x - formW / 2;
            int locY = y + 24;

            if (locY + formH > scr.Bottom)
            {
                _arrowDir = 1;
                locY = y - formH - 10;
                if (locY < scr.Top)
                {
                    _arrowDir = 2;
                    locX = x + 24;
                    locY = y - formH / 2;
                    if (locX + formW > scr.Right)
                    {
                        _arrowDir = 3;
                        locX = x - formW - 24;
                    }
                }
            }

            locX = Math.Max(scr.Left + 4, Math.Min(locX, scr.Right - formW - 4));
            locY = Math.Max(scr.Top + 4, Math.Min(locY, scr.Bottom - formH - 4));

            int arrowMargin = arrowSize + 4;
            _arrowCx = Math.Clamp(x - locX, arrowMargin, formW - arrowMargin);
            _arrowCy = Math.Clamp(y - locY, arrowMargin, formH - arrowMargin);

            if (_infoNeedsSetup)
            {
                _infoForm.Size = new System.Drawing.Size(formW, formH);
                _infoForm.Location = new System.Drawing.Point(locX, locY);
                _infoForm.Show();
                _infoNeedsSetup = false;
            }
            else
            {
                if (_infoForm.Size.Width != formW || _infoForm.Size.Height != formH)
                    _infoForm.Size = new System.Drawing.Size(formW, formH);
                _infoForm.Location = new System.Drawing.Point(locX, locY);
                _infoForm.Invalidate();
            }
        }

        private void InfoForm_Paint(object? sender, System.Windows.Forms.PaintEventArgs e)
        {
            var form = (System.Windows.Forms.Form)sender!;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var bgBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x28, 0x28, 0x2A));
            var borderPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x55, 0x55, 0x58), 1);
            var accentPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x80, 0xBF, 0x94, 0x00), 1);
            var textBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xF0, 0xF0, 0xF0));
            var dimBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xBB, 0xBB, 0xBB));

            int fw = form.Width, fh = form.Height;
            int arrowPad = 12, r = 5;

            var path = new System.Drawing.Drawing2D.GraphicsPath();

            // main body with rounded corners
            int rt2 = _arrowDir == 0 ? arrowPad : 0;
            int rb2 = _arrowDir == 1 ? arrowPad : 0;
            int rl2 = _arrowDir == 2 ? arrowPad : 0;
            int rr2 = _arrowDir == 3 ? arrowPad : 0;
            int rx = rl2, ry = rt2, rw = fw - rl2 - rr2, rh = fh - rt2 - rb2;
            path.AddArc(rx, ry, r * 2, r * 2, 180, 90);
            path.AddArc(rx + rw - r * 2, ry, r * 2, r * 2, 270, 90);
            path.AddArc(rx + rw - r * 2, ry + rh - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(rx, ry + rh - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();

            // arrow
            int arr = 8;
            System.Drawing.PointF[] pts;
            switch (_arrowDir)
            {
                case 0:
                    pts = [new(_arrowCx, 0), new(_arrowCx - arr, arrowPad), new(_arrowCx + arr, arrowPad)];
                    path.AddPolygon(pts);
                    break;
                case 1:
                    pts = [new(_arrowCx, fh - 1), new(_arrowCx - arr, fh - 1 - arrowPad), new(_arrowCx + arr, fh - 1 - arrowPad)];
                    path.AddPolygon(pts);
                    break;
                case 2:
                    pts = [new(0, _arrowCy), new(arrowPad, _arrowCy - arr), new(arrowPad, _arrowCy + arr)];
                    path.AddPolygon(pts);
                    break;
                case 3:
                    pts = [new(fw - 1, _arrowCy), new(fw - 1 - arrowPad, _arrowCy - arr), new(fw - 1 - arrowPad, _arrowCy + arr)];
                    path.AddPolygon(pts);
                    break;
            }

            using var bgRegion = new System.Drawing.Region(path);
            g.FillRegion(bgBrush, bgRegion);
            g.DrawPath(borderPen, path);

            // gold accent line on top edge of body
            int accentY = rt2 + 2;
            g.DrawLine(accentPen, rl2 + r + 2, accentY, fw - rr2 - r - 2, accentY);

            // text
            int tx = 14, ty2 = 10 + (_arrowDir == 0 ? arrowPad : 0);
            float yOff = ty2;
            var lines = _infoText.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var brush = i == 0 ? textBrush : dimBrush;
                g.DrawString(lines[i], _infoFont, brush, tx, yOff);
                yOff += g.MeasureString(lines[i], _infoFont, fw - 28).Height;
            }

            bgBrush.Dispose();
            borderPen.Dispose();
            accentPen.Dispose();
            textBrush.Dispose();
            dimBrush.Dispose();
        }

        private void DestroyInfo()
        {
            if (_infoForm == null) return;
            _infoForm.Close();
            _infoForm.Dispose();
            _infoForm = null;
        }

        private static string TruncatePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= 60) return path ?? "";
            string left = path[..25];
            string right = path[^30..];
            return $"{left}...{right}";
        }

        private void ShowContextMenu(int x, int y)
        {
            if (_contextForm != null) DestroyContextMenu();

            var sb = new StringBuilder(256);
            GetWindowText(_currentHwnd, sb, 256);
            string title = sb.Length > 0 ? sb.ToString() : $"0x{_currentHwnd.ToInt64():X8}";
            string sub = _detectedName.Length > 0 ? _detectedName : TruncatePath(_detectedPath);

            const int bw = 220;
            float avgW = 5f;
            int maxCW = (int)((bw - 20) / avgW);
            int titleLines = Math.Max(1, (int)Math.Ceiling((float)title.Length / maxCW));
            int subLines = sub.Length > 0 ? Math.Max(1, (int)Math.Ceiling((float)sub.Length / maxCW)) : 0;
            int titleH = titleLines * 15 + 4;
            int subH = subLines * 14;
            int labelArea = 8 + titleH + 2 + subH + 2;
            int btnTop = Math.Max(labelArea + 2, 36);
            int bh = btnTop + 4 + 4 * 36 + 4 + 24 + 10;

            var scr = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
            int lx = Math.Max(scr.Left + 4, Math.Min(x - bw / 2, scr.Right - bw - 4));
            int ly = Math.Max(scr.Top + 4, Math.Min(y - bh / 2, scr.Bottom - bh - 4));

            _contextForm = new System.Windows.Forms.Form
            {
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                ShowInTaskbar = false,
                TopMost = true,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Size = new System.Drawing.Size(bw, bh),
                Location = new System.Drawing.Point(lx, ly),
                BackColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E),
                Opacity = 0.85,
            };

            _contextForm.Deactivate += (_, _) => DestroyContextMenu();

            _contextForm.Paint += (_, e) =>
            {
                using var p = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x44, 0x44, 0x44));
                e.Graphics.DrawRectangle(p, 0, 0, bw - 1, bh - 1);
            };

            // title
            var lbl = new System.Windows.Forms.Label
            {
                Text = title,
                ForeColor = System.Drawing.Color.FromArgb(0xFF, 0xD7, 0x00),
                BackColor = System.Drawing.Color.Transparent,
                Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold),
                Location = new System.Drawing.Point(10, 8),
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(bw - 20, 0),
            };
            _contextForm.Controls.Add(lbl);

            // subtitle (process name / path)
            int subY = 8 + titleH + 2;
            if (sub.Length > 0)
            {
                var subLbl = new System.Windows.Forms.Label
                {
                    Text = sub,
                    ForeColor = System.Drawing.Color.FromArgb(0xAA, 0xAA, 0xAA),
                    BackColor = System.Drawing.Color.Transparent,
                    Font = new System.Drawing.Font("Segoe UI", 8f),
                    Location = new System.Drawing.Point(10, subY),
                    AutoSize = true,
                    MaximumSize = new System.Drawing.Size(bw - 20, 0),
                };
                _contextForm.Controls.Add(subLbl);
            }

            // separator line
            int sepY = btnTop - 6;
            var sep = new System.Windows.Forms.Label
            {
                BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
                BackColor = System.Drawing.Color.FromArgb(0x44, 0x44, 0x44),
                Location = new System.Drawing.Point(10, sepY),
                Size = new System.Drawing.Size(bw - 20, 1),
            };
            _contextForm.Controls.Add(sep);

            // buttons
            string[] btnTexts = ["Desinstalar", "Matar", "Matar + Deletar", "Abrir Pasta"];
            Color[] btnColors = [System.Drawing.Color.FromArgb(0xC4, 0x2B, 0x1C), System.Drawing.Color.FromArgb(0x33, 0x33, 0x33), System.Drawing.Color.FromArgb(0x33, 0x33, 0x33), System.Drawing.Color.FromArgb(0x33, 0x33, 0x33)];
            Action[] actions = [() => BtnUninstall_Click(null, null!), () => BtnKill_Click(null, null!), () => BtnKillDel_Click(null, null!), () => BtnOpenFolder_Click(null, null!)];

            for (int i = 0; i < btnTexts.Length; i++)
            {
                int idx = i;
                var btn = new System.Windows.Forms.Button
                {
                    Text = btnTexts[i],
                    FlatStyle = System.Windows.Forms.FlatStyle.Flat,
                    FlatAppearance = { BorderSize = 0 },
                    BackColor = btnColors[i],
                    ForeColor = System.Drawing.Color.White,
                    Font = new System.Drawing.Font("Segoe UI", 9f),
                    Location = new System.Drawing.Point(10, btnTop + 4 + i * 36),
                    Size = new System.Drawing.Size(bw - 20, 30),
                    Cursor = System.Windows.Forms.Cursors.Hand,
                };
                btn.Click += (_, _) =>
                {
                    DestroyContextMenu();
                    DestroyOverlay();
                    actions[idx]();
                };
                _contextForm.Controls.Add(btn);
            }

            // cancel
            var cancelBtn = new System.Windows.Forms.Button
            {
                Text = "Cancelar",
                FlatStyle = System.Windows.Forms.FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0 },
                BackColor = System.Drawing.Color.FromArgb(0x22, 0x22, 0x22),
                ForeColor = System.Drawing.Color.FromArgb(0x88, 0x88, 0x88),
                Font = new System.Drawing.Font("Segoe UI", 9f),
                Location = new System.Drawing.Point(10, btnTop + 4 + btnTexts.Length * 36 + 4),
                Size = new System.Drawing.Size(bw - 20, 24),
                Cursor = System.Windows.Forms.Cursors.Hand,
            };
            cancelBtn.Click += (_, _) => DestroyContextMenu();
            _contextForm.Controls.Add(cancelBtn);

            _contextForm.MouseDown += (_, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Right)
                    DestroyContextMenu();
            };

            _contextForm.Show();
        }

        private void DestroyContextMenu()
        {
            var f = _contextForm;
            if (f == null) return;
            _contextForm = null;
            f.Close();
            f.Dispose();
            DestroyOverlay();
            FinderButton.Background = System.Windows.Media.Brushes.DodgerBlue;
            TxtStatus.Text = _currentHwnd != IntPtr.Zero ? "OK" : "Nenhuma janela";
            TxtStatus.Foreground = _currentHwnd != IntPtr.Zero
                ? System.Windows.Media.Brushes.Gold
                : System.Windows.Media.Brushes.Red;
            EnableActions(_currentHwnd != IntPtr.Zero);
        }

        private IntPtr WindowFromPointEx(int x, int y)
        {
            IntPtr raw = WindowFromPoint(x, y);
            if (raw == IntPtr.Zero || GetWindowThreadProcessId(raw, out _) == _selfPid)
                raw = WindowFromPhysicalPoint(x, y);
            if (raw == IntPtr.Zero || GetWindowThreadProcessId(raw, out _) == _selfPid)
                return IntPtr.Zero;

            IntPtr root = GetAncestor(raw, GA_ROOT);
            if (root == IntPtr.Zero) root = raw;

            IntPtr bestTop = root;
            IntPtr w = root;
            while ((w = GetWindow(w, GW_HWNDPREV)) != IntPtr.Zero)
            {
                if (!IsWindowVisible(w)) continue;
                GetWindowRect(w, out var r);
                if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom)
                {
                    GetWindowThreadProcessId(w, out uint wp);
                    if (wp == _selfPid) continue;
                    bestTop = w;
                }
            }

            IntPtr parent = GetParent(bestTop);
            int style = GetWindowLong(bestTop, GWL_STYLE);
            if (parent == IntPtr.Zero || (style & WS_POPUP) != 0) parent = bestTop;

            IntPtr bestChild = IntPtr.Zero;
            uint bestArea = uint.MaxValue;

            EnumChildWindows(parent, (hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (GetParent(hwnd) != parent) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == _selfPid) return true;
                GetWindowRect(hwnd, out var r);
                if (x < r.Left || x >= r.Right || y < r.Top || y >= r.Bottom) return true;
                uint area = (uint)((r.Right - r.Left) * (r.Bottom - r.Top));
                if (area < bestArea) { bestArea = area; bestChild = hwnd; }
                return true;
            }, IntPtr.Zero);

            if (bestChild == IntPtr.Zero) bestChild = parent;

            while (bestChild != IntPtr.Zero && !IsWindowVisible(bestChild))
                bestChild = GetParent(bestChild);

            return bestChild;
        }

        private void UpdateInfo(IntPtr hWnd)
        {
            var title = new StringBuilder(1024);
            GetWindowText(hWnd, title, 1024);
            var cls = new StringBuilder(512);
            GetClassName(hWnd, cls, 512);
            GetWindowThreadProcessId(hWnd, out uint pid);
            GetWindowRect(hWnd, out var r);
            GetClientRect(hWnd, out var cr);
            IntPtr parent = GetParent(hWnd);

            TxtHwnd.Text = $"0x{hWnd.ToInt64():X8}";
            TxtPid.Text = pid.ToString();
            TxtTitle.Text = title.ToString();
            TxtClass.Text = cls.ToString();
            TxtRect.Text = $"({r.Left},{r.Top})-({r.Right},{r.Bottom})  {r.Right - r.Left}x{r.Bottom - r.Top}  Client: {cr.Right}x{cr.Bottom}";

            string procName = "";
            string procPath = "";
            try
            {
                var proc = Process.GetProcessById((int)pid);
                procName = proc.ProcessName;
                try { procPath = proc.MainModule?.FileName ?? ""; } catch { procPath = "(sem acesso)"; }
            }
            catch { procName = $"(PID {pid} morreu ou sem acesso)"; }

            TxtProcess.Text = $"{procName}  (PID: {pid})";
            TxtPath.Text = procPath;

            TxtVisible.Text = IsWindowVisible(hWnd) ? "Sim" : "Não";
            TxtParent.Text = parent != IntPtr.Zero ? $"0x{parent.ToInt64():X8}" : "0";

            int stylesVal = GetWindowLong(hWnd, GWL_STYLE);
            TxtStyles.Text = FormatWindowStyles(stylesVal);
            TxtExStyles.Text = FormatExWindowStyles(GetWindowLong(hWnd, GWL_EXSTYLE));

            UpdateOverlay(r);

            ListChildren.Items.Clear();
            EnumChildWindows(hWnd, (child, _) =>
            {
                if (!IsWindow(child)) return true;
                var ct = new StringBuilder(256);
                GetClassName(child, ct, 256);
                GetWindowRect(child, out var cr2);
                var ctitle = new StringBuilder(256);
                GetWindowText(child, ctitle, 256);
                string ctext = ctitle.Length > 0 ? $" \"{ctitle}\"" : "";
                string cvis = IsWindowVisible(child) ? "" : " [oculta]";
                ListChildren.Items.Add($"0x{child.ToInt64():X8}  {ct}{cvis}{ctext}  ({cr2.Right - cr2.Left}x{cr2.Bottom - cr2.Top})");
                return true;
            }, IntPtr.Zero);
        }

        private static string FormatWindowStyles(int style)
        {
            var parts = new List<string>();
            uint s = (uint)style;
            if ((s & WS_CHILD) != 0) parts.Add("WS_CHILD");
            else parts.Add("WS_OVERLAPPED");
            if ((s & WS_VISIBLE) != 0) parts.Add("WS_VISIBLE");
            if ((s & WS_DISABLED) != 0) parts.Add("WS_DISABLED");
            if ((s & WS_CLIPSIBLINGS) != 0) parts.Add("WS_CLIPSIBLINGS");
            if ((s & WS_CAPTION) != 0) parts.Add("WS_CAPTION");
            if ((s & WS_SYSMENU) != 0) parts.Add("WS_SYSMENU");
            if ((s & WS_THICKFRAME) != 0) parts.Add("WS_THICKFRAME");
            if ((s & WS_MINIMIZEBOX) != 0) parts.Add("WS_MINIMIZEBOX");
            if ((s & WS_MAXIMIZEBOX) != 0) parts.Add("WS_MAXIMIZEBOX");
            if ((s & WS_BORDER) != 0) parts.Add("WS_BORDER");
            if ((s & WS_DLGFRAME) != 0) parts.Add("WS_DLGFRAME");
            if ((s & WS_VSCROLL) != 0) parts.Add("WS_VSCROLL");
            if ((s & WS_HSCROLL) != 0) parts.Add("WS_HSCROLL");
            if ((s & WS_TABSTOP) != 0) parts.Add("WS_TABSTOP");
            if ((s & WS_GROUP) != 0) parts.Add("WS_GROUP");
            if ((s & WS_SIZEBOX) != 0) parts.Add("WS_SIZEBOX");
            if ((s & WS_POPUP) != 0) parts.Add("WS_POPUP");
            parts.Add($"0x{s:X8}");
            return string.Join(" | ", parts);
        }

        private static string FormatExWindowStyles(int exStyle)
        {
            var parts = new List<string>();
            int e = exStyle;
            if ((e & WS_EX_DLGMODALFRAME) != 0) parts.Add("WS_EX_DLGMODALFRAME");
            if ((e & WS_EX_NOPARENTNOTIFY) != 0) parts.Add("WS_EX_NOPARENTNOTIFY");
            if ((e & WS_EX_TOPMOST) != 0) parts.Add("WS_EX_TOPMOST");
            if ((e & WS_EX_ACCEPTFILES) != 0) parts.Add("WS_EX_ACCEPTFILES");
            if ((e & WS_EX_TRANSPARENT) != 0) parts.Add("WS_EX_TRANSPARENT");
            if ((e & WS_EX_MDICHILD) != 0) parts.Add("WS_EX_MDICHILD");
            if ((e & WS_EX_TOOLWINDOW) != 0) parts.Add("WS_EX_TOOLWINDOW");
            if ((e & WS_EX_WINDOWEDGE) != 0) parts.Add("WS_EX_WINDOWEDGE");
            if ((e & WS_EX_CLIENTEDGE) != 0) parts.Add("WS_EX_CLIENTEDGE");
            if ((e & WS_EX_APPWINDOW) != 0) parts.Add("WS_EX_APPWINDOW");
            if ((e & WS_EX_LAYERED) != 0) parts.Add("WS_EX_LAYERED");
            if ((e & WS_EX_NOACTIVATE) != 0) parts.Add("WS_EX_NOACTIVATE");
            parts.Add($"0x{e:X8}");
            return string.Join(" | ", parts);
        }

        private void BtnAutoRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_autoTimer != null)
            {
                _autoTimer.Stop();
                _autoTimer = null;
                BtnAutoRefresh.Background = System.Windows.Media.Brushes.DarkSlateGray;
                BtnAutoRefresh.Foreground = System.Windows.Media.Brushes.LightGray;
                TxtStatus.Text = "Auto desligado";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Gray;
                return;
            }

            _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _autoTimer.Tick += (_, _) =>
            {
                if (_currentHwnd != IntPtr.Zero)
                    UpdateInfo(_currentHwnd);
            };
            _autoTimer.Start();
            BtnAutoRefresh.Background = System.Windows.Media.Brushes.Goldenrod;
            BtnAutoRefresh.Foreground = System.Windows.Media.Brushes.Black;
            TxtStatus.Text = "Auto ON";
            TxtStatus.Foreground = System.Windows.Media.Brushes.Gold;
        }

        private string _detectedName = "";
        private string _detectedPath = "";
        private uint _detectedPid;

        private void CaptureCurrentInfo()
        {
            if (_currentHwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(_currentHwnd, out uint pid);
            _detectedPid = pid;
            try
            {
                var proc = Process.GetProcessById((int)pid);
                _detectedName = proc.ProcessName;
                try { _detectedPath = proc.MainModule?.FileName ?? ""; } catch { _detectedPath = ""; }
            }
            catch { _detectedName = ""; _detectedPath = ""; }
        }

        private async void BtnUninstall_Click(object? sender, RoutedEventArgs e)
        {
            CaptureCurrentInfo();
            if (string.IsNullOrEmpty(_detectedName)) return;
            if (MessageBox.Show($"Criar ponto de restauração?", "Ponto de Restauração",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await Task.Run(() => SystemUtils.CreateRestorePoint());

            var mode = MessageBox.Show(
                $"Modo para: {_detectedName}\n\nSim = Deep Uninstall\nNão = Só desinstalador\nCancelar = Sair",
                "Modo", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (mode == MessageBoxResult.Cancel) return;

            string il = "";
            if (!string.IsNullOrEmpty(_detectedPath)) il = Path.GetDirectoryName(_detectedPath) ?? "";

            var regResult = await Task.Run(() =>
            {
                string rdn = _detectedName, rus = "", rpb = "", ril = il;
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    if (key != null)
                        foreach (var sub in key.GetSubKeyNames())
                            try
                            {
                                using var sk = key.OpenSubKey(sub);
                                var nm = sk?.GetValue("DisplayName") as string;
                                if (nm != null && nm.IndexOf(_detectedName, StringComparison.OrdinalIgnoreCase) >= 0)
                                { rdn = nm; rus = sk?.GetValue("UninstallString") as string ?? ""; rpb = sk?.GetValue("Publisher") as string ?? ""; var loc = sk?.GetValue("InstallLocation") as string; if (!string.IsNullOrEmpty(loc)) ril = loc; break; }
                            }
                            catch { }
                return (dn: rdn, us: rus, pb: rpb, il: ril);
            });
            string dn = regResult.dn, us = regResult.us, pb = regResult.pb;
            il = regResult.il;

            if (string.IsNullOrEmpty(us))
            {
                if (MessageBox.Show("Nenhum desinstalador. Forçar remoção?", "Forçar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

                // Revo-like restore point prompt
                var forceRpDialog = new KitLugia.GUI.Windows.RestorePointPromptDialog(dn);
                forceRpDialog.Owner = this;
                forceRpDialog.ShowDialog();
                if (forceRpDialog.CreateRestorePoint)
                    DeepUninstaller.TryCreateRestorePoint($"KitLugia: Force Delete {dn}");

                var r = new DeepUninstaller.UninstallResult();
                DeepUninstaller.PerformCleanup(new List<string> { il }, new List<string>(), r);
                MessageBox.Show($"Forçada: {r.FilesDeleted} pasta(s), {r.RegistryDeleted} registro(s)", "Concluído", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (mode == MessageBoxResult.No)
            {
                DeepUninstaller.KillProcessesForApp(dn, il);
                try { Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{us}\"") { UseShellExecute = true, Verb = "runas" }); }
                catch (Exception ex) { MessageBox.Show($"Erro: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error); }
                return;
            }

            // Revo-like restore point prompt
            var rpDialog = new KitLugia.GUI.Windows.RestorePointPromptDialog(dn);
            rpDialog.Owner = this;
            rpDialog.ShowDialog();
            bool createRp = rpDialog.CreateRestorePoint;

            var dr = await Task.Run(() => DeepUninstaller.DeepUninstallProgram(dn, us, il, pb, "", createRp));
            // Show review dialog
            var dialog = new DeepCleanupDialog(dn, dr);
            bool? dlgResult = null;
            await Dispatcher.InvokeAsync(() => { dlgResult = dialog.ShowDialog(); });

            var cleanFiles = dialog.SelectedFiles;
            var cleanRegs = dialog.SelectedRegistry;
            var cleanupResult = new DeepUninstaller.UninstallResult();
            if (dialog.HasConfirmed && (cleanFiles.Count > 0 || cleanRegs.Count > 0))
                await Task.Run(() => DeepUninstaller.PerformCleanup(cleanFiles, cleanRegs, cleanupResult));

            string resultMsg = $"Deep Uninstall: {dn}\n\n" +
                $"Desinstalador: {(dr.UninstallSuccess ? "OK" : "Pode não ter sido completo")}\n" +
                $"Pastas/Arquivos limpos: {cleanupResult.FilesDeleted}\n" +
                $"Registro limpo: {cleanupResult.RegistryDeleted}";
            if (dr.Errors.Count > 0)
                resultMsg += $"\nErros: {string.Join("; ", dr.Errors.Take(3))}";
            MessageBox.Show(resultMsg, "Concluído", MessageBoxButton.OK,
                dr.UninstallSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private async void BtnKill_Click(object? sender, RoutedEventArgs e)
        {
            CaptureCurrentInfo();
            if (_detectedPid == 0) return;
            await Task.Run(() =>
            {
                try { var p = Process.GetProcessById((int)_detectedPid); p.Kill(); p.WaitForExit(3000); } catch { }
            });
        }

        private async void BtnKillDel_Click(object? sender, RoutedEventArgs e)
        {
            await Task.Run(() =>
            {
                try { var p = Process.GetProcessById((int)_detectedPid); p.Kill(); p.WaitForExit(3000); } catch { }
            });
            if (!string.IsNullOrEmpty(_detectedPath))
                await Task.Run(() =>
                {
                    try { string d = Path.GetDirectoryName(_detectedPath) ?? ""; if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
                });
        }

        private void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_detectedPath))
                try { Process.Start("explorer.exe", $"/select,\"{_detectedPath}\""); } catch { }
        }

        private void BtnProperties_Click(object? sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_detectedPath) && File.Exists(_detectedPath))
                try { Process.Start("properties", _detectedPath); } catch { }
            else if (!string.IsNullOrEmpty(_detectedName))
                MessageBox.Show($"Nome: {_detectedName}\nCaminho: {_detectedPath}", "Propriedades", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
