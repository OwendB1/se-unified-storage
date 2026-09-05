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
    private DistributionPolicy defaultPolicy = DistributionPolicy.ExistingStackFirst;
    private InventoryScopeMode scopeMode = InventoryScopeMode.MechanicalGroups;
    private int transfersPerFrame = 4;
    private int reachabilityQueriesPerFrame = 8;
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

    [Separator("Work budgets")]
    [Slider(1, 32, 1, SliderAttribute.SliderType.Integer, description: "Maximum inventory transfer requests issued in one simulation frame.")]
    public int TransfersPerFrame
    {
        get => transfersPerFrame;
        set => SetField(ref transfersPerFrame, value);
    }

    [Slider(1, 64, 1, SliderAttribute.SliderType.Integer, description: "Maximum conveyor reachability queries evaluated in one simulation frame.")]
    public int ReachabilityQueriesPerFrame
    {
        get => reachabilityQueriesPerFrame;
        set => SetField(ref reachabilityQueriesPerFrame, value);
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
