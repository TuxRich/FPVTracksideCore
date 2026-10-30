using System.Reflection;

namespace Tools
{
    public static class AppVersion
    {
        // The app's version for the title bar and logs: its informational version without the commit id after the '+'.
        // A build that isn't an official release has a label on the end, e.g. 2.78.0.906-BIRD (see Directory.Build.targets).
        public static string Text { get; } = GetText();

        private static string GetText()
        {
            Assembly entry = Assembly.GetEntryAssembly();
            if (entry == null)
                return "";

            string informational = entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(informational))
                return entry.GetName().Version.ToString();

            int metadata = informational.IndexOf('+');
            if (metadata >= 0)
                return informational.Substring(0, metadata);

            return informational;
        }
    }
}
