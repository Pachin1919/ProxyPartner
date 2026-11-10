using ProxyPartnerLite.Desktop;

bool startInTray = args.Contains("--tray");
string? settingsPath = Environment.GetEnvironmentVariable("PROXYPARTNER_DESKTOP_SETTINGS");
if (args.Contains("--demo") || args.Contains("--stress-1000"))
    return DesktopApp.Run(args.Contains("--stress-1000") ? new BaselineSource(1000) : new BaselineSource(),
        startInTray, settingsPath);

using var source = new LiveHostSource();
return DesktopApp.Run(source, startInTray, settingsPath);
