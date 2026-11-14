using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PCMonitor;

/// <summary>L'icône près de l'horloge et tout ce qui tourne derrière.</summary>
public sealed class TrayApp : ApplicationContext
{
    readonly AppConfig cfg;
    readonly Asus asus;
    readonly Sensors sensors;
    readonly Collector collector;
    readonly ModeService modes;
    readonly Controls controls;
    readonly GameWatcher games;
    readonly CpuTuner cpu;
    readonly WebServer web;
    readonly Discovery discovery;
    readonly NotifyIcon tray;
    readonly Icon icon;
    readonly Control ui; // pour revenir sur le thread de l'interface
    readonly System.Windows.Forms.Timer tipTimer;
    PairForm pair;
    bool startupEnabled;

    public TrayApp(string[] args)
    {
        ui = new Control();
        ui.CreateControl();
        ScreenColor.OnUi = f => ui.InvokeRequired ? (bool)ui.Invoke(f) : f();
        icon = LoadIcon();

        cfg = AppConfig.Load();
        Osd.Init(ui, cfg);
        asus = new Asus();
        sensors = new Sensors();
        sensors.Open();
        modes = new ModeService(cfg, asus);
        collector = new Collector(cfg, sensors, asus);
        collector.Start();

        cpu = new CpuTuner(cfg);
        cpu.Stress = new StressTest(cfg, collector);
        games = new GameWatcher(cfg, modes, cpu) { Collector = collector };
        collector.GameInfo = () =>
        {
            var name = games.ActiveName;
            if (name == null) return null;
            var since = games.Since;
            return new Dictionary<string, object>
            {
                ["name"] = name,
                ["since"] = since.HasValue ? (long)(DateTime.Now - since.Value).TotalSeconds : 0L,
            };
        };
        controls = new Controls(cfg, asus, modes, games);
        var actions = new Actions(cfg, modes, asus, games, cpu) { ResetPeaks = collector.ResetPeaks };
        HotKeys.PreGame = () => PreGame.Run(cfg, modes, cpu);
        HotKeys.Init(cfg, modes, cpu, asus);
        Updater.Init(cfg);
        Updater.Quit = () => OnUi(ExitThread);
        Updater.Announce = v => Balloon($"Keystone {v} est disponible : Options > Mise à jour dans l'appli, ou clic droit sur l'icône.");
        web = new WebServer(cfg, collector, actions, controls);
        string webError = null;
        try
        {
            web.Start();
        }
        catch (Exception e)
        {
            Log.Error("Serveur web", e);
            webError = $"Le port {cfg.Port} est déjà utilisé. Ferme l'ancienne fenêtre noire (pc_monitor.py) puis relance Keystone.";
        }
        discovery = new Discovery(cfg.DiscoveryPort, web.Hello);
        discovery.Start();

        tray = new NotifyIcon { Icon = icon, Text = "Keystone", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        tray.ContextMenuStrip.Opening += (s, e) => { FillMenu(tray.ContextMenuStrip); e.Cancel = false; };
        tray.DoubleClick += (s, e) => ShowPair();
        tray.BalloonTipClicked += (s, e) => ShowPair();

        tipTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        tipTimer.Tick += (s, e) => UpdateTip();
        tipTimer.Start();

        modes.Changed += _ => UpdateTip();
        modes.Changed += m => Task.Run(() => cpu.OnFanModeChanged(m));
        cpu.ProfileChanged += _ => UpdateTip();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        Catalog.Refresh();
        Task.Run(async () =>
        {
            Startup.EnsureFirewall(cfg.Port, cfg.DiscoveryPort);
            startupEnabled = Startup.IsEnabled();
            if (startupEnabled) Startup.EnsurePath();
            await Task.Delay(2500);
            modes.Reapply();
            controls.Reapply();
            cpu.Reapply();
            cpu.Start();
            _ = Task.Run(() => cpu.EnsureThreshold(true));
            games.Start();
            await Task.Delay(1500);
            LaunchTools();
            // nouvelle version ? une minute après le démarrage, puis toutes les 6 heures
            await Task.Delay(60000);
            while (true)
            {
                await Updater.Check();
                await Task.Delay(TimeSpan.FromHours(6));
            }
        });

        if (webError != null)
        {
            MessageBox.Show(webError, "Keystone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else if (!cfg.FirstRunDone)
        {
            cfg.FirstRunDone = true;
            cfg.Save();
            Task.Run(() =>
            {
                startupEnabled = Startup.IsEnabled() || Startup.Enable();
            });
            ShowPair();
            Balloon("Keystone tourne près de l'horloge. Clic droit sur l'icône pour les réglages.");
        }

        if (!sensors.Ok)
            Balloon("Capteurs indisponibles : vérifie que le pilote PawnIO est installé (voir le journal).");
        else if (Win.IsRunning("GHelper.exe"))
            Balloon("Ferme G-Helper et retire-le du démarrage : Keystone s'occupe maintenant des modes et des ventilos.");
        else if (Win.IsRunning("LibreHardwareMonitor.exe"))
            Balloon("Tu peux fermer LibreHardwareMonitor : Keystone lit les capteurs lui-même.");
    }

    static Icon LoadIcon()
    {
        try { return new Icon(new MemoryStream(WebServer.Resource("app.ico"))); }
        catch { return SystemIcons.Application; }
    }

    void OnUi(Action a)
    {
        try
        {
            if (ui.InvokeRequired) ui.BeginInvoke(a);
            else a();
        }
        catch { }
    }

    void Balloon(string text) => OnUi(() => tray.ShowBalloonTip(5000, "Keystone", text, ToolTipIcon.None));

    void UpdateTip()
    {
        OnUi(() =>
        {
            string T(float? v) => v.HasValue ? Math.Round(v.Value) + " °C" : "–";
            var mode = (string.IsNullOrEmpty(cfg.LastMode) ? "" : "\n" + cfg.ModeLabel(cfg.LastMode)) +
                       (cfg.Cpu.Enabled ? " · CPU " + CpuTuner.ProfileLabel(cfg.Cpu.Active) : "");
            var text = $"Keystone\nCPU {T(collector.CpuTemp)}  GPU {T(collector.GpuTemp)}{mode}";
            tray.Text = text.Length > 127 ? text[..127] : text;
        });
    }

    void ShowPair()
    {
        OnUi(() =>
        {
            if (pair != null && !pair.IsDisposed)
            {
                pair.Activate();
                return;
            }
            pair = new PairForm(cfg, icon);
            pair.Show();
            pair.Activate();
        });
    }

    void FillMenu(ContextMenuStrip m)
    {
        m.Items.Clear();
        m.Items.Add(new ToolStripMenuItem($"Keystone {Program.Version}  ({NetInfo.LocalIp()})") { Enabled = false });
        m.Items.Add("Connecter un téléphone…", null, (s, e) => ShowPair());
        m.Items.Add("Ouvrir le tableau de bord", null, (s, e) => Win.OpenAsUser($"http://localhost:{cfg.Port}/?k={cfg.Token}"));
        m.Items.Add(new ToolStripSeparator());

        foreach (var id in cfg.Modes)
        {
            var mode = id;
            var item = new ToolStripMenuItem(cfg.ModeLabel(mode)) { Checked = cfg.LastMode == mode };
            item.Click += (s, e) => Task.Run(() =>
            {
                var (ok, msg) = modes.Apply(mode);
                if (!(ok && Osd.Mode(mode))) Balloon(msg);
            });
            m.Items.Add(item);
        }
        if (cfg.Cpu.Enabled)
        {
            var prof = new ToolStripMenuItem("Profil CPU : " + CpuTuner.ProfileLabel(cfg.Cpu.Active));
            foreach (var id in ModeDefs.Order)
            {
                var pid = id;
                var item = new ToolStripMenuItem(CpuTuner.ProfileLabel(pid)) { Checked = cfg.Cpu.Active == pid };
                item.Click += (s, e) => Task.Run(() =>
                {
                    var (ok, msg) = cpu.SetProfile(pid);
                    if (!(ok && Osd.CpuProfile(pid))) Balloon(msg);
                });
                prof.DropDownItems.Add(item);
            }
            m.Items.Add(prof);
        }
        var fansOn = cfg.FanCurves.TryGetValue("turbo", out var turboCurve) && turboCurve.IsFull;
        var fans = new ToolStripMenuItem("Ventilos à fond en Turbo") { Checked = fansOn };
        fans.Click += (s, e) =>
        {
            lock (AppConfig.Gate)
            {
                if (fansOn) cfg.FanCurves.Remove("turbo");
                else cfg.FanCurves["turbo"] = FanProfile.Full();
            }
            cfg.Save();
            if (cfg.LastMode == "turbo") Task.Run(() => modes.Apply("turbo"));
        };
        m.Items.Add(fans);
        var gpu = new ToolStripMenuItem($"Limiter le GPU à {cfg.GpuLimit.Mhz} MHz") { Checked = cfg.GpuLimit.On };
        gpu.Click += (s, e) => Task.Run(() =>
        {
            var on = !cfg.GpuLimit.On;
            var (ok, msg) = Gpu.SetLimit(on ? cfg.GpuLimit.Mhz : 0);
            if (ok)
            {
                cfg.GpuLimit.On = on;
                cfg.Save();
            }
            Balloon(msg);
        });
        m.Items.Add(gpu);
        var colors = new ToolStripMenuItem("Couleurs de l'écran");
        List<string> names;
        lock (AppConfig.Gate) names = cfg.ColorProfiles.Select(c => c.Name).ToList();
        foreach (var n in names)
        {
            var name = n;
            var item = new ToolStripMenuItem(name) { Checked = string.Equals(cfg.ActiveColor, name, StringComparison.OrdinalIgnoreCase) };
            item.Click += (s, e) => Task.Run(() => { var (ok, msg) = Tweaks.SetColor(cfg, name); if (!ok) Balloon(msg); });
            colors.DropDownItems.Add(item);
        }
        m.Items.Add(colors);
        var winkey = new ToolStripMenuItem("Bloquer la touche Windows") { Checked = WinKey.Enabled };
        winkey.Click += (s, e) => Task.Run(() =>
        {
            var on = !WinKey.Enabled;
            if (Tweaks.SetWinKey(cfg, on).Ok) Osd.Toggle("Touche Windows", on ? "Bloquée" : "Débloquée", on);
        });
        m.Items.Add(winkey);
        m.Items.Add("Avant de jouer", null, (s, e) => Task.Run(() =>
        {
            var (ok, msg) = PreGame.Run(cfg, modes, cpu);
            if (!ok || !cfg.Osd.On) Balloon(msg);
        }));
        if (Updater.State == "ready")
            m.Items.Add($"Mettre à jour vers {Updater.Latest}", null, (s, e) => Balloon(Updater.Install().Message));
        m.Items.Add(new ToolStripSeparator());

        var launch = new ToolStripMenuItem("Lancer avec Keystone");
        var tools = Catalog.FindTools();
        if (tools.Count == 0)
            launch.DropDownItems.Add(new ToolStripMenuItem(Catalog.Loading ? "Recherche en cours…" : "ThrottleStop / Afterburner introuvables") { Enabled = false });
        foreach (var (name, path) in tools)
        {
            var on = cfg.LaunchWith.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            var item = new ToolStripMenuItem(name) { Checked = on };
            item.Click += (s, e) =>
            {
                if (on) cfg.LaunchWith.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                else
                {
                    cfg.LaunchWith.Add(path);
                    StartTool(path);
                }
                cfg.Save();
            };
            launch.DropDownItems.Add(item);
        }
        m.Items.Add(launch);

        var auto = new ToolStripMenuItem("Démarrer avec Windows") { Checked = startupEnabled };
        auto.Click += (s, e) => Task.Run(() =>
        {
            if (startupEnabled) Startup.Disable();
            else if (!Startup.Enable()) Balloon("Impossible de créer le démarrage automatique (voir le journal).");
            startupEnabled = Startup.IsEnabled();
        });
        m.Items.Add(auto);
        m.Items.Add("Ouvrir le dossier des réglages", null, (s, e) => Win.OpenAsUser(Log.Dir));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Quitter", null, (s, e) => ExitThread());
    }

    void LaunchTools()
    {
        foreach (var path in cfg.LaunchWith.ToList())
            if (File.Exists(path) && !Win.IsRunning(Path.GetFileName(path))) StartTool(path);
    }

    static void StartTool(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path),
                WindowStyle = ProcessWindowStyle.Minimized,
            });
        }
        catch (Exception e)
        {
            Log.Error("Lancement de " + path, e);
        }
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // Le BIOS remet le mode par défaut au réveil : on réapplique le dernier choisi.
        if (e.Mode == PowerModes.Resume || e.Mode == PowerModes.StatusChange)
        {
            var delay = e.Mode == PowerModes.Resume ? 4000 : 2000;
            Task.Run(async () =>
            {
                await Task.Delay(delay);
                modes.Reapply();
                if (e.Mode == PowerModes.Resume)
                {
                    controls.Reapply();
                    cpu.Reapply();
                }
            });
        }
    }

    void OnDisplayChanged(object sender, EventArgs e)
    {
        // Windows remet la table gamma à zéro quand l'écran change (fréquence, écran externe…)
        Task.Run(async () =>
        {
            await Task.Delay(1500);
            ScreenColor.Reapply();
        });
    }

    protected override void ExitThreadCore()
    {
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            games.Stop();
            Fps.Stop();
            WinKey.Set(false);
            ScreenColor.Reset();
            cpu.Stop();
            cpu.Stress?.Stop();
            Msr.Close();
            tipTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            web.Stop();
            discovery.Stop();
            collector.Stop();
            sensors.Dispose();
            asus.Dispose();
        }
        catch { }
        base.ExitThreadCore();
    }
}
