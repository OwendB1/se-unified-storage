using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClientPlugin.Settings.Elements;

namespace ClientPlugin;

public enum InventoryScopeMode
{
    MechanicalGroups,
    ConveyorComponents,
    BlockGroups
}

public sealed class Config : INotifyPropertyChanged
{
    private bool unifiedByDefault = true;
    private bool forceClientOnly;
    private DistributionPolicy defaultPolicy = DistributionPolicy.ExistingStackFirst;
    private InventoryScopeMode scopeMode = InventoryScopeMode.MechanicalGroups;
    private int transfersPerSecond = 240;
    private int reachabilityQueriesPerSecond = 480;
    private int refreshDebounceMilliseconds;
    private int acknowledgementTimeoutMilliseconds = 3000;

    public readonly string Title = "Unified Storage";

    [Separator("Inventory terminal")]
    [Checkbox(description: "Open the terminal inventory in Unified mode. The in-page toggle always restores vanilla mode.")]
    public bool UnifiedByDefault
    {
        get => unifiedByDefault;
        set => SetField(ref unifiedByDefault, value);
    }

    [Dropdown(description: "Default placement policy used by deposits and Rebalance actions.")]
    public DistributionPolicy DefaultPolicy
    {
        get => defaultPolicy;
        set => SetField(ref defaultPolicy, value);
    }

    [Dropdown(description: "How grid-side projected owners are split. Item transfers still enforce live conveyor rules.")]
    public InventoryScopeMode ScopeMode
    {
        get => scopeMode;
        set => SetField(ref scopeMode, value);
    }

    [Separator("Privacy")]
    [Checkbox(description: "Disable all companion discovery and messages, including on join. Uses vanilla inventory requests only. Existing server jobs are not stopped; local maintenance may conflict with server automation.")]
    public bool ForceClientOnly
    {
        get => forceClientOnly;
        set => SetField(ref forceClientOnly, value);
    }

    [Separator("Work budgets")]
    [Slider(1, 1920, 1, SliderAttribute.SliderType.Integer, description: "Maximum local inventory transfer requests per second. Set to 1 to send at most one per second. Server companion jobs use the server's own budgets.")]
    public int TransfersPerSecond
    {
        get => transfersPerSecond;
        set => SetField(ref transfersPerSecond, value);
    }

    [Slider(1, 3840, 1, SliderAttribute.SliderType.Integer, description: "Maximum local transfer candidates validated per second, including rejected candidates. Lower values reduce conveyor-check work but can slow transfers.")]
    public int ReachabilityQueriesPerSecond
    {
        get => reachabilityQueriesPerSecond;
        set => SetField(ref reachabilityQueriesPerSecond, value);
    }

    [Slider(0, 50, 10, SliderAttribute.SliderType.Integer, description: "Maximum coalescing delay for inventory display updates (legacy values are capped at 50 ms). Zero refreshes next frame.")]
    public int RefreshDebounceMilliseconds
    {
        get => refreshDebounceMilliseconds;
        set => SetField(ref refreshDebounceMilliseconds, value);
    }

    [Slider(500, 10000, 250, SliderAttribute.SliderType.Integer, description: "Maximum wait for replicated transfer or production state before an operation stops.")]
    public int AcknowledgementTimeoutMilliseconds
    {
        get => acknowledgementTimeoutMilliseconds;
        set => SetField(ref acknowledgementTimeoutMilliseconds, value);
    }

    public static readonly Config Default = new();
    public static readonly Config Current = Settings.ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
