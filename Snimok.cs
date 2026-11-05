// Снимок by mysik — скриншоты области или экрана, редактор со стрелками, текстом и размытием.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using Mysik;
using IOPath = System.IO.Path;
using WF = System.Windows.Forms;
using SD = System.Drawing;

[assembly: AssemblyTitle("Снимок")]
[assembly: AssemblyDescription("Снимок by mysik — скриншоты с редактором")]
[assembly: AssemblyCompany("mysik")]
[assembly: AssemblyProduct("Снимок by mysik")]
[assembly: AssemblyCopyright("by mysik")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace Snimok
{
    static class Program
    {
        [STAThread]
        static void Main() { Boot.Run("Снимок", "SnimokByMysik_SingleInstance", () => new MainWindow()); }
    }

    static class Native
    {
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    }

    // ------------------------------------------------------------------ съёмка экрана

    static class Screen
    {
        public static BitmapSource GrabAll()
        {
            var vs = WF.SystemInformation.VirtualScreen;
            using (var bmp = new SD.Bitmap(vs.Width, vs.Height, SD.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = SD.Graphics.FromImage(bmp))
                    g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, SD.CopyPixelOperation.SourceCopy);
                IntPtr h = bmp.GetHbitmap();
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { Native.DeleteObject(h); }
            }
        }

        public static string Folder
        {
            get
            {
                string f = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Снимки by mysik");
                Directory.CreateDirectory(f);
                return f;
            }
        }

        public static string Save(BitmapSource img, string path)
        {
            if (path == null)
            {
                path = IOPath.Combine(Folder, "Снимок " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".png");
                for (int i = 2; File.Exists(path); i++)
                    path = IOPath.Combine(Folder, "Снимок " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + " (" + i + ").png");
            }
            BitmapEncoder enc;
            if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                enc = new JpegBitmapEncoder { QualityLevel = 92 };
            else enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using (var fs = File.Create(path)) enc.Save(fs);
            return path;
        }

        public static BitmapSource Load(string path)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;   // не держим файл открытым
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        public static void Copy(BitmapSource img)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetImage(img); return; }
                catch { System.Threading.Thread.Sleep(60); }
            }
        }
    }

    // ------------------------------------------------------------------ выделение области

    class Overlay : Window
    {
        readonly BitmapSource shot;
        readonly Canvas canvas = new Canvas();
        readonly Path dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)) };
        readonly Rectangle frame = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(47, 179, 110)), StrokeThickness = 2, Visibility = Visibility.Collapsed };
        readonly Border sizeTag = new Border { Background = new SolidColorBrush(Color.FromArgb(220, 20, 22, 27)), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Visibility = Visibility.Collapsed };
        readonly TextBlock sizeText = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Segoe UI") };
        Point start;
        bool dragging;
        public Int32Rect? Result;

        public Overlay(BitmapSource shot)
        {
            this.shot = shot;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            Cursor = Cursors.Cross;
            Background = Brushes.Black;

            var img = new Image { Source = shot, Stretch = Stretch.Fill, Width = Width, Height = Height };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            canvas.Children.Add(img);
            dim.Data = new RectangleGeometry(new Rect(0, 0, Width, Height));
            canvas.Children.Add(dim);
            canvas.Children.Add(frame);
            sizeTag.Child = sizeText;
            canvas.Children.Add(sizeTag);

            var hint = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 28, 31, 38)), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 9, 16, 9),
                Child = new TextBlock
                {
                    Text = "Выдели область мышкой  ·  Enter — весь экран  ·  Esc — отмена",
                    Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 238)), FontSize = 13, FontFamily = new FontFamily("Segoe UI")
                },
                IsHitTestVisible = false
            };
            hint.Loaded += (s, e) =>
            {
                var area = SystemParameters.WorkArea;   // основной монитор
                Canvas.SetLeft(hint, area.Left - Left + (area.Width - hint.ActualWidth) / 2);
                Canvas.SetTop(hint, area.Top - Top + 24);
            };
            canvas.Children.Add(hint);
            Content = canvas;

            MouseLeftButtonDown += (s, e) => { start = e.GetPosition(canvas); dragging = true; CaptureMouse(); Update(start); };
            MouseMove += (s, e) => { if (dragging) Update(e.GetPosition(canvas)); };
            MouseLeftButtonUp += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                ReleaseMouseCapture();
                var r = RectOf(start, e.GetPosition(canvas));
                if (r.Width < 4 || r.Height < 4) { ClearSelection(); return; }
                Finish(r);
            };
            MouseRightButtonUp += (s, e) => Close();
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) Close();
                else if (e.Key == Key.Enter) Finish(new Rect(0, 0, Width, Height));
            };
            Loaded += (s, e) => { Activate(); Focus(); };
        }

        static Rect RectOf(Point a, Point b)
        {
            return new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        void ClearSelection()
        {
            frame.Visibility = sizeTag.Visibility = Visibility.Collapsed;
            dim.Data = new RectangleGeometry(new Rect(0, 0, Width, Height));
        }

        void Update(Point p)
        {
            var r = RectOf(start, p);
            dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, Width, Height)), new RectangleGeometry(r));
            frame.Visibility = sizeTag.Visibility = Visibility.Visible;
            Canvas.SetLeft(frame, r.X);
            Canvas.SetTop(frame, r.Y);
            frame.Width = r.Width;
            frame.Height = r.Height;
            double kx = shot.PixelWidth / Width, ky = shot.PixelHeight / Height;
            sizeText.Text = (int)Math.Round(r.Width * kx) + " × " + (int)Math.Round(r.Height * ky);
            Canvas.SetLeft(sizeTag, r.X);
            Canvas.SetTop(sizeTag, r.Y > 30 ? r.Y - 28 : r.Bottom + 6);
        }

        void Finish(Rect r)
        {
            double kx = shot.PixelWidth / Width, ky = shot.PixelHeight / Height;
            int x = Math.Max(0, (int)Math.Round(r.X * kx)), y = Math.Max(0, (int)Math.Round(r.Y * ky));
            int w = Math.Min(shot.PixelWidth - x, (int)Math.Round(r.Width * kx)), h = Math.Min(shot.PixelHeight - y, (int)Math.Round(r.Height * ky));
            if (w > 0 && h > 0) Result = new Int32Rect(x, y, w, h);
            Close();
        }
    }

    // ------------------------------------------------------------------ редактор

    enum Tool { Arrow, Rect, Marker, Text, Blur, Number }

    class Editor : Window
    {
        readonly MainWindow owner;
        readonly BitmapSource image;
        readonly Grid sheet = new Grid();
        readonly Canvas layer = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        readonly Stack<UIElement> history = new Stack<UIElement>();
        readonly TextBlock status = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(139, 146, 160)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Tool tool = Tool.Arrow;
        Color color = Color.FromRgb(240, 82, 95);
        Point p0;
        UIElement drawing;
        TextBox typing;
        int number = 1;
        bool saved;
        readonly double stroke, fontSize;

        static readonly Color[] Palette =
        {
            Color.FromRgb(240, 82, 95), Color.FromRgb(242, 179, 61), Color.FromRgb(47, 179, 110),
            Color.FromRgb(59, 130, 246), Color.FromRgb(255, 255, 255), Color.FromRgb(20, 22, 27)
        };

        public Editor(MainWindow owner, BitmapSource image)
        {
            this.owner = owner;
            this.image = image;
            stroke = Math.Max(3, Math.Min(image.PixelWidth, image.PixelHeight) / 160.0);
            fontSize = Math.Max(16, Math.Min(image.PixelWidth, image.PixelHeight) / 22.0);

            Title = "Снимок by mysik — редактор";
            Background = new SolidColorBrush(Color.FromRgb(20, 22, 27));
            Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 238));
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var wa = SystemParameters.WorkArea;
            double dpiK = 1;
            try { var ps = PresentationSource.FromVisual(owner); if (ps != null) dpiK = ps.CompositionTarget.TransformToDevice.M11; } catch { }
            Width = Math.Min(wa.Width * 0.92, Math.Max(760, image.PixelWidth / dpiK + 60));
            Height = Math.Min(wa.Height * 0.92, Math.Max(520, image.PixelHeight / dpiK + 150));
            try { Icon = owner.Icon; } catch { }

            var root = new DockPanel();
            root.Children.Add(Toolbar());
            var bottom = new Border { Background = new SolidColorBrush(Color.FromRgb(28, 31, 38)), Padding = new Thickness(16, 8, 16, 8), Child = status };
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);

            // лист: картинка + слой рисования, в пикселях снимка
            sheet.Width = image.PixelWidth;
            sheet.Height = image.PixelHeight;
            var img = new Image { Source = image, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            sheet.Children.Add(img);
            sheet.Children.Add(layer);
            var view = new Viewbox { Child = sheet, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(20) };
            var area = new Border { Background = new SolidColorBrush(Color.FromRgb(14, 15, 19)), Child = view, Cursor = Cursors.Cross };
            root.Children.Add(area);
            Content = root;

            layer.MouseLeftButtonDown += OnDown;
            layer.MouseMove += OnMove;
            layer.MouseLeftButtonUp += OnUp;
            KeyDown += OnKey;
            SetStatus(null);
        }

        UIElement Toolbar()
        {
            var bar = new Border { Background = new SolidColorBrush(Color.FromRgb(28, 31, 38)), Padding = new Thickness(12, 10, 12, 10) };
            DockPanel.SetDock(bar, Dock.Top);
            var dock = new DockPanel { LastChildFill = false };
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            string[][] items =
            {
                new[] { "↗", "Стрелка", "A" }, new[] { "▢", "Рамка", "R" }, new[] { "▬", "Маркер", "M" },
                new[] { "T", "Текст", "T" }, new[] { "▦", "Размытие", "B" }, new[] { "①", "Номер", "N" }
            };
            var pill = owner.Res("Pill");
            for (int i = 0; i < items.Length; i++)
            {
                var t = (Tool)i;
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = items[i][0], FontSize = 15, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = items[i][1], VerticalAlignment = VerticalAlignment.Center });
                var rb = new RadioButton { Style = pill, Content = sp, GroupName = "tool", IsChecked = i == 0, Margin = new Thickness(0, 0, 6, 0), ToolTip = items[i][1] + " (" + items[i][2] + ")" };
                rb.Checked += (s, e) => { CommitText(); tool = t; SetStatus(null); };
                tools.Children.Add(rb);
            }
            tools.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.FromRgb(58, 63, 75)), Margin = new Thickness(6, 4, 12, 4) });
            foreach (var c in Palette)
            {
                var col = c;
                var dot = new RadioButton { GroupName = "color", IsChecked = c == color, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Цвет" };
                dot.Template = ColorDotTemplate(c);
                dot.Checked += (s, e) => { color = col; };
                tools.Children.Add(dot);
            }
            dock.Children.Add(tools);

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);
            var undo = new Button { Style = owner.Res("SecondaryBtn"), Content = "↶ Отменить", Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(38, 42, 51)), ToolTip = "Ctrl+Z" };
            undo.Click += (s, e) => Undo();
            var save = new Button { Style = owner.Res("SecondaryBtn"), Content = "Сохранить", Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(38, 42, 51)), ToolTip = "Ctrl+S" };
            save.Click += (s, e) => DoSave(false);
            var saveAs = new Button { Style = owner.Res("SecondaryBtn"), Content = "…", Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(38, 42, 51)), ToolTip = "Сохранить как…" };
            saveAs.Click += (s, e) => DoSave(true);
            var copy = new Button { Style = owner.Res("PrimaryBtn"), Content = "Копировать", Padding = new Thickness(16, 9, 16, 9), ToolTip = "Ctrl+C" };
            copy.Click += (s, e) => DoCopy();
            actions.Children.Add(undo);
            actions.Children.Add(save);
            actions.Children.Add(saveAs);
            actions.Children.Add(copy);
            dock.Children.Add(actions);
            bar.Child = dock;
            return bar;
        }

        static ControlTemplate ColorDotTemplate(Color c)
        {
            var t = new ControlTemplate(typeof(RadioButton));
            var ring = new FrameworkElementFactory(typeof(Border));
            ring.Name = "Ring";
            ring.SetValue(Border.WidthProperty, 26.0);
            ring.SetValue(Border.HeightProperty, 26.0);
            ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
            ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            ring.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            ring.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var dot = new FrameworkElementFactory(typeof(Ellipse));
            dot.SetValue(Ellipse.WidthProperty, 16.0);
            dot.SetValue(Ellipse.HeightProperty, 16.0);
            dot.SetValue(Ellipse.FillProperty, new SolidColorBrush(c));
            dot.SetValue(Ellipse.StrokeProperty, new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)));
            dot.SetValue(Ellipse.StrokeThicknessProperty, 1.0);
            ring.AppendChild(dot);
            t.VisualTree = ring;
            var trig = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            trig.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(232, 234, 238)), "Ring"));
            t.Triggers.Add(trig);
            return t;
        }

        void SetStatus(string flash)
        {
            string tip = tool == Tool.Text ? "Нажми, где написать текст" : tool == Tool.Number ? "Нажимай по порядку: 1, 2, 3…" : "Тяни мышкой по снимку";
            status.Text = (flash != null ? flash + "   ·   " : "") + image.PixelWidth + " × " + image.PixelHeight +
                          "   ·   " + tip + "   ·   Ctrl+C копировать · Ctrl+S сохранить · Ctrl+Z отменить · Esc закрыть";
        }

        // -------------------------------------------------- рисование

        SolidColorBrush Paint(byte alpha = 255) { return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)); }

        void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (typing != null) { CommitText(); return; }
            p0 = e.GetPosition(layer);
            switch (tool)
            {
                case Tool.Text: StartText(p0); return;
                case Tool.Number: AddNumber(p0); return;
                case Tool.Arrow:
                    drawing = new Path { Stroke = Paint(), Fill = Paint(), StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Effect = Shadow() };
                    break;
                case Tool.Rect:
                    drawing = new Rectangle { Stroke = Paint(), StrokeThickness = stroke, RadiusX = stroke, RadiusY = stroke, Effect = Shadow() };
                    break;
                case Tool.Marker:
                    drawing = new Rectangle { Fill = Paint(110) };
                    break;
                case Tool.Blur:
                    drawing = new Rectangle { Stroke = Brushes.White, StrokeDashArray = new DoubleCollection { 4, 3 }, StrokeThickness = Math.Max(1, stroke / 2) };
                    break;
            }
            layer.Children.Add(drawing);
            layer.CaptureMouse();
            OnMove(sender, e);
        }

        static DropShadowEffect Shadow() { return new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.45, Color = Colors.Black }; }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (drawing == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(layer);
            if (tool == Tool.Arrow) ((Path)drawing).Data = ArrowGeometry(p0, p, stroke);
            else
            {
                var r = new Rect(p0, p);
                Canvas.SetLeft(drawing, r.X);
                Canvas.SetTop(drawing, r.Y);
                ((FrameworkElement)drawing).Width = r.Width;
                ((FrameworkElement)drawing).Height = r.Height;
            }
        }

        void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (drawing == null) return;
            layer.ReleaseMouseCapture();
            var p = e.GetPosition(layer);
            var el = drawing;
            drawing = null;
            if ((p - p0).Length < 4) { layer.Children.Remove(el); return; }
            if (tool == Tool.Blur)
            {
                layer.Children.Remove(el);
                var pix = Pixelate(new Rect(p0, p));
                if (pix != null) { layer.Children.Add(pix); history.Push(pix); }
                return;
            }
            history.Push(el);
        }

        static Geometry ArrowGeometry(Point a, Point b, double w)
        {
            var v = b - a;
            double len = v.Length;
            if (len < 1) return Geometry.Empty;
            v.Normalize();
            double head = Math.Min(len * 0.45, w * 5.5);
            var n = new Vector(-v.Y, v.X);
            Point baseP = b - v * head;
            var g = new GeometryGroup();
            g.Children.Add(new LineGeometry(a, b - v * head * 0.6));
            var tri = new StreamGeometry();
            using (var ctx = tri.Open())
            {
                ctx.BeginFigure(b, true, true);
                ctx.LineTo(baseP + n * head * 0.55, true, true);
                ctx.LineTo(baseP - n * head * 0.55, true, true);
            }
            g.Children.Add(tri);
            return g;
        }

        UIElement Pixelate(Rect r)
        {
            int x = Math.Max(0, (int)r.X), y = Math.Max(0, (int)r.Y);
            int w = Math.Min(image.PixelWidth - x, (int)r.Width), h = Math.Min(image.PixelHeight - y, (int)r.Height);
            if (w < 4 || h < 4) return null;
            var crop = new CroppedBitmap(image, new Int32Rect(x, y, w, h));
            double k = Math.Max(4, Math.Min(w, h) / 6.0);
            var small = new TransformedBitmap(crop, new ScaleTransform(Math.Max(1.0 / w, 1 / k), Math.Max(1.0 / h, 1 / k)));
            var img = new Image { Source = small, Width = w, Height = h, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(img, x);
            Canvas.SetTop(img, y);
            return img;
        }

        void AddNumber(Point p)
        {
            double d = fontSize * 1.6;
            var g = new Grid { Width = d, Height = d, Effect = Shadow() };
            g.Children.Add(new Ellipse { Fill = Paint(), Stroke = Brushes.White, StrokeThickness = Math.Max(2, stroke / 2) });
            bool light = color.R + color.G + color.B > 600;
            g.Children.Add(new TextBlock
            {
                Text = number.ToString(), FontSize = fontSize * 0.85, FontWeight = FontWeights.Bold,
                Foreground = light ? Brushes.Black : Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            g.Tag = "number";
            Canvas.SetLeft(g, p.X - d / 2);
            Canvas.SetTop(g, p.Y - d / 2);
            layer.Children.Add(g);
            history.Push(g);
            number++;
        }

        void StartText(Point p)
        {
            typing = new TextBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 2), BorderBrush = Paint(150),
                Foreground = Paint(), CaretBrush = Paint(), FontSize = fontSize, FontWeight = FontWeights.SemiBold,
                MinWidth = fontSize * 3, AcceptsReturn = true, FontFamily = new FontFamily("Segoe UI"), Effect = Shadow()
            };
            Canvas.SetLeft(typing, p.X);
            Canvas.SetTop(typing, p.Y - fontSize * 0.7);
            layer.Children.Add(typing);
            typing.Loaded += (s, e) => ((TextBox)s).Focus();
            typing.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; CommitText(); }
                else if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift) { e.Handled = true; CommitText(); }
            };
            typing.LostKeyboardFocus += (s, e) => CommitText();
        }

        void CommitText()
        {
            var tb = typing;
            if (tb == null) return;
            typing = null;
            layer.Children.Remove(tb);
            if (string.IsNullOrWhiteSpace(tb.Text)) return;
            var t = new TextBlock
            {
                Text = tb.Text, Foreground = tb.Foreground, FontSize = tb.FontSize, FontWeight = tb.FontWeight, FontFamily = tb.FontFamily,
                Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.9, Color = color.R + color.G + color.B > 600 ? Colors.Black : Colors.White }
            };
            Canvas.SetLeft(t, Canvas.GetLeft(tb));
            Canvas.SetTop(t, Canvas.GetTop(tb));
            layer.Children.Add(t);
            history.Push(t);
        }

        void Undo()
        {
            CommitText();
            if (history.Count == 0) return;
            var el = history.Pop();
            layer.Children.Remove(el);
            var fe = el as FrameworkElement;
            if (fe != null && "number".Equals(fe.Tag)) number = Math.Max(1, number - 1);
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (typing != null) return;
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (e.Key == Key.Escape) Close();
            else if (ctrl && e.Key == Key.C) DoCopy();
            else if (ctrl && e.Key == Key.S) DoSave(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            else if (ctrl && e.Key == Key.Z) Undo();
            else return;
            e.Handled = true;
        }

        // -------------------------------------------------- результат

        public BitmapSource Render()
        {
            CommitText();
            sheet.UpdateLayout();
            int w = image.PixelWidth, h = image.PixelHeight;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new VisualBrush(sheet) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        void DoCopy()
        {
            var img = history.Count == 0 && typing == null ? image : Render();
            Screen.Copy(img);
            if (owner.AutoSave && !saved) { owner.Remember(Screen.Save(img, null)); saved = true; }
            SetStatus("✔ Скопировано");
        }

        void DoSave(bool ask)
        {
            var img = Render();
            string path = null;
            if (ask)
            {
                var dlg = new SaveFileDialog
                {
                    Title = "Сохранить снимок", FileName = "Снимок " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"),
                    Filter = "PNG — без потерь|*.png|JPG — меньше размер|*.jpg", InitialDirectory = Screen.Folder
                };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
            }
            try
            {
                path = Screen.Save(img, path);
                saved = true;
                owner.Remember(path);
                SetStatus("✔ Сохранено: " + IOPath.GetFileName(path));
            }
            catch (Exception ex) { SetStatus("Не удалось сохранить: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ главное окно

    class MainWindow : ShellWindow
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static readonly string IniPath = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snimok by mysik", "settings.ini");

        Button captureBtn, fullBtn, delayBtn, folderBtn;
        TextBlock hotkeyHint, historyEmpty;
        WrapPanel historyPanel;
        CheckBox swPrintScreen, swAutoCopy, swAutostart;
        WF.NotifyIcon tray;
        IntPtr hook;
        readonly Native.HookProc hookProc;
        bool capturing, reallyExit, hintShown, suppress, startHidden;
        bool hookPrintScreen = true, autoCopy = true;
        public bool AutoSave = true;

        public MainWindow()
        {
            LoadUi("Снимок by mysik", 440, 720);
            hookProc = OnKey;
            startHidden = Environment.GetCommandLineArgs().Any(a => a == "--tray");
            if (startHidden) { Opacity = 0; ShowInTaskbar = false; }

            captureBtn = F<Button>("CaptureBtn");
            fullBtn = F<Button>("FullBtn");
            delayBtn = F<Button>("DelayBtn");
            folderBtn = F<Button>("FolderBtn");
            hotkeyHint = F<TextBlock>("HotkeyHint");
            historyEmpty = F<TextBlock>("HistoryEmpty");
            historyPanel = F<WrapPanel>("History");
            swPrintScreen = F<CheckBox>("SwPrintScreen");
            swAutoCopy = F<CheckBox>("SwAutoCopy");
            swAutostart = F<CheckBox>("SwAutostart");

            captureBtn.Click += async (s, e) => await Capture(false, 0);
            fullBtn.Click += async (s, e) => await Capture(true, 0);
            delayBtn.Click += async (s, e) => await Capture(false, 3);
            folderBtn.Click += (s, e) => { try { Process.Start("explorer.exe", "\"" + Screen.Folder + "\""); } catch { } };

            swPrintScreen.Checked += (s, e) => { if (!suppress) { hookPrintScreen = true; Save(); UpdateHint(); } };
            swPrintScreen.Unchecked += (s, e) => { if (!suppress) { hookPrintScreen = false; Save(); UpdateHint(); } };
            swAutoCopy.Checked += (s, e) => { if (!suppress) { autoCopy = true; Save(); } };
            swAutoCopy.Unchecked += (s, e) => { if (!suppress) { autoCopy = false; Save(); } };
            swAutostart.Checked += (s, e) => SetAutostart(true);
            swAutostart.Unchecked += (s, e) => SetAutostart(false);

            BuildTray();
        }

        public Style Res(string key) { return S(key); }

        public override void Init()
        {
            Load();
            suppress = true;
            swPrintScreen.IsChecked = hookPrintScreen;
            swAutoCopy.IsChecked = autoCopy;
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) swAutostart.IsChecked = k != null && k.GetValue("Snimok by mysik") != null;
            suppress = false;

            hook = Native.SetWindowsHookEx(13, hookProc, Native.GetModuleHandle(null), 0);
            UpdateHint();
            RefreshHistory();
            if (startHidden) { Hide(); Opacity = 1; ShowInTaskbar = true; }
        }

        void UpdateHint()
        {
            hotkeyHint.Text = hookPrintScreen
                ? "Или просто нажми PrintScreen, даже когда окно закрыто"
                : "Клавиша PrintScreen отключена в настройках";
        }

        IntPtr OnKey(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0 && hookPrintScreen)
            {
                var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(Native.KBDLLHOOKSTRUCT));
                if (k.vkCode == 0x2C)
                {
                    int msg = w.ToInt32();
                    if (msg == 0x101 || msg == 0x105) // отпускание PrtSc
                    {
                        bool full = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
                        Dispatcher.BeginInvoke(new Action(async () => await Capture(full, 0)));
                    }
                    return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(hook, code, w, l);
        }

        async Task Capture(bool full, int delay)
        {
            if (capturing) return;
            capturing = true;
            bool wasVisible = IsVisible;
            try
            {
                if (wasVisible) Hide();
                await Task.Delay(delay > 0 ? delay * 1000 : 180);   // окно успевает исчезнуть с экрана
                var shot = Screen.GrabAll();
                BitmapSource result = shot;
                if (!full)
                {
                    var ov = new Overlay(shot);
                    ov.ShowDialog();
                    if (ov.Result == null) { if (wasVisible) Show(); return; }
                    var crop = new CroppedBitmap(shot, ov.Result.Value);
                    crop.Freeze();
                    result = crop;
                }
                if (autoCopy) Screen.Copy(result);
                var ed = new Editor(this, result);
                ed.Closed += (s, e) => { if (wasVisible) { Show(); Activate(); } };
                ed.Show();
                ed.Activate();
            }
            catch (Exception ex)
            {
                if (wasVisible) Show();
                Ask("Не получилось сделать снимок", ex.Message, "Понятно", null);
            }
            finally { capturing = false; }
        }

        public void Remember(string path)
        {
            RefreshHistory();
            try { tray.ShowBalloonTip(1500, "Снимок сохранён", IOPath.GetFileName(path), WF.ToolTipIcon.None); } catch { }
        }

        void RefreshHistory()
        {
            historyPanel.Children.Clear();
            List<FileInfo> files;
            try
            {
                files = new DirectoryInfo(Screen.Folder).GetFiles("*.*")
                    .Where(f => f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTime).Take(9).ToList();
            }
            catch { files = new List<FileInfo>(); }
            historyEmpty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var f in files)
            {
                var file = f.FullName;
                var thumb = new Border { Width = 120, Height = 72, CornerRadius = new CornerRadius(10), Background = Br(CCard), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, ToolTip = f.Name + "\nНажми, чтобы открыть в редакторе" };
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.DecodePixelWidth = 240;
                    bi.UriSource = new Uri(file);
                    bi.EndInit();
                    bi.Freeze();
                    thumb.Background = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
                }
                catch { }
                thumb.MouseLeftButtonUp += (s, e) =>
                {
                    try { new Editor(this, Screen.Load(file)).Show(); }
                    catch (Exception ex) { Toast("Не открылось: " + ex.Message); }
                };
                historyPanel.Children.Add(thumb);
            }
        }

        // -------------------------------------------------- трей и настройки

        void BuildTray()
        {
            var menu = new WF.ContextMenuStrip();
            var shotItem = new WF.ToolStripMenuItem("Снимок области", null, async (s, e) => await Capture(false, 0));
            shotItem.Font = new SD.Font(shotItem.Font, SD.FontStyle.Bold);
            menu.Items.Add(shotItem);
            menu.Items.Add(new WF.ToolStripMenuItem("Весь экран", null, async (s, e) => await Capture(true, 0)));
            menu.Items.Add(new WF.ToolStripMenuItem("Открыть окно", null, (s, e) => { Show(); Activate(); }));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(new WF.ToolStripMenuItem("Выход", null, (s, e) => { reallyExit = true; Close(); }));
            tray = new WF.NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "Снимок by mysik — PrtSc" };
            try { tray.Icon = SD.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName); } catch { tray.Icon = SD.SystemIcons.Application; }
            tray.MouseClick += async (s, e) => { if (e.Button == WF.MouseButtons.Left) await Capture(false, 0); };
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!reallyExit)
            {
                e.Cancel = true;
                Hide();
                if (!hintShown)
                {
                    hintShown = true;
                    try { tray.ShowBalloonTip(3000, "Снимок ждёт в трее", "Нажми PrintScreen, чтобы сделать снимок. Выход — правый клик по значку.", WF.ToolTipIcon.Info); } catch { }
                }
                return;
            }
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            tray.Visible = false;
            tray.Dispose();
            foreach (Window w in Application.Current.Windows) if (w != this) w.Close();
            base.OnClosing(e);
        }

        void Load()
        {
            try
            {
                if (!File.Exists(IniPath)) return;
                foreach (var line in File.ReadAllLines(IniPath))
                {
                    if (line == "prtsc=0") hookPrintScreen = false;
                    if (line == "copy=0") autoCopy = false;
                    if (line == "save=0") AutoSave = false;
                }
            }
            catch { }
        }

        void Save()
        {
            try
            {
                Directory.CreateDirectory(IOPath.GetDirectoryName(IniPath));
                File.WriteAllLines(IniPath, new[] { "prtsc=" + (hookPrintScreen ? 1 : 0), "copy=" + (autoCopy ? 1 : 0), "save=" + (AutoSave ? 1 : 0) });
            }
            catch { }
        }

        void SetAutostart(bool on)
        {
            if (suppress) return;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("Snimok by mysik", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --tray");
                    else k.DeleteValue("Snimok by mysik", false);
                }
                Toast(on ? "Будет запускаться вместе с Windows" : "Автозапуск выключен");
            }
            catch (Exception ex) { Toast("Не получилось: " + ex.Message); }
        }
    }
}
