using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using PluginSdk.Config;
using PluginSdk.Logging;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Plugins;

#if !LOCAL_BUILD
using System.Reflection;
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif

namespace ServerPlugin;

// ReSharper disable once UnusedType.Global
public sealed class Plugin : IPlugin
{
    public const string Name = "UnifiedStorage";
    private readonly Logger log = Logger.Create(Name);
    private readonly CompanionStats stats = new();
    private CompanionServer server;
    private MySession session;
    private string configPath;
    private int configDirty;
    public CompanionConfig PluginConfig { get; private set; } = new();

    public void Init(object gameInstance)
    {
        configPath = Path.Combine(MyFileSystem.UserDataPath, "UnifiedStorage.companion.cfg");
        try { PluginConfig = ConfigStorage.LoadXml<CompanionConfig>(configPath); }
        catch (Exception exception)
        {
            PluginConfig.Enabled = false;
            log.Error("Companion config failed to load; disabled, existing file preserved", exception);
            configPath = null;
        }
        PluginConfig.PropertyChanged += ConfigChanged;
        MySession.OnUnloading += Unloading;
        log.Info("Companion loaded; shared profiles only. Authoritative transfers and automation are not enabled.");
    }

    public void Update()
    {
        if (Interlocked.Exchange(ref configDirty, 0) != 0) SaveConfig();
        var current = MySession.Static;
        if (current == null || !current.Ready || !Sync.IsServer) return;
        try
        {
            if (!ReferenceEquals(session, current))
            {
                Unloading();
                session = current;
                server = new CompanionServer(current, PluginConfig, log, stats);
            }
            server?.Update();
        }
        catch (Exception exception)
        {
            log.Error("Companion stopped for this world after an unexpected failure", exception);
            server?.Dispose();
            server = null;
        }
    }

    private void ConfigChanged(object sender, PropertyChangedEventArgs args) => Interlocked.Exchange(ref configDirty, 1);
    private void SaveConfig()
    {
        if (configPath == null) return;
        try { ConfigStorage.SaveXml(PluginConfig, configPath); }
        catch (Exception exception) { log.Error("Failed to save companion operator configuration", exception); }
    }
    private void Unloading()
    {
        server?.Dispose(); server = null; session = null;
    }
    public void Dispose()
    {
        MySession.OnUnloading -= Unloading;
        PluginConfig.PropertyChanged -= ConfigChanged;
        Unloading();
        if (Interlocked.Exchange(ref configDirty, 0) != 0) SaveConfig();
    }
}
