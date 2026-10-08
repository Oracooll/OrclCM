using System.Reflection;

[assembly: AssemblyTitle(OrclCM.AppInfo.Name)]
[assembly: AssemblyProduct(OrclCM.AppInfo.Name)]
[assembly: AssemblyCompany(OrclCM.AppInfo.Name)]
[assembly: AssemblyDescription("OrclCM - live laptop charging wattage")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Oracooll")]
[assembly: AssemblyVersion(OrclCM.AppInfo.FileVersion)]
[assembly: AssemblyFileVersion(OrclCM.AppInfo.FileVersion)]
[assembly: AssemblyInformationalVersion(OrclCM.AppInfo.Version)]

namespace OrclCM
{
    static class AppInfo
    {
        public const string Name = "OrclCM";
        // Shown in the window title bar. Format 1.X.XXX; keep FileVersion in step (tests check it).
        public const string Version = "1.4.000";
        public const string FileVersion = "1.4.0.0";
    }
}
