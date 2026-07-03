using System;
using System.Diagnostics;
using System.Globalization;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace TxtAIEditor.Core.Services
{
    internal static class AppBadgeNotificationService
    {
        public static void UpdateBadge(int count)
        {
            try
            {
                BadgeUpdater updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
                if (count <= 0)
                {
                    updater.Clear();
                    return;
                }

                int badgeValue = Math.Min(99, Math.Max(1, count));
                var xml = new XmlDocument();
                xml.LoadXml(string.Format(
                    CultureInfo.InvariantCulture,
                    "<badge value=\"{0}\"/>",
                    badgeValue));
                updater.Update(new BadgeNotification(xml));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to update app badge: {ex.Message}");
            }
        }
    }
}
