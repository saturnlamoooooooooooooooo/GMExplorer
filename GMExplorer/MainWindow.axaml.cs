using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GMExplorer.Gm;
using GMExplorer.Ui;

namespace GMExplorer;

public partial class MainWindow : Window
{
    enum Cat { Sprites, Backgrounds, Fonts, Pages, Sounds, Code, Native, Scripts, Objects, Rooms, Strings, GameFiles, Info }

    sealed class Row
    {
        public string Name = "";
        public string Detail = "";
        public Cat Cat;
        public int Index;
        public GmCode? Code;
    }

    sealed class CatRow
    {
        public Cat Cat;
        public string Name = "";
        public string Count = "";
    }

    GmData? data;
    Images? images;
    NativeCode? native;
    string nativeNote = "";
    GamePackage? package;
    SoundLibrary? sounds;
    readonly Dictionary<string, Bank> bankCache = new();
    readonly AudioPlayer player = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    List<Row> rows = new();
    Row? selected;
    List<int> frames = new();
    int frameIndex;
    double zoom = 1;
    RawImage? shownImage;
    bool showDisassembly;
    bool seekDragging;
    bool busy;

    public MainWindow()
    {
        InitializeComponent();

        OpenButton.Click += async (_, _) => await PickFile();
        OverlayOpen.Click += async (_, _) => await PickFile();
        CloseButton.Click += (_, _) => Unload();

        CategoryList.ItemTemplate = new FuncDataTemplate<CatRow>((c, _) =>
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var name = new TextBlock { Text = c?.Name ?? "", FontSize = 12.5 };
            var count = new TextBlock
            {
                Text = c?.Count ?? "",
                FontSize = 11,
                Foreground = Brush("Muted"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(count, 1);
            grid.Children.Add(name);
            grid.Children.Add(count);
            return grid;
        }, true);

        ItemList.ItemTemplate = new FuncDataTemplate<Row>((r, _) =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = r?.Name ?? "",
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!string.IsNullOrEmpty(r?.Detail))
                panel.Children.Add(new TextBlock
                {
                    Text = r!.Detail,
                    FontSize = 11,
                    Foreground = Brush("Muted"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            return panel;
        }, true);

        CategoryList.SelectionChanged += (_, _) => ShowCategory();
        ItemList.SelectionChanged += (_, _) => ShowSelection();
        SearchBox.TextChanged += (_, _) => ApplyFilter();

        PrevFrame.Click += (_, _) => StepFrame(-1);
        NextFrame.Click += (_, _) => StepFrame(1);
        ZoomIn.Click += (_, _) => SetZoom(zoom * 2);
        ZoomOut.Click += (_, _) => SetZoom(zoom / 2);
        ZoomFit.Click += (_, _) => FitZoom();
        ImageScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Control) return;
            SetZoom(e.Delta.Y > 0 ? zoom * 1.25 : zoom / 1.25);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ShowChecker.IsCheckedChanged += (_, _) => UpdateChecker();

        TabDecompiled.Click += (_, _) => { showDisassembly = false; UpdateCodeTabs(); ShowSelection(); };
        TabDisassembly.Click += (_, _) => { showDisassembly = true; UpdateCodeTabs(); ShowSelection(); };

        PlayButton.Click += (_, _) => { player.Toggle(); UpdateAudioUi(); };
        StopButton.Click += (_, _) => { player.Stop(); UpdateAudioUi(); };
        VolumeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase_ValueProperty) player.Volume = (float)VolumeSlider.Value;
        };
        SeekSlider.AddHandler(PointerPressedEvent, (_, _) => seekDragging = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (player.HasTrack && player.Duration > TimeSpan.Zero)
                player.Position = TimeSpan.FromSeconds(player.Duration.TotalSeconds * SeekSlider.Value / 1000.0);
            seekDragging = false;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        ExportButton.Click += async (_, _) => await ExportSelected();
        ExportAllButton.Click += async (_, _) => await ExportCategory();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) =>
        {
            DropZone.BorderBrush = new SolidColorBrush(Color.Parse("#282828"));
            DropZone.Background = new SolidColorBrush(Color.Parse("#0D0D0D"));
        });

        timer.Tick += (_, _) => UpdateAudioClock();
        timer.Start();

        player.StateChanged += () => Dispatcher.UIThread.Post(UpdateAudioUi);
        Closed += (_, _) => { player.Dispose(); native?.Dispose(); sounds?.Dispose(); };

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) _ = LoadFile(args[1]);
    }

    static readonly AvaloniaProperty RangeBase_ValueProperty = Slider.ValueProperty;

    IBrush Brush(string key) => Resources[key] as IBrush ?? Brushes.Gray;

    static readonly Easing Ease = new CubicEaseOut();

    static void Reveal(Control? c, double ms = 170, double lift = 6)
    {
        if (c == null) return;
        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(ms),
            Easing = Ease,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0d),
                        new Setter(MarginProperty, new Thickness(0, lift, 0, -lift))
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1d),
                        new Setter(MarginProperty, new Thickness(0))
                    }
                }
            }
        };
        _ = anim.RunAsync(c);
    }

    async Task HideOverlay()
    {
        if (!Overlay.IsVisible) return;
        Overlay.Opacity = 0;
        await Task.Delay(230);
        Overlay.IsVisible = false;
    }

    void ShowOverlay()
    {
        Overlay.IsVisible = true;
        Overlay.Opacity = 1;
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFile() != null ? DragDropEffects.Copy : DragDropEffects.None;
        bool ok = e.DragEffects == DragDropEffects.Copy;
        DropZone.BorderBrush = ok ? Brush("Accent") : new SolidColorBrush(Color.Parse("#282828"));
        DropZone.Background = new SolidColorBrush(Color.Parse(ok ? "#151515" : "#0D0D0D"));
        e.Handled = true;
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.BorderBrush = new SolidColorBrush(Color.Parse("#282828"));
        DropZone.Background = new SolidColorBrush(Color.Parse("#0D0D0D"));
        string? path = e.DataTransfer.TryGetFile()?.TryGetLocalPath();
        if (path != null) await LoadFile(path);
        e.Handled = true;
    }

    async Task PickFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a game",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Game executable or data file")
                {
                    Patterns = new[] { "*.exe", "*.win", "*.unx", "*.ios", "*.droid" }
                },
                FilePickerFileTypes.All
            }
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) await LoadFile(path);
    }

    async Task LoadFile(string path)
    {
        if (busy) return;
        busy = true;
        player.Stop();
        ShowOverlay();
        OverlaySub.Text = "reading " + Path.GetFileName(path);
        StatusText.Text = "Loading...";
        try
        {
            void Report(string m) => Dispatcher.UIThread.Post(() => OverlaySub.Text = m);
            var pkg = await Task.Run(() => GamePackage.Discover(path, Report));
            var loaded = await Task.Run(() => pkg.LoadData(Report));
            var lib = await Task.Run(() => SoundLibrary.Build(loaded, pkg, Report));
            package = pkg;
            bankCache.Clear();
            sounds?.Dispose();
            sounds = lib;
            data = loaded;
            images = new Images(loaded);
            BuildCategories();
            await HideOverlay();
            Body.IsVisible = true;
            Reveal(Body, 260, 10);
            CloseButton.IsVisible = true;
            HeaderTitle.Text = string.IsNullOrWhiteSpace(loaded.DisplayName) ? loaded.FileName : loaded.DisplayName;
            HeaderSub.Text = $"{pkg.Root}  -  bytecode {loaded.BytecodeVersion}  -  {Bytes(loaded.Raw.Length)}" + (pkg.Files.Count > 0 ? $"  -  {pkg.Files.Count} files" : "");
            StatusText.Text = "Loaded " + loaded.FileName;
            if (loaded.Warnings.Count > 0) StatusText.Text = loaded.FileName + " loaded, " + loaded.Warnings.Count + " notes (see File info)";
            if (!loaded.HasCode) _ = LoadNative(loaded);
        }
        catch (Exception e)
        {
            OverlaySub.Text = "Could not read that game: " + e.Message;
            StatusText.Text = "Load failed";
        }
        finally { busy = false; }
    }

    async Task LoadNative(GmData loaded)
    {
        if (!NativeCode.Available)
        {
            nativeNote = "The native analyser (gmnative.dll) was not found. Build native\\gmnative.vcxproj " + "for x64 to read the code of games compiled with the YoYo Compiler.";
            BuildCategories();
            return;
        }
        string? exe = package?.ExePath ?? NativeCode.FindExecutable(loaded.Path);
        if (exe == null)
        {
            nativeNote = "This game has no bytecode, and no executable was found next to the data file.";
            BuildCategories();
            return;
        }

        nativeNote = "Reading native code from " + Path.GetFileName(exe) + "...";
        StatusText.Text = nativeNote;
        BuildCategories();
        try
        {
            var hints = loaded.Scripts.Select(x => x.Name).Concat(loaded.Objects.Select(x => x.Name)).Concat(loaded.Sprites.Select(x => x.Name)).ToList();
            var nc = await Task.Run(() => NativeCode.Open(exe, hints));
            if (data != loaded) { nc?.Dispose(); return; }
            native?.Dispose();
            native = nc;
            nativeNote = nc == null ? "Could not read " + Path.GetFileName(exe) + ": " + NativeCode.LastError() : $"{nc.NamedCount} named functions recovered from {Path.GetFileName(exe)}";
            StatusText.Text = nativeNote;
        }
        catch (Exception e)
        {
            nativeNote = "Native analysis failed: " + e.Message;
        }
        BuildCategories();
    }

    void Unload()
    {
        player.Stop();
        native?.Dispose();
        native = null;
        nativeNote = "";
        package = null;
        bankCache.Clear();
        sounds?.Dispose();
        sounds = null;
        data = null;
        images = null;
        rows = new List<Row>();
        ItemList.ItemsSource = null;
        Body.IsVisible = false;
        CloseButton.IsVisible = false;
        ShowOverlay();
        OverlaySub.Text = "explore sprites, textures, sounds, rooms and decompiled code";
        HeaderTitle.Text = "GMExplorer";
        HeaderSub.Text = "no file loaded";
        StatusText.Text = "Drop a game executable onto the window to begin";
        StatusRight.Text = "";
    }

    static string Bytes(long n)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = n;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v.ToString(u == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[u];
    }

    void BuildCategories()
    {
        if (data == null) return;
        var cats = new List<CatRow>
        {
            new() { Cat = Cat.Sprites, Name = "Sprites", Count = data.Sprites.Count.ToString() },
            new() { Cat = Cat.Backgrounds, Name = "Backgrounds", Count = data.Backgrounds.Count.ToString() },
            new() { Cat = Cat.Fonts, Name = "Fonts", Count = data.Fonts.Count.ToString() },
            new() { Cat = Cat.Pages, Name = "Texture pages", Count = data.Pages.Count.ToString() },
            new() { Cat = Cat.Sounds, Name = "Sounds", Count = (sounds?.Entries.Count ?? data.Sounds.Count).ToString() },
            new() { Cat = Cat.Code, Name = "Code", Count = data.Code.Count.ToString() },
            new() { Cat = Cat.Scripts, Name = "Scripts", Count = data.Scripts.Count.ToString() },
            new() { Cat = Cat.Objects, Name = "Objects", Count = data.Objects.Count.ToString() },
            new() { Cat = Cat.Rooms, Name = "Rooms", Count = data.Rooms.Count.ToString() },
            new() { Cat = Cat.Strings, Name = "Strings", Count = data.Strings.Count.ToString() },
            new() { Cat = Cat.GameFiles, Name = "Game files", Count = (package?.Files.Count ?? 0).ToString() },
            new() { Cat = Cat.Info, Name = "File info", Count = "" },
        };
        if (!data.HasCode)
            cats.Insert(6, new CatRow
            {
                Cat = Cat.Native,
                Name = "Native code",
                Count = native != null ? native.Functions.Count.ToString() : "..."
            });

        int keep = CategoryList.SelectedIndex;
        CategoryList.ItemsSource = cats;
        CategoryList.SelectedIndex = keep >= 0 && keep < cats.Count ? keep : 0;
    }

    Cat CurrentCat => CategoryList.SelectedItem is CatRow c ? c.Cat : Cat.Sprites;

    void ShowCategory()
    {
        if (data == null) return;
        rows = BuildRows(CurrentCat);
        SearchBox.Text = "";
        ApplyFilter();
        ExportAllButton.IsVisible = CurrentCat is Cat.Sprites or Cat.Backgrounds or Cat.Fonts or Cat.Pages or Cat.Sounds or Cat.Code or Cat.Scripts or Cat.Strings or Cat.Native or Cat.GameFiles;
        if (CurrentCat == Cat.Info)
        {
            ItemList.ItemsSource = null;
            ShowInfo();
        }
    }

    List<Row> BuildRows(Cat cat)
    {
        var list = new List<Row>();
        if (data == null) return list;
        switch (cat)
        {
            case Cat.Sprites:
                foreach (var s in data.Sprites)
                    list.Add(new Row { Name = s.Name, Cat = cat, Index = s.Index, Detail = $"{s.Width}x{s.Height}  {s.Frames.Count} frame{(s.Frames.Count == 1 ? "" : "s")}" });
                break;
            case Cat.Backgrounds:
                foreach (var b in data.Backgrounds)
                    list.Add(new Row { Name = b.Name, Cat = cat, Index = b.Index, Detail = ItemSize(b.Item) });
                break;
            case Cat.Fonts:
                foreach (var f in data.Fonts)
                    list.Add(new Row { Name = f.Name, Cat = cat, Index = f.Index, Detail = f.DisplayName });
                break;
            case Cat.Pages:
                foreach (var p in data.Pages)
                    list.Add(new Row { Name = "Page " + p.Index, Cat = cat, Index = p.Index, Detail = p.FormatName + (p.Width > 0 ? $"  {p.Width}x{p.Height}" : "") });
                break;
            case Cat.Sounds:
                if (sounds != null)
                {
                    for (int i = 0; i < sounds.Entries.Count; i++)
                    {
                        var e = sounds.Entries[i];
                        list.Add(new Row
                        {
                            Name = e.Name,
                            Cat = cat,
                            Index = i,
                            Detail = e.Detail + "  -  " + e.Source
                        });
                    }
                }
                break;
            case Cat.Code:
                foreach (var c in data.Code)
                    list.Add(new Row { Name = c.Name, Cat = cat, Index = c.Index, Code = c, Detail = $"{c.Length} bytes" + (c.IsChild ? "  (sub-function)" : "") });
                break;
            case Cat.Native:
                if (native != null)
                {
                    foreach (var f in native.Functions.Where(f => f.Named).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                        list.Add(new Row { Name = f.ShortName, Cat = cat, Index = f.Index, Detail = $"{f.Kind}  {f.Size} bytes" });
                    foreach (var f in native.Functions.Where(f => !f.Named))
                        list.Add(new Row { Name = "sub_" + f.Address.ToString("X"), Cat = cat, Index = f.Index, Detail = $"{f.Size} bytes" });
                }
                break;
            case Cat.Scripts:
                foreach (var s in data.Scripts)
                {
                    var code = s.CodeIndex >= 0 && s.CodeIndex < data.Code.Count ? data.Code[s.CodeIndex] : null;
                    list.Add(new Row { Name = s.Name, Cat = cat, Index = s.Index, Code = code, Detail = code == null ? "no code" : s.Detail });
                }
                break;
            case Cat.Objects:
                foreach (var o in data.Objects)
                    list.Add(new Row { Name = o.Name, Cat = cat, Index = o.Index, Detail = $"{o.Events.Count} event{(o.Events.Count == 1 ? "" : "s")}" });
                break;
            case Cat.Rooms:
                foreach (var r in data.Rooms)
                    list.Add(new Row { Name = r.Name, Cat = cat, Index = r.Index, Detail = $"{r.Width}x{r.Height}  {r.Instances.Count} instances" });
                break;
            case Cat.GameFiles:
                if (package != null)
                    for (int i = 0; i < package.Files.Count; i++)
                    {
                        var f = package.Files[i];
                        list.Add(new Row
                        {
                            Name = f.RelPath,
                            Cat = cat,
                            Index = i,
                            Detail = f.KindName + "  " + Bytes(f.Size) + (f.Detail.Length > 0 ? "  " + f.Detail : "")
                        });
                    }
                break;
            case Cat.Strings:
                for (int i = 0; i < data.Strings.Count; i++)
                    list.Add(new Row { Name = Ellipsize(data.Strings[i]), Cat = cat, Index = i, Detail = "#" + i });
                break;
        }
        return list;
    }

    static string Ellipsize(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 90 ? s.Substring(0, 90) + "..." : s;
    }

    string ItemSize(int item)
    {
        if (data == null || item < 0 || item >= data.TexItems.Count) return "";
        var t = data.TexItems[item];
        return $"{t.BoundW}x{t.BoundH}  page {t.Page}";
    }

    void ApplyFilter()
    {
        string q = SearchBox.Text ?? "";
        var view = q.Length == 0 ? rows : rows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        ItemList.ItemsSource = view;
        Reveal(ItemList, 150, 4);
        StatusRight.Text = view.Count + (q.Length == 0 ? " items" : " of " + rows.Count + " items");
        if (view.Count > 0) ItemList.SelectedIndex = 0;
        else ShowNothing();
    }

    void HideAllPanes()
    {
        ImagePane.IsVisible = false;
        AudioPane.IsVisible = false;
        CodePane.IsVisible = false;
        InfoPane.IsVisible = false;
        EmptyDetail.IsVisible = false;
    }

    void ShowNothing()
    {
        selected = null;
        HideAllPanes();
        EmptyDetail.IsVisible = true;
        DetailTitle.Text = "";
        DetailSub.Text = "";
        ExportButton.IsVisible = false;
    }

    void ShowSelection()
    {
        if (data == null) return;
        if (ItemList.SelectedItem is not Row row) { if (CurrentCat != Cat.Info) ShowNothing(); return; }
        selected = row;
        HideAllPanes();
        ExportButton.IsVisible = true;
        DetailTitle.Text = row.Name;
        DetailSub.Text = "";

        switch (row.Cat)
        {
            case Cat.Sprites: ShowSprite(data.Sprites[row.Index]); break;
            case Cat.Backgrounds: ShowSingleImage(data.Backgrounds[row.Index].Item, "background"); break;
            case Cat.Fonts: ShowFont(data.Fonts[row.Index]); break;
            case Cat.Pages: ShowPage(row.Index); break;
            case Cat.Sounds: ShowSound(row.Index); break;
            case Cat.Code:
            case Cat.Scripts: ShowCode(row.Code); break;
            case Cat.Native: ShowNative(row.Index); break;
            case Cat.Objects: ShowObject(data.Objects[row.Index]); break;
            case Cat.Rooms: ShowRoom(data.Rooms[row.Index]); break;
            case Cat.Strings: ShowString(row.Index); break;
            case Cat.GameFiles: ShowPackageFile(row.Index); break;
        }
        Reveal(DetailBody);
    }

    void ShowSprite(GmSprite s)
    {
        frames = s.Frames;
        frameIndex = 0;
        DetailSub.Text = $"{s.Width}x{s.Height}  origin {s.OriginX},{s.OriginY}  " + $"bbox {s.MarginLeft},{s.MarginTop} to {s.MarginRight},{s.MarginBottom}  " + $"{s.Frames.Count} frames" + (s.Note != null ? "  -  " + s.Note : "");
        ImagePane.IsVisible = true;
        RenderFrame(true);
    }

    void ShowFont(GmFont f)
    {
        frames = f.Item >= 0 ? new List<int> { f.Item } : new List<int>();
        frameIndex = 0;
        DetailSub.Text = f.DisplayName;
        ImagePane.IsVisible = true;
        RenderFrame(true);
    }

    void ShowSingleImage(int item, string kind)
    {
        frames = item >= 0 ? new List<int> { item } : new List<int>();
        frameIndex = 0;
        DetailSub.Text = ItemSize(item);
        ImagePane.IsVisible = true;
        RenderFrame(true);
    }

    void ShowPage(int index)
    {
        if (data == null || images == null) return;
        frames = new List<int>();
        frameIndex = 0;
        var p = data.Pages[index];
        ImagePane.IsVisible = true;
        var img = images.Page(index);
        shownImage = img;
        if (img == null)
        {
            Preview.Source = null;
            DetailSub.Text = p.FormatName + " - could not decode: " + (images.PageError(index) ?? p.Error ?? "unknown format");
        }
        else
        {
            Preview.Source = Images.ToBitmap(img);
            DetailSub.Text = $"{img.Width}x{img.Height}  {p.FormatName}  at offset 0x{p.DataOffset:X}";
            Reveal(CheckerBorder, 200, 0);
        }
        FrameLabel.Text = "whole page";
        PrevFrame.IsEnabled = NextFrame.IsEnabled = false;
        FitZoom();
        UpdateChecker();
    }

    void RenderFrame(bool fit)
    {
        if (images == null) return;
        PrevFrame.IsEnabled = NextFrame.IsEnabled = frames.Count > 1;
        if (frames.Count == 0)
        {
            shownImage = null;
            Preview.Source = null;
            FrameLabel.Text = "no image";
            return;
        }
        frameIndex = Math.Clamp(frameIndex, 0, frames.Count - 1);
        var img = images.Item(frames[frameIndex]);
        shownImage = img;
        Preview.Source = img == null ? null : Images.ToBitmap(img);
        Reveal(CheckerBorder, 200, 0);
        FrameLabel.Text = frames.Count > 1 ? $"frame {frameIndex + 1} / {frames.Count}" : (img != null ? $"{img.Width}x{img.Height}" : "no image");
        if (fit) FitZoom(); else ApplyZoom();
        UpdateChecker();
    }

    void StepFrame(int delta)
    {
        if (frames.Count == 0) return;
        frameIndex = (frameIndex + delta + frames.Count) % frames.Count;
        RenderFrame(false);
    }

    void SetZoom(double z)
    {
        zoom = Math.Clamp(z, 0.05, 32);
        ApplyZoom();
    }

    void FitZoom()
    {
        if (shownImage == null) { zoom = 1; ApplyZoom(); return; }
        if (ImageScroll.Bounds.Width < 32)
        {
            Dispatcher.UIThread.Post(FitZoom, DispatcherPriority.Loaded);
            return;
        }
        double aw = Math.Max(64, ImageScroll.Bounds.Width - 60);
        double ah = Math.Max(64, ImageScroll.Bounds.Height - 60);
        double fit = Math.Min(aw / Math.Max(1, shownImage.Width), ah / Math.Max(1, shownImage.Height));
        zoom = fit >= 1 ? Math.Max(1, Math.Floor(Math.Min(fit, 8))) : fit;
        ApplyZoom();
    }

    void ApplyZoom()
    {
        if (shownImage == null) { ZoomLabel.Text = "-"; return; }
        RenderOptions.SetBitmapInterpolationMode(Preview, zoom >= 1 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        Preview.Width = Math.Max(1, shownImage.Width * zoom);
        Preview.Height = Math.Max(1, shownImage.Height * zoom);
        ZoomLabel.Text = (zoom * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    void UpdateChecker()
    {
        bool on = ShowChecker.IsChecked == true && shownImage != null;
        CheckerBorder.Background = on ? CheckerBrush() : null;
    }

    IBrush? checkerBrush;

    IBrush CheckerBrush()
    {
        if (checkerBrush != null) return checkerBrush;
        var img = new RawImage { Width = 2, Height = 2, Bgra = new byte[16] };
        void Set(int i, byte v) { img.Bgra[i * 4] = v; img.Bgra[i * 4 + 1] = v; img.Bgra[i * 4 + 2] = v; img.Bgra[i * 4 + 3] = 255; }
        Set(0, 0x1A); Set(1, 0x26); Set(2, 0x26); Set(3, 0x1A);
        checkerBrush = new ImageBrush(Images.ToBitmap(img))
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
            DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute)
        };
        return checkerBrush;
    }

    void ShowSound(int index)
    {
        if (sounds == null || index < 0 || index >= sounds.Entries.Count) return;
        var e = sounds.Entries[index];
        AudioPane.IsVisible = true;
        player.Stop();
        AudioFacts.Children.Clear();
        AudioError.IsVisible = false;
        DetailTitle.Text = e.Name;
        DetailSub.Text = e.Detail + "  -  " + e.Source;

        if (e.FromBank)
        {
            Fact(AudioFacts, "Bank", e.Source);
            Fact(AudioFacts, "Codec", e.Section!.CodecName);
            Fact(AudioFacts, "Channels", e.Sample!.Channels.ToString());
            Fact(AudioFacts, "Sample rate", e.Sample.Frequency + " Hz");
            Fact(AudioFacts, "Length", e.Sample.Seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s  (" + e.Sample.Samples.ToString("N0") + " samples)");
            Fact(AudioFacts, "Compressed size", Bytes(e.Sample.Size));
        }
        else
        {
            var a = e.Archive!;
            Fact(AudioFacts, "File name", a.File);
            Fact(AudioFacts, "Type", a.Type.Length > 0 ? a.Type : "-");
            Fact(AudioFacts, "Volume", a.Volume.ToString("0.##", CultureInfo.InvariantCulture));
            Fact(AudioFacts, "Pitch", a.Pitch.ToString("0.##", CultureInfo.InvariantCulture));
            Fact(AudioFacts, "Audio group", a.GroupId.ToString());
            Fact(AudioFacts, "Storage", a.Embedded ? "embedded" : "streamed");
        }

        PlayButton.IsEnabled = StopButton.IsEnabled = SeekSlider.IsEnabled = true;
        TimeLabel.Text = "0:00 / 0:00";
        SeekSlider.Value = 0;
        _ = LoadSound(e);
    }

    async Task LoadSound(SoundEntry e)
    {
        var lib = sounds;
        if (lib == null) return;
        if (e.FromBank) Status("Decoding " + e.Name + "...");

        byte[]? bytes = null;
        string container = "?";
        try
        {
            var result = await Task.Run(() =>
            {
                var b = lib.Audio(e, out string c);
                return (b, c);
            });
            bytes = result.b;
            container = result.c;
        }
        catch (Exception ex) { lib.FmodNote = ex.Message; }

        if (sounds != lib || selected == null) return;

        if (bytes == null)
        {
            PlayButton.IsEnabled = false;
            AudioError.Text = e.FromBank ? (lib.FmodNote ?? "This sample could not be decoded.") : "This sound has no audio data in the file.";
            AudioError.IsVisible = true;
            Status("");
            return;
        }

        if (!player.Load(bytes, container))
        {
            PlayButton.IsEnabled = false;
            AudioError.Text = "Could not decode this sound: " + player.Error;
            AudioError.IsVisible = true;
        }
        else
        {
            player.Volume = (float)VolumeSlider.Value;
            PlayButton.IsEnabled = true;
        }
        if (e.FromBank) Status(lib.FmodNote ?? "");
        UpdateAudioUi();
    }

    void UpdateAudioUi()
    {
        PlayButton.Content = player.IsPlaying ? "Pause" : "Play";
        UpdateAudioClock();
    }

    void UpdateAudioClock()
    {
        if (!AudioPane.IsVisible || !player.HasTrack) return;
        var pos = player.Position;
        var dur = player.Duration;
        TimeLabel.Text = Clock(pos) + " / " + Clock(dur);
        if (!seekDragging && dur > TimeSpan.Zero)
            SeekSlider.Value = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds * 1000.0, 0, 1000);
    }

    static string Clock(TimeSpan t) => ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");

    void UpdateCodeTabs()
    {
        TabDecompiled.Classes.Set("on", !showDisassembly);
        TabDisassembly.Classes.Set("on", showDisassembly);
    }

    void ShowCode(GmCode? code)
    {
        CodePane.IsVisible = true;
        TabDecompiled.Content = "Decompiled";
        TabDisassembly.Content = "Disassembly";
        UpdateCodeTabs();
        if (data == null || code == null)
        {
            CodeText.Text = data != null && !data.HasCode ? "// This game ships compiled native code (YYC), so there is no bytecode to read." : "// No code is attached to this entry.";
            DetailSub.Text = "";
            return;
        }
        DetailSub.Text = $"{code.Length} bytes  -  {code.Locals} locals  -  {code.Args} arguments" + (code.IsChild ? "  -  sub-function at offset " + code.Offset : "");
        CodeText.Text = showDisassembly ? Bytecode.Disassemble(data, code) : Decompiler.Decompile(data, code);
    }

    void ShowNative(int index)
    {
        CodePane.IsVisible = true;
        TabDecompiled.Content = "Pseudo-code";
        TabDisassembly.Content = "Disassembly";
        UpdateCodeTabs();
        if (native == null || index < 0 || index >= native.Functions.Count)
        {
            CodeText.Text = "// " + nativeNote;
            return;
        }
        var f = native.Functions[index];
        DetailTitle.Text = f.Named ? f.ShortName : "sub_" + f.Address.ToString("X");
        DetailSub.Text = $"{f.Kind}  -  0x{f.Address:X}  -  {f.Size} bytes  -  {(native.Is64 ? "x86-64" : "x86")}" + (f.Named && f.Name != f.ShortName ? "  -  " + f.Name : "");
        CodeText.Text = native.Text(f.Index, !showDisassembly);
    }

    void ShowObject(GmObject o)
    {
        if (data == null) return;
        InfoPane.IsVisible = true;
        InfoStack.Children.Clear();
        DetailSub.Text = o.Events.Count + " events";

        Fact(InfoStack, "Sprite", o.SpriteIndex >= 0 && o.SpriteIndex < data.Sprites.Count ? data.Sprites[o.SpriteIndex].Name : "-");
        Fact(InfoStack, "Parent", o.ParentIndex >= 0 && o.ParentIndex < data.Objects.Count ? data.Objects[o.ParentIndex].Name : "-");
        Fact(InfoStack, "Depth", o.Depth.ToString());
        Fact(InfoStack, "Visible", o.Visible ? "yes" : "no");
        Fact(InfoStack, "Solid", o.Solid ? "yes" : "no");
        Fact(InfoStack, "Persistent", o.Persistent ? "yes" : "no");

        if (o.Events.Count > 0)
        {
            InfoStack.Children.Add(new TextBlock
            {
                Text = "Events",
                Margin = new Thickness(0, 14, 0, 4),
                FontWeight = FontWeight.SemiBold
            });
            var wrap = new WrapPanel();
            foreach (var e in o.Events)
            {
                var b = new Button { Content = EventLabel(o, e), Margin = new Thickness(0, 0, 6, 6) };
                var captured = e;
                b.Click += (_, _) => JumpToCode(captured);
                wrap.Children.Add(b);
            }
            InfoStack.Children.Add(wrap);
        }
    }

    static string EventLabel(GmObject o, GmCode c)
    {
        string prefix = "gml_Object_" + o.Name + "_";
        return c.Name.StartsWith(prefix, StringComparison.Ordinal) ? c.Name.Substring(prefix.Length) : c.Name;
    }

    void JumpToCode(GmCode code)
    {
        if (CategoryList.ItemsSource is not IEnumerable<CatRow> cats) return;
        var target = cats.FirstOrDefault(c => c.Cat == Cat.Code);
        if (target == null) return;
        CategoryList.SelectedItem = target;
        SearchBox.Text = code.Name;
        ApplyFilter();
        if (ItemList.ItemsSource is IEnumerable<Row> view)
        {
            var hit = view.FirstOrDefault(r => r.Code == code);
            if (hit != null) ItemList.SelectedItem = hit;
        }
    }

    void ShowRoom(GmRoom r)
    {
        if (data == null) return;
        InfoPane.IsVisible = true;
        InfoStack.Children.Clear();
        DetailSub.Text = $"{r.Width}x{r.Height}";

        Fact(InfoStack, "Caption", r.Caption.Length > 0 ? r.Caption : "-");
        Fact(InfoStack, "Size", r.Width + " x " + r.Height);
        Fact(InfoStack, "Speed", r.Speed.ToString());
        Fact(InfoStack, "Persistent", r.Persistent ? "yes" : "no");
        Fact(InfoStack, "Background", "#" + (r.BgColor & 0xFFFFFF).ToString("X6"));
        Fact(InfoStack, "Instances", r.Instances.Count.ToString());
        if (r.Note != null) Fact(InfoStack, "Note", r.Note);

        if (r.CreationCodeId >= 0 && r.CreationCodeId < data.Code.Count)
        {
            var code = data.Code[r.CreationCodeId];
            var b = new Button { Content = "Creation code", Margin = new Thickness(0, 10, 0, 0) };
            b.Click += (_, _) => JumpToCode(code);
            InfoStack.Children.Add(b);
        }

        if (r.Instances.Count > 0)
        {
            InfoStack.Children.Add(new TextBlock
            {
                Text = "Instances",
                Margin = new Thickness(0, 14, 0, 4),
                FontWeight = FontWeight.SemiBold
            });
            var sb = new StringBuilder();
            foreach (var inst in r.Instances.Take(400))
            {
                string name = inst.ObjectIndex >= 0 && inst.ObjectIndex < data.Objects.Count ? data.Objects[inst.ObjectIndex].Name : "object " + inst.ObjectIndex;
                sb.Append(name).Append("  at ").Append(inst.X.ToString("0", CultureInfo.InvariantCulture)).Append(", ").Append(inst.Y.ToString("0", CultureInfo.InvariantCulture)).Append('\n');
            }
            if (r.Instances.Count > 400) sb.Append("... and ").Append(r.Instances.Count - 400).Append(" more\n");
            InfoStack.Children.Add(new TextBox
            {
                Text = sb.ToString(),
                IsReadOnly = true,
                AcceptsReturn = true,
                Height = 320,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                FontSize = 12
            });
        }
    }

    void ShowString(int index)
    {
        if (data == null) return;
        InfoPane.IsVisible = true;
        InfoStack.Children.Clear();
        DetailTitle.Text = "String #" + index;
        DetailSub.Text = data.Strings[index].Length + " characters";
        InfoStack.Children.Add(new TextBox
        {
            Text = data.Strings[index],
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 200,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace")
        });
    }

    void ShowPackageFile(int index)
    {
        if (package == null || index < 0 || index >= package.Files.Count) return;
        var f = package.Files[index];
        DetailTitle.Text = f.Name;
        DetailSub.Text = f.KindName + "  -  " + Bytes(f.Size) + (f.IsEmbedded ? "  -  inside " + Path.GetFileName(f.FullPath) : "  -  " + f.RelPath);

        switch (f.Kind)
        {
            case FileKind.Image:
                ShowFileImage(f);
                return;
            case FileKind.Bank:
                ShowBank(f);
                return;
            case FileKind.Text:
                ShowTextFile(f);
                return;
        }

        if (f.IsEmbedded && f.Bytes != null)
        {
            if (f.Detail.Contains("image", StringComparison.OrdinalIgnoreCase)) { ShowFileImage(f); return; }
            if (f.Detail is "JSON" or "XML / markup") { ShowTextFile(f); return; }
        }

        InfoPane.IsVisible = true;
        InfoStack.Children.Clear();
        Fact(InfoStack, "Type", f.KindName + (f.Detail.Length > 0 ? " (" + f.Detail + ")" : ""));
        Fact(InfoStack, "Size", Bytes(f.Size) + "  (" + f.Size.ToString("N0") + " bytes)");
        Fact(InfoStack, "Location", f.IsEmbedded ? Path.GetFileName(f.FullPath) + " at 0x" + f.SourceOffset.ToString("X") : f.FullPath);

        if (f.Kind is FileKind.Library or FileKind.Executable && !f.IsEmbedded)
        {
            var pe = PeFile.Read(f.FullPath);
            if (pe != null)
            {
                Fact(InfoStack, "Architecture", pe.Is64 ? "x86-64" : "x86");
                Fact(InfoStack, "Kind", pe.IsDll ? "library" : "program");
                Fact(InfoStack, "Built", pe.Timestamp.ToString("yyyy-MM-dd HH:mm"));
                Fact(InfoStack, "Sections", string.Join(", ", pe.Sections.Select(x => x.Name)));
                Fact(InfoStack, "Resources", pe.Resources.Count.ToString());
                if (pe.OverlaySize > 0) Fact(InfoStack, "Appended data", Bytes(pe.OverlaySize));
            }
        }

        InfoStack.Children.Add(new TextBlock
        {
            Text = "First bytes",
            Margin = new Thickness(0, 14, 0, 4),
            FontWeight = FontWeight.SemiBold
        });
        InfoStack.Children.Add(new TextBox
        {
            Text = HexDump(f),
            IsReadOnly = true,
            AcceptsReturn = true,
            Height = 260,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 12
        });
    }

    void ShowFileImage(PackageFile f)
    {
        frames = new List<int>();
        frameIndex = 0;
        ImagePane.IsVisible = true;
        PrevFrame.IsEnabled = NextFrame.IsEnabled = false;
        FrameLabel.Text = "";
        try
        {
            using var ms = new MemoryStream(f.Read());
            var bmp = new Bitmap(ms);
            shownImage = null;
            Preview.Source = bmp;
            Preview.Width = bmp.PixelSize.Width;
            Preview.Height = bmp.PixelSize.Height;
            ZoomLabel.Text = "100%";
            FrameLabel.Text = bmp.PixelSize.Width + "x" + bmp.PixelSize.Height;
            Reveal(CheckerBorder, 200, 0);
        }
        catch (Exception e)
        {
            Preview.Source = null;
            FrameLabel.Text = "cannot decode: " + e.Message;
        }
        UpdateChecker();
    }

    void ShowTextFile(PackageFile f)
    {
        CodePane.IsVisible = true;
        TabDecompiled.Content = "Contents";
        TabDisassembly.Content = "Bytes";
        UpdateCodeTabs();
        try
        {
            var bytes = f.Read();
            if (showDisassembly) CodeText.Text = HexDump(f);
            else
            {
                int max = Math.Min(bytes.Length, 2_000_000);
                CodeText.Text = Encoding.UTF8.GetString(bytes, 0, max) + (bytes.Length > max ? "\n\n... truncated" : "");
            }
        }
        catch (Exception e) { CodeText.Text = "// " + e.Message; }
    }

    void ShowBank(PackageFile f)
    {
        CodePane.IsVisible = true;
        TabDecompiled.Content = "Contents";
        TabDisassembly.Content = "Bytes";
        UpdateCodeTabs();
        if (showDisassembly) { CodeText.Text = HexDump(f); return; }

        if (!bankCache.TryGetValue(f.FullPath, out var bank))
        {
            CodeText.Text = "// reading " + f.Name + "...";
            bank = Bank.Read(f.FullPath);
            bankCache[f.FullPath] = bank;
        }
        DetailSub.Text = f.KindName + "  -  " + Bytes(f.Size) + "  -  " + bank.SampleCount + " samples";
        CodeText.Text = bank.Describe();
    }

    static string HexDump(PackageFile f)
    {
        byte[] head;
        try
        {
            if (f.Bytes != null) head = f.Bytes;
            else
            {
                using var fs = File.OpenRead(f.FullPath);
                head = new byte[(int)Math.Min(4096, fs.Length)];
                fs.ReadExactly(head, 0, head.Length);
            }
        }
        catch (Exception e) { return e.Message; }

        int n = Math.Min(head.Length, 4096);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i += 16)
        {
            sb.Append(i.ToString("X8")).Append("  ");
            for (int k = 0; k < 16; k++)
                sb.Append(i + k < n ? head[i + k].ToString("X2") + " " : "   ");
            sb.Append(' ');
            for (int k = 0; k < 16 && i + k < n; k++)
            {
                byte c = head[i + k];
                sb.Append(c >= 32 && c < 127 ? (char)c : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    void ShowInfo()
    {
        if (data == null) return;
        HideAllPanes();
        InfoPane.IsVisible = true;
        ExportButton.IsVisible = false;
        InfoStack.Children.Clear();
        DetailTitle.Text = data.FileName;
        DetailSub.Text = data.Path;

        Fact(InfoStack, "Game", data.GameName);
        Fact(InfoStack, "Display name", data.DisplayName);
        Fact(InfoStack, "Runtime", data.Version);
        Fact(InfoStack, "Bytecode", data.BytecodeVersion.ToString());
        Fact(InfoStack, "Window", data.WindowWidth + " x " + data.WindowHeight);
        Fact(InfoStack, "Built", data.Timestamp == default ? "-" : data.Timestamp.ToString("yyyy-MM-dd HH:mm"));
        Fact(InfoStack, "File size", Bytes(data.Raw.Length));
        Fact(InfoStack, "Audio groups", string.Join(", ", data.AudioGroups.Select(a => a.Name)));
        if (sounds != null && sounds.BankSampleCount > 0)
            Fact(InfoStack, "FMOD banks", sounds.BankCount + " banks, " + sounds.BankSampleCount + " samples");
        if (package != null)
        {
            Fact(InfoStack, "Game folder", package.Root);
            Fact(InfoStack, "Executable", package.ExePath ?? "-");
            Fact(InfoStack, "Files", package.Files.Count + " found");
            foreach (var note in package.Notes) Fact(InfoStack, "Note", note);
        }

        InfoStack.Children.Add(new TextBlock { Text = "Chunks", Margin = new Thickness(0, 14, 0, 4), FontWeight = FontWeight.SemiBold });
        var sb = new StringBuilder();
        foreach (var c in data.ChunkList)
            sb.Append(c.Name).Append("   ").Append(Bytes(c.Length).PadLeft(9)).Append("   at 0x").Append(c.Offset.ToString("X")).Append('\n');
        InfoStack.Children.Add(new TextBox
        {
            Text = sb.ToString(),
            IsReadOnly = true,
            AcceptsReturn = true,
            Height = 220,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 12
        });

        if (!data.HasCode)
        {
            InfoStack.Children.Add(new TextBlock { Text = "Native code", Margin = new Thickness(0, 14, 0, 4), FontWeight = FontWeight.SemiBold });
            InfoStack.Children.Add(new TextBox
            {
                Text = (native?.Summary ?? "") + (nativeNote.Length > 0 ? "\n" + nativeNote : ""),
                IsReadOnly = true,
                AcceptsReturn = true,
                MaxHeight = 260,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                FontSize = 12
            });
        }

        if (data.Warnings.Count > 0)
        {
            InfoStack.Children.Add(new TextBlock { Text = "Notes", Margin = new Thickness(0, 14, 0, 4), FontWeight = FontWeight.SemiBold });
            InfoStack.Children.Add(new TextBox
            {
                Text = string.Join("\n", data.Warnings),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 220,
                FontSize = 12
            });
        }
    }

    void Fact(Panel host, string key, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*") };
        var k = new TextBlock { Text = key, FontSize = 12, Foreground = Brush("Muted") };
        var v = new TextBlock { Text = value, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v, 1);
        grid.Children.Add(k);
        grid.Children.Add(v);
        host.Children.Add(grid);
    }

    async Task<string?> AskSave(string suggested, string ext, string label)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggested,
            DefaultExtension = ext,
            FileTypeChoices = new[] { new FilePickerFileType(label) { Patterns = new[] { "*." + ext } } }
        });
        return file?.TryGetLocalPath();
    }

    async Task<string?> AskFolder(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    static string Safe(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        string s = sb.ToString().Trim();
        return s.Length == 0 ? "unnamed" : s;
    }

    async Task ExportSelected()
    {
        if (data == null || images == null || selected == null) return;
        try
        {
            switch (selected.Cat)
            {
                case Cat.Sprites:
                    {
                        var s = data.Sprites[selected.Index];
                        if (s.Frames.Count > 1)
                        {
                            string? dir = await AskFolder("Export " + s.Frames.Count + " frames of " + s.Name);
                            if (dir == null) return;
                            for (int i = 0; i < s.Frames.Count; i++)
                            {
                                var img = images.Item(s.Frames[i]);
                                if (img != null) File.WriteAllBytes(Path.Combine(dir, Safe(s.Name) + "_" + i + ".png"), Png.Encode(img));
                            }
                            Status("Exported " + s.Frames.Count + " frames to " + dir);
                        }
                        else await SaveImage(shownImage, s.Name);
                        break;
                    }
                case Cat.Backgrounds:
                case Cat.Fonts:
                case Cat.Pages:
                    await SaveImage(shownImage, selected.Name);
                    break;
                case Cat.Sounds:
                    {
                        if (sounds == null) return;
                        var e = sounds.Entries[selected.Index];
                        string ext = e.Extension;
                        string? path = await AskSave(Safe(e.Name) + "." + ext, ext, ext.ToUpperInvariant() + " audio");
                        if (path == null) return;
                        Status("Preparing " + e.Name + "...");
                        var lib = sounds;
                        var bytes = await Task.Run(() => lib.Audio(e, out _));
                        if (bytes == null) { Status(lib.FmodNote ?? "That sound could not be exported."); return; }
                        File.WriteAllBytes(path, bytes);
                        Status("Saved " + path);
                        break;
                    }
                case Cat.GameFiles:
                    {
                        if (package == null) return;
                        var f = package.Files[selected.Index];
                        string ext = Path.GetExtension(f.Name).TrimStart('.');
                        if (ext.Length == 0) ext = "bin";
                        string? fp = await AskSave(Safe(Path.GetFileNameWithoutExtension(f.Name)) + "." + ext, ext, f.KindName);
                        if (fp == null) return;
                        File.WriteAllBytes(fp, f.Read());
                        Status("Saved " + fp);
                        break;
                    }
                case Cat.Native:
                    {
                        string? np = await AskSave(Safe(selected.Name) + ".txt", "txt", "Text");
                        if (np == null) return;
                        File.WriteAllText(np, CodeText.Text ?? "");
                        Status("Saved " + np);
                        break;
                    }
                case Cat.Code:
                case Cat.Scripts:
                    {
                        var code = selected.Code;
                        if (code == null) return;
                        string? path = await AskSave(Safe(selected.Name) + ".gml", "gml", "GML source");
                        if (path == null) return;
                        File.WriteAllText(path, CodeText.Text ?? "");
                        Status("Saved " + path);
                        break;
                    }
                case Cat.Rooms:
                case Cat.Objects:
                case Cat.Strings:
                    {
                        string? path = await AskSave(Safe(selected.Name) + ".txt", "txt", "Text");
                        if (path == null) return;
                        File.WriteAllText(path, DescribeSelection());
                        Status("Saved " + path);
                        break;
                    }
            }
        }
        catch (Exception e) { Status("Export failed: " + e.Message); }
    }

    string DescribeSelection()
    {
        if (data == null || selected == null) return "";
        switch (selected.Cat)
        {
            case Cat.Strings: return data.Strings[selected.Index];
            case Cat.Objects:
                {
                    var o = data.Objects[selected.Index];
                    var sb = new StringBuilder();
                    sb.Append(o.Name).Append('\n');
                    foreach (var e in o.Events) sb.Append("  ").Append(e.Name).Append('\n');
                    return sb.ToString();
                }
            case Cat.Rooms:
                {
                    var r = data.Rooms[selected.Index];
                    var sb = new StringBuilder();
                    sb.Append(r.Name).Append("  ").Append(r.Width).Append('x').Append(r.Height).Append('\n');
                    foreach (var i in r.Instances)
                    {
                        string name = i.ObjectIndex >= 0 && i.ObjectIndex < data.Objects.Count ? data.Objects[i.ObjectIndex].Name : "?";
                        sb.Append(name).Append('\t').Append(i.X).Append('\t').Append(i.Y).Append('\n');
                    }
                    return sb.ToString();
                }
        }
        return "";
    }

    async Task SaveImage(RawImage? img, string name)
    {
        if (img == null) { Status("Nothing to export."); return; }
        string? path = await AskSave(Safe(name) + ".png", "png", "PNG image");
        if (path == null) return;
        File.WriteAllBytes(path, Png.Encode(img));
        Status("Saved " + path);
    }

    async Task ExportCategory()
    {
        if (data == null || images == null || busy) return;
        var cat = CurrentCat;
        string? dir = await AskFolder("Choose a folder to export into");
        if (dir == null) return;

        busy = true;
        ExportAllButton.IsEnabled = false;
        var d = data;
        var im = images;
        try
        {
            if (cat == Cat.Sounds)
            {
                var lib = sounds;
                if (lib == null) return;
                int saved = await Task.Run(() =>
                {
                    int k = 0, failed = 0;
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in lib.Entries)
                    {
                        byte[]? bytes;
                        try { bytes = lib.Audio(e, out _); }
                        catch { bytes = null; }
                        if (bytes == null) { failed++; continue; }
                        string name = Safe(e.Name);
                        string target = Path.Combine(dir, name + "." + e.Extension);
                        for (int i = 2; !used.Add(target); i++)
                            target = Path.Combine(dir, name + "_" + i + "." + e.Extension);
                        File.WriteAllBytes(target, bytes);
                        k++;
                        if (k % 10 == 0)
                        {
                            int done = k;
                            Dispatcher.UIThread.Post(() => Status("Exporting sounds... " + done));
                        }
                    }
                    return k;
                });
                Status("Exported " + saved + " sounds to " + dir);
                return;
            }

            if (cat == Cat.GameFiles)
            {
                var pkg = package;
                if (pkg == null) return;
                int saved = await Task.Run(() =>
                {
                    int k = 0;
                    foreach (var f in pkg.Files)
                    {
                        try
                        {
                            string target = Path.Combine(dir, Safe(f.RelPath.Replace('/', '_').Replace('\\', '_').Replace(':', '_')));
                            File.WriteAllBytes(target, f.Read());
                            k++;
                            if (k % 20 == 0) Dispatcher.UIThread.Post(() => Status("Copying game files... " + k));
                        }
                        catch { }
                    }
                    return k;
                });
                Status("Exported " + saved + " files to " + dir);
                return;
            }

            if (cat == Cat.Native)
            {
                var nc = native;
                if (nc == null) { Status("Native code is still loading."); return; }
                int done = await Task.Run(() =>
                {
                    int k = 0;
                    foreach (var f in nc.Functions.Where(f => f.Named))
                    {
                        File.WriteAllText(Path.Combine(dir, Safe(f.ShortName) + ".txt"), nc.Text(f.Index, true));
                        k++;
                        if (k % 100 == 0) Dispatcher.UIThread.Post(() => Status("Writing native functions... " + k));
                    }
                    return k;
                });
                Status("Exported " + done + " functions to " + dir);
                return;
            }
            int written = await Task.Run(() => ExportAllCore(d, im, cat, dir, msg => Dispatcher.UIThread.Post(() => Status(msg))));
            Status("Exported " + written + " files to " + dir);
        }
        catch (Exception e) { Status("Export failed: " + e.Message); }
        finally
        {
            busy = false;
            ExportAllButton.IsEnabled = true;
        }
    }

    static int ExportAllCore(GmData d, Images im, Cat cat, string dir, Action<string> report)
    {
        int n = 0;
        switch (cat)
        {
            case Cat.Sprites:
                foreach (var s in d.Sprites)
                {
                    for (int i = 0; i < s.Frames.Count; i++)
                    {
                        var img = im.Item(s.Frames[i]);
                        if (img == null) continue;
                        string name = Safe(s.Name) + (s.Frames.Count > 1 ? "_" + i : "") + ".png";
                        File.WriteAllBytes(Path.Combine(dir, name), Png.Encode(img));
                        n++;
                    }
                    if (n % 50 == 0) report("Exporting sprites... " + n + " files");
                }
                break;
            case Cat.Backgrounds:
                foreach (var b in d.Backgrounds)
                {
                    var img = im.Item(b.Item);
                    if (img == null) continue;
                    File.WriteAllBytes(Path.Combine(dir, Safe(b.Name) + ".png"), Png.Encode(img));
                    n++;
                }
                break;
            case Cat.Fonts:
                foreach (var f in d.Fonts)
                {
                    var img = im.Item(f.Item);
                    if (img == null) continue;
                    File.WriteAllBytes(Path.Combine(dir, Safe(f.Name) + ".png"), Png.Encode(img));
                    n++;
                }
                break;
            case Cat.Pages:
                foreach (var p in d.Pages)
                {
                    var img = im.Page(p.Index);
                    if (img == null) continue;
                    File.WriteAllBytes(Path.Combine(dir, "page_" + p.Index + ".png"), Png.Encode(img));
                    n++;
                    report("Exporting texture pages... " + n);
                }
                break;
            case Cat.Sounds:
                break;
            case Cat.Code:
            case Cat.Scripts:
                foreach (var c in d.Code)
                {
                    File.WriteAllText(Path.Combine(dir, Safe(c.Name) + ".gml"), Decompiler.Decompile(d, c));
                    n++;
                    if (n % 100 == 0) report("Decompiling... " + n + " files");
                }
                break;
            case Cat.Strings:
                File.WriteAllLines(Path.Combine(dir, "strings.txt"), d.Strings);
                n = 1;
                break;
        }
        return n;
    }

    void Status(string s) => StatusText.Text = s;
}