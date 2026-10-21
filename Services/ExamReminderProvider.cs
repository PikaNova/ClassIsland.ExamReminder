using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using ClassIsland.ExamReminder.Models;
using NotificationSettings = ClassIsland.Shared.Models.Notification.NotificationSettings;

namespace ClassIsland.ExamReminder.Services;

public enum ExamReminderKind
{
    BeforeStart,
    BeforeEnd,
    Ended,
    ScheduleChanged,
    DayBefore
}

[NotificationProviderInfo(ProviderId, "考试提醒", "\uE7C0", "从考试看板同步考试，并在开考前显示提醒")]
public sealed class ExamReminderProvider(PluginSettingsStore store) : NotificationProviderBase
{
    public const string ProviderId = "8A997522-E450-4FE8-924F-04A4829B8B5C";

    public void ShowExamChangeReminder(IReadOnlyList<ExamChange> changes, string dataWarning = "")
    {
        var settings = store.Settings;
        if (!settings.EnableExamChangeReminder || changes.Count == 0) return;
        var text = ExamSchedule.FormatChange(settings.ExamChangeTemplate, changes, settings);
        Dispatcher.UIThread.Post(() =>
        {
            var mask = NotificationContent.CreateTwoIconsMask(text);
            mask.SpeechContent = text;
            mask.IsSpeechEnabled = settings.EnableSpeech;
            mask.Duration = TimeSpan.FromSeconds(settings.ReminderDurationSeconds);
            var overlayText = $"考试安排变更 · {settings.BoundClassTag}";
            if (!string.IsNullOrWhiteSpace(dataWarning)) overlayText += "\n" + dataWarning;
            var overlay = NotificationContent.CreateSimpleTextContent(overlayText);
            overlay.IsSpeechEnabled = false;
            ShowNotification(new NotificationRequest
            {
                MaskContent = mask,
                OverlayContent = overlay,
                RequestNotificationSettings = new NotificationSettings
                {
                    IsSettingsEnabled = true,
                    IsNotificationEnabled = true,
                    IsSpeechEnabled = settings.EnableSpeech
                }
            });
        });
    }

    public void ShowDayBeforeReminder(IReadOnlyList<ExamBoardExam> exams, string dataWarning = "")
    {
        var settings = store.Settings;
        if (!settings.EnableDayBeforeReminder || exams.Count == 0) return;
        var text = ExamSchedule.FormatDayBefore(settings.DayBeforeTemplate, exams, settings);
        Dispatcher.UIThread.Post(() =>
        {
            var mask = NotificationContent.CreateTwoIconsMask(text);
            mask.SpeechContent = text;
            mask.IsSpeechEnabled = settings.EnableSpeech;
            mask.Duration = TimeSpan.FromSeconds(settings.ReminderDurationSeconds);
            var overlayText = $"次日考试预告 · {settings.BoundClassTag}";
            if (!string.IsNullOrWhiteSpace(dataWarning)) overlayText += "\n" + dataWarning;
            var overlay = NotificationContent.CreateSimpleTextContent(overlayText);
            overlay.IsSpeechEnabled = false;
            ShowNotification(new NotificationRequest
            {
                MaskContent = mask,
                OverlayContent = overlay,
                RequestNotificationSettings = new NotificationSettings
                {
                    IsSettingsEnabled = true,
                    IsNotificationEnabled = true,
                    IsSpeechEnabled = settings.EnableSpeech
                }
            });
        });
    }

    public void ShowExamReminder(ExamBoardExam exam, TimeSpan remaining,
        ExamReminderKind kind = ExamReminderKind.BeforeStart, string dataWarning = "")
    {
        var settings = store.Settings;
        var minutes = kind == ExamReminderKind.Ended ? 0 : Math.Max(0, (int)Math.Ceiling(remaining.TotalMinutes));
        var template = kind switch
        {
            ExamReminderKind.BeforeEnd => settings.EndTemplate,
            ExamReminderKind.Ended => settings.ExamEndTemplate,
            _ => minutes == 0 ? settings.StartTemplate : settings.ReminderTemplate
        };
        var text = ExamSchedule.Format(template, exam, minutes, settings);
        var time = TimeZoneInfo.ConvertTime(kind == ExamReminderKind.BeforeStart ? exam.StartAt : exam.EndAt,
            ExamSchedule.TimeZone(settings));
        var overlayText = kind switch
        {
            ExamReminderKind.BeforeEnd => $"结束时间：{time:HH:mm} · {settings.BoundClassTag}",
            ExamReminderKind.Ended => $"考试已结束：{time:HH:mm} · {settings.BoundClassTag}",
            _ => $"开考时间：{time:HH:mm} · {settings.BoundClassTag}"
        };
        if (!string.IsNullOrWhiteSpace(dataWarning)) overlayText += "\n" + dataWarning;
        Dispatcher.UIThread.Post(() =>
        {
            var mask = NotificationContent.CreateTwoIconsMask(text);
            mask.SpeechContent = text;
            mask.IsSpeechEnabled = settings.EnableSpeech;
            mask.Duration = TimeSpan.FromSeconds(settings.ReminderDurationSeconds);
            var overlay = NotificationContent.CreateSimpleTextContent(overlayText);
            overlay.IsSpeechEnabled = false;
            ShowNotification(new NotificationRequest
            {
                MaskContent = mask,
                OverlayContent = overlay,
                RequestNotificationSettings = new NotificationSettings
                {
                    IsSettingsEnabled = true,
                    IsNotificationEnabled = true,
                    IsSpeechEnabled = settings.EnableSpeech
                }
            });
        });
    }
}
