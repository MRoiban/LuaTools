using LuaToolsGui.Resources;
using LuaToolsGui.Services.Cabinet;

namespace LuaToolsGui.Services;

public static class CabinetNotificationLocalizer
{
    public static string Format(CabinetNotification notification)
    {
        string Items(string template) => string.Join("; ", notification.Items.Select(item =>
        {
            var split = item.LastIndexOf('|');
            return split < 0 ? item : string.Format(template, item[..split], item[(split + 1)..]);
        }));
        return notification.Kind switch
        {
            CabinetNotificationKind.AchievementsSynced => string.Format(Strings.Cabinet_Notification_AchievementsSynced, Items(Strings.Cabinet_Notification_AchievementItem)),
            CabinetNotificationKind.AlreadyInSync => Strings.Cabinet_Notification_AlreadyInSync,
            CabinetNotificationKind.PlaytimeAdded => string.Format(Strings.Cabinet_Notification_PlaytimeAdded, Items(Strings.Cabinet_Notification_PlaytimeItem)),
            CabinetNotificationKind.AchievementListsFound => string.Format(Strings.Cabinet_Notification_AchievementListsFound, notification.Count),
            CabinetNotificationKind.WorkshopChanged => string.Format(Strings.Cabinet_Notification_WorkshopChanged, notification.Count, string.Join(", ", notification.Items)),
            _ => throw new ArgumentOutOfRangeException(nameof(notification)),
        };
    }
}
