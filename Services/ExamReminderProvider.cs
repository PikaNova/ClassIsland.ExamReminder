using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using ClassIsland.ExamReminder.Models;

namespace ClassIsland.ExamReminder.Services;

[NotificationProviderInfo(
    "8A997522-E450-4FE8-924F-04A4829B8B5C",
    "考试提醒",
    "\uE7C0",
    "从考试看板同步考试，并在开考前显示提醒")]
public sealed class ExamReminderProvider : NotificationProviderBase
{
    public void ShowExamReminder(ExamBoardExam exam, TimeSpan remaining)
    {
        var examName = exam.Name.EndsWith("考试", StringComparison.Ordinal) ||
                       exam.Name.EndsWith("测试", StringComparison.Ordinal)
            ? exam.Name
            : $"{exam.Name}考试";
        var hasStarted = remaining <= TimeSpan.Zero;
        var reminderText = hasStarted
            ? $"{examName}现在开始"
            : $"{FormatRemaining(remaining)}后开始{examName}";

        Dispatcher.UIThread.Post(() => ShowNotification(new NotificationRequest
        {
            MaskContent = NotificationContent.CreateTwoIconsMask(reminderText),
            OverlayContent = NotificationContent.CreateSimpleTextContent(
                $"开考时间：{exam.StartAt.ToLocalTime():HH:mm}"),
        }));
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromMinutes(1))
        {
            return "不到 1 分钟";
        }

        var totalMinutes = Math.Max(1, (int)Math.Floor(remaining.TotalMinutes));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        if (hours == 0)
        {
            return $"{minutes} 分钟";
        }

        return minutes == 0
            ? $"{hours} 小时"
            : $"{hours} 小时 {minutes} 分钟";
    }
}
