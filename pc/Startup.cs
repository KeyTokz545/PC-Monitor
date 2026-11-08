using System;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace PCMonitor;

/// <summary>Démarrage avec Windows (tâche planifiée, sans demande d'autorisation) et pare-feu.</summary>
public static class Startup
{
    const string TaskName = "PC Monitor";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PCMonitor.exe");

    public static bool IsEnabled() => Win.Run("schtasks", $"/Query /TN \"{TaskName}\"", 8000).Code == 0;

    public static bool Enable()
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var exe = SecurityElement.Escape(ExePath);
        // Les réglages par défaut de schtasks empêchent le démarrage sur batterie : on passe par un fichier XML.
        var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Lance PC Monitor à l'ouverture de session.</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author""><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>{exe}</Command><Arguments>--startup</Arguments></Exec>
  </Actions>
</Task>";
        var file = Path.Combine(Path.GetTempPath(), "pcmonitor-task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        var (code, output) = Win.Run("schtasks", $"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", 15000);
        try { File.Delete(file); } catch { }
        if (code != 0) Log.Info("schtasks /Create a échoué : " + Win.Decode(output));
        return code == 0;
    }

    public static void Disable() => Win.Run("schtasks", $"/Delete /TN \"{TaskName}\" /F", 8000);

    /// <summary>Ouvre les ports du tableau de bord et de la découverte sur les réseaux privés.</summary>
    public static void EnsureFirewall(int tcpPort, int udpPort)
    {
        Rule($"PC Monitor (TCP {tcpPort})", "TCP", tcpPort);
        Rule($"PC Monitor (UDP {udpPort})", "UDP", udpPort);
    }

    static void Rule(string name, string protocol, int port)
    {
        if (Win.Run("netsh", $"advfirewall firewall show rule name=\"{name}\"", 8000).Code == 0) return;
        var (code, output) = Win.Run("netsh",
            $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol={protocol} localport={port} profile=private,domain", 8000);
        Log.Info($"Pare-feu « {name} » : " + (code == 0 ? "ajouté" : Win.Decode(output)));
    }
}
