using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services.SpeechService;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.ExamReminder.Models;
using ClassIsland.ExamReminder.Services;
using ClassIsland.Shared;
using Markdown.Avalonia;

namespace ClassIsland.ExamReminder.Views;

[SettingsPageInfo("classisland.exam-reminder.settings", "考试提醒", "\uE7C0", "\uE7C0", SettingsPageCategory.External)]
public sealed class ExamReminderSettingsPage : SettingsPageBase
{
    private const string TextPrimaryBrush = "TextFillColorPrimaryBrush";
    private const string TextSecondaryBrush = "TextFillColorSecondaryBrush";
    private const string TextTertiaryBrush = "TextFillColorTertiaryBrush";
    private const string CardBackgroundBrush = "CardBackgroundFillColorDefaultBrush";
    private const string CardStrokeBrush = "CardStrokeColorDefaultBrush";
    private const string ControlFillBrush = "ControlFillColorDefaultBrush";
    private const string ControlFillSecondaryBrush = "ControlFillColorSecondaryBrush";
    private const string ControlStrokeBrush = "ControlStrokeColorDefaultBrush";
    private const string AccentBrush = "AccentFillColorDefaultBrush";
    private const string CriticalBrush = "SystemFillColorCriticalBrush";
    private const string SuccessBrush = "SystemFillColorSuccessBrush";
    private const string CautionBrush = "SystemFillColorCautionBrush";

    private static readonly Dictionary<string, (Color Light, Color Dark)> ThemeFallbacks = new()
    {
        [TextPrimaryBrush] = (Color.Parse("#E4000000"), Color.Parse("#FFFFFFFF")),
        [TextSecondaryBrush] = (Color.Parse("#99000000"), Color.Parse("#B3FFFFFF")),
        [TextTertiaryBrush] = (Color.Parse("#66000000"), Color.Parse("#80FFFFFF")),
        [CardBackgroundBrush] = (Color.Parse("#B3FFFFFF"), Color.Parse("#0DFFFFFF")),
        [CardStrokeBrush] = (Color.Parse("#0F000000"), Color.Parse("#12FFFFFF")),
        [ControlFillBrush] = (Color.Parse("#B3FFFFFF"), Color.Parse("#0FFFFFFF")),
        [ControlFillSecondaryBrush] = (Color.Parse("#80F9F9F9"), Color.Parse("#15FFFFFF")),
        [ControlStrokeBrush] = (Color.Parse("#19000000"), Color.Parse("#14FFFFFF")),
        [AccentBrush] = (Color.Parse("#0078D4"), Color.Parse("#60CDFF")),
        [CriticalBrush] = (Color.Parse("#C42B1C"), Color.Parse("#FF99A4")),
        [SuccessBrush] = (Color.Parse("#0F7B0F"), Color.Parse("#6CCB5F")),
        [CautionBrush] = (Color.Parse("#9D5D00"), Color.Parse("#FCE100"))
    };
    private static bool _themeFallbacksAdded;

    private readonly PluginSettingsStore _store;
    private readonly ExamBoardClient _client;
    private readonly ExamSyncService _sync;
    private readonly ExternalUriLauncher _launcher;
    private readonly ReminderSuppressionService _suppression;
    private readonly AnnouncementService _announcements;
    private readonly List<Action> _loaders = [];
    private readonly TextBox _url = new() { Watermark = "http://192.168.1.20:3000 或 https://exam.example.com" };
    private readonly TextBlock _connection = Text("", 14);
    private readonly TextBlock _nextTitle = Text("暂无考试", 26, true);
    private readonly TextBlock _nextDetail = Text("", 13);
    private readonly TextBlock _heroCountdown = Text("—", 42, true);
    private readonly TextBlock _heroLabel = Text("同步后显示", 13);
    private readonly StatusChip _heroPhase = new("暂无", ChipTone.Neutral);
    private readonly StatusChip _heroTime = new("—", ChipTone.Neutral);
    private readonly StatusChip _heroClass = new("未绑定", ChipTone.Neutral);
    private readonly TextBlock _mode = Text("", 13);
    private readonly StatusChip _modeChip = new("等待数据", ChipTone.Neutral);
    private readonly TextBlock _modeLeadValue = Text("—", 14);
    private readonly TextBlock _modeTailValue = Text("—", 14);
    private readonly TextBlock _modeBoundaryValue = Text("—", 14);
    private readonly TextBlock _modeSuppressValue = Text("—", 14);
    private readonly TextBlock _syncStatus = Text("", 12);
    private readonly TextBlock _lastSuccessValue = Text("—", 14);
    private readonly TextBlock _nextCheckValue = Text("—", 14);
    private readonly TextBlock _classValue = Text("未绑定", 14);
    private readonly StatusChip _connectionChip = new("未连接", ChipTone.Neutral);
    private readonly StatusChip _viewerChip = new("未知", ChipTone.Neutral);
    private readonly StatusChip _dataChip = new("等待数据", ChipTone.Neutral);
    private readonly TextBlock _feedback = Text("");
    private readonly TextBlock _announcementStatus = Text("", 13);
    private readonly TextBlock _announcementCount = Text("", 13);
    private readonly StatusChip _unreadChip = new("未读 0", ChipTone.Accent);
    private readonly TextBlock _suppressionStatus = Text("", 13);
    private readonly TextBlock _sourceSummary = Text("", 13);
    private readonly TextBlock _diagnostics = Text("", 12);
    private readonly TextBlock _todayHint = Text("", 13);
    private readonly TextBlock _speechStatus = Text("", 13);
    private readonly StatusChip _speechChip = new("未检查", ChipTone.Neutral);
    private readonly StackPanel _todayList = new() { Spacing = 0 };
    private readonly StackPanel _announcementList = new() { Spacing = 0 };
    private readonly StackPanel _sources = new() { Spacing = 8 };
    private readonly CheckBox _unreadOnly = new() { Content = "只看未读", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _markAllRead = new() { Content = "全部已读", Classes = { "accent" } };
    private readonly Button _refreshAnnouncements = new() { Content = "刷新" };
    private readonly Button _connect = new() { Content = "连接并在浏览器绑定", Classes = { "accent" } };
    private readonly Button _skip = new() { Content = "本时段暂不启用" };
    private readonly TabItem _announcementTab = new() { Header = "作者公告" };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<string> _expandedAnnouncements = [];
    private Border? _connectionCard;
    private Button? _disconnect;
    private bool _disconnectArmed;
    private DateTimeOffset _disconnectExpiresAt = DateTimeOffset.MinValue;
    private bool _refreshing;
    private bool _attached;
    private string _announcementFingerprint = "";
    private string _todayFingerprint = "";
    private string _stickyFeedback = "";
    private DateTimeOffset _feedbackExpiresAt = DateTimeOffset.MaxValue;

    public ExamReminderSettingsPage(PluginSettingsStore store, ExamBoardClient client, ExamSyncService sync,
        ExternalUriLauncher launcher, ReminderSuppressionService suppression, AnnouncementService announcements)
    {
        _store = store; _client = client; _sync = sync; _launcher = launcher; _suppression = suppression; _announcements = announcements;
        EnsureThemeFallbacks();
        StylePrimary(_connect);
        StylePrimary(_markAllRead);
        StyleSecondary(_refreshAnnouncements);
        StyleSecondary(_skip);
        _speechChip.Margin = new Thickness(0, 0, 8, 0);
        _speechStatus.VerticalAlignment = VerticalAlignment.Center;
        _connect.Click += async (_, _) => await ConnectAsync();
        _skip.Click += (_, _) => _sync.SkipCurrentSession();
        _markAllRead.Click += (_, _) =>
        {
            _announcements.MarkAllRead();
            _expandedAnnouncements.Clear();
            RenderAnnouncements(true);
        };
        _refreshAnnouncements.Click += (_, _) => { _expandedAnnouncements.Clear(); _announcements.Refresh(); RefreshStatus(); };
        _unreadOnly.PropertyChanged += (_, e) =>
        {
            if (e.Property != ToggleButton.IsCheckedProperty) return;
            _expandedAnnouncements.Clear();
            RenderAnnouncements(true);
        };
        var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(0, 0, 0, 4) };
        header.Children.Add(new StackPanel { Spacing = 4, Children = { Text("考试提醒", 28, true), Text("连接考试看板，让提醒跟随考试安排。", 13) } });
        var version = Text("v" + ExamBoardClient.ClientVersion, 12);
        Grid.SetColumn(version, 1); header.Children.Add(version);

        var heroCard = BuildHeroCard();
        var todayCard = Card("今日安排", new StackPanel { Spacing = 12, Children = { _todayHint, _todayList } });
        var dashboard = new Grid
        {
            ColumnDefinitions = new("1.1*,0.9*"),
            RowDefinitions = new("Auto,Auto"),
            ColumnSpacing = 16
        };
        dashboard.Children.Add(heroCard);
        Grid.SetColumn(todayCard, 1);
        dashboard.Children.Add(todayCard);

        var modeCard = Card("自动考试模式", new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _modeChip, _mode, BuildModeMetrics(),
                Text("考试模式期间屏蔽已选来源，紧急提醒保留。", 12),
                _skip
            }
        });
        var syncCard = Card("设备与同步", new StackPanel
        {
            Spacing = 12,
            Children = { BuildSyncMetrics(), BuildSyncActions(), _syncStatus }
        });
        var metrics = new Grid
        {
            ColumnDefinitions = new("*,*"),
            RowDefinitions = new("Auto,Auto"),
            ColumnSpacing = 16
        };
        metrics.Children.Add(modeCard);
        Grid.SetColumn(syncCard, 1);
        metrics.Children.Add(syncCard);

        _connectionCard = BuildConnectionCard();
        var overview = new StackPanel { Spacing = 16, Children = { dashboard, metrics, _connectionCard } };
        void LayoutOverview(double width)
        {
            var narrow = width > 0 && width < 760;
            dashboard.ColumnDefinitions = new(narrow ? "*" : "1.1*,0.9*");
            Grid.SetColumn(todayCard, narrow ? 0 : 1);
            Grid.SetRow(todayCard, narrow ? 1 : 0);
            todayCard.Margin = narrow ? new Thickness(0, 16, 0, 0) : new Thickness(0);
            metrics.ColumnDefinitions = new(narrow ? "*" : "*,*");
            Grid.SetColumn(syncCard, narrow ? 0 : 1);
            Grid.SetRow(syncCard, narrow ? 1 : 0);
            syncCard.Margin = narrow ? new Thickness(0, 16, 0, 0) : new Thickness(0);
        }
        LayoutOverview(1120);
        overview.SizeChanged += (_, e) => LayoutOverview(e.NewSize.Width);
        var options = new StackPanel { Spacing = 16 };
        options.Children.Add(Card("考试语音", new StackPanel
        {
            Spacing = 12, Children =
            {
                Switch("考试语音", "使用 ClassIsland 当前语音引擎；仍遵守宿主的语音开关。", s => s.EnableSpeech, (s,v) => s.EnableSpeech = v),
                Buttons(_speechChip, _speechStatus),
                Buttons(SecondaryButton("测试语音引擎", TestSpeechEngine)),
                Text("测试会直接调用 ClassIsland 当前语音引擎，不依赖考试数据。", 12)
            }
        }));
        options.Children.Add(Card("考前与开考提醒", new StackPanel
        {
            Spacing = 12, Children =
            {
                TextSetting("考前提醒节点", "填写提前分钟数，用逗号分隔；留空关闭考前提醒。最多 20 个，范围 1–1440。上一场未结束时不会打断播报下一场的考前提醒。",
                    s => JoinMinutes(s.ReminderMinutes), (s,v) => s.ReminderMinutes = ParseMinutes(v)),
                Switch("开考时提醒", "考试开始时显示并按语音开关播报。", s => s.EnableStartReminder, (s,v) => s.EnableStartReminder = v),
                TextSetting("考前文案", "可用占位符：{考试}、{分钟}、{班级}、{时间}、{结束时间}。", s => s.ReminderTemplate, (s,v) => s.ReminderTemplate = v),
                TextSetting("开考文案", "显示文字与语音使用同一文案。", s => s.StartTemplate, (s,v) => s.StartTemplate = v)
            }
        }));
        options.Children.Add(Card("结束提醒", new StackPanel
        {
            Spacing = 12, Children =
            {
                TextSetting("结束前提醒节点", "填写结束前分钟数，用逗号分隔；留空关闭结束前提醒。最多 20 个，范围 1–1440。",
                    s => JoinMinutes(s.EndReminderMinutes), (s,v) => s.EndReminderMinutes = ParseMinutes(v)),
                Switch("考试结束时提醒", "考试结束后显示一次提醒，按语音开关播报。", s => s.EnableExamEndReminder, (s,v) => s.EnableExamEndReminder = v),
                TextSetting("结束前文案", "可用占位符：{考试}、{分钟}、{班级}、{时间}、{结束时间}。", s => s.EndTemplate, (s,v) => s.EndTemplate = v),
                TextSetting("结束文案", "考试结束时显示的文字；可用 {考试}、{班级}、{时间}、{结束时间}。", s => s.ExamEndTemplate, (s,v) => s.ExamEndTemplate = v)
            }
        }));
        options.Children.Add(Card("安排变更与次日预告", new StackPanel
        {
            Spacing = 12, Children =
            {
                Switch("考试安排变更提醒", "考试被取消，或名称、开考、结束时间变化时提醒一次；首次同步、换班和缓存恢复不提醒。", s => s.EnableExamChangeReminder, (s,v) => s.EnableExamChangeReminder = v),
                TextSetting("变更文案", "可用占位符：{变更}、{数量}、{班级}。超过 3 处变更时自动汇总。", s => s.ExamChangeTemplate, (s,v) => s.ExamChangeTemplate = v),
                Switch("次日考试预告", "每天指定时间播报一次明天的考试；明天没有考试时保持安静。", s => s.EnableDayBeforeReminder, (s,v) => s.EnableDayBeforeReminder = v),
                TextSetting("次日预告时间", "学校当地时间，24 小时制，例如 17:30。", s => s.DayBeforeReminderTime, (s,v) =>
                {
                    if (!TimeSpan.TryParse(v.Trim(), out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromHours(24))
                        throw new ArgumentException("请输入 00:00–23:59 的时间");
                    s.DayBeforeReminderTime = time.ToString(@"hh\:mm");
                }),
                TextSetting("次日预告文案", "可用占位符：{数量}、{首场}、{首场时间}、{末场}、{末场时间}、{班级}。", s => s.DayBeforeTemplate, (s,v) => s.DayBeforeTemplate = v)
            }
        }));
        options.Children.Add(Card("提醒显示", new StackPanel
        {
            Spacing = 12, Children =
            {
                Number("提醒显示时长", "秒，3–120。", s => s.ReminderDurationSeconds, (s,v) => s.ReminderDurationSeconds = v, 3, 120),
                Number("允许延迟触发", "秒；超过此时间的提醒不补播，避免启动后集中响起。", s => s.ReminderGraceSeconds, (s,v) => s.ReminderGraceSeconds = v, 0, 600)
            }
        }));
        options.Children.Add(Card("自动考试模式", new StackPanel
        {
            Spacing = 12, Children =
            {
                Switch("启用自动考试模式", "同一上午或下午连续生效，场间也屏蔽选中的提醒。", s => s.EnableExamMode, (s,v) => s.EnableExamMode = v),
                Number("提前进入", "分钟，首场考试前开始。", s => s.ExamModeLeadMinutes, (s,v) => s.ExamModeLeadMinutes = v, 0, 240, s => s.EnableExamMode),
                Number("延后退出", "分钟，最后一场结束后继续保持。", s => s.ExamModeTailMinutes, (s,v) => s.ExamModeTailMinutes = v, 0, 240, s => s.EnableExamMode),
                Number("上午／下午分界", "学校当地时间的小时；默认 13:00，跨中午考试全程覆盖。", s => s.SessionBoundaryHour, (s,v) => s.SessionBoundaryHour = v, 1, 23, s => s.EnableExamMode),
                TextSetting("学校时区", "默认 Asia/Shanghai，也支持系统可识别的其他时区。", s => s.SchoolTimeZone, (s,v) =>
                {
                    TimeZoneInfo.FindSystemTimeZoneById(v.Trim()); s.SchoolTimeZone = v.Trim();
                })
            }
        }));
        options.Children.Add(Card("考试期间屏蔽提醒", new StackPanel
        {
            Spacing = 12, Children =
            {
                Text("勾选考试期间要屏蔽的提醒提供方。上下课整组控制，包含准备上课和课间休息。未勾选的来源正常保留。", 13),
                _suppressionStatus,
                _sourceSummary,
                _sources,
                Buttons(SecondaryButton("默认来源", () => SetSuppressed(PluginSettings.DefaultSuppressedProviderIds)),
                    SecondaryButton("全选", () => SetSuppressed(_suppression.Sources().Select(x => x.Id))),
                    SecondaryButton("清空", () => SetSuppressed([])),
                    SecondaryButton("刷新可选提醒来源", BuildSources))
            }
        }));
        options.Children.Add(Card("看板自动打开与守护", new StackPanel
        {
            Spacing = 12, Children =
            {
                Switch("自动打开考试看板", "由本地考试时间触发；看板在线时不会重复打开。", s => s.AutoOpenBoard, (s,v) => s.AutoOpenBoard = v),
                Number("提前打开", "分钟，1–240；建议至少 20 分钟。", s => s.BrowserLeadMinutes, (s,v) => s.BrowserLeadMinutes = v, 1, 240, s => s.AutoOpenBoard),
                Switch("考试期间守护看板", "网络未知不会直接当作离线；检查频率跟随同步设置。", s => s.EnableExamGuard, (s,v) => s.EnableExamGuard = v, s => s.AutoOpenBoard),
                Number("连续离线确认次数", "成功检查确认离线达到此次数后重开。", s => s.OfflineChecksBeforeReopen, (s,v) => s.OfflineChecksBeforeReopen = v, 1, 10, s => s.AutoOpenBoard && s.EnableExamGuard),
                Number("重开最短间隔", "秒，连续失败会逐步延长间隔。", s => s.BrowserCooldownSeconds, (s,v) => s.BrowserCooldownSeconds = v, 30, 3600, s => s.AutoOpenBoard),
                Number("重开退避上限", "分钟；不低于上面的最短间隔。", s => s.MaxBrowserCooldownMinutes, (s,v) => s.MaxBrowserCooldownMinutes = v, 1, 120, s => s.AutoOpenBoard)
            }
        }));
        var network = new StackPanel
        {
            Spacing = 16, Children =
            {
                Card("同步与服务器负载", new StackPanel
                {
                    Spacing = 12, Children =
                    {
                        Text("默认每分钟检查版本，只有数据变化才拉取完整考试。缩短间隔会增加服务器压力。", 13),
                        Switch("自动同步", "关闭后仅启动、配对或手动操作时同步，临时考试变更不会自动获取。", s => s.EnableAutoSync, (s,v) => { s.EnableAutoSync = v; _sync.Reschedule(); }),
                        Number("检查间隔", "秒，30–3600；默认 60 秒。保存后立即按新间隔重新计时。", s => s.SyncSeconds, (s,v) => { s.SyncSeconds = v; _sync.Reschedule(); }, 30, 3600),
                        Number("完整校验周期", "分钟；实际不会快于检查间隔。", s => s.FullSyncMinutes, (s,v) => s.FullSyncMinutes = v, 1, 1440),
                        Number("请求超时", "秒；网络等待不会阻塞考试提醒。", s => s.RequestTimeoutSeconds, (s,v) => s.RequestTimeoutSeconds = v, 3, 60),
                        Number("失败退避上限", "分钟；服务器 Retry-After 可要求等待更久。", s => s.MaxRetryMinutes, (s,v) => s.MaxRetryMinutes = v, 1, 120),
                        Switch("错开设备请求", "在检查间隔后增加少量随机延迟，减少集中访问。", s => s.StaggerRequests, (s,v) => s.StaggerRequests = v)
                    }
                }),
                Card("离线缓存", new StackPanel
                {
                    Spacing = 12, Children =
                    {
                        Switch("断网继续使用考试缓存", "使用已成功校验的考试安排，考试模式仍会按时结束。", s => s.UseOfflineCache, (s,v) => s.UseOfflineCache = v),
                        Number("缓存有效期", "小时，超过有效期后停止根据旧数据自动操作。", s => s.CacheMaxHours, (s,v) => s.CacheMaxHours = v, 1, 168, s => s.UseOfflineCache)
                    }
                }),
                Card("公告获取", new StackPanel
                {
                    Spacing = 12, Children =
                    {
                        Switch("自动检查作者公告", "仅在设置页展示，不弹窗、不播报。", s => s.EnableAnnouncements, (s,v) => { s.EnableAnnouncements = v; _announcements.Refresh(); }),
                        Number("公告检查周期", "分钟，5–1440；失败会降低检查频率。", s => s.AnnouncementRefreshMinutes, (s,v) => { s.AnnouncementRefreshMinutes = v; _announcements.Refresh(); }, 5, 1440),
                        Text("公告地址自动跟随当前看板；未连接时使用作者公开接口。", 12)
                    }
                }),
                Card("关于与诊断", new StackPanel
                {
                    Spacing = 10, Children =
                    {
                        Text("适配 ClassIsland 2.1.0.1 · 提醒屏蔽使用运行时兼容适配，不改动课表或宿主提醒设置。", 13),
                        new Expander { Header = "同步诊断", Content = _diagnostics },
                        Buttons(PrimaryButton("复制诊断信息", () => _ = CopyDiagnosticsAsync()),
                            SecondaryButton("打开项目主页", () => _launcher.TryOpen(new Uri("https://github.com/PikaNova/ClassIsland.ExamReminder"), out _)))
                    }
                })
            }
        };
        _unreadChip.VerticalAlignment = VerticalAlignment.Top;
        var announcementTop = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 16 };
        announcementTop.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children = { Text("作者公告", 17, true), _announcementStatus, _announcementCount }
        });
        Grid.SetColumn(_unreadChip, 1);
        announcementTop.Children.Add(_unreadChip);
        var announcementToolbar = new Border
        {
            Padding = new Thickness(0, 0, 0, 14),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    announcementTop,
                    Buttons(_unreadOnly, _refreshAnnouncements, _markAllRead),
                    Text("作者发布的更新与使用说明。公告只在本页展示，考试期间也不会打断提醒。", 12)
                }
            }
        };
        Themed(announcementToolbar, Border.BorderBrushProperty, CardStrokeBrush);
        _unreadOnly.Margin = new Thickness(0, 0, 12, 8);
        _announcementTab.Content = Scroll(new StackPanel
        {
            Spacing = 16,
            Children = { announcementToolbar, _announcementList }
        });
        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "概览", Content = Scroll(overview) },
                new TabItem { Header = "提醒与考试模式", Content = Scroll(options) },
                _announcementTab,
                new TabItem { Header = "连接与高级", Content = Scroll(network) }
            }
        };
        tabs.SelectionChanged += (_, _) => { if (tabs.SelectedItem == _announcementTab) RenderAnnouncements(true); };
        Content = new Grid
        {
            Margin = new Thickness(24, 12, 24, 20),
            MaxWidth = 1120,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RowDefinitions = new("Auto,Auto,*"),
            Children = { header }
        };
        var grid = (Grid)Content;
        _feedback.Margin = new(0, 4, 0, 10); Grid.SetRow(_feedback, 1); grid.Children.Add(_feedback);
        Grid.SetRow(tabs, 2); grid.Children.Add(tabs);
        _timer.Tick += (_, _) => RefreshStatus();
        AttachedToVisualTree += (_, _) =>
        {
            if (_attached) return;
            _attached = true;
            _store.Changed += OnChanged; _announcements.Changed += OnChanged;
            BuildSources(); LoadSettings(); RefreshStatus(); _timer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false; _timer.Stop();
            _store.Changed -= OnChanged; _announcements.Changed -= OnChanged;
        };
        BuildSources(); LoadSettings(); RefreshStatus();
    }

    private Control BuildHeroCard()
    {
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        header.Children.Add(Text("下一场考试", 13));
        _heroPhase.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_heroPhase, 1);
        header.Children.Add(_heroPhase);
        _heroTime.Margin = new Thickness(0, 0, 8, 8);
        _heroClass.Margin = new Thickness(0, 0, 8, 8);
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        chips.Children.Add(_heroTime);
        chips.Children.Add(_heroClass);
        var countdown = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Bottom };
        countdown.Children.Add(_heroCountdown);
        _heroLabel.VerticalAlignment = VerticalAlignment.Bottom;
        _heroLabel.Margin = new Thickness(0, 0, 0, 7);
        countdown.Children.Add(_heroLabel);
        return Card("", new StackPanel
        {
            Spacing = 10,
            Children = { header, _nextTitle, chips, countdown, _nextDetail }
        });
    }

    private Control BuildSyncMetrics()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new("*,*"),
            RowDefinitions = new("Auto,Auto,Auto"),
            ColumnSpacing = 16
        };
        AddMetric(grid, 0, 0, "连接", _connectionChip);
        AddMetric(grid, 0, 1, "班级", _classValue);
        AddMetric(grid, 1, 0, "看板", _viewerChip);
        AddMetric(grid, 1, 1, "数据", _dataChip);
        AddMetric(grid, 2, 0, "下次检查", _nextCheckValue);
        AddMetric(grid, 2, 1, "最近成功", _lastSuccessValue);
        return grid;
    }

    private Control BuildModeMetrics()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new("*,*"),
            RowDefinitions = new("Auto,Auto"),
            ColumnSpacing = 16
        };
        AddMetric(grid, 0, 0, "提前进入", _modeLeadValue);
        AddMetric(grid, 0, 1, "延后退出", _modeTailValue);
        AddMetric(grid, 1, 0, "上午/下午分界", _modeBoundaryValue);
        AddMetric(grid, 1, 1, "屏蔽来源", _modeSuppressValue);
        return grid;
    }

    private static void AddMetric(Grid grid, int row, int column, string label, Control value)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 0) };
        panel.Children.Add(Text(label, 12));
        panel.Children.Add(value);
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private Control BuildSyncActions()
    {
        _disconnect = DangerTextButton("断开本机连接", RequestDisconnect);
        return Buttons(
            PrimaryButton("立即同步", () => _sync.RequestImmediateSync()),
            SecondaryButton("完整校验", () => _sync.RequestImmediateSync(true)),
            SecondaryButton("打开看板", OpenBoard),
            SecondaryButton("测试考试提醒", () =>
            {
                if (_sync.TryShowCurrentOrNextExamReminder(out var error)) SetFeedback("已显示测试提醒");
                else SetFeedback(error, true);
            }),
            _disconnect);
    }

    private Border BuildConnectionCard() => Card("考试看板连接", new StackPanel
    {
        Spacing = 12, Children =
        {
            _connection, _url,
            Text("支持内网 HTTP、域名及端口，接口和考试大屏路径自动补全。HTTP 连接未加密。", 12),
            Buttons(_connect)
        }
    });

    private void RequestDisconnect()
    {
        if (_disconnect is not {} button) return;
        if (_disconnectArmed && DateTimeOffset.Now < _disconnectExpiresAt)
        {
            ResetDisconnectButton();
            Disconnect();
            return;
        }
        _disconnectArmed = true;
        _disconnectExpiresAt = DateTimeOffset.Now.AddSeconds(5);
        button.Content = "再次点击确认断开";
        SetFeedback("断开只会停用本机考试快照，不会撤销服务器设备授权。请再次点击确认。");
    }

    private void ResetDisconnectButton()
    {
        _disconnectArmed = false;
        _disconnectExpiresAt = DateTimeOffset.MinValue;
        if (_disconnect is {} button) button.Content = "断开本机连接";
    }

    private void Disconnect()
    {
        _store.Update(s => { s.IsPaired = false; s.PendingPairToken = ""; s.PairExpiresAt = null; s.BoundClassTag = ""; s.LastStatus = "已断开，请重新连接"; });
        _sync.ResetConnection();
    }

    private async Task ConnectAsync()
    {
        if (!ExamBoardUrls.TryNormalizeBaseUrl(_url.Text ?? "", out var url, out var error)) { SetFeedback(error, true); return; }
        _connect.IsEnabled = false;
        SetFeedback("正在创建配对，请稍候…");
        try
        {
            var token = PluginSettings.CreateSecret();
            _store.Update(s => { s.BaseUrl = url; s.IsPaired = false; s.BoundClassTag = ""; s.BoundGradeId = s.BoundClassId = ""; s.PendingPairToken = token; s.PairExpiresAt = DateTimeOffset.Now.AddMinutes(5); s.LastStatus = "正在创建浏览器配对"; s.ServerOffsetMilliseconds = 0; });
            _sync.ResetConnection();
            var s = _store.Settings;
            var result = await _client.StartPairingAsync(url, s.PluginInstanceId, s.ClientSecret, token);
            if (!result.IsSuccess || result.Value is null)
            {
                _store.Update(v => { v.PendingPairToken = ""; v.PairExpiresAt = null; v.LastStatus = result.Error ?? "配对失败"; });
                SetFeedback(result.Error ?? "配对失败", true); return;
            }
            if (_launcher.TryOpen(result.Value, out var launchError))
            {
                SetFeedback("请在浏览器中确认班级绑定。");
                _sync.RequestImmediateSync();
            }
            else SetFeedback("浏览器启动失败：" + launchError, true);
            _announcements.Refresh();
        }
        catch (Exception ex) { SetFeedback("连接失败：" + ex.Message, true); }
        finally { _connect.IsEnabled = true; }
    }

    private void OpenBoard()
    {
        if (!ExamBoardUrls.TryNormalizeBaseUrl(_store.Settings.BaseUrl, out var url, out var error)) { SetFeedback(error, true); return; }
        if (!_launcher.TryOpen(ExamBoardUrls.Board(url, _store.Settings.PluginInstanceId), out error)) SetFeedback(error, true);
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_attached) return;
        LoadSettings(); RefreshStatus();
    });

    private void LoadSettings()
    {
        _refreshing = true;
        try
        {
            if (!_url.IsFocused) _url.Text = _store.Settings.BaseUrl;
            foreach (var loader in _loaders) loader();
        }
        finally { _refreshing = false; }
    }

    private void RefreshStatus()
    {
        var s = _store.Settings;
        var now = _sync.Now;
        _connection.Text = s.IsPaired ? $"已连接 · {s.BoundClassTag}" : !string.IsNullOrEmpty(s.PendingPairToken)
            ? $"等待浏览器绑定 · 有效至 {s.PairExpiresAt:HH:mm:ss}" : "尚未连接考试看板";

        var exam = _sync.Exams.Where(x => x.EndAt > now).OrderBy(x => x.StartAt).FirstOrDefault();
        var phase = exam is null ? "" : ExamSchedule.Phase(exam, now);
        _nextTitle.Text = exam?.Name ?? "暂无考试";
        if (exam is null)
        {
            _heroCountdown.Text = "—";
            _heroLabel.Text = "同步后显示";
            _heroPhase.Text = "暂无";
            _heroPhase.SetTone(ChipTone.Neutral);
            _heroTime.IsVisible = false;
            _heroClass.Text = string.IsNullOrWhiteSpace(s.BoundClassTag) ? "未绑定班级" : s.BoundClassTag;
            _nextDetail.Text = "同步成功后将在此显示当前或下一场考试。";
        }
        else
        {
            var ending = exam.StartAt <= now;
            _heroCountdown.Text = FormatCountdown(ending ? exam.EndAt - now : exam.StartAt - now);
            _heroLabel.Text = ending ? "距离结束" : "距离开考";
            _heroPhase.Text = phase;
            _heroPhase.SetTone(phase == "进行中" ? ChipTone.Success : phase == "待开始" ? ChipTone.Accent : ChipTone.Neutral);
            var start = Local(exam.StartAt);
            var end = Local(exam.EndAt);
            _heroTime.Text = start.Date == end.Date ? $"{start:MM-dd HH:mm}–{end:HH:mm}" : $"{start:MM-dd HH:mm}–{end:MM-dd HH:mm}";
            _heroTime.IsVisible = true;
            _heroClass.Text = string.IsNullOrWhiteSpace(s.BoundClassTag) ? "未绑定班级" : s.BoundClassTag;
            _nextDetail.Text = $"{start:yyyy-MM-dd} · {phase}";
        }

        var session = _sync.NextSession;
        _modeChip.Text = _sync.ModeDescription switch
        {
            "考试模式进行中" => "进行中",
            "本时段已暂不启用" => "本时段暂停",
            "自动考试模式已关闭" => "已关闭",
            "尚未进入考试时段" => "待进入",
            _ => "等待数据"
        };
        _modeChip.SetTone(_sync.ModeDescription == "考试模式进行中" ? ChipTone.Success :
            _sync.ModeDescription is "本时段已暂不启用" or "缓存已停用或过期，等待联网" ? ChipTone.Caution :
            _sync.ModeDescription == "尚未进入考试时段" ? ChipTone.Accent : ChipTone.Neutral);
        _mode.Text = session is null
            ? "未发现即将开始的考试时段。"
            : $"{Local(session.Start):MM-dd HH:mm}–{Local(session.End):HH:mm}";
        _modeLeadValue.Text = $"{s.ExamModeLeadMinutes} 分钟";
        _modeTailValue.Text = $"{s.ExamModeTailMinutes} 分钟";
        _modeBoundaryValue.Text = $"{s.SessionBoundaryHour:00}:00";
        _modeSuppressValue.Text = $"{s.SuppressedProviders.Count} 个";
        var skippedCurrent = _sync.CurrentSession is {} active && s.SkippedSessionKey == active.Key;
        _skip.IsEnabled = s.EnableExamMode && _sync.CurrentSession is not null && !skippedCurrent;
        _skip.Content = skippedCurrent ? "本时段已跳过" : "本时段暂不启用";

        var pending = !string.IsNullOrEmpty(s.PendingPairToken);
        _connectionChip.Text = s.IsPaired ? "已连接" : pending ? "待绑定" : "未连接";
        _connectionChip.SetTone(s.IsPaired ? ChipTone.Success : pending ? ChipTone.Caution : ChipTone.Neutral);
        _classValue.Text = s.IsPaired ? s.BoundClassTag : "未绑定";
        _viewerChip.Text = !_sync.ViewerStatusKnown ? "未知" : s.ViewerOnline ? "在线" : "离线";
        _viewerChip.SetTone(!_sync.ViewerStatusKnown ? ChipTone.Neutral : s.ViewerOnline ? ChipTone.Success : ChipTone.Critical);
        _dataChip.Text = _sync.IsOfflineData ? "离线" : _sync.Exams.Count > 0 ? "最新" : "等待数据";
        _dataChip.SetTone(_sync.IsOfflineData ? ChipTone.Caution : _sync.Exams.Count > 0 ? ChipTone.Success : ChipTone.Neutral);
        _lastSuccessValue.Text = s.LastSyncAt is {} success ? $"{Local(success):MM-dd HH:mm:ss}" : "—";
        _nextCheckValue.Text = _sync.NextSyncAt is {} nextAt ? $"{Local(nextAt):HH:mm:ss}" : "已关闭";
        _syncStatus.Text = $"{_sync.SyncMode} · {s.LastStatus}" + (_sync.IsOfflineData ? $" · {_sync.OfflineWarning}" : "");
        if (_connectionCard is not null) _connectionCard.IsVisible = !s.IsPaired;
        if (_disconnect is not null)
        {
            _disconnect.IsVisible = s.IsPaired;
            if (!_disconnect.IsVisible || (_disconnectArmed && DateTimeOffset.Now >= _disconnectExpiresAt)) ResetDisconnectButton();
        }

        RenderToday(now);
        _suppressionStatus.Text = _suppression.Status;
        _speechStatus.Text = SpeechStatusText(s.EnableSpeech);
        var speech = IAppHost.TryGetService<ISpeechService>();
        var speechVolume = ISpeechService.GlobalSettings?.SpeechVolume;
        _speechChip.Text = speech is null ? "不可用" : !s.EnableSpeech ? "已关闭" : speechVolume is { } level && level <= 0.001 ? "音量为 0" : "已开启";
        _speechChip.SetTone(speech is null ? ChipTone.Critical : !s.EnableSpeech ? ChipTone.Neutral :
            speechVolume is { } volume && volume <= 0.001 ? ChipTone.Caution : ChipTone.Success);
        _diagnostics.Text = BuildRuntimeDiagnostics();
        var error = _store.SaveError ?? (_sync.RuntimeError.Length > 0 ? _sync.RuntimeError : "");
        if (error.Length > 0) { _feedback.Text = error; _stickyFeedback = error; }
        else
        {
            if (_stickyFeedback.Length > 0) { _feedback.Text = ""; _stickyFeedback = ""; }
            if (_feedbackExpiresAt != DateTimeOffset.MaxValue && DateTimeOffset.Now >= _feedbackExpiresAt)
            {
                _feedback.Text = "";
                _feedbackExpiresAt = DateTimeOffset.MaxValue;
            }
        }
        _announcementTab.Header = _announcements.UnreadCount > 0 ? $"作者公告 · {_announcements.UnreadCount}" : "作者公告";
        _announcementStatus.Text = AnnouncementStatusText();
        _unreadChip.Text = $"未读 {_announcements.UnreadCount}";
        _unreadChip.SetTone(_announcements.UnreadCount > 0 ? ChipTone.Accent : ChipTone.Neutral);
        _announcementCount.Text = $"共 {_announcements.Items.Count} 条";
        _markAllRead.IsEnabled = _announcements.UnreadCount > 0 && !_announcements.IsChecking;
        _refreshAnnouncements.IsEnabled = !_announcements.IsChecking;
        _refreshAnnouncements.Content = _announcements.IsChecking ? "检查中…" : "刷新";
        _unreadOnly.IsEnabled = _announcements.Items.Count > 0;
        RenderAnnouncements();
    }

    private string AnnouncementStatusText()
    {
        if (!_store.Settings.EnableAnnouncements) return "作者公告自动检查已关闭";
        var text = _announcements.IsChecking ? "正在检查作者公告…" : _announcements.Status;
        try { _ = _announcements.Source(); }
        catch (ArgumentException ex) { return ex.Message; }
        if (_announcements.LastCheckedAt is {} at) text += $"\n最近成功：{at:MM-dd HH:mm}";
        if (!_announcements.IsChecking && _announcements.LastAttemptAt is {} attempt &&
            (_announcements.LastCheckedAt is null || attempt > _announcements.LastCheckedAt.Value.AddSeconds(1)))
            text += $" · 上次尝试：{attempt:HH:mm}";
        return text;
    }

    private static string SpeechProviderName(ISpeechService speech) => speech.GetType().Name switch
    {
        "SystemSpeechService" => "系统 TTS",
        "EdgeTtsService" => "Edge TTS",
        "GptSoVitsService" => "GPT-SoVITS",
        _ => speech.GetType().Name
    };

    private string SpeechStatusText(bool pluginSpeechEnabled)
    {
        var speech = IAppHost.TryGetService<ISpeechService>();
        if (speech is null) return "语音引擎：宿主未提供语音服务";
        var volume = ISpeechService.GlobalSettings?.SpeechVolume;
        var volumeText = volume is null ? "宿主音量未知" : $"宿主音量 {volume:P0}";
        var line = $"语音引擎：{SpeechProviderName(speech)} · {volumeText} · 本插件考试语音{(pluginSpeechEnabled ? "已开启" : "已关闭")}";
        return volume is { } value && value <= 0.001 ? line + "\n宿主语音音量为 0，提醒不会发声。" : line;
    }

    private void TestSpeechEngine()
    {
        var speech = IAppHost.TryGetService<ISpeechService>();
        if (speech is null) { SetFeedback("宿主未提供语音服务，请重启 ClassIsland 后再试。", true); return; }
        var volume = ISpeechService.GlobalSettings?.SpeechVolume;
        if (volume is { } value && value <= 0.001)
        {
            SetFeedback("宿主语音音量为 0%，请在 ClassIsland 设置中调高音量后再测试。", true);
            return;
        }
        var provider = SpeechProviderName(speech);
        try
        {
            speech.ClearSpeechQueue();
            speech.EnqueueSpeechQueue("这是考试提醒的语音引擎测试。如果你听到这句话，说明语音引擎工作正常。");
            SetFeedback(provider == "Edge TTS"
                ? "已请求 Edge TTS 朗读。Edge TTS 需要联网；如果无声，请切换到系统 TTS。"
                : $"已请求{provider}朗读，宿主音量 {(volume ?? 1):P0}。如果无声，请检查 Windows 输出设备。", provider == "Edge TTS");
        }
        catch (Exception ex) { SetFeedback("语音引擎调用失败：" + ex.GetBaseException().Message, true); }
    }

    private string BuildRuntimeDiagnostics()
    {
        var detail = _announcements.LastErrorDetail;
        if (detail.Length > 500) detail = detail[..500] + "…";
        return $"本次运行：成功 {_sync.SuccessfulChecks} 次，失败 {_sync.FailedChecks} 次\n" +
            $"最近请求耗时：{_sync.LastRequestMilliseconds:F0} ms\n" +
            $"自动打开看板：{_sync.BrowserOpenAttempts} 次\n" +
            $"服务端校时偏差：{_store.Settings.ServerOffsetMilliseconds} ms\n" +
            $"最近尝试：{Time(_sync.LastAttemptAt)}\n" +
            $"数据校验：{Time(_sync.DataValidatedAt)}\n" +
            $"数据状态：{(_sync.IsOfflineData ? _sync.OfflineWarning : "在线/最新")}\n" +
            $"{_suppression.Status}\n" +
            $"作者公告：{_announcements.Status}" +
            (detail.Length > 0 ? $"\n公告错误：{detail}" : "");
    }

    private string BuildClipboardDiagnostics()
    {
        var s = _store.Settings;
        var lines = new[]
        {
            $"考试提醒 {ExamBoardClient.ClientVersion}",
            $"服务器：{s.BaseUrl}",
            $"班级：{s.BoundClassTag}（{(s.IsPaired ? "已连接" : "未连接")}）",
            $"同步：{_sync.SyncMode} · 成功 {_sync.SuccessfulChecks} / 失败 {_sync.FailedChecks}",
            $"最近成功：{Time(s.LastSyncAt)} · 上次尝试：{Time(_sync.LastAttemptAt)} · 数据校验：{Time(_sync.DataValidatedAt)}",
            $"数据状态：{(_sync.IsOfflineData ? _sync.OfflineWarning : "在线/最新")}",
            $"看板：{(_sync.ViewerStatusKnown ? (s.ViewerOnline ? "在线" : "离线") : "未知")} · 使用缓存：{(_sync.UsingCache ? "是" : "否")}",
            $"状态：{s.LastStatus}",
            $"提醒屏蔽：{_suppression.Status} · 已选 {s.SuppressedProviders.Count} 项",
            $"作者公告：{_announcements.Status} · 最近成功 {Time(_announcements.LastCheckedAt)}",
            _announcements.LastErrorDetail.Length > 0 ? $"公告错误：{_announcements.LastErrorDetail}" : "",
            "--- 运行诊断 ---",
            BuildRuntimeDiagnostics()
        };
        return string.Join("\n", lines.Where(x => x.Length > 0));
    }

    private async Task CopyDiagnosticsAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) { SetFeedback("无法访问剪贴板", true); return; }
        try
        {
            await clipboard.SetTextAsync(BuildClipboardDiagnostics());
            SetFeedback("诊断信息已复制");
        }
        catch (Exception ex) { SetFeedback("复制失败：" + ex.Message, true); }
    }

    private void RenderToday(DateTimeOffset now)
    {
        IReadOnlyList<ExamBoardExam> today;
        try { today = ExamSchedule.Today(_sync.Exams, _store.Settings, now); }
        catch (Exception ex)
        {
            _todayHint.Text = "无法计算今日安排：" + ex.Message;
            _todayList.Children.Clear();
            _todayFingerprint = "";
            return;
        }
        var offline = _sync.IsOfflineData;
        var fingerprint = (_sync.Exams.Count > 0 ? "1" : "0") + (offline ? "O" : "N") + "|" +
            string.Join(";", today.Select(x =>
                $"{x.Id}|{x.StartAt.UtcTicks}|{x.EndAt.UtcTicks}|{ExamSchedule.Phase(x, now)}"));
        if (fingerprint == _todayFingerprint) return;
        _todayFingerprint = fingerprint;
        _todayList.Children.Clear();
        if (_sync.Exams.Count == 0)
        {
            _todayHint.Text = "同步成功后将在此显示今天的考试安排。";
            _todayList.Children.Add(EmptyState("\uE787", "等待考试数据", "同步成功后将在此显示今天的考试安排。"));
            return;
        }
        if (today.Count == 0)
        {
            _todayHint.Text = "今天没有考试安排。" + (offline ? " · 离线数据" : "");
            _todayList.Children.Add(EmptyState("\uE787", "今天没有考试", "新的考试安排会自动同步到这里。"));
            return;
        }
        _todayHint.Text = $"共 {today.Count} 场" + (offline ? " · 离线数据" : "");
        var shown = today.Take(6).ToList();
        for (var index = 0; index < shown.Count; index++)
        {
            var exam = shown[index];
            var phase = ExamSchedule.Phase(exam, now);
            var row = new Grid { ColumnDefinitions = new("16,58,*,Auto"), ColumnSpacing = 12 };

            var rail = new Grid();
            var line = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch };
            Themed(line, Border.BackgroundProperty, ControlStrokeBrush);
            var dot = new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 0, 0)
            };
            Themed(dot, Border.BackgroundProperty, phase == "进行中" ? SuccessBrush : phase == "待开始" ? AccentBrush : ControlStrokeBrush);
            rail.Children.Add(line);
            rail.Children.Add(dot);
            row.Children.Add(rail);

            var times = new StackPanel { Spacing = 2 };
            times.Children.Add(Text($"{Local(exam.StartAt):HH:mm}", 14, true, secondary: phase == "已结束"));
            times.Children.Add(Text($"{Local(exam.EndAt):HH:mm}", 12));
            Grid.SetColumn(times, 1);
            row.Children.Add(times);

            var details = new StackPanel { Spacing = 2 };
            details.Children.Add(Text(exam.Name, 14, phase == "进行中", secondary: phase == "已结束"));
            details.Children.Add(Text($"{Math.Max(0, (int)Math.Round((exam.EndAt - exam.StartAt).TotalMinutes))} 分钟", 12));
            Grid.SetColumn(details, 2);
            row.Children.Add(details);

            var chip = new StatusChip(phase, phase == "进行中" ? ChipTone.Success : phase == "待开始" ? ChipTone.Accent : ChipTone.Neutral)
            {
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(chip, 3);
            row.Children.Add(chip);

            var container = new Border
            {
                Padding = new Thickness(0, 10),
                Child = row,
                BorderThickness = new Thickness(0, 0, 0, index < shown.Count - 1 ? 1 : 0)
            };
            Themed(container, Border.BorderBrushProperty, CardStrokeBrush);
            _todayList.Children.Add(container);
        }
        if (today.Count > shown.Count)
            _todayList.Children.Add(Text($"另有 {today.Count - shown.Count} 场，请查看看板。", 12));
    }

    private static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "—";

    private DateTimeOffset Local(DateTimeOffset value)
    {
        try { return TimeZoneInfo.ConvertTime(value, ExamSchedule.TimeZone(_store.Settings)); }
        catch { return value.ToLocalTime(); }
    }

    private void BuildSources()
    {
        _sources.Children.Clear();
        var sources = _suppression.Sources();
        foreach (var source in sources)
        {
            var check = new CheckBox
            {
                Content = new TextBlock { Text = source.Name, TextWrapping = TextWrapping.Wrap },
                IsChecked = _store.Settings.SuppressedProviders.Contains(source.Id, StringComparer.OrdinalIgnoreCase),
                IsEnabled = _suppression.Supported
            };
            check.Click += (_, _) =>
            {
                Save(() => _store.Update(s => s.SuppressedProviders = check.IsChecked == true
                    ? s.SuppressedProviders.Append(source.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : s.SuppressedProviders.Where(x => !x.Equals(source.Id, StringComparison.OrdinalIgnoreCase)).ToList()));
                UpdateSourceSummary(sources);
            };
            _sources.Children.Add(check);
        }
        if (sources.Count == 0) _sources.Children.Add(Text("未发现可选的提醒来源。", 13));
        UpdateSourceSummary(sources);
    }

    private void UpdateSourceSummary(IReadOnlyList<ReminderSource>? sources = null)
    {
        sources ??= _suppression.Sources();
        var selected = sources.Count(x => _store.Settings.SuppressedProviders.Contains(x.Id, StringComparer.OrdinalIgnoreCase));
        _sourceSummary.Text = _suppression.Supported
            ? $"已选择 {selected} / {sources.Count} 个来源；未勾选的来源保持正常提醒。"
            : $"当前宿主不支持提醒屏蔽；{_suppression.Status}";
    }

    private void SetSuppressed(IEnumerable<string> ids)
    {
        if (!_suppression.Supported) { SetFeedback("当前宿主不支持提醒屏蔽", true); return; }
        var next = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save(() => _store.Update(s => s.SuppressedProviders = next));
        BuildSources();
    }

    private void RenderAnnouncements(bool force = false)
    {
        if (force) _expandedAnnouncements.Clear();
        var all = _announcements.Items;
        _expandedAnnouncements.IntersectWith(all.Select(x => x.ReadKey));
        var unreadOnly = _unreadOnly.IsChecked == true;
        var items = unreadOnly
            ? all.Where(x => !_announcements.IsRead(x) || _expandedAnnouncements.Contains(x.ReadKey)).ToList()
            : all;
        string source;
        try { source = _announcements.Source().AbsoluteUri; }
        catch (ArgumentException) { return; }
        var fingerprint = AnnouncementFingerprint(source, all, unreadOnly);
        if (!force && fingerprint == _announcementFingerprint) return;
        _announcementFingerprint = fingerprint;
        _announcementList.Children.Clear();
        if (all.Count == 0)
        {
            _announcementList.Children.Add(EmptyState("\uE7C3", "暂无作者公告", "成功获取后将在此显示；网络失败时保留上次缓存。"));
            return;
        }
        if (items.Count == 0)
        {
            _announcementList.Children.Add(EmptyState("\uE73E", "没有未读公告", "已读公告仍会保留；取消“只看未读”即可查看。"));
            return;
        }
        foreach (var item in items)
        {
            var key = item.ReadKey;
            var pinnedChip = new StatusChip("置顶", ChipTone.Accent)
            {
                IsVisible = item.Pinned,
                Margin = new Thickness(0, 0, 6, 0)
            };
            var unreadChip = new StatusChip("未读", ChipTone.Critical) { IsVisible = !_announcements.IsRead(item) };
            var chips = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            chips.Children.Add(pinnedChip);
            chips.Children.Add(unreadChip);
            var title = Text(item.Title, 15, true);
            var updated = Text(DateTimeOffset.FromUnixTimeMilliseconds(item.UpdatedAt).ToLocalTime().ToString("MM-dd HH:mm"), 12);
            var header = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8 };
            header.Children.Add(chips);
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            Grid.SetColumn(updated, 2);
            header.Children.Add(updated);

            var expander = new Expander
            {
                Header = header,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0)
            };
            expander.Styles.Add(new Style(x => x.OfType<ToggleButton>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0))
                }
            });
            expander.Styles.Add(new Style(x => x.OfType<ContentPresenter>())
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) }
            });
            expander.Expanding += (_, _) =>
            {
                // Keep a read article visible until the list is refreshed or the filter changes.
                _expandedAnnouncements.Add(key);
                if (expander.Content is null)
                {
                    var body = new StackPanel { Spacing = 12, Margin = new Thickness(0, 6, 0, 4) };
                    body.Children.Add(Text(DateTimeOffset.FromUnixTimeMilliseconds(item.UpdatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 12));
                    body.Children.Add(new MarkdownScrollViewer
                    {
                        Markdown = item.Content,
                        AssetPathRoot = _announcements.Source().GetLeftPart(UriPartial.Authority),
                        MaxHeight = 640
                    });
                    if (Uri.TryCreate(item.Url, UriKind.Absolute, out var link) && link.Scheme is "http" or "https")
                        body.Children.Add(SecondaryButton(item.ButtonLabel ?? "打开文档", () => _launcher.TryOpen(link, out _)));
                    expander.Content = body;
                }
                _announcements.MarkRead(item);
                unreadChip.IsVisible = false;
                // Preserve the expanded article instead of rebuilding it when marking read.
                _announcementFingerprint = AnnouncementFingerprint(source, _announcements.Items, _unreadOnly.IsChecked == true);
            };
            var row = new Border
            {
                Padding = new Thickness(0, 14),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = expander
            };
            Themed(row, Border.BorderBrushProperty, CardStrokeBrush);
            _announcementList.Children.Add(row);
        }
    }

    private string AnnouncementFingerprint(string source, IReadOnlyList<AuthorAnnouncement> items, bool unreadOnly) =>
        string.Join(";", items.Select(x => $"{x.ReadKey}|{_announcements.IsRead(x)}")) +
        $"|{source}|{items.Count}|{unreadOnly}|{string.Join(",", _expandedAnnouncements.OrderBy(x => x))}";

    private Control Switch(string title, string help, Func<PluginSettings, bool> get, Action<PluginSettings, bool> set, Func<PluginSettings, bool>? enabled = null)
    {
        var toggle = new ToggleSwitch { VerticalAlignment = VerticalAlignment.Center, OnContent = "开启", OffContent = "关闭" };
        toggle.PropertyChanged += (_, e) =>
        {
            if (!_refreshing && e.Property == ToggleButton.IsCheckedProperty) Save(() => _store.Update(s => set(s, toggle.IsChecked == true)));
        };
        _loaders.Add(() => { toggle.IsChecked = get(_store.Settings); toggle.IsEnabled = enabled?.Invoke(_store.Settings) ?? true; });
        return SettingRow(title, help, toggle);
    }

    private Control Number(string title, string help, Func<PluginSettings, int> get, Action<PluginSettings, int> set, int min, int max, Func<PluginSettings, bool>? enabled = null)
    {
        var number = new NumericUpDown { Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Width = 116, HorizontalAlignment = HorizontalAlignment.Right };
        number.ValueChanged += (_, _) =>
        {
            if (!_refreshing && number.Value is {} value) Save(() => _store.Update(s => set(s, (int)value)));
        };
        _loaders.Add(() =>
        {
            if (!number.IsKeyboardFocusWithin) number.Value = get(_store.Settings);
            number.IsEnabled = enabled?.Invoke(_store.Settings) ?? true;
        });
        return SettingRow(title, help, number);
    }

    private Control TextSetting(string title, string help, Func<PluginSettings, string> get, Action<PluginSettings, string> set)
    {
        var box = new TextBox { MaxLength = 2000, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.LostFocus += (_, _) =>
        {
            if (!_refreshing && box.Text != get(_store.Settings)) Save(() => _store.Update(s => set(s, box.Text ?? "")));
        };
        _loaders.Add(() => { if (!box.IsFocused) box.Text = get(_store.Settings); });
        return new StackPanel { Spacing = 6, Children = { Text(title, 14, true), Text(help, 12), box } };
    }

    private void Save(Action action)
    {
        try
        {
            action();
            if (_store.SaveError is {} saveError) SetFeedback(saveError, true);
            else SetFeedback("设置已保存");
        }
        catch (Exception ex) { SetFeedback("设置未应用：" + ex.Message, true); }
    }

    private void SetFeedback(string text, bool sticky = false)
    {
        _feedback.Text = text;
        _stickyFeedback = "";
        _feedbackExpiresAt = sticky ? DateTimeOffset.MaxValue : DateTimeOffset.Now.AddSeconds(6);
    }

    private static string JoinMinutes(IEnumerable<int>? minutes) => string.Join(", ", minutes ?? Array.Empty<int>());

    private static List<int> ParseMinutes(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var pieces = value.Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<int>();
        foreach (var piece in pieces)
        {
            if (!int.TryParse(piece, out var minute) || minute < 1 || minute > 1440) throw new ArgumentException("提醒分钟数需在 1–1440 之间");
            result.Add(minute);
        }
        if (result.Distinct().Count() > 20) throw new ArgumentException("最多设置 20 个提醒节点");
        return result.Distinct().OrderDescending().ToList();
    }

    private static Control SettingRow(string title, string help, Control editor)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(Text(title, 14, true)); panel.Children.Add(Text(help, 12));
        var row = new Grid { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 20 };
        row.Children.Add(panel); Grid.SetColumn(editor, 1); row.Children.Add(editor);
        var narrow = false;
        row.SizeChanged += (_, e) =>
        {
            var next = e.NewSize.Width < 420;
            if (next == narrow) return;
            narrow = next;
            row.ColumnDefinitions = new(narrow ? "*" : "*,Auto");
            Grid.SetColumn(editor, narrow ? 0 : 1); Grid.SetRow(editor, narrow ? 1 : 0);
            editor.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            editor.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        };
        return row;
    }

    private static Border Card(string title, Control content)
    {
        var panel = new StackPanel { Spacing = 14 };
        if (title.Length > 0) panel.Children.Add(Text(title, 17, true));
        panel.Children.Add(content);
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            BorderThickness = new Thickness(1),
            Child = panel
        };
        Themed(card, Border.BackgroundProperty, CardBackgroundBrush);
        Themed(card, Border.BorderBrushProperty, CardStrokeBrush);
        return card;
    }

    private static TextBlock Text(string text, double size = 14, bool bold = false, bool? secondary = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            TextWrapping = TextWrapping.Wrap
        };
        Themed(block, TextBlock.ForegroundProperty,
            secondary == true || (secondary is null && size <= 13) ? TextSecondaryBrush : TextPrimaryBrush);
        return block;
    }

    private static ScrollViewer Scroll(Control content) => new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = new Border { Margin = new Thickness(0, 16, 0, 24), Child = content }
    };

    private static Button PrimaryButton(string text, Action action) =>
        StylePrimary(new Button { Content = text, Classes = { "accent" } }, action);

    private static Button StylePrimary(Button button, Action? action = null)
    {
        button.CornerRadius = new CornerRadius(10);
        button.Padding = new Thickness(14, 6);
        button.Margin = new Thickness(0, 0, 8, 8);
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }

    private static Button SecondaryButton(string text, Action action) =>
        StyleSecondary(new Button { Content = text }, action);

    private static Button StyleSecondary(Button button, Action? action = null)
    {
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(10);
        button.Padding = new Thickness(12, 6);
        button.Margin = new Thickness(0, 0, 8, 8);
        Themed(button, Button.BorderBrushProperty, ControlStrokeBrush);
        Themed(button, Button.ForegroundProperty, TextPrimaryBrush);
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }

    private static Button DangerTextButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6),
            Margin = new Thickness(0, 0, 8, 8)
        };
        Themed(button, Button.ForegroundProperty, CriticalBrush);
        button.Click += (_, _) => action();
        return button;
    }

    private static WrapPanel Buttons(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    private static Control EmptyState(string glyph, string title, string description)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Themed(icon, TextBlock.ForegroundProperty, TextTertiaryBrush);
        var titleBlock = Text(title, 15, true);
        titleBlock.HorizontalAlignment = HorizontalAlignment.Center;
        var descriptionBlock = Text(description, 12);
        descriptionBlock.HorizontalAlignment = HorizontalAlignment.Center;
        descriptionBlock.TextAlignment = TextAlignment.Center;
        var panel = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24)
        };
        panel.Children.Add(icon);
        panel.Children.Add(titleBlock);
        panel.Children.Add(descriptionBlock);
        return panel;
    }

    private static string FormatCountdown(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalDays >= 1
            ? $"{(int)value.TotalDays}天 {value.Hours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }

    private static void Themed(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, new DynamicResourceExtension(key));

    private static void EnsureThemeFallbacks()
    {
        if (_themeFallbacksAdded || Application.Current is not { } app) return;
        _themeFallbacksAdded = true;
        var missing = ThemeFallbacks.Where(pair => !app.TryFindResource(pair.Key, out _)).ToList();
        if (missing.Count == 0) return;
        var light = new ResourceDictionary();
        var dark = new ResourceDictionary();
        foreach (var (key, colors) in missing)
        {
            light[key] = new SolidColorBrush(colors.Light);
            dark[key] = new SolidColorBrush(colors.Dark);
        }
        var fallback = new ResourceDictionary();
        fallback.ThemeDictionaries[ThemeVariant.Light] = light;
        fallback.ThemeDictionaries[ThemeVariant.Dark] = dark;
        app.Resources.MergedDictionaries.Add(fallback);
    }

    private enum ChipTone
    {
        Neutral,
        Accent,
        Success,
        Critical,
        Caution
    }

    private sealed class StatusChip : Border
    {
        private readonly TextBlock _label;
        private ChipTone _tone = (ChipTone)(-1);

        public StatusChip(string text, ChipTone tone = ChipTone.Neutral)
        {
            _label = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Child = _label;
            CornerRadius = new CornerRadius(999);
            Padding = new Thickness(8, 3);
            BorderThickness = new Thickness(1);
            HorizontalAlignment = HorizontalAlignment.Left;
            Themed(this, BackgroundProperty, ControlFillSecondaryBrush);
            Themed(this, BorderBrushProperty, ControlStrokeBrush);
            SetTone(tone);
        }

        public string Text
        {
            get => _label.Text ?? "";
            set => _label.Text = value;
        }

        public void SetTone(ChipTone tone)
        {
            if (_tone == tone) return;
            _tone = tone;
            Themed(_label, TextBlock.ForegroundProperty, tone switch
            {
                ChipTone.Accent => AccentBrush,
                ChipTone.Success => SuccessBrush,
                ChipTone.Critical => CriticalBrush,
                ChipTone.Caution => CautionBrush,
                _ => TextSecondaryBrush
            });
        }
    }
}
