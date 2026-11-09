using ProxyPartnerLite.Desktop;

var source = args is ["--stress-1000"] ? new BaselineSource(1000) : new BaselineSource();
return DesktopApp.Run(source, startInTray: args is ["--tray"]);
