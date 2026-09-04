using System;
using System.IO;
using System.Threading;
using System.ComponentModel;
using ClientPlugin.Inventory;
using ClientPlugin.Automation;
using ClientPlugin.Profiles;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using ClientPlugin.Transfers;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using Shared.Config;
using Shared.Logging;
using Shared.Patches;
using Shared.Plugin;
using VRage.FileSystem;
using VRage.Game;
using VRage.Plugins;

// Define assembly version when compiled by Pulsar
#if !LOCAL_BUILD
using System.Reflection;

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif

namespace ClientPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin, ICommonPlugin
{
    public const string Name = "UnifiedStorage";
    public static Plugin Instance { get; private set; }
    private SettingsGenerator settingsGenerator;
    public MechanicalInventoryScopeScanner InventoryScopes { get; private set; }
    public LocalProfileStore Profiles { get; private set; }
    public TransferExecutor Transfers { get; private set; }
    public RefinerySortExecutor RefinerySorts { get; private set; }
    public ProductionQueueExecutor ProductionQueue { get; private set; }
    public BottleRefillCoordinator BottleRefills { get; private set; }
    public LocalAutomationService Automation { get; private set; }
    public Companion.CompanionClient Companion { get; private set; }
    public long Tick { get; private set; }
    private static bool failed;

    public IPluginLogger Log => Logger;
    private static readonly IPluginLogger Logger = new PluginLogger(Name);

    public IPluginConfig Config => config?.Data;
    private PersistentConfig<PluginConfig> config;
    private static readonly string ConfigFileName = $"{Name}.cfg";

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        failed = false;
        Tick = 0;
#if DEBUG
        // Allow the debugger some time to connect once the plugin assembly is loaded
        Thread.Sleep(100);
#endif

        Instance = this;
        Instance.settingsGenerator = new SettingsGenerator();

        Log.Info("Loading");

        var configPath = Path.Combine(MyFileSystem.UserDataPath, ConfigFileName);
        config = PersistentConfig<PluginConfig>.Load(Log, configPath);
        global::ClientPlugin.Config.Current.PropertyChanged += ClientConfigChanged;

        var gameVersion = MyFinalBuildConstants.APP_VERSION_STRING.ToString();
        Common.SetPlugin(this, gameVersion, MyFileSystem.UserDataPath);
        try
        {
            InventoryScopes = new MechanicalInventoryScopeScanner();
            Profiles = new LocalProfileStore(Log);
            Transfers = new TransferExecutor();
            RefinerySorts = new RefinerySortExecutor();
            ProductionQueue = new ProductionQueueExecutor();
            BottleRefills = new BottleRefillCoordinator();
            Automation = new LocalAutomationService(InventoryScopes, Profiles);
            Companion = new Companion.CompanionClient();
        }
        catch (Exception ex)
        {
            Log.Critical(ex, "Inventory discovery initialization failed");
            failed = true;
            return;
        }

        if (!PatchHelpers.HarmonyPatchAll(Log, new Harmony(Name)))
        {
            failed = true;
            return;
        }

        Log.Debug("Successfully loaded");
    }

    public void Dispose()
    {
        try
        {
            global::ClientPlugin.Config.Current.PropertyChanged -= ClientConfigChanged;
            ConfigStorage.Save(global::ClientPlugin.Config.Current);
            Profiles?.Save();
            Companion?.Dispose();
            Automation?.Dispose();
            BottleRefills?.Clear();
            Transfers?.Clear("plugin unloaded");
            RefinerySorts?.Clear();
            ProductionQueue?.Clear();
            config?.Dispose();
            // IMPORTANT: Do NOT call harmony.UnpatchAll() here! It may break other plugins.
        }
        catch (Exception ex)
        {
            Log.Critical(ex, "Dispose failed");
        }

        Instance = null;
        InventoryScopes = null;
        Profiles = null;
        Transfers = null;
        RefinerySorts = null;
        ProductionQueue = null;
        BottleRefills = null;
        Automation = null;
        Companion = null;
    }

    private static void ClientConfigChanged(object sender, PropertyChangedEventArgs e) =>
        ConfigStorage.Save(global::ClientPlugin.Config.Current);

    public void Update()
    {
        if (failed)
            return;

#if DEBUG
        CustomUpdate();
        Tick++;
#else        
        try
        {
            CustomUpdate();
            Tick++;
        }
        catch (Exception e)
        {
            Log.Critical(e, "Update failed");
            failed = true;
        }
#endif       
    }

    private void CustomUpdate()
    {
        PatchHelpers.PatchUpdates();
        Companion?.Update();
        Transfers?.Update();
        RefinerySorts?.Update();
        ProductionQueue?.Update();
        BottleRefills?.Update();
        Automation?.Update(Tick);
    }

    // ReSharper disable once UnusedMember.Global
    public void OpenConfigDialog()
    {
        Instance.settingsGenerator.SetLayout<Simple>();
        MyGuiSandbox.AddScreen(Instance.settingsGenerator.Dialog);
    }
        
}
