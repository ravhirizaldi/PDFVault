#nullable enable
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PdfVaultDesktop;

internal static class Program
{
    const string GitHub = "https://github.com/ravhirizaldi";
    const int PageSize = 24;
    const string ImageFilter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff";
    static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    sealed record Tile(int Index, string Name, BitmapSource? Image) : System.ComponentModel.INotifyPropertyChanged
    {
        public bool Selected { get; private set; }
        public Visibility SelectionVisibility { get; private set; }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void Update(bool selecting, bool selected)
        {
            Selected = selected;
            SelectionVisibility = selecting ? Visibility.Visible : Visibility.Collapsed;
            PropertyChanged?.Invoke(this, new(nameof(Selected)));
            PropertyChanged?.Invoke(this, new(nameof(SelectionVisibility)));
        }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSRegisterSessionNotification(nint handle, int flags);
    [DllImport("wtsapi32.dll")]
    static extern bool WTSUnRegisterSessionNotification(nint handle);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public RectI Monitor, Work; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] struct PointI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MinMax { public PointI Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }

    [STAThread]
    static void Main()
    {
        var app = new Application();
        try { app.Run(CreateWindow()); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "PDFVault", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    internal static Window CreateWindow()
    {
        using var xaml = typeof(Program).Assembly.GetManifestResourceStream("Shell.xaml")!;
        var window = (Window)XamlReader.Load(xaml);
        T Get<T>(string name) where T : FrameworkElement => window.FindName(name) as T ?? throw new InvalidOperationException($"Missing control: {name}");
        using (var icon = typeof(Program).Assembly.GetManifestResourceStream("App.ico"))
            if (icon != null) window.Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        foreach (var (name, glyph) in new[] {
            ("About", "\uE946"), ("MinimizeWindow", "\uE921"), ("CloseWindow", "\uE8BB"), ("CancelWork", "\uE711"),
            ("PickPdf", "\uE838"), ("AddPhotos", "\uE710"), ("ClearPhotos", "\uE894"), ("SaveVault", "\uE74E"),
            ("GeneratePass", "\uE8D7"), ("PickVault", "\uE838"), ("Unlock", "\uE785"), ("Lock", "\uE72E"),
            ("AddToVault", "\uE710"), ("SelectPhotos", "\uE73A"), ("RemovePhoto", "\uE74D"),
            ("PreviousPage", "\uE72B"), ("NextPage", "\uE72A"), ("BackToGrid", "\uE80A"),
            ("Fit", "\uE740"), ("ActualSize", "\uE7C4"), ("ZoomOut", "\uE71F"), ("ZoomIn", "\uE8A3") })
        {
            var button = Get<Button>(name);
            ButtonIcon(button, glyph, name is "MinimizeWindow" or "CloseWindow" or "ZoomOut" or "ZoomIn" ? null : (string)button.Content);
        }
        var format = Get<ComboBox>("Format");
        var pdfPath = Get<TextBox>("PdfPath");
        var photos = Get<ListBox>("Photos");
        var count = Get<TextBlock>("PhotoCount");
        var clear = Get<Button>("ClearPhotos");
        var createPanel = Get<Grid>("CreatePanel");
        var createStatus = Get<TextBlock>("CreateStatus");
        var pass = Get<PasswordBox>("Pass");
        var confirm = Get<PasswordBox>("Confirm");
        var vaultPath = Get<TextBox>("VaultPath");
        var unlockPass = Get<PasswordBox>("UnlockPass");
        var unlockPanel = Get<StackPanel>("UnlockPanel");
        var unlockStatus = Get<TextBlock>("UnlockStatus");
        var viewer = Get<Grid>("Viewer");
        var gallery = Get<ItemsControl>("GalleryPhotos");
        var gridScroll = Get<ScrollViewer>("GridScroll");
        var gridControls = Get<StackPanel>("GridControls");
        var previous = Get<Button>("PreviousPage");
        var next = Get<Button>("NextPage");
        var back = Get<Button>("BackToGrid");
        var preview = Get<Image>("Preview");
        var frame = Get<Border>("PhotoFrame");
        var scroll = Get<ScrollViewer>("PhotoScroll");
        var zoomControls = Get<StackPanel>("ZoomControls");
        var caption = Get<TextBlock>("Caption");
        var phase = Get<TextBlock>("Phase");
        var bar = Get<ProgressBar>("WorkProgress");
        var operation = Get<DockPanel>("Operation");
        var cancelButton = Get<Button>("CancelWork");
        var unlockButton = Get<Button>("Unlock");
        var addToVault = Get<Button>("AddToVault");
        var removePhoto = Get<Button>("RemovePhoto");
        var selectButton = Get<Button>("SelectPhotos");
        var loading = Get<Border>("GalleryLoading");
        var loadingText = Get<TextBlock>("LoadingText");
        var selected = new HashSet<int>();
        bool selecting = false, opening = false;
        (long Length, DateTime Modified) openedStamp = default;
        ZipArchive? archive = null;
        ZipArchiveEntry[] entries = [];
        CancellationTokenSource? job = null;
        bool loadingPhoto = false, closing = false;
        int page = 0, index = 0;
        CancellationTokenSource? galleryRead = null;
        double? zoom = null;
        Point? dragStart = null;
        double dragX = 0, dragY = 0;
        long lastInput = Stopwatch.GetTimestamp();
        var idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        void UpdateCount()
        {
            count.Text = $"Images ({photos.Items.Count})";
            clear.IsEnabled = photos.HasItems;
        }

        void Lock(string status = "")
        {
            var old = archive;
            archive = null;
            idle.Stop();
            galleryRead?.Cancel();
            preview.Source = null;
            preview.ReleaseMouseCapture();
            caption.Text = "";
            zoomControls.IsEnabled = false;
            gallery.ItemsSource = null;
            entries = [];
            selecting = false;
            selected.Clear();
            loading.Visibility = Visibility.Collapsed;
            if (!loadingPhoto) old?.Dispose();
            viewer.Visibility = Visibility.Collapsed;
            unlockPanel.Visibility = Visibility.Visible;
            unlockPass.Clear();
            unlockButton.IsEnabled = !loadingPhoto;
            unlockStatus.Text = status;
        }

        void CheckIdle()
        {
            if (archive != null && Stopwatch.GetElapsedTime(lastInput) >= IdleTimeout)
                Lock("Locked after 5 minutes of inactivity.");
        }

        void Activity()
        {
            CheckIdle();
            lastInput = Stopwatch.GetTimestamp();
        }

        void CancelJob()
        {
            if (job == null || job.IsCancellationRequested) return;
            job.Cancel();
            cancelButton.IsEnabled = false;
            phase.Text = "Cancelling…";
        }

        void ResizePhoto()
        {
            if (preview.Source is not BitmapSource bitmap) return;
            var dpi = VisualTreeHelper.GetDpi(frame);
            double width = bitmap.PixelWidth / dpi.DpiScaleX, height = bitmap.PixelHeight / dpi.DpiScaleY;
            double scale = zoom ?? Math.Min(1, Math.Min(Math.Max(1, frame.ActualWidth - 16) / width, Math.Max(1, frame.ActualHeight - 16) / height));
            preview.Width = width * scale;
            preview.Height = height * scale;
            scroll.HorizontalScrollBarVisibility = scroll.VerticalScrollBarVisibility = zoom.HasValue ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            scroll.ToolTip = $"{scale:P0} · Ctrl + mouse wheel to zoom";
        }

        void StepZoom(int direction)
        {
            if (preview.Source is not BitmapSource bitmap) return;
            double current = zoom ?? preview.Width * VisualTreeHelper.GetDpi(frame).DpiScaleX / bitmap.PixelWidth;
            zoom = Math.Clamp(current * Math.Pow(1.25, direction), 0.005, 8);
            ResizePhoto();
        }

        void ShowGrid()
        {
            preview.Source = null;
            frame.Visibility = zoomControls.Visibility = back.Visibility = Visibility.Collapsed;
            addToVault.Visibility = selecting ? Visibility.Collapsed : Visibility.Visible;
            selectButton.Visibility = entries.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            removePhoto.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;
            gridScroll.Visibility = gridControls.Visibility = Visibility.Visible;
            int pages = (entries.Length + PageSize - 1) / PageSize;
            gridControls.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            previous.IsEnabled = page > 0;
            next.IsEnabled = page + 1 < pages;
            caption.Text = pages > 1 ? $"{page + 1} / {pages}" : entries.Length == 0 ? "No images in this vault." : "";
            UpdateSelection();
        }

        void UpdateSelection()
        {
            foreach (var tile in gallery!.Items.OfType<Tile>()) tile.Update(selecting, selected!.Contains(tile.Index));
            ButtonIcon(selectButton, "\uE73A", selecting ? "Done" : "Select");
            ButtonIcon(removePhoto, "\uE74D", !selecting || selected!.Count == 0 ? "Remove image…" : $"Remove {selected.Count} {(selected.Count == 1 ? "image" : "images")}…");
            removePhoto.IsEnabled = !loadingPhoto && entries!.Length > 1 && (!selecting || selected!.Count > 0);
            removePhoto.ToolTip = entries.Length <= 1 ? "Keep at least one image in the vault." : null;
        }

        void SetLoading(string? text)
        {
            loadingText!.Text = text ?? "";
            loading!.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        }

        async Task ReadGallery<T>(Func<CancellationToken, T> read, Action<T> show)
        {
            if (archive is not { } active || loadingPhoto) return;
            using var source = new CancellationTokenSource();
            galleryRead = source;
            loadingPhoto = true;
            gridControls.IsEnabled = back.IsEnabled = addToVault.IsEnabled = removePhoto.IsEnabled = selectButton.IsEnabled = false;
            SetLoading(frame.IsVisible ? "Loading image…" : "Loading thumbnails…");
            try
            {
                T data = await Task.Run(() => read(source.Token));
                if (archive == active) show(data);
            }
            catch (Exception ex) { if (archive == active) caption.Text = ex.Message; }
            finally
            {
                loadingPhoto = false;
                galleryRead = null;
                gridControls.IsEnabled = back.IsEnabled = unlockButton.IsEnabled = addToVault.IsEnabled = selectButton.IsEnabled = true;
                SetLoading(null);
                UpdateSelection();
                if (archive != active) active.Dispose();
                if (closing) window.Close();
            }
        }

        async Task LoadPage(int number)
        {
            if (loadingPhoto || archive == null) return;
            page = number;
            gallery.ItemsSource = null;
            var batch = entries.Skip(page * PageSize).Take(PageSize).ToArray();
            ShowGrid();
            gridScroll.ScrollToTop();
            if (batch.Length == 0) return;
            await ReadGallery(cancel => batch.Select((entry, i) =>
            {
                cancel.ThrowIfCancellationRequested();
                BitmapSource? thumbnail = null;
                try { thumbnail = Decode(entry, 260); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or FileFormatException or COMException) { }
                return new Tile(number * PageSize + i, entry.Name, thumbnail);
            }).ToArray(), tiles => { gallery.ItemsSource = tiles; ShowGrid(); });
        }

        async Task ShowPhoto(int position)
        {
            if (loadingPhoto || archive == null || position < 0 || position >= entries.Length) return;
            index = position;
            var entry = entries[position];
            gridScroll.Visibility = gridControls.Visibility = Visibility.Collapsed;
            frame.Visibility = zoomControls.Visibility = back.Visibility = Visibility.Visible;
            addToVault.Visibility = Visibility.Collapsed;
            selectButton.Visibility = Visibility.Collapsed;
            removePhoto.Visibility = Visibility.Visible;
            zoomControls.IsEnabled = false;
            preview.Source = null;
            if (!frame.IsKeyboardFocusWithin) back.Focus();
            await ReadGallery(_ => Decode(entry), image =>
            {
                preview.Source = image;
                zoom = null;
                ResizePhoto();
                scroll.ScrollToHome();
                zoomControls.IsEnabled = true;
                caption.Text = $"{position + 1} / {entries.Length}   ·   {entry.Name}";
            });
        }

        async Task Run(FrameworkElement panel, TextBlock status, Func<CancellationToken, Action<string>, Action<int>, Task> work)
        {
            if (job != null) return;
            using var source = new CancellationTokenSource();
            job = source;
            panel.IsEnabled = false;
            status.Text = "";
            void Stage(string text) => window.Dispatcher.BeginInvoke(() =>
            {
                if (job != source || source.IsCancellationRequested) return;
                operation.Visibility = Visibility.Visible;
                phase.Text = text;
                bar.Value = 0;
                bar.IsIndeterminate = true;
            });
            void Progress(int percent) => window.Dispatcher.BeginInvoke(() =>
            {
                if (job != source || source.IsCancellationRequested) return;
                bar.Value = percent;
                bar.IsIndeterminate = false;
            });
            try { await work(source.Token, Stage, Progress); }
            catch (OperationCanceledException) { if (status.Text.Length == 0) status.Text = "Cancelled."; }
            catch (Exception ex) { status.Text = ex.Message; }
            finally
            {
                panel.IsEnabled = true;
                operation.Visibility = Visibility.Collapsed;
                cancelButton.IsEnabled = true;
                job = null;
                if (closing) window.Close();
            }
        }

        string? Pick(string filter)
        {
            var dialog = new OpenFileDialog { Filter = filter };
            return dialog.ShowDialog(window) == true ? dialog.FileName : null;
        }

        Get<Button>("About").Click += (_, _) => ShowAbout(window);
        Get<Button>("MinimizeWindow").Click += (_, _) => window.WindowState = WindowState.Minimized;
        Get<Button>("CloseWindow").Click += (_, _) => window.Close();
        window.StateChanged += (_, _) => { if (window.WindowState == WindowState.Normal) window.WindowState = WindowState.Maximized; };
        Get<Button>("GeneratePass").Click += (_, _) =>
        {
            if (ShowGeneratedPassphrase(window) is { } password) pass.Password = confirm.Password = password;
        };
        cancelButton.Click += (_, _) => CancelJob();
        format.SelectionChanged += (_, _) => Get<DockPanel>("PdfRow").Visibility = format.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        Get<Button>("PickPdf").Click += (_, _) => pdfPath.Text = Pick("PDF documents|*.pdf") ?? pdfPath.Text;
        Get<Button>("AddPhotos").Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Multiselect = true, Filter = ImageFilter };
            if (dialog.ShowDialog(window) != true) return;
            var existing = new HashSet<string>(photos.Items.Cast<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (string path in dialog.FileNames) if (existing.Add(path)) photos.Items.Add(path);
            UpdateCount();
        };
        clear.Click += (_, _) => { photos.Items.Clear(); UpdateCount(); };
        Get<Button>("SaveVault").Click += async (_, _) => await Run(createPanel, createStatus, async (cancel, log, progress) =>
        {
            var paths = photos.Items.Cast<string>().ToArray();
            string password = pass.Password;
            if (paths.Length == 0) throw new InvalidOperationException("Add at least one image.");
            if (password.Length == 0) throw new InvalidOperationException("Enter a passphrase.");
            if (password != confirm.Password) throw new InvalidOperationException("Passphrases do not match.");
            string? source = format.SelectedIndex == 1 ? pdfPath.Text : null;
            if (source != null && !File.Exists(source)) throw new InvalidOperationException("Choose a source PDF.");
            bool generate = format.SelectedIndex == 2, isPdf = source != null || generate;
            var dialog = new SaveFileDialog
            {
                Filter = isPdf ? "PDF documents|*.pdf" : "PDFVault galleries|*.pvault",
                FileName = isPdf ? "gallery.pdf" : "gallery.pvault"
            };
            if (dialog.ShowDialog(window) != true) return;
            await Task.Run(() => Vault.Hide(source, generate, paths, password, dialog.FileName, log, cancel, progress));
            pass.Clear(); confirm.Clear();
            createStatus.Text = $"Saved {paths.Length} image(s).";
        });

        Get<Button>("PickVault").Click += (_, _) => vaultPath.Text = Pick("PDFVault files|*.pvault;*.pdf|All files|*.*") ?? vaultPath.Text;
        async Task OpenAndShow(string path, string password, CancellationToken cancel, Action<string> log, Action<int> progress, int keepPage = 0)
        {
            opening = true;
            SetLoading("Opening vault…");
            try
            {
            var opened = await Task.Run(() =>
            {
                var stamp = Vault.Stamp(path);
                var zip = Vault.OpenGallery(path, password, log, cancel, progress);
                try
                {
                    if (Vault.Stamp(path) != stamp) throw new IOException("The vault changed while opening. Try again.");
                    return (zip, rows: zip.Entries.Where(e => e.Name.Length != 0).ToArray(), stamp);
                }
                catch { zip.Dispose(); throw; }
            });
            if (cancel.IsCancellationRequested) { opened.zip.Dispose(); cancel.ThrowIfCancellationRequested(); }
            (archive, entries, openedStamp) = opened;
            lastInput = Stopwatch.GetTimestamp();
            idle.Start();
            unlockPass.Clear();
            unlockPanel.Visibility = Visibility.Collapsed;
            viewer.Visibility = Visibility.Visible;
            _ = LoadPage(Math.Clamp(keepPage, 0, Math.Max(0, (entries.Length - 1) / PageSize)));
            }
            finally { opening = false; if (!loadingPhoto) SetLoading(null); }
        }

        unlockButton.Click += async (_, _) => await Run(unlockPanel, unlockStatus, async (cancel, log, progress) =>
        {
            string path = vaultPath.Text, password = unlockPass.Password;
            if (!File.Exists(path)) throw new InvalidOperationException("Choose a vault file.");
            if (password.Length == 0) throw new InvalidOperationException("Enter the vault passphrase.");
            await OpenAndShow(path, password, cancel, log, progress);
        });

        async Task EditVault(string[] additions, int[] remove)
        {
            if (archive is not { } original || loadingPhoto || job != null) return;
            if (remove.Length > 0 && remove.Length >= entries.Length) return;
            var removals = remove.ToDictionary(i => i, i => entries[i].FullName);
            string? password = PromptEdit(window, remove.Length == 0 ? null : remove.Length == 1 ? $"Remove “{entries[remove[0]].Name}” from this vault?" : $"Remove {remove.Length} images from this vault?");
            if (password == null || archive != original) return;
            string path = vaultPath.Text;
            var stamp = openedStamp;
            int keepPage = page;
            Lock();
            await Run(unlockPanel, unlockStatus, async (cancel, log, progress) =>
            {
                await Task.Run(() => Vault.Edit(path, stamp, password, additions, removals, log, cancel, progress));
                unlockStatus.Text = "Changes saved.";
                if (!cancel.IsCancellationRequested) await OpenAndShow(path, password, cancel, log, progress, keepPage);
            });
        }
        addToVault.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog { Multiselect = true, Filter = ImageFilter };
            if (dialog.ShowDialog(window) == true) await EditVault(dialog.FileNames, []);
        };
        selectButton.Click += (_, _) => { selecting = !selecting; selected.Clear(); ShowGrid(); };
        removePhoto.Click += async (_, _) => await EditVault([], selecting ? selected.ToArray() : [index]);
        gallery.AddHandler(Button.ClickEvent, new RoutedEventHandler(async (_, e) =>
        {
            if (e.OriginalSource is not Button { DataContext: Tile tile } || loadingPhoto) return;
            if (!selecting) { await ShowPhoto(tile.Index); return; }
            if (!selected.Remove(tile.Index))
            {
                if (selected.Count == entries.Length - 1) { caption.Text = "Keep at least one image in the vault."; return; }
                selected.Add(tile.Index);
            }
            ShowGrid();
        }));
        previous.Click += async (_, _) => await LoadPage(page - 1);
        next.Click += async (_, _) => await LoadPage(page + 1);
        async Task BackToGrid()
        {
            if (loadingPhoto) return;
            if (index / PageSize != page) await LoadPage(index / PageSize);
            else ShowGrid();
            if (gridScroll.IsVisible) gridScroll.Focus();
        }
        back.Click += async (_, _) => await BackToGrid();
        Get<Button>("Lock").Click += (_, _) => { Lock(); unlockButton.IsEnabled = !loadingPhoto; unlockPass.Focus(); };
        Get<Button>("Fit").Click += (_, _) => { zoom = null; ResizePhoto(); scroll.ScrollToHome(); };
        Get<Button>("ActualSize").Click += (_, _) => { zoom = 1; ResizePhoto(); };
        Get<Button>("ZoomIn").Click += (_, _) => StepZoom(1);
        Get<Button>("ZoomOut").Click += (_, _) => StepZoom(-1);
        frame.SizeChanged += (_, _) => ResizePhoto();
        window.DpiChanged += (_, _) => ResizePhoto();
        preview.MouseLeftButtonDown += (_, e) =>
        {
            if (scroll.ScrollableWidth <= 0 && scroll.ScrollableHeight <= 0 || !preview.CaptureMouse()) return;
            dragStart = e.GetPosition(scroll);
            dragX = scroll.HorizontalOffset;
            dragY = scroll.VerticalOffset;
            preview.Cursor = Cursors.ScrollAll;
            e.Handled = true;
        };
        preview.MouseMove += (_, e) =>
        {
            if (dragStart is not Point start) return;
            if (e.LeftButton != MouseButtonState.Pressed) { preview.ReleaseMouseCapture(); return; }
            Vector delta = e.GetPosition(scroll) - start;
            scroll.ScrollToHorizontalOffset(dragX - delta.X);
            scroll.ScrollToVerticalOffset(dragY - delta.Y);
        };
        preview.MouseLeftButtonUp += (_, _) => preview.ReleaseMouseCapture();
        preview.LostMouseCapture += (_, _) => { dragStart = null; preview.Cursor = null; };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            Activity();
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || preview.Source == null) return;
            StepZoom(Math.Sign(e.Delta));
            e.Handled = true;
        };
        window.PreviewMouseMove += (_, _) => Activity();
        window.PreviewMouseDown += (_, _) => Activity();
        window.PreviewKeyDown += async (_, e) =>
        {
            Activity();
            if (!frame.IsVisible || loadingPhoto || Keyboard.Modifiers != ModifierKeys.None || entries.Length == 0) return;
            if (e.Key == Key.Escape) { e.Handled = true; await BackToGrid(); return; }
            if (e.Key != Key.Left && e.Key != Key.Right) return;
            e.Handled = true;
            int nextIndex = index + (e.Key == Key.Left ? -1 : 1);
            if (nextIndex >= 0 && nextIndex < entries.Length) await ShowPhoto(nextIndex);
        };
        idle.Tick += (_, _) => CheckIdle();
        window.Activated += (_, _) => CheckIdle();
        nint handle = 0;
        bool registered = false;
        window.SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(handle)?.AddHook((nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
            {
                if (msg == 0x0024)
                {
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info))
                    {
                        var limits = Marshal.PtrToStructure<MinMax>(lParam);
                        limits.MaxPosition = new PointI { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
                        limits.MaxSize = new PointI { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
                        Marshal.StructureToPtr(limits, lParam, false);
                        handled = true;
                    }
                }
                // WM_WTSSESSION_CHANGE: lock, logoff, and remote disconnect.
                if (msg == 0x02B1 && (wParam == 7 || wParam == 6 || wParam == 4))
                {
                    if (unlockPanel.IsEnabled == false || opening) CancelJob();
                    if (archive != null || loadingPhoto) Lock("Gallery locked.");
                    unlockButton.IsEnabled = !loadingPhoto;
                }
                return 0;
            });
            registered = WTSRegisterSessionNotification(handle, 0);
        };
        window.Closing += (_, e) =>
        {
            if (job == null && !loadingPhoto) return;
            e.Cancel = closing = true;
            CancelJob();
            if (archive != null && loadingPhoto) Lock();
        };
        window.Closed += (_, _) => { if (registered) WTSUnRegisterSessionNotification(handle); Lock(); };
        return window;
    }

    internal static BitmapSource Decode(ZipArchiveEntry entry, int thumbnail = 0)
    {
        if (entry.Length is <= 0 or > (128L << 20)) throw new InvalidDataException("Image is empty or over 128 MiB.");
        byte[] bytes = new byte[(int)entry.Length];
        try
        {
            using (var source = entry.Open())
            {
                source.ReadExactly(bytes);
                if (source.ReadByte() != -1) throw new InvalidDataException("Image length does not match the archive.");
            }
            using var stream = new MemoryStream(bytes, false);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > 64_000_000) throw new InvalidDataException("Image exceeds the 64 megapixel preview limit.");
            BitmapSource bitmap;
            if (thumbnail > 0)
            {
                var pixels = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
                double scale = Math.Min(1, (double)thumbnail / Math.Max(frame.PixelWidth, frame.PixelHeight));
                BitmapSource thumb = scale < 1 ? new TransformedBitmap(pixels, new ScaleTransform(scale, scale)) : pixels;
                int stride = thumb.PixelWidth * 4;
                var resized = new byte[stride * thumb.PixelHeight];
                try
                {
                    thumb.CopyPixels(resized, stride, 0);
                    bitmap = BitmapSource.Create(thumb.PixelWidth, thumb.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, resized, stride);
                }
                finally { CryptographicOperations.ZeroMemory(resized); }
            }
            else bitmap = new CachedBitmap(frame, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            bitmap.Freeze();
            return bitmap;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    static void ButtonIcon(Button button, string glyph, string? label)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13,
            FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        if (label == null) { button.Content = icon; return; }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(new AccessText { Text = label, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        button.Content = row;
        System.Windows.Automation.AutomationProperties.SetName(button, label.Replace("_", ""));
    }

    static string? PromptEdit(Window owner, string? remove)
    {
        string title = remove == null ? "Add images" : "Remove image";
        var password = new PasswordBox { MinHeight = 36, Padding = new Thickness(8), ToolTip = "Vault passphrase" };
        System.Windows.Automation.AutomationProperties.SetName(password, "Vault passphrase");
        var ok = new Button { Content = remove == null ? "Save changes" : "Remove from vault", IsDefault = true,
            IsEnabled = false, Style = (Style)owner.FindResource("Primary") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)owner.FindResource(typeof(Button)) };
        ButtonIcon(ok, remove == null ? "\uE74E" : "\uE74D", (string)ok.Content);
        ButtonIcon(cancel, "\uE711", "Cancel");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(20) };
        if (remove != null) panel.Children.Add(new TextBlock { Text = remove, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new Label { Content = "_Vault passphrase", Target = password, Padding = new Thickness(0, 0, 0, 5) });
        panel.Children.Add(password);
        panel.Children.Add(buttons);
        var dialog = new Window { Title = title, Owner = owner, Content = panel, Width = 420,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = owner.Background, Foreground = owner.Foreground,
            FontFamily = owner.FontFamily, FontSize = owner.FontSize };
        password.PasswordChanged += (_, _) =>
        {
            using var value = password.SecurePassword;
            ok.IsEnabled = value.Length != 0;
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => password.Focus();
        string? result = dialog.ShowDialog() == true ? password.Password : null;
        password.Clear();
        return result;
    }

    static string? ShowGeneratedPassphrase(Window owner)
    {
        Span<byte> random = stackalloc byte[24];
        RandomNumberGenerator.Fill(random);
        string password = Convert.ToBase64String(random);
        CryptographicOperations.ZeroMemory(random);
        var field = new TextBox { Text = password, FontFamily = new FontFamily("Consolas"), IsReadOnly = true, Padding = new Thickness(8) };
        var accept = new Button { Content = "Use passphrase", Style = (Style)owner.FindResource("Primary"),
            HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, Margin = new Thickness(0, 12, 0, 0) };
        ButtonIcon(accept, "\uE73E", "Use passphrase");
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Save this in your password manager.", Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(field);
        panel.Children.Add(accept);
        var dialog = new Window { Title = "Generated passphrase", Owner = owner, Content = panel, Width = 425,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = owner.Background, Foreground = owner.Foreground,
            FontFamily = owner.FontFamily, FontSize = owner.FontSize };
        accept.Click += (_, _) => dialog.DialogResult = true;
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(); };
        dialog.Loaded += (_, _) => { field.Focus(); field.SelectAll(); };
        return dialog.ShowDialog() == true ? password : null;
    }

    static void ShowAbout(Window owner)
    {
        var panel = new StackPanel { Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = "PDFVault", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Created by Ravhi Rizaldi", Margin = new Thickness(0, 7, 0, 16) });
        var link = new Button { Content = "GitHub · @ravhirizaldi", HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)owner.FindResource(typeof(Button)) };
        ButtonIcon(link, "\uE8A7", "GitHub · @ravhirizaldi");
        panel.Children.Add(link);
        var about = new Window { Title = "About PDFVault", Owner = owner, Content = panel, Width = 330,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = owner.Background, Foreground = owner.Foreground,
            FontFamily = owner.FontFamily, FontSize = owner.FontSize, UseLayoutRounding = true, SnapsToDevicePixels = true };
        link.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(GitHub) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(about, ex.Message, "PDFVault"); }
        };
        about.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) about.Close(); };
        about.ShowDialog();
    }
}
