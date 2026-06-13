using System;

namespace TxtAIEditor.Core.Services
{
    internal static class SettingsAppVersionProvider
    {
        public static string GetAppVersion()
        {
            try
            {
                try
                {
                    var package = Windows.ApplicationModel.Package.Current;
                    var version = package.Id.Version;
                    return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
                }
                catch (InvalidOperationException)
                {
                }

                string dir = AppContext.BaseDirectory;
                while (!string.IsNullOrEmpty(dir))
                {
                    string manifestPath = System.IO.Path.Combine(dir, "Package.appxmanifest");
                    if (System.IO.File.Exists(manifestPath))
                    {
                        var doc = new System.Xml.XmlDocument();
                        doc.Load(manifestPath);
                        var nsmgr = new System.Xml.XmlNamespaceManager(doc.NameTable);
                        nsmgr.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
                        var identityNode = doc.SelectSingleNode("//f:Identity", nsmgr) ?? doc.SelectSingleNode("//Identity");
                        if (identityNode is System.Xml.XmlElement element)
                        {
                            string version = element.GetAttribute("Version");
                            if (!string.IsNullOrEmpty(version))
                            {
                                return version;
                            }
                        }
                    }

                    string? parent = System.IO.Path.GetDirectoryName(dir);
                    if (parent == dir || string.IsNullOrEmpty(parent))
                    {
                        break;
                    }

                    dir = parent;
                }
            }
            catch
            {
            }

            try
            {
                var assemblyVersion = typeof(SettingsDialogService).Assembly.GetName().Version;
                if (assemblyVersion != null)
                {
                    return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}.{assemblyVersion.Revision}";
                }
            }
            catch
            {
            }

            return "1.0.1.0";
        }
    }
}
