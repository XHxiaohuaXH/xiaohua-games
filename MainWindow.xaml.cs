using System.Windows.Documents;
using Microsoft.Win32;
using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using XiaohuaGames.Models;
using XiaohuaGames.Services;

namespace XiaohuaGames;

public partial class MainWindow : Window
{
    private void GameRowGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Grid grid = sender as Grid;

        if (grid == null || grid.ColumnDefinitions.Count <= 6)
            return;

        Button unzipButton = null;

        foreach (var child in grid.Children)
        {
            Button button = child as Button;

            if (button != null && Grid.GetColumn(button) == 6)
            {
                unzipButton = button;
                break;
            }
        }

        if (unzipButton == null)
            return;

        // 获取解压码所在列的实际宽度，并扣除按钮左右边距
        double availableWidth = Math.Max(
            0,
            grid.ColumnDefinitions[6].ActualWidth
            - unzipButton.Margin.Left
            - unzipButton.Margin.Right
        );

        // 空间充足时最小宽度为 110，空间不足时自动缩小
        unzipButton.MinWidth = Math.Min(110, availableWidth);

        // 最大宽度跟随当前列的可用空间，不固定为 110
        unzipButton.MaxWidth = availableWidth;
    }

    private readonly GameDataService _gamesService = new();
    private readonly PersonalDataService _personalService = new();
    private readonly SystemShareService _systemShare = new();
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private PersonalDataService.Data _personal = new();
    private readonly ObservableCollection<Game> _visible = new();
    private List<Game> _allGames = [];
    private List<GameDataService.CustomerService> _customerServices = [];
    private string? _dataUpdateTime;
    private string _filter = "all";
    private int _page = 1;
    private bool _historyManage;
    private string _activeSortValue = "default";
    private bool _loading;
    private bool _resettingSortBox;
    private ScrollViewer? _gameListScrollViewer;
    private Window? _toastWindow;
    private DispatcherTimer? _toastTimer;
    private const int FreshDays = 15;
    private const string DefaultPassword = "小花123321";
    private const string HomeUrl = "https://vlink.cc/xhfx";
    private const string BackupUrl = "https://docs.qq.com/doc/DU3lxQ2F6RERqemVQ";
    private const string FatalUrl = "https://docs.qq.com/doc/DU2tKclVXU3VSZGVo";

    public MainWindow()
    {
        InitializeComponent();
        ModalBackdrop.Initialize(this);
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _page = 1; Render(); };
        SearchBox.PreviewKeyDown += SearchBox_KeyDown;
        Closed += (_, _) => _searchTimer.Stop();
        GameList.ItemsSource = _visible;
        // 游戏列表数量有限（每页最多 50 条），使用像素滚动以获得连续滚动手感。
        ScrollViewer.SetCanContentScroll(GameList, false);
        GameList.SizeChanged += (_, _) =>
        {
            if (_gameListScrollViewer is not null)
                UpdateBackToTop(_gameListScrollViewer.VerticalOffset, _gameListScrollViewer.ScrollableHeight);
        };
        SortBox.SelectedIndex = -1;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _personal = await _personalService.LoadAsync();
            // 每次启动只重置排序状态，不影响收藏、已玩、浏览记录及显示设置。
            _activeSortValue = "default";
            _personal.Sort.ActiveSortValue = "default";
            _personal.Sort.LastSortValue = "default";
            foreach (var key in new[] { "name", "stars", "size", "date" }) _personal.Sort.Directions[key] = 0;
            UpdateSortLabel();
            ApplySettings();
        }
        catch (Exception ex) { MessageBox.Show("个人数据读取失败，已使用空白会话。\n" + ex.Message, "小花游戏", MessageBoxButton.OK, MessageBoxImage.Warning); _personal = new(); }
        GameList.ApplyTemplate();
        _ = Dispatcher.BeginInvoke(new Action(AttachGameListScrollViewer), DispatcherPriority.Loaded);
        await LoadGamesAsync();
    }
    private async Task LoadGamesAsync()
    {
        if (_loading) return; _loading = true;
        DataStatus.Text = "正在获取最新游戏列表…"; LoadingPanel.Visibility = Visibility.Visible; FatalPanel.Visibility = Visibility.Collapsed; EmptyPanel.Visibility = Visibility.Collapsed;
        try
        {
            var result = await _gamesService.LoadAsync(); _allGames = result.Data.Games.ToList(); _customerServices = result.Data.CustomerService; _dataUpdateTime = result.Data.UpdateTime;
            var newestRecordDate = _allGames.SelectMany(g => new[] { ParseDate(g.Date), ParseDate(g.UpdateDate) }).Where(d => d.HasValue).Select(d => d!.Value).DefaultIfEmpty().Max();
            var latestDateText = newestRecordDate == default ? "暂无" : newestRecordDate.ToString("yyyy-MM-dd");
            DataStatus.Text = $"游戏记录最新日期：{latestDateText}（按新增/更新日期统计） · {result.Status}";
            LoadingPanel.Visibility = Visibility.Collapsed; FatalPanel.Visibility = Visibility.Collapsed; _page = 1; Render();
        }
        catch (Exception ex) { DataStatus.Text = ex.Message; FatalMessage.Text = ex.Message; LoadingPanel.Visibility = Visibility.Collapsed; FatalPanel.Visibility = Visibility.Visible; EmptyPanel.Visibility = Visibility.Collapsed; _visible.Clear(); }
        finally { _loading = false; }
    }
    private IEnumerable<Game> FilteredGames()
    {
        IEnumerable<Game> q = _allGames;
        var keyword = SearchBox.Text.Trim();
        if (keyword.Length > 0) q = q.Where(g => g.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));
        q = _filter switch
        {
            "new" => q.Where(IsNew), "updated" => q.Where(IsUpdated),
            "favorites" => q.Where(g => _personal.Favorites.Contains(g.Key)),
            "played" => q.Where(g => _personal.Played.Contains(g.Key)),
            "recent" => q.Where(g => _personal.Recent.ContainsKey(g.Key)).OrderByDescending(g => _personal.Recent.GetValueOrDefault(g.Key)?.Time ?? 0),
            _ => q
        };
        var sort = _activeSortValue;
        if (_filter == "recent" && sort == "default") q = q.OrderByDescending(g => _personal.Recent.GetValueOrDefault(g.Key)?.Time ?? 0);
        q = sort switch
        {
            "name_desc" => q.OrderByDescending(g => g.Name, StringComparer.CurrentCulture),
            "name_asc" => q.OrderBy(g => g.Name, StringComparer.CurrentCulture),
            "stars_desc" => q.OrderByDescending(g => g.StarCount).ThenBy(g => g.Name, StringComparer.CurrentCulture),
            "stars_asc" => q.OrderBy(g => g.StarCount).ThenBy(g => g.Name, StringComparer.CurrentCulture),
            "size_desc" => q.OrderBy(g => SizeBytes(g) < 0 ? 1 : 0).ThenByDescending(SizeBytes).ThenBy(g => g.Name, StringComparer.CurrentCulture),
            "size_asc" => q.OrderBy(g => SizeBytes(g) < 0 ? 1 : 0).ThenBy(SizeBytes).ThenBy(g => g.Name, StringComparer.CurrentCulture),
            "date_desc" => q.OrderByDescending(g => ParseDate(_filter == "updated" ? g.UpdateDate : g.Date)),
            "date_asc" => q.OrderBy(g => ParseDate(_filter == "updated" ? g.UpdateDate : g.Date)),
            "price_desc" => q.OrderByDescending(g => g.Price ?? 0).ThenBy(g => g.Name, StringComparer.CurrentCulture),
            "price_asc" => q.OrderBy(g => g.Price ?? 0).ThenBy(g => g.Name, StringComparer.CurrentCulture),
            _ => q
        };
        return q;
    }
    private void Render()
    {
        var items = FilteredGames().ToList();
        if (items.Count == 0) _historyManage = false;
        EmptyPanel.Visibility = items.Count == 0 && !string.IsNullOrWhiteSpace(DataStatus.Text) && !DataStatus.Text.StartsWith("正在") ? Visibility.Visible : Visibility.Collapsed;
        EmptyClearButton.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        EmptyMessage.Text = !string.IsNullOrWhiteSpace(SearchBox.Text) ? $"没有找到与“{SearchBox.Text.Trim()}”相关的游戏" : _filter switch
        {
            "new" => "暂无新增游戏记录",
            "updated" => "暂无更新记录",
            "favorites" => "还没有收藏的游戏，点击游戏旁边的爱心即可收藏",
            "played" => "还没有已玩过的游戏，点击游戏旁边的勋章即可标记",
            "recent" => "还没有浏览记录，打开任意游戏后会自动记录",
            _ => "此分类下暂无游戏数据"
        };
        HeaderDate.Text = _filter == "updated" ? "更新日期" : "新增日期";
        var showDate = _filter == "new" || _filter == "updated"; HeaderDate.Visibility = showDate ? Visibility.Visible : Visibility.Collapsed; ListHeaderGrid.ColumnDefinitions[1].Width = showDate ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
        ManageHistoryButton.Visibility = _filter == "recent" && items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.Visibility = _filter == "recent" && _historyManage && items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ManageHistoryButton.Content = _historyManage ? "完成" : "管理记录";
        UpdateHistoryButtonStyles();
        var totalPages = Math.Max(1, (int)Math.Ceiling(items.Count / (double)Math.Clamp(_personal.Settings.PageSize, 10, 50)));
        _page = Math.Clamp(_page, 1, totalPages);
        RenderPagination(totalPages);
        var pageItems = items.Skip((_page - 1) * _personal.Settings.PageSize).Take(_personal.Settings.PageSize).ToList();
        foreach (var g in pageItems)
        {
            g.DisplayName = g.Name;
            g.PrefixIcon = FreshPrefixIcon(g);
            g.DisplayDate = _filter == "updated" ? (g.UpdateDate ?? "") : (!string.IsNullOrWhiteSpace(g.Date) ? g.Date! : "历史归档");
            g.DateColumnWidth = showDate ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
            g.HasPreview = !string.IsNullOrWhiteSpace(g.PreviewUrl); g.HasCloud = !string.IsNullOrWhiteSpace(g.CloudUrl); g.HasBaidu = !string.IsNullOrWhiteSpace(g.BaiduUrl); g.HasUnzip = !string.IsNullOrWhiteSpace(g.UnzipUrl);
            g.DisplayFontSize = sFontSize(_personal.Settings.FontSize);
            g.CardPadding = _personal.Settings.Spacing switch { "compact" => "5,6,10,6", "loose" => "5,15,10,15", _ => "5,10,10,10" };
            g.ShowUpdateDetails = _filter == "updated";
            g.ShowHistoryDelete = _filter == "recent" && _historyManage;
            g.ShowFavoriteAndPlayed = !g.ShowHistoryDelete;
            var isFavorite = _personal.Favorites.Contains(g.Key);
            var played = _personal.Played.Contains(g.Key);
            g.FavoriteGlyph = "";
            g.FavoriteToolTip = isFavorite ? "取消收藏" : "加入收藏";
            g.PlayedToolTip = played ? "取消已玩标记" : "标记为已玩";
            g.FavoriteIcon = isFavorite ? "/Resources/Icons/favorite.png" : "/Resources/Icons/white-heart.png";
            g.FavoriteColor = isFavorite ? "#E53950" : "#9AA0A6";
            g.FavoriteFill = isFavorite ? "#E53950" : "Transparent";
            g.PlayedGlyph = "🎖️"; g.PlayedColor = played ? "#D9A514" : "#89919A"; g.PlayedOpacity = played ? 1 : 0.38; g.PlayedIcon = played ? "/Resources/Icons/played.png" : "/Resources/Icons/played-gray.png";
        }
        _visible.Clear(); foreach (var g in pageItems) _visible.Add(g);
        ResultCount.Text = $"共找到 {items.Count} 个游戏 (第 {_page} / {totalPages} 页)";
    }
    private static double sFontSize(string size) => size switch { "14px" => 14, "18px" => 18, "20px" => 20, _ => 16 };
    private static bool IsNew(Game g) => ParseDate(g.Date).HasValue;
    private static bool IsUpdated(Game g) => ParseDate(g.UpdateDate).HasValue;
    private static string FreshPrefixIcon(Game g) => IsUpdated(g) ? (IsFresh(g.UpdateDate) ? "/Resources/Icons/update.png" : "/Resources/Icons/games.png") : IsNew(g) ? (IsFresh(g.Date) ? "/Resources/Icons/new.png" : "/Resources/Icons/games.png") : "/Resources/Icons/games.png";
    private static bool IsFresh(string? date)
    {
        var d = ParseDate(date);
        if (!d.HasValue) return false;
        var days = (DateTime.Today - d.Value.Date).Days;
        return days >= 0 && days <= FreshDays;
    }
    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(value, @"(\d{4})[-/年](\d{1,2})[-/月](\d{1,2})");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var year) || !int.TryParse(match.Groups[2].Value, out var month) || !int.TryParse(match.Groups[3].Value, out var day)) return null;
        if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        return new DateTime(year, month, day);
    }
    private static double SizeBytes(Game g)
    {
        var m = System.Text.RegularExpressions.Regex.Match((g.Size ?? "").Replace(",", ""), @"([\d.]+)\s*(TB|GB|MB|KB|B|G|M|K)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var n)) return -1;
        var unit = (m.Groups[2].Value ?? "MB").ToUpperInvariant();
        return n * (unit switch { "TB" => Math.Pow(1024,4), "GB" or "G" => Math.Pow(1024,3), "MB" or "M" or "" => Math.Pow(1024,2), "KB" or "K" => 1024, _ => 1 });
    }
    private async Task RecordRecentAsync(Game game)
    {
        var previousRecent = new Dictionary<string, PersonalDataService.RecentEntry>(_personal.Recent);
        _personal.Recent[game.Key] = new PersonalDataService.RecentEntry { Name = game.Name, Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        if (_personal.Recent.Count > 100)
        {
            foreach (var key in _personal.Recent.OrderBy(x => x.Value.Time).Take(_personal.Recent.Count - 100).Select(x => x.Key).ToList())
                _personal.Recent.Remove(key);
        }
        if (!await PersistAsync()) _personal.Recent = previousRecent;
    }
    private async Task<bool> PersistAsync()
    {
        try { await _personalService.SaveAsync(_personal); return true; }
        catch (Exception ex) { ShowToast("⚠️ 个人数据保存失败：" + ex.Message); return false; }
    }
    private void AttachGameListScrollViewer()
    {
        // 优先通过 ListView 模板部件取得外层滚动控件，避免误取游戏名称内部的横向 ScrollViewer。
        var viewer = GameList.Template?.FindName("ScrollViewer", GameList) as ScrollViewer
            ?? FindVisualChild<ScrollViewer>(GameList);

        if (ReferenceEquals(viewer, _gameListScrollViewer))
        {
            if (viewer is not null)
                UpdateBackToTop(viewer.VerticalOffset, viewer.ScrollableHeight);
            return;
        }

        if (_gameListScrollViewer is not null)
            _gameListScrollViewer.ScrollChanged -= GameList_ScrollChanged;

        _gameListScrollViewer = viewer;
        if (_gameListScrollViewer is not null)
        {
            _gameListScrollViewer.ScrollChanged += GameList_ScrollChanged;
            SmoothScrollBehavior.Attach(_gameListScrollViewer);
            UpdateBackToTop(_gameListScrollViewer.VerticalOffset, _gameListScrollViewer.ScrollableHeight);
        }
    }

    private void GameList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // ScrollChanged 会沿视觉树冒泡；只处理真正的游戏列表外层 ScrollViewer，
        // 忽略游戏名称内部横向滚动区域产生的事件。
        var source = e.OriginalSource as DependencyObject;
        var sourceViewer = source as ScrollViewer
            ?? (source is not null ? FindVisualParent<ScrollViewer>(source) : null);
        if (!ReferenceEquals(sourceViewer, _gameListScrollViewer))
            return;

        var range = Math.Max(0, e.ExtentHeight - e.ViewportHeight);
        UpdateBackToTop(e.VerticalOffset, range);
    }

    private void UpdateBackToTop(double verticalOffset, double scrollRange)
    {
        var progress = scrollRange <= 0 || scrollRange - verticalOffset <= 1
            ? 1
            : Math.Clamp(verticalOffset / scrollRange, 0, 1);
        BackToTopButton.ApplyTemplate();
        if (BackToTopButton.Template.FindName("ProgressRing", BackToTopButton) is Ellipse ring)
            ring.StrokeDashOffset = 50.9 * (1 - progress);
        BackToTopButton.Visibility = verticalOffset > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void BackToTop_Click(object sender, RoutedEventArgs e)
    {
        SmoothScrollBehavior.AnimateTo(_gameListScrollViewer, 0, 520);
    }
    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T match) return match;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) return match;
            var nested = FindVisualChild<T>(child); if (nested is not null) return nested;
        }
        return null;
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ClearSearchButton is not null) ClearSearchButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (SearchPlaceholder is not null) SearchPlaceholder.Visibility = SearchBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        _searchTimer.Stop(); _searchTimer.Start();
    }
    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { _searchTimer.Stop(); _page = 1; Render(); e.Handled = true; System.Windows.Input.Keyboard.ClearFocus(); }
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { _searchTimer.Stop(); SearchBox.Clear(); _searchTimer.Stop(); SearchBox.Focus(); _page = 1; Render(); }
    private async void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _resettingSortBox || SortBox.SelectedItem is not ComboBoxItem c) return;
        var type = c.Tag?.ToString() ?? "";
        if (type.Length == 0) return;

        if (type is "json" or "default")
        {
            _activeSortValue = "default";
        }
        else
        {
            var currentDirection = _personal.Sort.Directions.GetValueOrDefault(type, 0);
            var direction = currentDirection == 0 ? "desc" : "asc";
            _personal.Sort.Directions[type] = currentDirection == 0 ? 1 : 0;
            _activeSortValue = type + "_" + direction;
        }

        _personal.Sort.ActiveSortValue = _activeSortValue;
        if (_filter == "all") _personal.Sort.LastSortValue = _activeSortValue;
        UpdateSortLabel();

        // ComboBox 只负责接收本次选择；清空选中项，避免当前排序残留在下拉菜单中。
        _resettingSortBox = true;
        SortBox.SelectedIndex = -1;
        _resettingSortBox = false;

        _page = 1;
        Render();
        await PersistAsync();
    }

    private void UpdateSortLabel()
    {
        if (SortCurrentLabel is null) return;
        SortCurrentLabel.Text = _activeSortValue switch
        {
            "name_desc" => "名称排序 ↓", "name_asc" => "名称排序 ↑",
            "stars_desc" => "推荐指数 ↓", "stars_asc" => "推荐指数 ↑",
            "size_desc" => "游戏大小 ↓", "size_asc" => "游戏大小 ↑",
            "date_desc" => "日期排序 ↓", "date_asc" => "日期排序 ↑",
            "price_desc" => "价格排序 ↓", "price_asc" => "价格排序 ↑",
            _ => "默认排序"
        };
    }
    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var nextFilter = b.Tag?.ToString() ?? "all";
        if (_filter == "all" && nextFilter != "all") _personal.Sort.LastSortValue = _activeSortValue;
        _filter = nextFilter; _historyManage = false; _page = 1;
        _activeSortValue = _filter is "new" or "updated" ? "date_desc" : _filter == "all" ? (_personal.Sort.LastSortValue is "json" or "default" ? "default" : _personal.Sort.LastSortValue) : "default";
        _personal.Sort.ActiveSortValue = _activeSortValue; UpdateSortLabel();
        UpdateFilterButtonStyles();
        Render(); _ = PersistAsync();
    }
    private void RenderPagination(int maxPage)
    {
        PaginationPanel.Children.Clear();
        if (maxPage <= 1) return;
        Button MakeButton(string label, int? page, bool disabled = false)
        {
            var active = page == _page; var accent = (Brush)Resources["AccentBrush"]; var surface = (Brush)Resources["SurfaceBrush"]; var text = (Brush)Resources["TextBrush"]; var night = _personal.Settings.Background == "night";
            var button = new Button { Content = label, Tag = page, IsEnabled = !disabled, MinWidth = 34, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(2, 0, 2, 0), Background = active ? accent : surface, Foreground = active ? Brushes.White : (night ? text : accent), BorderBrush = accent, BorderThickness = new Thickness(1.5), FontWeight = FontWeights.Bold };
            button.Template = RoundButtonTemplate(6);
            if (page.HasValue && !disabled) button.Click += (_, _) => { _page = page.Value; Render(); SmoothScrollBehavior.AnimateTo(_gameListScrollViewer, 0, 360); };
            return button;
        }
        void AddPage(int page) => PaginationPanel.Children.Add(MakeButton(page.ToString(), page));
        void AddEllipsis() => PaginationPanel.Children.Add(new TextBlock { Text = "...", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0), Foreground = (Brush)Resources["MutedBrush"] });
        PaginationPanel.Children.Add(MakeButton("上一页", _page - 1, _page == 1));
        if (maxPage <= 7) { for (var i = 1; i <= maxPage; i++) AddPage(i); }
        else if (_page <= 4) { for (var i = 1; i <= 5; i++) AddPage(i); AddEllipsis(); AddPage(maxPage); }
        else if (_page >= maxPage - 3) { AddPage(1); AddEllipsis(); for (var i = maxPage - 4; i <= maxPage; i++) AddPage(i); }
        else { AddPage(1); AddEllipsis(); for (var i = _page - 1; i <= _page + 1; i++) AddPage(i); AddEllipsis(); AddPage(maxPage); }
        PaginationPanel.Children.Add(MakeButton("下一页", _page + 1, _page == maxPage));
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadGamesAsync();
    private void Home_Click(object sender, RoutedEventArgs e) => OpenUrl(HomeUrl);
    private void BackupSite_Click(object sender, RoutedEventArgs e) => OpenUrl(FatalUrl);
    private async void Preview_Click(object sender, RoutedEventArgs e) { if (GetGame(sender) is { } g && !string.IsNullOrWhiteSpace(g.PreviewUrl)) { await RecordRecentAsync(g); OpenUrl(g.PreviewUrl); } }
    private async void Cloud_Click(object sender, RoutedEventArgs e)
    {
        if (GetGame(sender) is not { } g) return;
        await RecordRecentAsync(g);
        if (!string.IsNullOrWhiteSpace(g.CloudCode)) { var copied = TryCopyText(g.CloudCode); var opened = TryOpenUrl(g.CloudUrl); ShowCopyCodeDialog(g.CloudCode, copied, opened, g.CloudUrl); }
        else OpenUrl(g.CloudUrl);
    }
    private async void Baidu_Click(object sender, RoutedEventArgs e)
    {
        if (GetGame(sender) is not { } g) return;
        await RecordRecentAsync(g);
        if (!string.IsNullOrWhiteSpace(g.BaiduCode)) { var copied = TryCopyText(g.BaiduCode); var opened = TryOpenUrl(g.BaiduUrl); ShowCopyCodeDialog(g.BaiduCode, copied, opened, g.BaiduUrl); }
        else OpenUrl(g.BaiduUrl);
    }
    private async void Unzip_Click(object sender, RoutedEventArgs e) { if (GetGame(sender) is { } g) { await RecordRecentAsync(g); OpenUrl(g.UnzipUrl); } }
    private void CopyDefaultPassword_Click(object sender, RoutedEventArgs e)
    {
        var copied = TryCopyText(DefaultPassword);
        ShowToast(copied ? "✅️ 已成功复制解压码" : "⚠️ 复制失败，请手动复制：" + DefaultPassword);
    }
    private static bool TryCopyText(string text)
    {
        try { Clipboard.SetText(text); return true; } catch { return false; }
    }
    private static bool TryOpenUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return false;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); return true; } catch { return false; }
    }
    private void ShowCopyCodeDialog(string password, bool copied, bool opened, string? url)
    {
        var hasLink = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http");
        // HTML 的普通提示弹窗使用 340px 内容宽度，按内容自动决定高度。
        var w = DialogWindow("", 340, 0, 0.85);
        var desc = copied ? (opened ? "请在新打开的页面中直接粘贴即可下载。" : "请手动打开网盘页面并粘贴以下提取码。") : "自动复制失败，请手动选中并复制以下提取码，再打开网盘粘贴。";
        var content = new StackPanel { Margin = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Top };
        var statusTitle = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        // 与 HTML 保持一致：复制成功显示成功图标，复制失败显示警告图标。
        statusTitle.Children.Add(IconImage(copied ? "success" : "warning", 25));
        statusTitle.Children.Add(new TextBlock { Text = copied ? "提取码已复制" : "提取码复制失败", FontSize = 22, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Resources["TextBrush"] });
        content.Children.Add(statusTitle);
        content.Children.Add(new TextBlock { Text = desc, FontSize = 17, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 20), Foreground = (Brush)Resources["TextBrush"] });
        var codeText = new TextBox
        {
            Text = password,
            IsReadOnly = true,
            FontSize = 28,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontWeight = FontWeights.Bold,
            Foreground = Brush("#F44336"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            IsReadOnlyCaretVisible = false,
            FocusVisualStyle = null,
            Cursor = Cursors.IBeam
        };
        var code = new Border { Background = (Brush)Resources["SurfaceBrush"], CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 14), Child = codeText };
        content.Children.Add(code);
        if (hasLink && !opened)
        {
            var link = new Button { Content = IconLabel("link", "手动打开网盘链接"), Background = Brush("#2196F3"), Foreground = Brushes.White, FontSize = 16, MinHeight = 42, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Stretch };
            link.Click += (_, _) => OpenUrl(url); content.Children.Add(link);
        }
        var close = new Button { Content = "我知道了", Background = Brush("#2196F3"), Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold, MinHeight = 48, Margin = new Thickness(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        close.Click += (_, _) => w.Close(); content.Children.Add(close);
        w.Content = content; ModalBackdrop.ShowDialog(w);
    }
    private static Image IconImage(string name, double size = 18) => new()
    {
        Source = new BitmapImage(new Uri($"pack://application:,,,/Resources/Icons/{name}.png", UriKind.Absolute)),
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        Margin = new Thickness(0, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center
    };
    private static StackPanel IconLabel(string name, string label, double size = 18) => new()
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
        Children = { IconImage(name, size), new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } }
    };

    private static Game? GetGame(object sender) => (sender as FrameworkElement)?.Tag as Game;
    private void OpenUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            ShowToast("⚠️ 链接格式无效");
            return;
        }
        if (uri.Scheme != "https" && uri.Scheme != "http")
        {
            ShowToast("⚠️ 链接协议不安全，已阻止打开");
            return;
        }
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast("⚠️ 无法打开链接：" + ex.Message); }
    }
    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (GetGame(sender) is not { } g) return;
        var added = _personal.Favorites.Add(g.Key);
        if (!added) _personal.Favorites.Remove(g.Key);
        if (!await PersistAsync())
        {
            if (added) _personal.Favorites.Remove(g.Key);
            else _personal.Favorites.Add(g.Key);
            Render();
            return;
        }
        Render();
        ShowToast(added ? "❤️ 已加入收藏" : "已取消收藏");
    }
    private async void Played_Click(object sender, RoutedEventArgs e)
    {
        if (GetGame(sender) is not { } g) return;
        var added = _personal.Played.Add(g.Key);
        if (!added) _personal.Played.Remove(g.Key);
        if (!await PersistAsync())
        {
            if (added) _personal.Played.Remove(g.Key);
            else _personal.Played.Add(g.Key);
            Render();
            return;
        }
        Render();
        ShowToast(added ? "✓ 已标记为玩过" : "已取消已玩标记");
    }
    private void GameNameScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 游戏名称区域内部使用横向滚动；垂直滚轮应交给游戏列表外层滚动控件。
        if (_gameListScrollViewer is null || e.Delta == 0) return;
        SmoothScrollBehavior.AnimateTo(_gameListScrollViewer,
            _gameListScrollViewer.VerticalOffset - (e.Delta / 120.0) * 90, 230);
        e.Handled = true;
    }

    private void GameNameText_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not TextBlock textBlock || string.IsNullOrWhiteSpace(textBlock.Text)) return;

        var typeface = new Typeface(textBlock.FontFamily, textBlock.FontStyle, textBlock.FontWeight, textBlock.FontStretch);
        var formatted = new FormattedText(
            textBlock.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            textBlock.FontSize,
            textBlock.Foreground,
            VisualTreeHelper.GetDpi(textBlock).PixelsPerDip);

        var host = FindVisualParent<ScrollViewer>(textBlock);
        var viewportWidth = host?.ViewportWidth ?? 0;
        var contentWidth = Math.Max(textBlock.ActualWidth, formatted.Width);
        var overflow = contentWidth - viewportWidth;
        if (overflow <= 3) return;

        var transform = textBlock.RenderTransform as TranslateTransform;
        if (transform == null)
        {
            transform = new TranslateTransform();
            textBlock.RenderTransform = transform;
        }

        transform.X = 0;
        var animation = new DoubleAnimation(-overflow, TimeSpan.FromSeconds(Math.Max(1.8, overflow / 32.0)))
        {
            BeginTime = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        transform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void GameNameText_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not TextBlock textBlock || textBlock.RenderTransform is not TranslateTransform transform) return;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
    }

    private async void GameName_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (GetGame(sender) is not { } g) return;
        await RecordRecentAsync(g);
        var w = DialogWindow("🎮 " + g.Name + " - 游戏介绍", 340, 0, 0.85);
        var panel = new Grid { Margin = new Thickness(4) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Stretch };
        var favImage = IconImage(_personal.Favorites.Contains(g.Key) ? "favorite" : "white-heart", 25);
        var fav = new Button { Content = favImage, Width = 36, Height = 36, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "收藏" };
        fav.Click += async (_, _) =>
        {
            var added = _personal.Favorites.Add(g.Key);
            if (!added) _personal.Favorites.Remove(g.Key);
            if (!await PersistAsync())
            {
                if (added) _personal.Favorites.Remove(g.Key);
                else _personal.Favorites.Add(g.Key);
                favImage.Source = IconImage(_personal.Favorites.Contains(g.Key) ? "favorite" : "white-heart", 25).Source;
                Render();
                return;
            }
            favImage.Source = IconImage(added ? "favorite" : "white-heart", 25).Source;
            Render();
            ShowToast(added ? "❤️ 已加入收藏" : "已取消收藏");
        };
        var isPlayed = _personal.Played.Contains(g.Key);
        var playedImage = IconImage(isPlayed ? "played" : "played-gray", 26);
        var played = new Button { Content = playedImage, FontSize = 26, Background = Brushes.Transparent, Foreground = isPlayed ? Brush("#D9A514") : Brush("#89919A"), Opacity = 1 };
        played.Click += async (_, _) =>
        {
            var added = _personal.Played.Add(g.Key);
            if (!added) _personal.Played.Remove(g.Key);
            if (!await PersistAsync())
            {
                if (added) _personal.Played.Remove(g.Key);
                else _personal.Played.Add(g.Key);
                return;
            }
            playedImage.Source = new BitmapImage(new Uri($"pack://application:,,,/Resources/Icons/{(added ? "played" : "played-gray")}.png", UriKind.Absolute));
            played.Foreground = added ? Brush("#D9A514") : Brush("#89919A");
            Render();
            ShowToast(added ? "✓ 已标记为玩过" : "已取消已玩标记");
        };
        var share = new Button
        {
            Content = IconImage("share", 26),
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = "分享游戏",
            Tag = g
        };
        share.Click += Share_Click;
        status.Children.Add(fav); status.Children.Add(played); status.Children.Add(share); Grid.SetRow(status, 0); panel.Children.Add(status);
        var text = new TextBox { Text = g.Intro, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 15, Padding = new Thickness(8), MaxHeight = SystemParameters.WorkArea.Height * 0.55 };
        var introFrame = new Border { BorderBrush = (Brush)Resources["BorderBrushSoft"], BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Background = (Brush)Resources["CardBrush"], Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0), Child = text };
        Grid.SetRow(introFrame, 1); panel.Children.Add(introFrame);
        var close = new Button { Content = "关闭", Background = Brush("#2196F3"), HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 120, MinHeight = 42, FontSize = 16, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 10, 0, 0) }; close.Click += (_, _) => w.Close(); Grid.SetRow(close, 2); panel.Children.Add(close); w.Content = panel; ModalBackdrop.ShowDialog(w);
    }
    private void UpdateDetail_Click(object sender, RoutedEventArgs e) { if (GetGame(sender) is { } g) ShowTextDialog("📋 " + g.Name + " - 更新详情", string.IsNullOrWhiteSpace(g.UpdateContent) ? "暂无更新" : g.UpdateContent); }
    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (GetGame(sender) is not { } g) return;
        var lines = new List<string> { "📤 小花游戏 · 游戏分享", "", $"🎮 游戏名称：{g.Name}" };
        void AddLink(string label, string icon, string? url) { if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) { lines.Add(""); lines.Add($"{icon} {label}："); lines.Add(url!); } }
        AddLink("游戏预览", "🔍", g.PreviewUrl); AddLink("移动云盘", "☁️", g.CloudUrl);
        if (!string.IsNullOrWhiteSpace(g.CloudCode)) { lines.Add(""); lines.Add("🔑 移动云盘提取码："); lines.Add(g.CloudCode); }
        AddLink("百度网盘", "☁️", g.BaiduUrl); if (!string.IsNullOrWhiteSpace(g.BaiduCode)) { lines.Add(""); lines.Add("🔑 百度网盘提取码："); lines.Add(g.BaiduCode); }
        AddLink("获取解压码", "🔐", g.UnzipUrl); lines.Add(""); lines.Add("🏠 小花游戏主页："); lines.Add(HomeUrl); var text = string.Join("\n", lines);
        var w = DialogWindow("📤 分享：" + g.Name, 340, 0, 0.85); var panel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        panel.Children.Add(new TextBlock { Text = "请选择分享方式", Margin = new Thickness(0, 0, 0, 14), FontSize = 18, TextAlignment = TextAlignment.Center });
        Button MakeShareButton(string icon, string label, string color)
        {
            var button = new Button { Content = string.IsNullOrEmpty(icon) ? label : IconLabel(icon, label), Background = Brush(color), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(3, 4, 3, 4), MinHeight = 53, FontSize = 18, FontWeight = FontWeights.Bold, Padding = new Thickness(12, 8, 12, 8) };
            button.Template = RoundButtonTemplate(28);
            return button;
        }
        var copy = MakeShareButton("copy", "复制分享", "#2196F3"); copy.Click += (_,_) => { var ok = TryCopyText(text); if (ok) w.Close(); ShowToast(ok ? "✓ 分享内容已复制。" : "⚠️ 复制失败，请检查剪贴板权限。"); };
        var system = MakeShareButton("share", "系统分享", "#16A36F"); system.Click += (_,_) => { try { _systemShare.Show(this, "小花游戏 · " + g.Name, text); w.Close(); } catch { ShowToast("⚠️ 系统分享失败，请尝试“复制分享”"); } };
        var close = MakeShareButton("", "关闭", "#78909C"); close.Click += (_,_) => w.Close(); panel.Children.Add(copy); panel.Children.Add(system); panel.Children.Add(close); w.Content=panel; ModalBackdrop.ShowDialog(w);
    }
    private void CustomerService_Click(object sender, RoutedEventArgs e)
    {
        var serviceCount = _customerServices.Count + 1;
        // 为标题、弹窗内边距和全部按钮预留空间，确保客服按钮与关闭按钮完整显示。
        var dialogHeight = Math.Clamp(150 + serviceCount * 58, 260, 430);
        var w = DialogWindow("💬 请选择客服", 340, 0, 0.85); var p = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        foreach (var service in _customerServices)
        {
            var button = new Button { Content = IconLabel("chat", service.Name), Background = Brush("#4CAF50"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(3, 4, 3, 4), MinHeight = 42, FontSize = 16, Padding = new Thickness(12, 8, 12, 8) };
            button.Click += (_, _) => OpenUrl(service.Url); p.Children.Add(button);
        }
        var backup = new Button { Content = IconLabel("chat", "【备用客服】"), Background = Brush("#4CAF50"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(3, 4, 3, 4), MinHeight = 42, FontSize = 16, Padding = new Thickness(12, 8, 12, 8) }; backup.Click += (_, _) => OpenUrl(BackupUrl); p.Children.Add(backup);
        var close = new Button { Content = "关闭", Background = Brush("#2196F3"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(3, 4, 3, 0), MinHeight = 42, FontSize = 16, Padding = new Thickness(12, 8, 12, 8) }; close.Click += (_, _) => w.Close(); p.Children.Add(close); w.Content = p; ModalBackdrop.ShowDialog(w);
    }
    private void Help_Click(object sender, RoutedEventArgs e)
    {
        string help;
        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("XiaohuaGames.Resources.HELP.txt");
            if (stream is null) throw new InvalidOperationException("内嵌帮助资源不存在。");
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            help = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(help)) help = "帮助内容为空。";
        }
        catch (Exception ex)
        {
            help = "帮助内容暂时无法读取。\n\n详细信息：" + ex.Message;
        }
        var w = DialogWindow("❓ 帮助说明", 560, 0, 0.90); var root = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var contact = new Button { Content = IconLabel("chat", "联系客服"), Background = Brush("#4CAF50"), FontSize = 16, MinHeight = 42, Padding = new Thickness(12, 8, 12, 8) }; contact.Click += (_, _) => CustomerService_Click(this, new RoutedEventArgs());
        var close = new Button { Content = "我知道了", Background = Brush("#2196F3"), FontSize = 16, MinHeight = 42, Padding = new Thickness(12, 8, 12, 8) }; close.Click += (_, _) => w.Close();
        actions.Children.Add(contact); actions.Children.Add(close); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var helpDocument = new System.Windows.Documents.FlowDocument { FontSize = 15, PagePadding = new Thickness(0) };
        bool highlightUnzipSection = false;
        foreach (var line in help.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("二、如何获取解压码？", StringComparison.Ordinal)) highlightUnzipSection = true;
            if (line.StartsWith("三、密码本怎么用？", StringComparison.Ordinal)) highlightUnzipSection = false;
            var paragraph = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 0, 0, 3) };
            AppendHelpLineWithIcons(paragraph, line);
            if (highlightUnzipSection)
            {
                paragraph.Foreground = Brush("#D32F2F");
                paragraph.FontWeight = FontWeights.Bold;
            }
            helpDocument.Blocks.Add(paragraph);
        }
        var content = new RichTextBox { Document = helpDocument, IsReadOnly = true, IsDocumentEnabled = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(12), Foreground = (Brush)Resources["TextBrush"], MaxHeight = SystemParameters.WorkArea.Height * 0.55 };
        var contentFrame = new Border { BorderBrush = (Brush)Resources["BorderBrushSoft"], BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Background = (Brush)Resources["CardBrush"], Padding = new Thickness(8), Margin = new Thickness(4, 4, 4, 10), Child = content };
        root.Children.Add(contentFrame);
        w.Content = root; ModalBackdrop.ShowDialog(w);
    }
    private void ShowTextDialog(string title, string text)
    {
        var w = DialogWindow(title, 340, 0, 0.85);
        var panel = new DockPanel { Margin = new Thickness(14) };
        var close = new Button { Content = "关闭", Background = Brush("#2196F3"), HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 120, MinHeight = 42, FontSize = 16, Margin = new Thickness(0, 10, 0, 0) };
        close.Click += (_, _) => w.Close(); DockPanel.SetDock(close, Dock.Bottom);
        var textBox = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(12), FontSize = 16, MaxHeight = SystemParameters.WorkArea.Height * 0.55 };
        var frame = new Border { BorderBrush = (Brush)Resources["BorderBrushSoft"], BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Background = (Brush)Resources["CardBrush"], Padding = new Thickness(8), Child = textBox };
        panel.Children.Add(close); panel.Children.Add(frame); w.Content = panel; ModalBackdrop.ShowDialog(w);
    }
    // width 对应 HTML 弹窗的内容宽度；高度不固定，按内容自动撑开。
    // maxWidthFraction 对应 HTML 的 max-width: xxvw；密码本额外使用 90vh 高度上限。
    private Window DialogWindow(string title, double width, double height, double maxWidthFraction = 0.85, double? maxHeightFraction = null)
    {
        var workArea = SystemParameters.WorkArea;
        var window = new Window
        {
            Title = title,
            Width = width + 48, // ModalBackdrop 会再增加 36px 阴影边距，外框内侧保留 48px 内容留白。
            Height = double.NaN,
            SizeToContent = SizeToContent.Height,
            MinWidth = 300,
            MinHeight = 0,
            MaxWidth = workArea.Width * maxWidthFraction + 84,
            MaxHeight = maxHeightFraction.HasValue ? workArea.Height * maxHeightFraction.Value + 36 : workArea.Height - 24,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brushes.Transparent,
            Foreground = (Brush)Resources["TextBrush"],
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        return window;
    }

    private static readonly (string Token, string Icon)[] HelpIconTokens =
    [
        ("☁️", "cloud"), ("🔑", "key"), ("💬", "chat"), ("📒", "notepad"),
        ("⚙️", "settings"), ("🕘", "recent"), ("❤️", "favorite"), ("🎖️", "played"),
        ("📤", "share"), ("🎮", "games"), ("📋", "update"), ("🔍", "search"),
        ("💡", "bulb"), ("🏠", "home"), ("🔎", "search"), ("🤍", "white-heart"), ("🗑️", "delete"),
        ("📂", "folder"), ("💾", "save"), ("✏️", "edit"), ("🌙", "moon"),
        ("⚠️", "warning"), ("❌", "error"), ("✅", "success"), ("🔐", "key"), ("👉", "pointer")
    ];

    private static void AppendHelpLineWithIcons(Paragraph paragraph, string line)
    {
        var index = 0;
        while (index < line.Length)
        {
            var match = HelpIconTokens.FirstOrDefault(item => line.AsSpan(index).StartsWith(item.Token.AsSpan(), StringComparison.Ordinal));
            if (match.Token is not null)
            {
                var icon = new Image
                {
                    Source = new BitmapImage(new Uri($"pack://application:,,,/Resources/Icons/{match.Icon}.png", UriKind.Absolute)),
                    Width = 17, Height = 17, Stretch = Stretch.Uniform, Margin = new Thickness(1, 0, 3, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                paragraph.Inlines.Add(new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center });
                index += match.Token.Length;
            }
            else
            {
                var start = index++;
                while (index < line.Length && !HelpIconTokens.Any(item => line.AsSpan(index).StartsWith(item.Token.AsSpan(), StringComparison.Ordinal))) index++;
                paragraph.Inlines.Add(new Run(line[start..index]));
            }
        }
    }
    private void ManageHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_filter != "recent") { MessageBox.Show("请先切换到“最近浏览”列表，再管理浏览记录。", "小花游戏"); return; }
        _historyManage = !_historyManage; Render();
    }
    private async void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (GetGame(sender) is not { } game) return;
        if (MessageBox.Show($"确定删除“{game.Name}”的浏览记录吗？", "小花提示", MessageBoxButton.YesNo, MessageBoxImage.None) != MessageBoxResult.Yes) return;
        if (!_personal.Recent.Remove(game.Key, out var removedEntry)) return;
        if (!await PersistAsync())
        {
            _personal.Recent[game.Key] = removedEntry;
            Render();
            return;
        }
        Render();
        ShowToast("✅️ 已删除这条浏览记录");
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_filter != "recent" || !_historyManage) return;
        var recordCount = _personal.Recent.Count;
        if (MessageBox.Show($"确定清空全部 {recordCount} 条浏览记录吗？此操作无法撤销。", "小花提示", MessageBoxButton.YesNo, MessageBoxImage.None) != MessageBoxResult.Yes) return;
        var previousRecent = new Dictionary<string, PersonalDataService.RecentEntry>(_personal.Recent);
        _personal.Recent.Clear();
        if (!await PersistAsync())
        {
            _personal.Recent = previousRecent;
            Render();
            return;
        }
        Render();
        ShowToast("✅️ 已清空全部浏览记录");
    }

    private bool ConfirmRestoreBackup()
    {
        var owner = Application.Current.Windows.OfType<Window>().LastOrDefault(window => window.IsActive) ?? this;
        var dialog = new Window
        {
            Width = 408, // HTML 360px 内容宽度 + 通用弹窗内边距。
            SizeToContent = SizeToContent.Height,
            MinWidth = 360,
            MaxWidth = SystemParameters.WorkArea.Width * 0.88 + 84,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            ShowActivated = true
        };

        var panel = new StackPanel { Margin = new Thickness(30, 24, 30, 28) };
        var promptTitle = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        promptTitle.Children.Add(IconImage("flower", 25));
        promptTitle.Children.Add(new TextBlock { Text = "小花提示", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = (Brush)Resources["TextBrush"], VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(promptTitle);
        panel.Children.Add(new TextBlock
        {
            Text = "导入备份将覆盖当前的收藏、我已玩过的记录、最近浏览、显示设置（含每页数量）和排序偏好，是否继续？",
            FontSize = 17,
            Foreground = (Brush)Resources["TextBrush"],
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Left,
            LineHeight = 27,
            Margin = new Thickness(0, 0, 0, 24)
        });
        var actions = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        var confirmed = false;
        var cancel = new Button
        {
            Content = "取消",
            Background = Brush("#78909C"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            MinHeight = 50,
            Margin = new Thickness(0, 0, 7, 0),
            Padding = new Thickness(12, 8, 12, 8)
        };
        cancel.Click += (_, _) => dialog.Close();
        var confirm = new Button
        {
            Content = "确定",
            Background = Brush("#2196F3"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            MinHeight = 50,
            Margin = new Thickness(7, 0, 0, 0),
            Padding = new Thickness(12, 8, 12, 8)
        };
        confirm.Click += (_, _) => { confirmed = true; dialog.Close(); };
        Grid.SetColumn(cancel, 0);
        Grid.SetColumn(confirm, 1);
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        panel.Children.Add(actions);
        dialog.Content = panel;
        ModalBackdrop.ShowDialog(dialog);
        return confirmed;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var w=DialogWindow("⚙️ 个人设置",380,0,0.90);var p=new StackPanel { Margin = new Thickness(22, 14, 22, 16) };
        var draft = new UserSettings { FontSize = _personal.Settings.FontSize, Spacing = _personal.Settings.Spacing, Background = _personal.Settings.Background, PageSize = _personal.Settings.PageSize };
        AddSetting("字体大小",new[]{("较小","14px"),("默认","16px"),("较大","18px"),("更大","20px")},draft.FontSize, v=>draft.FontSize=v);
        AddSetting("卡片高度",new[]{("紧凑","compact"),("默认","normal"),("宽松","loose")},draft.Spacing,v=>draft.Spacing=v);
        AddSetting("每页数量",new[]{("10 个 / 页","10"),("20 个 / 页","20"),("30 个 / 页","30"),("40 个 / 页","40"),("50 个 / 页","50")},draft.PageSize.ToString(),v=>draft.PageSize=int.Parse(v));
        AddSetting("页面主题",new[]{("默认","default"),("浅灰","#F5F5F5"),("蓝灰","#F0F4F8"),("淡紫","#F5F0FA"),("奶油白","#FFF8F0"),("浅绿","#F1F5F1"),("樱花粉","#FCEFF3"),("夜间模式","night")},draft.Background,v=>draft.Background=v);
        var actions=new StackPanel{Margin=new Thickness(0,14,0,0)};
        var settingActions=new Grid(); settingActions.ColumnDefinitions.Add(new ColumnDefinition()); settingActions.ColumnDefinitions.Add(new ColumnDefinition());
        var save=new Button{Content="应用设置",FontSize=16,MinHeight=42,Padding=new Thickness(12,8,12,8)};save.Click+=async(_,_)=>
        {
            var previous = new UserSettings { FontSize = _personal.Settings.FontSize, Spacing = _personal.Settings.Spacing, Background = _personal.Settings.Background, PageSize = _personal.Settings.PageSize };
            var previousPage = _page;
            _personal.Settings = draft;
            if (previous.PageSize != draft.PageSize) _page = 1;
            ApplySettings();
            if (!await PersistAsync()) { _personal.Settings = previous; _page = previousPage; ApplySettings(); Render(); return; }
            Render();
            w.Close();
            ShowToast("✓ 个人设置已应用");
        };
        var reset=new Button{Content="恢复默认",Background = Brush("#607D8B"),FontSize=16,MinHeight=42,Padding=new Thickness(12,8,12,8)};reset.Click+=async(_,_)=>
        {
            var previousSettings = new UserSettings { FontSize = _personal.Settings.FontSize, Spacing = _personal.Settings.Spacing, Background = _personal.Settings.Background, PageSize = _personal.Settings.PageSize };
            var previousSort = new PersonalDataService.SortState { ActiveSortValue = _personal.Sort.ActiveSortValue, LastSortValue = _personal.Sort.LastSortValue, Directions = new Dictionary<string, int>(_personal.Sort.Directions) };
            var previousActiveSort = _activeSortValue;
            var previousPage = _page;
            _personal.Settings = new(); _personal.Sort = new(); _activeSortValue = "default"; _page = 1; UpdateSortLabel(); ApplySettings();
            if (!await PersistAsync())
            {
                _personal.Settings = previousSettings; _personal.Sort = previousSort; _activeSortValue = previousActiveSort; _page = previousPage;
                UpdateSortLabel(); ApplySettings(); Render(); return;
            }
            Render();
            w.Close();
            ShowToast("✓ 已恢复默认显示设置");
        };
        var backup=new Button{Content="导出备份",Background = Brush("#4CAF50"),FontSize=16,MinHeight=42,Padding=new Thickness(12,8,12,8)};backup.Click+=async(_,_)=>{var dlg=new SaveFileDialog{Filter="JSON 备份|*.json",FileName="小花游戏个人数据备份.json"};if(dlg.ShowDialog()==true){try{await _personalService.ExportAsync(_personal,dlg.FileName);ShowToast("✓ 备份文件已导出");}catch(Exception ex){ShowToast("⚠️ 备份文件导出失败："+ex.Message);}}};
        var restore=new Button{Content="导入恢复",Background = Brush("#607D8B"),FontSize=16,MinHeight=42,Padding=new Thickness(12,8,12,8)};restore.Click+=async(_,_)=>{var dlg=new OpenFileDialog{Filter="JSON 备份|*.json"};if(dlg.ShowDialog()==true){if(ConfirmRestoreBackup()){try{_personal=await _personalService.ImportAsync(dlg.FileName);_activeSortValue = _personal.Sort.ActiveSortValue is "default" or "json" ? "default" : _personal.Sort.ActiveSortValue; UpdateSortLabel(); ApplySettings();Render();ShowToast("✓ 个人数据恢复成功");w.Close();}catch(Exception ex){ShowToast("恢复失败："+ex.Message);}}}};
        save.Margin=new Thickness(3,3,5,3); reset.Margin=new Thickness(5,3,3,3); Grid.SetColumn(reset,0); settingActions.Children.Add(reset); Grid.SetColumn(save,1); settingActions.Children.Add(save); actions.Children.Add(settingActions);
        var backupTitle=new TextBlock{Text="数据备份与恢复",FontSize=16,FontWeight=FontWeights.Bold,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,16,0,8)}; actions.Children.Add(backupTitle);
        var backupActions=new Grid(); backupActions.ColumnDefinitions.Add(new ColumnDefinition()); backupActions.ColumnDefinitions.Add(new ColumnDefinition()); backup.Margin=new Thickness(3,3,5,3); restore.Margin=new Thickness(5,3,3,3); Grid.SetColumn(restore,1); backupActions.Children.Add(backup); backupActions.Children.Add(restore); actions.Children.Add(backupActions);
        actions.Children.Add(new TextBlock{Text="备份收藏、已玩记录、最近浏览、显示设置（含每页数量）和排序偏好；密码本数据不包含在内。",FontSize=12,Foreground=(Brush)Resources["MutedBrush"],TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,Margin=new Thickness(2,5,2,10)}); p.Children.Add(actions);var close=new Button{Content="关闭",Background = Brush("#2196F3"),HorizontalAlignment=HorizontalAlignment.Stretch,Margin=new Thickness(3,8,3,0),MinHeight=42,FontSize=16,Padding=new Thickness(12,8,12,8)};close.Click+=(_,_)=>w.Close();p.Children.Add(close);w.Content=p;ModalBackdrop.ShowDialog(w);
        void AddSetting(string label,(string name,string value)[] opts,string current,Action<string> set){var row=new Grid{Margin=new Thickness(6,7,6,7),MinHeight=40};row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1.15,GridUnitType.Star)});row.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(2,0,10,0),FontSize=14});var cb=new ComboBox{ItemsSource=opts.Select(x=>x.name).ToList(),SelectedIndex=Math.Max(0,Array.FindIndex(opts,x=>x.value==current)),Background=(Brush)Resources["SurfaceBrush"],Foreground=(Brush)Resources["TextBrush"],BorderBrush=(Brush)Resources["BorderBrushSoft"]};
            if (label == "页面主题")
            {
                var themeTemplate = new DataTemplate();
                var themePanel = new FrameworkElementFactory(typeof(StackPanel)); themePanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
                var themeIcon = new FrameworkElementFactory(typeof(Image)); themeIcon.Name = "ThemeOptionIcon"; themeIcon.SetValue(FrameworkElement.WidthProperty, 16d); themeIcon.SetValue(FrameworkElement.HeightProperty, 16d); themeIcon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0)); themeIcon.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed); themePanel.AppendChild(themeIcon);
                var themeText = new FrameworkElementFactory(typeof(TextBlock)); themeText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding()); themeText.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); themePanel.AppendChild(themeText);
                themeTemplate.VisualTree = themePanel;
                var nightTrigger = new DataTrigger { Binding = new System.Windows.Data.Binding(), Value = "夜间模式" };
                nightTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "ThemeOptionIcon"));
                nightTrigger.Setters.Add(new Setter(Image.SourceProperty, new BitmapImage(new Uri("pack://application:,,,/Resources/Icons/moon.png", UriKind.Absolute)), "ThemeOptionIcon"));
                themeTemplate.Triggers.Add(nightTrigger); cb.ItemTemplate = themeTemplate;
            }
            cb.Resources[SystemColors.WindowBrushKey]=(Brush)Resources["SurfaceBrush"];cb.Resources[SystemColors.ControlTextBrushKey]=(Brush)Resources["TextBrush"];cb.Resources[SystemColors.HighlightBrushKey]=(Brush)Resources["AccentSoftBrush"];cb.Resources[SystemColors.HighlightTextBrushKey]=(Brush)Resources["TextBrush"];cb.SelectionChanged+=(_,_)=>{if(cb.SelectedIndex>=0)set(opts[cb.SelectedIndex].value);};cb.Margin=new Thickness(0,0,0,0); cb.MinWidth=0; Grid.SetColumn(cb,1);row.Children.Add(cb);p.Children.Add(row);}
    }
    private void ApplySettings()
    {
        var s = _personal.Settings;
        var isNight = s.Background == "night";
        var selectedBg = s.Background is "default" or "night" ? "#FFFFFF" : s.Background;
        var page = isNight ? "#20242C" : selectedBg;
        var surface = isNight ? "#2B313B" : selectedBg;
        var card = isNight ? "#303743" : s.Background == "default" ? "#FFFFFF" : MixHex(selectedBg, "#FFFFFF", 0.72);
        var modal = isNight ? "#2B313B" : s.Background == "default" ? "#FFFFFF" : selectedBg;
        var border = isNight ? "#596473" : s.Background == "default" ? "#CBD5DF" : MixHex(selectedBg, "#718096", 0.22);
        var text = isNight ? "#EDF1F7" : "#263238";
        var muted = isNight ? "#C1CAD6" : "#56616D";
        var placeholder = isNight ? "#B6C0CD" : "#687582";
        var accent = isNight || s.Background == "default" ? "#2196F3" : MixHex(selectedBg, "#2196F3", 0.18);
        var accentSoft = isNight ? "#374454" : MixHex(selectedBg, "#E8F3FD", 0.28);
        Resources["PageBrush"] = Brush(page); Resources["SurfaceBrush"] = Brush(surface); Resources["CardBrush"] = Brush(card); Resources["ModalBrush"] = Brush(modal);
        Resources["TextBrush"] = Brush(text); Resources["MutedBrush"] = Brush(muted); Resources["PlaceholderBrush"] = Brush(placeholder); Resources["BorderBrushSoft"] = Brush(border);
        Resources["AccentBrush"] = Brush(accent); Resources["AccentSoftBrush"] = Brush(accentSoft);
        var appResources = Application.Current.Resources;
        appResources["PageBrush"] = Brush(page);
        appResources["SurfaceBrush"] = Brush(surface);
        appResources["CardBrush"] = Brush(card);
        appResources["ModalBrush"] = Brush(modal);
        appResources["TextBrush"] = Brush(text);
        appResources["MutedBrush"] = Brush(muted);
        appResources["BorderBrushSoft"] = Brush(border);
        appResources["NotepadPasswordBrush"] = Brush(isNight ? "#59414A" : "#FFEBEE");
        appResources["NotepadPasswordTextBrush"] = Brush(isNight ? "#FFE4E9" : "#9F2338");
        appResources["AccentBrush"] = Brush(accent);
        appResources["AccentSoftBrush"] = Brush(accentSoft);
        appResources["PlaceholderBrush"] = Brush(placeholder);
        appResources[SystemColors.WindowBrushKey] = Brush(surface);
        appResources[SystemColors.ControlBrushKey] = Brush(surface);
        appResources[SystemColors.ControlTextBrushKey] = Brush(text);
        appResources[SystemColors.HighlightBrushKey] = Brush(accentSoft);
        appResources[SystemColors.HighlightTextBrushKey] = Brush(text);
        Resources[SystemColors.WindowBrushKey] = Brush(surface);
        Resources[SystemColors.ControlBrushKey] = Brush(surface);
        Resources[SystemColors.ControlTextBrushKey] = Brush(text);
        Resources[SystemColors.HighlightBrushKey] = Brush(accentSoft);
        Resources[SystemColors.HighlightTextBrushKey] = Brush(text);
        foreach (var header in ListHeaderGrid.Children.OfType<TextBlock>()) header.Foreground = Brush(isNight ? "#FFFFFFFF" : "#FF111111");
        Resources["NotepadPasswordBrush"] = Brush(isNight ? "#59414A" : "#FFEBEE"); Resources["NotepadPasswordTextBrush"] = Brush(isNight ? "#FFE4E9" : "#9F2338");
        Background = Brush(page);
        SearchBox.Background = Brush(surface); SearchBox.Foreground = Brush(text); SearchBox.BorderBrush = Brush(border); SearchBox.CaretBrush = Brush(accent); if (SearchPlaceholder.Children.OfType<TextBlock>().FirstOrDefault() is { } placeholderText) placeholderText.Foreground = Brush(placeholder);
        SortBox.Background = Brush(surface); SortBox.Foreground = Brush(text); SortBox.BorderBrush = Brush(border);
        SortBox.Resources[SystemColors.WindowBrushKey] = Brush(surface);
        SortBox.Resources[SystemColors.ControlBrushKey] = Brush(surface);
        SortBox.Resources[SystemColors.ControlTextBrushKey] = Brush(text);
        SortBox.Resources[SystemColors.HighlightBrushKey] = Brush(accentSoft);
        SortBox.Resources[SystemColors.HighlightTextBrushKey] = Brush(text);
        SortBox.Resources["SurfaceBrush"] = Brush(surface);
        SortBox.Resources["TextBrush"] = Brush(text);
        SortBox.Resources["BorderBrushSoft"] = Brush(border);
        SortBox.Resources["AccentBrush"] = Brush(accent);
        SortBox.Resources["AccentSoftBrush"] = Brush(accentSoft);
        SettingsButton.Background = Brush(surface); SettingsButton.Foreground = Brush(accent); SettingsButton.BorderBrush = Brush(accent);
        HomeButton.Foreground = Brushes.White; HomeButton.Background = Brush("#2196F3"); HelpButton.Foreground = Brush(accent); HelpButton.Background = Brush(surface); HelpButton.BorderBrush = Brush(accent); HelpButton.BorderThickness = new Thickness(1.5);
        UpdateHistoryButtonStyles();
        foreach (var pageButton in PaginationPanel.Children.OfType<Button>())
        {
            var active = pageButton.Tag is int pageNumber && pageNumber == _page;
            pageButton.Background = active ? Brush(accent) : Brush(surface);
            pageButton.Foreground = active ? Brushes.White : (isNight ? Brush(text) : Brush(accent));
            pageButton.BorderBrush = Brush(accent);
            pageButton.BorderThickness = new Thickness(1.5);
        }
        BackToTopButton.Background = Brush(accent); BackToTopButton.Foreground = Brushes.White;
        GameList.FontSize = sFontSize(s.FontSize);
        UpdateFilterButtonStyles();
        UpdateSortLabel();
    }
    private void UpdateHistoryButtonStyles()
    {
        if (!IsInitialized) return;

        var accent = (Brush)Resources["AccentBrush"];
        var surface = (Brush)Resources["SurfaceBrush"];

        // 管理记录和完成共用同一套主题颜色，不随文字状态改变。
        ManageHistoryButton.SetCurrentValue(Control.BackgroundProperty, surface);
        ManageHistoryButton.SetCurrentValue(Control.ForegroundProperty, accent);
        ManageHistoryButton.SetCurrentValue(Control.BorderBrushProperty, accent);
        ManageHistoryButton.SetCurrentValue(Control.BorderThicknessProperty, new Thickness(1.5));

        // 清空按钮保留危险操作的红色提示，不随主题强调色改变。
        var night = _personal.Settings.Background == "night";
        var danger = Brush(night ? "#FFFF8A80" : "#FFD9534F");

        ClearHistoryButton.SetCurrentValue(Control.BackgroundProperty, surface);
        ClearHistoryButton.SetCurrentValue(Control.ForegroundProperty, danger);
        ClearHistoryButton.SetCurrentValue(Control.BorderBrushProperty, danger);
        ClearHistoryButton.SetCurrentValue(Control.BorderThicknessProperty, new Thickness(1.5));
    }

    private void UpdateFilterButtonStyles()
    {
        var accent = (Brush)Resources["AccentBrush"];
        var surface = (Brush)Resources["SurfaceBrush"];
        var night = _personal.Settings.Background == "night";
        foreach (var item in FilterPanel.Children.OfType<Button>())
        {
            // 只处理真正的筛选按钮；帮助说明按钮没有筛选 Tag，不能把它误判成“全部游戏”。
            var tag = item.Tag?.ToString();
            if (tag is not ("all" or "new" or "updated" or "favorites" or "played" or "recent"))
                continue;

            var active = tag == _filter;
            item.Background = active ? accent : surface;
            item.Foreground = active || night ? Brushes.White : Brushes.Black;
            // 未选中状态采用柔和的灰蓝色边框；选中状态继续突出主题强调色。
            item.BorderBrush = active ? accent : Brush("#9AAAB8");
            item.BorderThickness = new Thickness(1.5);
        }

        // 帮助说明独立跟随主题底色，不参与“全部游戏”的选中态。
        HelpButton.SetCurrentValue(Control.BackgroundProperty, surface);
        // 与普通筛选按钮的文字颜色规则一致：浅色主题黑字，夜间模式白字。
        HelpButton.SetCurrentValue(Control.ForegroundProperty, night ? Brushes.White : Brushes.Black);
        HelpButton.SetCurrentValue(Control.BorderBrushProperty, Brush("#9AAAB8"));
        HelpButton.SetCurrentValue(Control.BorderThicknessProperty, new Thickness(1.5));
    }
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private void CenterToastOverOwnerClientArea(Window toast)
    {
        var ownerHandle = new WindowInteropHelper(this).Handle;
        var toastHandle = new WindowInteropHelper(toast).Handle;
        if (ownerHandle == IntPtr.Zero || toastHandle == IntPtr.Zero) return;
        if (!GetClientRect(ownerHandle, out var clientRect)
            || !GetWindowRect(ownerHandle, out var ownerWindowRect)
            || !GetWindowRect(toastHandle, out var toastRect)) return;

        var clientTopLeft = new NativePoint { X = clientRect.Left, Y = clientRect.Top };
        var clientBottomRight = new NativePoint { X = clientRect.Right, Y = clientRect.Bottom };
        if (!ClientToScreen(ownerHandle, ref clientTopLeft)
            || !ClientToScreen(ownerHandle, ref clientBottomRight)) return;

        var clientWidth = clientBottomRight.X - clientTopLeft.X;
        var toastWidth = toastRect.Right - toastRect.Left;
        var x = clientTopLeft.X + (clientWidth - toastWidth) / 2;
        // 纵向仍贴近主窗口顶部；使用实际窗口矩形，避免最大化时读取到旧 Top。
        var y = ownerWindowRect.Top + 20;
        SetWindowPos(toastHandle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private void ShowToast(string message)
    {
        try
        {
            _toastTimer?.Stop();
            _toastWindow?.Close();

            // 去掉 Emoji 变体选择符，避免 PNG 图标后残留方框字符。
            var remaining = (message ?? string.Empty)
                .Replace("\uFE0F", string.Empty, StringComparison.Ordinal)
                .Replace("\uFE0E", string.Empty, StringComparison.Ordinal);

            var toastTokens = new (string Token, string Icon)[]
            {
                ("✅", "success"), ("⚠", "warning"), ("❤", "favorite"), ("🤍", "white-heart"),
                ("❌", "error"), ("🎮", "games"), ("📋", "update"), ("📤", "share"),
                ("🔑", "key"), ("🌸", "flower"), ("💬", "chat"), ("🗑", "delete"),
                ("✏", "edit"), ("👉", "pointer"), ("✓", "success"), ("🔐", "key"),
                ("☁", "cloud"), ("🏠", "home")
            };

            Image? toastIcon = null;
            var matchedToken = toastTokens.FirstOrDefault(item => remaining.StartsWith(item.Token, StringComparison.Ordinal));
            if (matchedToken.Token is not null)
            {
                toastIcon = IconImage(matchedToken.Icon, 18);
                toastIcon.Margin = new Thickness(2, 0, 7, 0);
                toastIcon.VerticalAlignment = VerticalAlignment.Center;
                remaining = remaining[matchedToken.Token.Length..].TrimStart();
            }

            var maxToastWidth = Math.Max(240, Math.Min(SystemParameters.WorkArea.Width * 0.88, ActualWidth * 0.9));
            var textMaxWidth = Math.Max(180, maxToastWidth - (toastIcon is null ? 44 : 74));
            var toastText = new TextBlock
            {
                Text = remaining,
                Foreground = Brushes.White,
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = textMaxWidth
            };

            // 图标和文字作为一个整体排版，短消息、长消息都以整体宽度居中。
            var toastContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = maxToastWidth - 44
            };
            if (toastIcon is not null) toastContent.Children.Add(toastIcon);
            toastContent.Children.Add(toastText);

            var toast = new Window
            {
                Owner = this,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                // 先保持透明，完成定位后再显示，避免首次创建时短暂闪到桌面左上角。
                Opacity = 0,
                Topmost = false,
                ResizeMode = ResizeMode.NoResize,
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(222, 0, 0, 0)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(22, 12, 22, 12),
                    MaxWidth = maxToastWidth,
                    Child = toastContent,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        BlurRadius = 12,
                        ShadowDepth = 3,
                        Opacity = 0.2
                    }
                }
            };

            _toastWindow = toast;
            toast.ContentRendered += (_, _) =>
            {
                // 最大化时 Window.Left/Top 可能仍是恢复窗口的位置，不能与最大化后的 ActualWidth 混算。
                // 先按主窗口实际客户区定位，定位完成后才显示，杜绝桌面位置闪现。
                CenterToastOverOwnerClientArea(toast);
                toast.Opacity = 1;
            };
            toast.Show();

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer?.Stop();
                if (_toastWindow == toast) _toastWindow = null;
                toast.Close();
            };
            _toastTimer.Start();
        }
        catch { }
    }
    private static string MixHex(string a, string b, double ratio)
    {
        var ca = (Color)System.Windows.Media.ColorConverter.ConvertFromString(a); var cb = (Color)System.Windows.Media.ColorConverter.ConvertFromString(b);
        return $"#{(byte)Math.Round(ca.R*ratio+cb.R*(1-ratio)):X2}{(byte)Math.Round(ca.G*ratio+cb.G*(1-ratio)):X2}{(byte)Math.Round(ca.B*ratio+cb.B*(1-ratio)):X2}";
    }
    private static ControlTemplate RoundClearTemplate() => RoundButtonTemplate(20);
    private static ControlTemplate RoundButtonTemplate(double radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        border.AppendChild(content); template.VisualTree = border; return template;
    }
    private static SolidColorBrush Brush(string hex)=>(SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    private void Notepad_Click(object sender, RoutedEventArgs e) => OpenNotepad();
    private void OpenNotepad()
    {
        var w = DialogWindow("📒 我的密码本", 602, 0, 0.95, 0.90);
        // HTML 密码本采用 border-box：最大外框宽度直接限制为 95vw。
        w.MaxWidth = SystemParameters.WorkArea.Width * 0.95 + 36;
        w.Tag = "NotepadCustomHeader";
        // 三行布局：顶部内容自适应，中间列表占据剩余高度，底部分页和关闭按钮固定显示。
        // 避免 DockPanel 在外层 Auto 测量时让列表按完整内容扩展，挤压底部可视区域。
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new StackPanel();
        // 密码本采用独立标题行：标题靠左，导入/导出靠右；其他弹窗标题仍由统一弹窗层居中显示。
        var headerRow = new Grid { Margin = new Thickness(3, 0, 3, 8) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition());
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerTitle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerTitle.Children.Add(IconImage("notepad", 24));
        headerTitle.Children.Add(new TextBlock { Text = "我的密码本", FontSize = 21, FontWeight = FontWeights.Bold, Foreground = (Brush)Resources["TextBrush"], VerticalAlignment = VerticalAlignment.Center });
        headerRow.Children.Add(headerTitle);
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(headerActions, 1); headerRow.Children.Add(headerActions);
        var separator = new Border { Height = 1, Background = (Brush)Resources["BorderBrushSoft"], Margin = new Thickness(3, 0, 3, 12) };
        var inputRow = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition()); inputRow.ColumnDefinitions.Add(new ColumnDefinition());
        var saveRow = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
        var name = new TextBox { Margin = new Thickness(0), ToolTip = "游戏名", Background=(Brush)Resources["SurfaceBrush"], Foreground=(Brush)Resources["TextBrush"], BorderBrush=(Brush)Resources["BorderBrushSoft"], Height = 42, VerticalContentAlignment = VerticalAlignment.Center };
        var pwd = new TextBox { Margin = new Thickness(0), ToolTip = "解压码", Background=(Brush)Resources["SurfaceBrush"], Foreground=(Brush)Resources["TextBrush"], BorderBrush=(Brush)Resources["BorderBrushSoft"], Height = 42, VerticalContentAlignment = VerticalAlignment.Center };
        var add = new Button { Content = "保存", Background = Brush("#2196F3"), Foreground = Brushes.White, FontSize = 16, Height = 44, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 8, 12, 8) };
        add.Template = RoundButtonTemplate(24);
        var import = new Button { Content = IconLabel("folder", "导入"), Background = Brush("#FF9800"), Foreground = Brushes.White, FontSize = 16, Height = 42, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(3, 0, 3, 0), MinWidth = 105 };
        import.Template = RoundButtonTemplate(24);
        var export = new Button { Content = IconLabel("save", "导出"), Background = Brush("#607D8B"), Foreground = Brushes.White, FontSize = 16, Height = 42, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(3, 0, 0, 0), MinWidth = 105 };
        export.Template = RoundButtonTemplate(24);
        headerActions.Children.Add(import); headerActions.Children.Add(export);
        Grid MakeClearableInput(TextBox input)
        {
            var holder = new Grid { Height = 42, Margin = new Thickness(3), VerticalAlignment = VerticalAlignment.Center };
            input.Width = double.NaN; input.Margin = new Thickness(0); input.Padding = new Thickness(9, 7, 34, 7);
            holder.Children.Add(input);
            var placeholder = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 32, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            placeholder.Children.Add(IconImage("edit", 15));
            placeholder.Children.Add(new TextBlock { Text = ReferenceEquals(input, name) ? "输入游戏名..." : "输入密码...", Foreground = (Brush)Resources["PlaceholderBrush"], VerticalAlignment = VerticalAlignment.Center });
            holder.Children.Add(placeholder);
            var clearIconHost = new Grid { Width = 21, Height = 21 };
            clearIconHost.Children.Add(new Image { Source = IconImage("close", 13).Source, Width = 13, Height = 13, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var clear = new Button { Content = clearIconHost, Width = 22, Height = 22, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Background = Brush("#88999999"), Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0), Visibility = Visibility.Collapsed };
            clear.Template = RoundClearTemplate(); clear.Click += (_, _) => input.Clear();
            void UpdateInputDecorations() { clear.Visibility = input.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; }
            input.TextChanged += (_, _) => UpdateInputDecorations();
            input.GotFocus += (_, _) => UpdateInputDecorations();
            input.LostFocus += (_, _) => UpdateInputDecorations();
            UpdateInputDecorations();
            holder.Children.Add(clear); return holder;
        }
        var nameInput = MakeClearableInput(name); var pwdInput = MakeClearableInput(pwd);
        Grid.SetColumn(nameInput, 0); Grid.SetColumn(pwdInput, 1); inputRow.Children.Add(nameInput); inputRow.Children.Add(pwdInput);
        saveRow.Children.Add(add);
        top.Children.Add(headerRow); top.Children.Add(separator); top.Children.Add(inputRow); top.Children.Add(saveRow);
        var search = new TextBox { Margin = new Thickness(3, 8, 3, 8), Padding = new Thickness(10, 8, 34, 8), ToolTip = "搜索游戏名或解压码", Background=(Brush)Resources["SurfaceBrush"], Foreground=(Brush)Resources["TextBrush"], BorderBrush=(Brush)Resources["BorderBrushSoft"] };
        var searchGrid = new Grid();
        searchGrid.Children.Add(search);
        var searchPlaceholder = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 34, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        searchPlaceholder.Children.Add(IconImage("search", 15));
        searchPlaceholder.Children.Add(new TextBlock { Text = "搜索游戏名或解压码...", Foreground = (Brush)Resources["PlaceholderBrush"], VerticalAlignment = VerticalAlignment.Center });
        searchGrid.Children.Add(searchPlaceholder);
        var clearSearchIconHost = new Grid { Width = 22, Height = 22 };
        clearSearchIconHost.Children.Add(new Image { Source = IconImage("close", 13).Source, Width = 13, Height = 13, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var clearSearch = new Button { Content = clearSearchIconHost, Width = 22, Height = 22, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Background = Brush("#88999999"), Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed }; clearSearch.Template = RoundClearTemplate(); clearSearch.Click += (_, _) => search.Clear();
        void UpdateSearchDecorations() { clearSearch.Visibility = search.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; searchPlaceholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; }
        search.TextChanged += (_, _) => UpdateSearchDecorations();
        UpdateSearchDecorations();
        searchGrid.Children.Add(clearSearch); top.Children.Add(searchGrid);
        name.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };
        pwd.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };
        var headings = new Grid { Margin = new Thickness(4, 2, 4, 6) };
        headings.ColumnDefinitions.Add(new ColumnDefinition()); headings.ColumnDefinitions.Add(new ColumnDefinition()); headings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        headings.Children.Add(new TextBlock { Text = "游戏名", FontWeight = FontWeights.Bold, FontSize = 18, VerticalAlignment = VerticalAlignment.Center }); var pwdHead = new TextBlock { Text = "解压码", FontWeight = FontWeights.Bold, FontSize = 18, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(pwdHead, 1); headings.Children.Add(pwdHead); var actHead = new TextBlock { Text = "操作", FontWeight = FontWeights.Bold, FontSize = 18, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(actHead, 2); headings.Children.Add(actHead); top.Children.Add(headings);
        Grid.SetRow(top, 0); root.Children.Add(top);
        // 列表本身设置最大可视高度，而不是给整个弹窗固定高度：记录较少时弹窗自然收缩，
        // 记录较多时列表在自身滚动区域内滚动，底部分页/关闭区始终留在可视范围内。
        var list = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MaxHeight = Math.Max(120, SystemParameters.WorkArea.Height * 0.90 - 420)
        };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        // 与主页 GameList 保持一致，使用像素滚动，避免逻辑滚动与平滑滚动动画混用。
        ScrollViewer.SetCanContentScroll(list, false);
        var footer = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var prev = new Button { Content = "上一页", Background = (Brush)Resources["SurfaceBrush"], Foreground = (Brush)Resources["TextBrush"], BorderBrush = Brush("#2196F3"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 5, 10, 5) }; prev.Template = RoundButtonTemplate(8);
        var pageButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        var next = new Button { Content = "下一页", Background = (Brush)Resources["SurfaceBrush"], Foreground = (Brush)Resources["TextBrush"], BorderBrush = Brush("#2196F3"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 5, 10, 5) }; next.Template = RoundButtonTemplate(8);
        bottom.Children.Add(prev); bottom.Children.Add(pageButtons); bottom.Children.Add(next);
        footer.Children.Add(bottom);
        var close = new Button { Content = "关闭", Background = Brush("#2196F3"), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0), MinHeight = 44, FontSize = 16, Padding = new Thickness(12, 8, 12, 8) };
        close.Template = RoundButtonTemplate(24);
        close.Click += (_, _) => w.Close();
        footer.Children.Add(close);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        Grid.SetRow(list, 1); root.Children.Add(list);
        int currentPage = 1; const int pageSize = 10;
        void Refresh()
        {
            var filtered = _personal.Notepad.Where(x => string.IsNullOrWhiteSpace(search.Text) || x.Name.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase) || x.Password.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)).Reverse().ToList();
            var pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)pageSize)); currentPage = Math.Clamp(currentPage, 1, pages);
            pageButtons.Children.Clear();
            Button MakePageButton(string label, int targetPage, bool active = false)
            {
                var button = new Button { Content = label, MinWidth = 30, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2, 0, 2, 0),
                    Background = active ? Brush("#2196F3") : (Brush)Resources["SurfaceBrush"],
                    Foreground = active ? Brushes.White : (Brush)Resources["TextBrush"],
                    BorderBrush = Brush("#2196F3"), BorderThickness = new Thickness(1) };
                button.Template = RoundButtonTemplate(8);
                button.Click += (_, _) => { currentPage = targetPage; Refresh(); };
                return button;
            }
            void AddPage(int page) => pageButtons.Children.Add(MakePageButton(page.ToString(), page, page == currentPage));
            void AddEllipsis() => pageButtons.Children.Add(new TextBlock { Text = "…", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), Foreground = (Brush)Resources["MutedBrush"] });
            if (pages <= 7) { for (var page = 1; page <= pages; page++) AddPage(page); }
            else
            {
                AddPage(1);
                if (currentPage > 3) AddEllipsis();
                for (var page = Math.Max(2, currentPage - 1); page <= Math.Min(pages - 1, currentPage + 1); page++) AddPage(page);
                if (currentPage < pages - 2) AddEllipsis();
                AddPage(pages);
            }
            list.Items.Clear();
            if (filtered.Count == 0)
            {
                list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = string.IsNullOrWhiteSpace(search.Text) ? "暂无记录，请在上方添加" : "没有找到匹配的记录", Foreground = Brush("#999999"), TextAlignment = TextAlignment.Center, Margin = new Thickness(12, 24, 12, 24) }, HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(0) });
                prev.IsEnabled = false; next.IsEnabled = false; return;
            }
            foreach (var entry in filtered.Skip((currentPage - 1) * pageSize).Take(pageSize))
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
                row.Children.Add(new TextBlock { Text = entry.Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = entry.Name, Margin = new Thickness(8, 0, 4, 0) });
                var passwordText = new TextBox
                {
                    Text = entry.Password,
                    IsReadOnly = true,
                    FontSize = 14,
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    FontWeight = FontWeights.Bold,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Padding = new Thickness(0),
                    FocusVisualStyle = null,
                    IsReadOnlyCaretVisible = false,
                    Cursor = Cursors.IBeam,
                    ToolTip = entry.Password,
                    Foreground = (Brush)Resources["NotepadPasswordTextBrush"],
                    MinWidth = 0,
                    MaxWidth = 240
                };
                var passwordBox = new Border { Background = (Brush)Resources["NotepadPasswordBrush"], Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Child = passwordText, Margin = new Thickness(8, 0, 4, 0) }; Grid.SetColumn(passwordBox, 1); row.Children.Add(passwordBox);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                var copy = new Button { Content = "复制", Background = Brush("#2196F3"), Foreground = Brushes.White, FontSize = 16, MinWidth = 82, MinHeight = 42, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(12, 8, 12, 8) }; copy.Template = RoundButtonTemplate(24); copy.Click += (_, _) => { var copied = TryCopyText(entry.Password); ShowToast(copied ? "✓ 解压码已复制。" : "⚠️ 复制失败，请手动选择密码复制。"); };
                var edit = new Button { Content = "修改", Background = Brush("#2196F3"), Foreground = Brushes.White, FontSize = 16, MinWidth = 82, MinHeight = 42, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(12, 8, 12, 8) }; edit.Template = RoundButtonTemplate(24); edit.Click += async (_, _) =>
                {
                    var oldName = entry.Name;
                    var oldPassword = entry.Password;
                    var edited = EditNotepadEntry(entry);
                    if (!edited) return;
                    if (await PersistAsync()) { Refresh(); ShowToast("✅ 修改成功"); }
                    else { entry.Name = oldName; entry.Password = oldPassword; Refresh(); }
                };
                var del = new Button { Content = "删除", Background = Brush("#2196F3"), Foreground = Brushes.White, FontSize = 16, MinWidth = 82, MinHeight = 42, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(12, 8, 12, 8) }; del.Template = RoundButtonTemplate(24); del.Click += async (_, _) =>
                {
                    if (!ConfirmNotepadDelete(entry.Name)) return;
                    var index = _personal.Notepad.IndexOf(entry);
                    _personal.Notepad.Remove(entry);
                    if (!await PersistAsync()) { _personal.Notepad.Insert(Math.Clamp(index, 0, _personal.Notepad.Count), entry); Refresh(); return; }
                    Refresh();
                    ShowToast("✓ 已删除密码");
                };
                buttons.Children.Add(copy); buttons.Children.Add(edit); buttons.Children.Add(del); Grid.SetColumn(buttons, 2); row.Children.Add(buttons);
                var card = new Border { Background = (Brush)Resources["CardBrush"], BorderBrush = (Brush)Resources["BorderBrushSoft"], BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(2, 3, 2, 3), Child = row };
                list.Items.Add(new ListBoxItem { Content = card, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            prev.IsEnabled = currentPage > 1; next.IsEnabled = currentPage < pages;
        }
        add.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(pwd.Text)) { ShowToast("⚠️ 请填写游戏名和解压码。"); return; }
            var duplicate = _personal.Notepad.Any(x => x.Name.Trim().Equals(name.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
            if (duplicate) { ShowToast("⚠️ 密码本中已有同名游戏，请编辑原记录或使用不同名称。"); return; }
            var newEntry = new NotepadEntry { Name = name.Text.Trim(), Password = pwd.Text.Trim() };
            _personal.Notepad.Add(newEntry);
            var saved = await PersistAsync();
            if (saved) { name.Clear(); pwd.Clear(); currentPage = 1; Refresh(); ShowToast("✅ 已保存"); }
            else { _personal.Notepad.Remove(newEntry); Refresh(); }
        };
        search.TextChanged += (_, _) => { currentPage = 1; Refresh(); };
        prev.Click += (_, _) => { currentPage--; Refresh(); }; next.Click += (_, _) => { currentPage++; Refresh(); };
        export.Click += async (_, _) =>
        {
            if (_personal.Notepad.Count == 0) { ShowToast("⚠️ 密码本为空"); return; }
            var dlg = new SaveFileDialog { Filter = "JSON 文件|*.json|TXT 文件|*.txt", FileName = "小花游戏密码本.json" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                if (System.IO.Path.GetExtension(dlg.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase)) await File.WriteAllLinesAsync(dlg.FileName, _personal.Notepad.Select(x => $"{x.Name}：{x.Password}"));
                else await File.WriteAllTextAsync(dlg.FileName, JsonSerializer.Serialize(_personal.Notepad, new JsonSerializerOptions { WriteIndented = true }));
                ShowToast("✓ 密码本已导出");
            }
            catch (Exception ex) { ShowToast("⚠️ 密码本导出失败：" + ex.Message); }
        };
        import.Click += async (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "密码本文件|*.json;*.txt" }; if (dlg.ShowDialog() != true) return;
            try
            {
                var importFile = new FileInfo(dlg.FileName);
                if (importFile.Length > 5 * 1024 * 1024) { ShowToast("⚠️ 文件过大，请选择小于 5MB 的文件"); return; }
                var text = await File.ReadAllTextAsync(dlg.FileName); List<NotepadEntry> rows;
                if (System.IO.Path.GetExtension(dlg.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)) rows = JsonSerializer.Deserialize<List<NotepadEntry>>(text) ?? [];
                else rows = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Select(line => { var i = line.IndexOfAny(new[] { '：', ':' }); return i < 0 ? null : new NotepadEntry { Name = line[..i].Trim(), Password = line[(i + 1)..].Trim() }; }).Where(x => x != null).Cast<NotepadEntry>().ToList();
                if (rows.Any(row => row is null)) throw new InvalidDataException("导入文件包含异常记录，未执行导入。");
                foreach (var row in rows) { row.Id = string.IsNullOrWhiteSpace(row.Id) ? Guid.NewGuid().ToString("N") : row.Id; row.Name = (row.Name ?? "").Trim(); row.Password = (row.Password ?? "").Trim(); }
                var previousNotepad = _personal.Notepad.Select(x => new NotepadEntry { Id = x.Id, Name = x.Name, Password = x.Password }).ToList();
                var addedCount = 0; var updatedCount = 0;
                foreach (var row in rows)
                {
                    if (string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Password)) continue;
                    var old = _personal.Notepad.FirstOrDefault(x => x.Name.Trim().Equals(row.Name, StringComparison.CurrentCultureIgnoreCase));
                    if (old == null) { _personal.Notepad.Add(row); addedCount++; }
                    else if (string.Equals(old.Password?.Trim(), row.Password, StringComparison.Ordinal)) { continue; }
                    else { old.Password = row.Password; updatedCount++; }
                }
                if (addedCount + updatedCount > 0)
                {
                    if (!await PersistAsync()) { _personal.Notepad = previousNotepad; Refresh(); return; }
                    currentPage = 1;
                    Refresh();
                }
                if (addedCount + updatedCount == 0)
                {
                    ShowToast("⚠️ 没有新的数据需要导入");
                }
                else
                {
                    ShowToast($"✅️ 导入完成，新增：{addedCount} 条，更新：{updatedCount} 条");
                }
            }
            catch (JsonException)
            {
                ShowToast("⚠️ 解析失败，文件损坏");
            }
            catch (InvalidDataException)
            {
                ShowToast("⚠️ 解析失败，文件损坏");
            }
            catch (IOException)
            {
                ShowToast("⚠️ 文件读取失败，请重新选择文件");
            }
            catch (Exception ex)
            {
                ShowToast("⚠️ 密码本导入失败：" + ex.Message);
            }
        };
        Refresh(); w.Content = root; ModalBackdrop.ShowDialog(w);
    }
    private bool ConfirmNotepadDelete(string gameName)
    {
        var window = new Window
        {
            Title = "", Width = 420, SizeToContent = SizeToContent.Height, MinWidth = 340,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false,
            Owner = Application.Current.MainWindow,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent,
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        var content = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        var title = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 0, 0, 12) };
        title.Children.Add(IconImage("flower", 24));
        title.Children.Add(new TextBlock { Text = "小花提示", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brush("#F44336"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(title);
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 16, Foreground = (Brush)Resources["TextBrush"], TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 22) };
        message.Inlines.Add(new System.Windows.Documents.Run("确定删除“"));
        message.Inlines.Add(new System.Windows.Documents.Run(gameName) { Foreground = Brush("#F44336"), FontWeight = FontWeights.Bold });
        message.Inlines.Add(new System.Windows.Documents.Run("”的密码吗？"));
        content.Children.Add(message);
        var buttons = new Grid(); buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition());
        var cancel = new Button { Content = "取消", Background = Brush("#2196F3"), Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold, MinHeight = 44, Margin = new Thickness(0, 0, 6, 0), Template = RoundButtonTemplate(24) };
        var confirm = new Button { Content = "确定", Background = Brush("#2196F3"), Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold, MinHeight = 44, Margin = new Thickness(6, 0, 0, 0), Template = RoundButtonTemplate(24) };
        var result = false; cancel.Click += (_, _) => window.Close(); confirm.Click += (_, _) => { result = true; window.Close(); };
        buttons.Children.Add(cancel); Grid.SetColumn(confirm, 1); buttons.Children.Add(confirm); content.Children.Add(buttons);
        window.Content = content;
        ModalBackdrop.ShowDialog(window);
        return result;
    }

    private bool EditNotepadEntry(NotepadEntry entry)
    {
        var w = DialogWindow("✏️ 修改密码", 300, 0, 0.85); var panel = new StackPanel { Margin = new Thickness(22, 16, 22, 16) };
        var nameBox = new TextBox { Text = entry.Name, MinHeight = 38, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8), Background=(Brush)Resources["SurfaceBrush"], Foreground=(Brush)Resources["TextBrush"], BorderBrush=(Brush)Resources["BorderBrushSoft"] }; var pwdBox = new TextBox { Text = entry.Password, MinHeight = 38, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8), Background=(Brush)Resources["SurfaceBrush"], Foreground=(Brush)Resources["TextBrush"], BorderBrush=(Brush)Resources["BorderBrushSoft"] };
        panel.Children.Add(new TextBlock { Text = "游戏名", FontSize = 16, Margin = new Thickness(2,0,2,2), Foreground=(Brush)Resources["TextBrush"] }); panel.Children.Add(nameBox); panel.Children.Add(new TextBlock { Text = "新密码", FontSize = 16, Margin = new Thickness(2,4,2,2), Foreground=(Brush)Resources["TextBrush"] }); panel.Children.Add(pwdBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var save = new Button { Content = "保存", FontSize = 16, MinWidth = 110, MinHeight = 42, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(4, 8, 4, 0) }; var cancel = new Button { Content = "取消", Background = Brush("#78909C"), FontSize = 16, MinWidth = 110, MinHeight = 42, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(4, 8, 4, 0) }; var result = false;
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(pwdBox.Text)) { ShowToast("⚠️ 游戏名和密码不能为空"); return; }
            var newName = nameBox.Text.Trim();
            if (_personal.Notepad.Any(x => x != entry && x.Name.Trim().Equals(newName, StringComparison.CurrentCultureIgnoreCase))) { ShowToast("⚠️ 已存在同名游戏，请修改为其他名称"); return; }
            entry.Name = newName; entry.Password = pwdBox.Text.Trim(); result = true; w.Close();
        };
        nameBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };
        pwdBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };
        cancel.Click += (_, _) => w.Close(); buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons); w.Content = panel; ModalBackdrop.ShowDialog(w); return result;
    }

}
