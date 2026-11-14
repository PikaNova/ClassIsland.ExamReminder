using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.ExamReminder.Services;

namespace ClassIsland.ExamReminder.Views;

public sealed class ExamReminderProviderControlSettings
{
    public bool EnableBeforeStartReminder { get; set; } = true;
    public bool EnableStartReminder { get; set; } = true;
    public bool EnableBeforeEndReminder { get; set; } = true;
    public bool EnableExamEndReminder { get; set; } = true;
    public bool EnableExamChangeReminder { get; set; } = true;
    public bool EnableDayBeforeReminder { get; set; }
}

public sealed class ExamReminderProviderSettingsControl : NotificationProviderControlBase<ExamReminderProviderControlSettings>
{
    private readonly PluginSettingsStore _store;
    private readonly Dictionary<ReminderType, CheckBox> _checks = [];

    public ExamReminderProviderSettingsControl(PluginSettingsStore store)
    {
        _store = store;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "提醒类型",
            FontSize = 18,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "选择本插件要显示的提醒。具体时间节点和文案请在插件设置页配置。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.72
        });

        foreach (var item in ExamSchedule.ReminderTypes)
        {
            var check = new CheckBox
            {
                Margin = new Thickness(0, 4, 0, 0),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = item.Title, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                        new TextBlock
                        {
                            Text = item.Description,
                            FontSize = 12,
                            Opacity = 0.64,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        }
                    }
                }
            };
            check.Click += (_, _) =>
            {
                var enabled = check.IsChecked == true;
                _store.Update(settings => ExamSchedule.SetReminderTypeEnabled(settings, item.Type, enabled));
                SetProviderSetting(item.Type, enabled);
            };
            _checks[item.Type] = check;
            panel.Children.Add(check);
        }

        panel.Children.Add(new TextBlock
        {
            Text = "自动考试模式按绑定班级的完整适用日程运行，不受提醒类型选择影响。",
            FontSize = 12,
            Opacity = 0.64,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        Content = panel;
        AttachedToVisualTree += (_, _) =>
        {
            _store.Changed -= OnStoreChanged;
            _store.Changed += OnStoreChanged;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) => _store.Changed -= OnStoreChanged;
        Refresh();
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        foreach (var (type, check) in _checks)
            check.IsChecked = ExamSchedule.IsReminderTypeEnabled(_store.Settings, type);
    }

    private void SetProviderSetting(ReminderType type, bool enabled)
    {
        switch (type)
        {
            case ReminderType.Upcoming: Settings.EnableBeforeStartReminder = enabled; break;
            case ReminderType.Start: Settings.EnableStartReminder = enabled; break;
            case ReminderType.BeforeEnd: Settings.EnableBeforeEndReminder = enabled; break;
            case ReminderType.Ended: Settings.EnableExamEndReminder = enabled; break;
            case ReminderType.ScheduleChange: Settings.EnableExamChangeReminder = enabled; break;
            case ReminderType.DayBefore: Settings.EnableDayBeforeReminder = enabled; break;
        }
    }
}
