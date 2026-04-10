using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Compass.Infrastructure;
using Compass.Models;
using Compass.Services;
using Compass.UI;

using AutoCADApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Compass.ViewModels;

public class DrillManagerViewModel : INotifyPropertyChanged
{
    public const int MinimumDrills = 1;
    public const int MaximumDrills = 20;

    private int _drillCount = 12;
    private int _selectedDrillIndex = 1;
    private string _selectedDrillName = string.Empty;
    private string _delimitedNames = string.Empty;
    private string _delimitedSeparator = ",";
    private string _autoFillPrefix = "DRILL ";
    private int _autoFillStartIndex = 1;
    private int _swapFirstIndex = MinimumDrills;
    private int _swapSecondIndex = MinimumDrills + 1;

    private readonly WellCornerTableService _wellCornerTableService;
    private readonly DrillAttributeSyncService _drillAttributeSyncService;
    private readonly DrillCadToolService _drillCadToolService;
    private readonly string[] _committedNames = new string[MaximumDrills];
    private static readonly string[] HeadingChoices = { "ICP", "HEEL", "LANDING" };
    private int _stateChangeSuppressionCount;
    private bool _stateChangePending;

    private readonly RelayCommand _setSelectedCommand;
    private readonly RelayCommand _clearSelectedCommand;
    private readonly RelayCommand _setAllCommand;
    private readonly RelayCommand _clearAllCommand;
    private readonly RelayCommand _applyDelimitedCommand;
    private readonly RelayCommand _copyDelimitedCommand;
    private readonly RelayCommand _updateFromBlockAttributesCommand;
    private readonly RelayCommand _createWellCornersTableCommand;
    private readonly RelayCommand _autoFillCommand;
    private readonly RelayCommand _swapCommand;
    private readonly RelayCommand _checkCommand;
    private readonly RelayCommand _headingsAllCommand;
    private readonly RelayCommand _createXlsCommand;
    private readonly RelayCommand _completeCordsCommand;
    private readonly RelayCommand _completeCordsArchiveCommand;
    private readonly RelayCommand _getUtmsCommand;
    private readonly RelayCommand _addDrillPointsCommand;
    private readonly RelayCommand _addOffsetsCommand;
    private readonly RelayCommand _updateOffsetsCommand;
    private readonly RelayCommand _buildDrillCommand;

    private string _heading = "ICP";

    public DrillManagerViewModel()
    {
        CompassEnvironment.Initialize();
        var log = CompassEnvironment.Log;
        _wellCornerTableService = new WellCornerTableService(log, new LayerService());
        _drillAttributeSyncService = new DrillAttributeSyncService(log, new AutoCADBlockService());
        _drillCadToolService = new DrillCadToolService(log, new LayerService());

        Drills = new ObservableCollection<DrillSlotViewModel>();
        DrillCountOptions = Enumerable.Range(MinimumDrills, MaximumDrills - MinimumDrills + 1).ToList();
        UpdateDrillSlots();
        for (var i = 0; i < Drills.Count; i++)
        {
            Drills[i].Commit();
            SetCommittedName(i + 1, Drills[i].Name);
        }
        ClearCommittedBeyondCount();
        DrillProps = new DrillPropsAccessor(this);

        _swapFirstIndex = MinimumDrills;
        _swapSecondIndex = Math.Min(MinimumDrills + 1, DrillCount);

        _setSelectedCommand = new RelayCommand(SetSelectedDrill, CanMutateDrill);
        _clearSelectedCommand = new RelayCommand(ClearSelectedDrill, CanMutateDrill);
        _setAllCommand = new RelayCommand(_ => SetAllDrills(), _ => DrillCount > 0);
        _clearAllCommand = new RelayCommand(_ => ClearAllDrills(), _ => DrillCount > 0);
        _applyDelimitedCommand = new RelayCommand(_ => ApplyDelimitedNames(), _ => true);
        _copyDelimitedCommand = new RelayCommand(_ => CopyDelimitedNames(), _ => DrillCount > 0);
        _updateFromBlockAttributesCommand = new RelayCommand(_ => UpdateFromBlockAttributes(), _ => DrillCount > 0);
        _createWellCornersTableCommand = new RelayCommand(_ => CreateWellCornersTable());
        _autoFillCommand = new RelayCommand(_ => AutoFillEmpty(), _ => DrillCount > 0);
        _swapCommand = new RelayCommand(_ => SwapDrills(), _ => CanSwap());
        _checkCommand = new RelayCommand(_ => RunCheck(), _ => DrillCount > 0);
        _headingsAllCommand = new RelayCommand(_ => RunHeadingsAll(), _ => DrillCount > 0);
        _createXlsCommand = new RelayCommand(_ => _drillCadToolService.CreateXlsFromTable());
        _completeCordsCommand = new RelayCommand(_ => RunCompleteCords(), _ => DrillCount > 0);
        _completeCordsArchiveCommand = new RelayCommand(_ => RunCompleteCordsArchive(), _ => DrillCount > 0);
        _getUtmsCommand = new RelayCommand(_ => _drillCadToolService.GetUtms());
        _addDrillPointsCommand = new RelayCommand(_ => _drillCadToolService.AddDrillPoints());
        _addOffsetsCommand = new RelayCommand(_ => _drillCadToolService.AddOffsets());
        _updateOffsetsCommand = new RelayCommand(_ => _drillCadToolService.UpdateOffsets());
        _buildDrillCommand = new RelayCommand(_ => RunBuildDrill(), _ => DrillCount > 0);

        SelectedDrillIndex = 1;
        OnPropertyChanged(nameof(SwapFirstIndex));
        OnPropertyChanged(nameof(SwapSecondIndex));
    }

    public ObservableCollection<DrillSlotViewModel> Drills { get; }

    public IReadOnlyList<int> DrillCountOptions { get; }

    public DrillPropsAccessor DrillProps { get; }

    public IReadOnlyList<string> HeadingOptions => HeadingChoices;

    public ICommand SetSelectedCommand => _setSelectedCommand;
    public ICommand ClearSelectedCommand => _clearSelectedCommand;
    public ICommand SetAllCommand => _setAllCommand;
    public ICommand ClearAllCommand => _clearAllCommand;
    public ICommand ApplyDelimitedCommand => _applyDelimitedCommand;
    public ICommand CopyDelimitedCommand => _copyDelimitedCommand;
    public ICommand UpdateFromBlockAttributesCommand => _updateFromBlockAttributesCommand;
    public ICommand CreateWellCornersTableCommand => _createWellCornersTableCommand;
    public ICommand AutoFillCommand => _autoFillCommand;
    public ICommand SwapCommand => _swapCommand;
    public string Heading
    {
        get => _heading;
        set
        {
            var normalized = NormalizeHeading(value);
            if (!string.Equals(_heading, normalized, StringComparison.Ordinal))
            {
                _heading = normalized;
                OnPropertyChanged();
                RaiseStateChanged();
            }
        }
    }
    public ICommand CheckCommand => _checkCommand;
    public ICommand HeadingsAllCommand => _headingsAllCommand;
    public ICommand CreateXlsCommand => _createXlsCommand;
    public ICommand CompleteCordsCommand => _completeCordsCommand;
    public ICommand CompleteCordsArchiveCommand => _completeCordsArchiveCommand;
    public ICommand GetUtmsCommand => _getUtmsCommand;
    public ICommand AddDrillPointsCommand => _addDrillPointsCommand;
    public ICommand AddOffsetsCommand => _addOffsetsCommand;
    public ICommand UpdateOffsetsCommand => _updateOffsetsCommand;
    public ICommand BuildDrillCommand => _buildDrillCommand;

    public int DrillCount
    {
        get => _drillCount;
        set
        {
            var newValue = Math.Max(MinimumDrills, Math.Min(MaximumDrills, value));
            if (_drillCount != newValue)
            {
                _drillCount = newValue;
                OnPropertyChanged();
                UpdateDrillSlots();
                EnsureSelectedIndexInRange();
                EnsureSwapIndexesInRange();
                RefreshCommandStates();
                RaiseStateChanged();
            }
        }
    }

    public int SelectedDrillIndex
    {
        get => _selectedDrillIndex;
        set
        {
            var maxAllowed = Math.Max(MinimumDrills, Math.Min(MaximumDrills, DrillCount));
            var newValue = Math.Max(MinimumDrills, Math.Min(maxAllowed, value));
            if (_selectedDrillIndex != newValue)
            {
                _selectedDrillIndex = newValue;
                OnPropertyChanged();
                RefreshSelectedName();
                RefreshCommandStates();
            }
        }
    }

    public string SelectedDrillName
    {
        get => _selectedDrillName;
        set
        {
            if (_selectedDrillName != value)
            {
                _selectedDrillName = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public int SwapFirstIndex
    {
        get => _swapFirstIndex;
        set
        {
            var maxAllowed = Math.Max(MinimumDrills, Math.Min(MaximumDrills, DrillCount));
            var newValue = Math.Max(MinimumDrills, Math.Min(maxAllowed, value));
            if (_swapFirstIndex != newValue)
            {
                _swapFirstIndex = newValue;
                OnPropertyChanged();
                RefreshCommandStates();
            }
        }
    }

    public int SwapSecondIndex
    {
        get => _swapSecondIndex;
        set
        {
            var maxAllowed = Math.Max(MinimumDrills, Math.Min(MaximumDrills, DrillCount));
            var newValue = Math.Max(MinimumDrills, Math.Min(maxAllowed, value));
            if (_swapSecondIndex != newValue)
            {
                _swapSecondIndex = newValue;
                OnPropertyChanged();
                RefreshCommandStates();
            }
        }
    }

    public string DelimitedNames
    {
        get => _delimitedNames;
        set
        {
            if (_delimitedNames != value)
            {
                _delimitedNames = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public string DelimitedSeparator
    {
        get => _delimitedSeparator;
        set
        {
            var normalized = string.IsNullOrEmpty(value) ? "," : value.Substring(0, 1);
            if (_delimitedSeparator != normalized)
            {
                _delimitedSeparator = normalized;
                OnPropertyChanged();
            }
        }
    }

    public string AutoFillPrefix
    {
        get => _autoFillPrefix;
        set
        {
            if (_autoFillPrefix != value)
            {
                _autoFillPrefix = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public int AutoFillStartIndex
    {
        get => _autoFillStartIndex;
        set
        {
            var newValue = Math.Max(1, value);
            if (_autoFillStartIndex != newValue)
            {
                _autoFillStartIndex = newValue;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? StateChanged;

    public void LoadExistingNames(IEnumerable<string> names, bool commit = true)
    {
        var nameList = names?.Where(name => !string.IsNullOrWhiteSpace(name)).ToList() ?? new List<string>();
        DrillCount = Math.Max(MinimumDrills, Math.Min(MaximumDrills, nameList.Count));
        for (var i = 0; i < Drills.Count; i++)
        {
            Drills[i].Name = i < nameList.Count ? nameList[i] : string.Empty;
            if (commit)
            {
                Drills[i].Commit();
                SetCommittedName(i + 1, Drills[i].Name);
            }
        }

        if (commit)
        {
            ClearCommittedBeyondCount();
        }

        RefreshSelectedName();
        RefreshCommandStates();
    }

    public void ApplyState(DrillGridState state)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        var names = state.DrillNames ?? new List<string>();
        if (names.Count == 0)
        {
            names = Enumerable.Range(1, DrillCount).Select(index => $"DRILL_{index}").ToList();
        }

        _stateChangeSuppressionCount++;
        try
        {
            Heading = NormalizeHeading(state.Heading);
            LoadExistingNames(names);
        }
        finally
        {
            ResumeStateNotifications(skipPendingRaise: true);
        }
    }

    public DrillGridState CaptureState()
    {
        var state = new DrillGridState
        {
            DrillNames = Drills.Select(slot => slot.Name ?? string.Empty).ToList(),
            Heading = Heading
        };

        return state;
    }

    internal DrillSlotViewModel EnsureSlot(int index)
    {
        if (index < MinimumDrills || index > MaximumDrills)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be between {MinimumDrills} and {MaximumDrills}.");
        }

        if (_drillCount < index)
        {
            DrillCount = index;
        }

        return Drills[index - 1];
    }

    internal DrillSlotViewModel? TryGetSlot(int index)
    {
        if (index < MinimumDrills || index > MaximumDrills)
        {
            return null;
        }

        if (index > Drills.Count)
        {
            return null;
        }

        return Drills[index - 1];
    }

    private void UpdateDrillSlots()
    {
        if (Drills.Count < _drillCount)
        {
            for (var i = Drills.Count + 1; i <= _drillCount; i++)
            {
                var slot = new DrillSlotViewModel(i);
                slot.PropertyChanged += OnSlotPropertyChanged;
                Drills.Add(slot);
                slot.Commit();
                SetCommittedName(i, slot.Name);
            }
        }
        else if (Drills.Count > _drillCount)
        {
            while (Drills.Count > _drillCount)
            {
                var index = Drills.Count - 1;
                var slot = Drills[index];
                slot.PropertyChanged -= OnSlotPropertyChanged;
                Drills.RemoveAt(index);
            }
        }

        ClearCommittedBeyondCount();
        OnPropertyChanged(nameof(Drills));
    }

    private IReadOnlyList<string> GetCurrentDrillNames()
    {
        return Drills.Select(slot => slot.Name ?? string.Empty).ToList();
    }

    private IReadOnlyList<string> GetBuildableDrillNames()
    {
        return Drills
            .Select(slot =>
            {
                var name = (slot.Name ?? string.Empty).Trim();
                return string.IsNullOrWhiteSpace(name) ? $"DRILL_{slot.Index}" : name;
            })
            .ToList();
    }

    private void OnSlotPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(DrillSlotViewModel.Name), StringComparison.Ordinal))
        {
            RaiseStateChanged();
        }
    }

    private void RaiseStateChanged()
    {
        if (_stateChangeSuppressionCount > 0)
        {
            _stateChangePending = true;
            return;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResumeStateNotifications(bool skipPendingRaise = false)
    {
        if (_stateChangeSuppressionCount == 0)
        {
            return;
        }

        _stateChangeSuppressionCount--;
        if (skipPendingRaise)
        {
            _stateChangePending = false;
        }

        if (_stateChangeSuppressionCount == 0 && _stateChangePending)
        {
            _stateChangePending = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RunCheck()
    {
        var summary = _drillCadToolService.Check(GetCurrentDrillNames());
        if (!summary.Completed)
        {
            return;
        }

        var updated = new HashSet<int>();
        foreach (var result in summary.Results)
        {
            if (result.Index >= MinimumDrills && result.Index <= Drills.Count)
            {
                Drills[result.Index - 1].ApplyCheckResult(result);
                updated.Add(result.Index);
            }
        }

        for (var i = 0; i < Drills.Count; i++)
        {
            var slotIndex = i + 1;
            if (!updated.Contains(slotIndex))
            {
                Drills[i].ClearCheckStatus();
            }
        }
    }

    private void RunHeadingsAll()
    {
        _drillCadToolService.HeadingsAll(GetCurrentDrillNames(), Heading);
    }

    private void RunCompleteCords()
    {
        _drillCadToolService.CompleteCords(GetCurrentDrillNames(), Heading);
    }

    private void RunCompleteCordsArchive()
    {
        _drillCadToolService.CompleteCordsArchive(GetCurrentDrillNames(), Heading);
    }

    private void RunBuildDrill()
    {
        var dialog = new BuildDrillWindow(GetBuildableDrillNames());
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                MessageBox.Show("No active AutoCAD document is available.", "Build a Drill", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!BuildDrillCommandBridge.TryQueue(dialog.Result, document.Name, out var error))
            {
                MessageBox.Show(error ?? "Could not queue Build a Drill.", "Build a Drill", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BuildDrillCommandBridge.QueueExecution(document);
        }
    }

    private bool CanMutateSelectedDrill()
    {
        return SelectedDrillIndex >= MinimumDrills && SelectedDrillIndex <= DrillCount;
    }

    private void SetSelectedDrill(object? parameter)
    {
        var index = SelectedDrillIndex;
        if (TryGetIndex(parameter, out var specifiedIndex))
        {
            index = specifiedIndex;
        }

        var slot = TryGetSlot(index);

        if (SelectedDrillIndex != index)
        {
            SelectedDrillIndex = index;
        }
        else if (slot != null)
        {
            // When setting via the right-hand list, ensure the selected drill name mirrors
            // the latest edits made directly in the list view textbox.
            SelectedDrillName = slot.Name;
        }

        var newNameSource = slot?.Name ?? SelectedDrillName;
        var newName = (newNameSource ?? string.Empty).Trim();
        var committedName = GetCommittedName(index);
        var result = _drillAttributeSyncService.SetDrillName(index, newName, committedName, updateMatchingValues: true);
        if (!result.Success)
        {
            return;
        }

        DrillProps.SetDrillProp(index, newName);
        Drills[index - 1].Commit();
        SetCommittedName(index, newName);

        var defaultName = $"DRILL_{index}";
        if (result.UpdatedAttributes > 0)
        {
            MessageBox.Show($"Updated {result.UpdatedAttributes} attribute(s) for {defaultName}.", "Set Drill", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"No DRILL_x attributes found for {defaultName} to update.", "Set Drill", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshSelectedName();
    }

    private void ClearSelectedDrill(object? parameter)
    {
        var index = SelectedDrillIndex;
        if (TryGetIndex(parameter, out var specifiedIndex))
        {
            SelectedDrillIndex = specifiedIndex;
            index = specifiedIndex;
        }

        DrillProps.ClearDrillProp(index);
        RefreshSelectedName();
    }

    private bool TryGetIndex(object? parameter, out int index)
    {
        switch (parameter)
        {
            case DrillSlotViewModel slot:
                index = slot.Index;
                return true;
            case int value:
                index = value;
                return true;
            case string text when int.TryParse(text, out var parsed):
                index = parsed;
                return true;
            default:
                index = 0;
                return false;
        }
    }

    private bool CanMutateDrill(object? parameter)
    {
        if (!TryGetIndex(parameter, out var index))
        {
            return CanMutateSelectedDrill();
        }

        return index >= MinimumDrills && index <= DrillCount;
    }

    private void SetAllDrills()
    {
        var confirmResult = MessageBox.Show(
            "Are you sure you want to set DRILLNAME for all drills?",
            "Confirm SET ALL",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        var changes = new List<string>();
        for (var i = 0; i < DrillCount; i++)
        {
            var index = i + 1;
            var defaultName = $"DRILL_{index}";
            var newName = (Drills[i].Name ?? string.Empty).Trim();
            if (!string.Equals(Drills[i].Name, newName, StringComparison.Ordinal))
            {
                Drills[i].Name = newName;
            }

            var committedName = GetCommittedName(index);
            var shouldSet = index == 1 || !string.Equals(newName, defaultName, StringComparison.OrdinalIgnoreCase);
            if (!shouldSet && string.Equals(committedName, defaultName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var result = _drillAttributeSyncService.SetDrillName(index, newName, committedName, updateMatchingValues: true);
            if (!result.Success)
            {
                return;
            }

            if (!string.Equals(committedName, newName, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add($"{defaultName}: '{committedName}' -> '{newName}'");
            }

            DrillProps.SetDrillProp(index, newName);
            Drills[i].Commit();
            SetCommittedName(index, newName);
        }

        var summary = changes.Count > 0 ? string.Join("\n", changes) : "No changes were necessary.";
        MessageBox.Show(summary, "Set All", MessageBoxButton.OK, MessageBoxImage.Information);

        ClearCommittedBeyondCount();
        RefreshSelectedName();
        RefreshCommandStates();
    }

    private void ClearAllDrills()
    {
        DrillProps.ClearAllDrillProps();
        RefreshSelectedName();
    }

    private void ApplyDelimitedNames()
    {
        var separator = string.IsNullOrEmpty(DelimitedSeparator) ? ',' : DelimitedSeparator[0];
        DrillProps.LoadFromDelimitedList(DelimitedNames, separator);
        RefreshSelectedName();
    }

    private void CopyDelimitedNames()
    {
        var separator = string.IsNullOrEmpty(DelimitedSeparator) ? ',' : DelimitedSeparator[0];
        DelimitedNames = DrillProps.ToDelimitedList(separator);
    }

    private void UpdateFromBlockAttributes()
    {
        var results = _drillAttributeSyncService.GetDrillNamesFromSelection(DrillCount);
        if (results == null || results.Count == 0)
        {
            return;
        }

        // Suppress state change events while we batch update to avoid multiple JSON saves.
        _stateChangeSuppressionCount++;
        try
        {
            var limit = Math.Min(results.Count, DrillCount);
            for (var i = 0; i < limit; i++)
            {
                var name = results[i] ?? string.Empty;
                DrillProps.SetDrillProp(i + 1, name);
                Drills[i].Name = name;
                Drills[i].Commit();
                SetCommittedName(i + 1, name);
            }

            ClearCommittedBeyondCount();
            RefreshSelectedName();
        }
        finally
        {
            // Resume notifications so a single save occurs after the batch update completes.
            ResumeStateNotifications(skipPendingRaise: false);
        }

        MessageBox.Show("Form fields updated from block attributes.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CreateWellCornersTable()
    {
        _wellCornerTableService.CreateWellCornersTable();
    }

    private void AutoFillEmpty()
    {
        var prefix = AutoFillPrefix ?? string.Empty;
        var start = AutoFillStartIndex;
        DrillProps.FillEmptyWith(i =>
        {
            var value = start + i - 1;
            return $"{prefix}{value}".Trim();
        });
        RefreshSelectedName();
    }

    private void RefreshSelectedName()
    {
        if (SelectedDrillIndex >= MinimumDrills && SelectedDrillIndex <= MaximumDrills)
        {
            SelectedDrillName = DrillProps.GetDrillProp(SelectedDrillIndex);
        }
    }

    private void SwapDrills()
    {
        if (!CanSwap())
        {
            return;
        }

        var firstIndex = SwapFirstIndex;
        var secondIndex = SwapSecondIndex;
        var firstValue = DrillProps.GetDrillProp(firstIndex);
        var secondValue = DrillProps.GetDrillProp(secondIndex);

        if (!_drillAttributeSyncService.SwapDrillNames(firstIndex, secondIndex, firstValue, secondValue))
        {
            return;
        }

        DrillProps.SetDrillProp(firstIndex, secondValue);
        DrillProps.SetDrillProp(secondIndex, firstValue);
        Drills[firstIndex - 1].Commit();
        Drills[secondIndex - 1].Commit();
        SetCommittedName(firstIndex, secondValue);
        SetCommittedName(secondIndex, firstValue);

        MessageBox.Show($"Swapped {firstValue} <-> {secondValue}", "Swap Complete", MessageBoxButton.OK, MessageBoxImage.Information);

        ClearCommittedBeyondCount();
        RefreshSelectedName();
        RefreshCommandStates();
    }

    private bool CanSwap()
    {
        if (DrillCount <= 1)
        {
            return false;
        }

        var maxAllowed = Math.Max(MinimumDrills, Math.Min(MaximumDrills, DrillCount));
        if (SwapFirstIndex < MinimumDrills || SwapFirstIndex > maxAllowed)
        {
            return false;
        }

        if (SwapSecondIndex < MinimumDrills || SwapSecondIndex > maxAllowed)
        {
            return false;
        }

        return SwapFirstIndex != SwapSecondIndex;
    }

    private string GetCommittedName(int index)
    {
        if (index < MinimumDrills || index > MaximumDrills)
        {
            return string.Empty;
        }

        return _committedNames[index - 1] ?? string.Empty;
    }

    private void SetCommittedName(int index, string? name)
    {
        if (index < MinimumDrills || index > MaximumDrills)
        {
            return;
        }

        _committedNames[index - 1] = name?.Trim() ?? string.Empty;
    }

    private void ClearCommittedBeyondCount()
    {
        for (var i = DrillCount; i < _committedNames.Length; i++)
        {
            _committedNames[i] = string.Empty;
        }
    }

    private void EnsureSwapIndexesInRange()
    {
        var maxAllowed = Math.Max(MinimumDrills, Math.Min(MaximumDrills, DrillCount));

        if (_swapFirstIndex < MinimumDrills)
        {
            _swapFirstIndex = MinimumDrills;
        }
        else if (_swapFirstIndex > maxAllowed)
        {
            _swapFirstIndex = maxAllowed;
        }

        if (_swapSecondIndex < MinimumDrills)
        {
            _swapSecondIndex = Math.Min(maxAllowed, MinimumDrills + 1);
        }
        else if (_swapSecondIndex > maxAllowed)
        {
            _swapSecondIndex = maxAllowed;
        }

        if (_swapFirstIndex == _swapSecondIndex && maxAllowed > MinimumDrills)
        {
            if (_swapSecondIndex < maxAllowed)
            {
                _swapSecondIndex++;
            }
            else if (_swapFirstIndex > MinimumDrills)
            {
                _swapFirstIndex--;
            }
        }

        OnPropertyChanged(nameof(SwapFirstIndex));
        OnPropertyChanged(nameof(SwapSecondIndex));
    }

    private void EnsureSelectedIndexInRange()
    {
        if (_selectedDrillIndex > _drillCount)
        {
            SelectedDrillIndex = _drillCount;
        }
        else if (_selectedDrillIndex < MinimumDrills)
        {
            SelectedDrillIndex = MinimumDrills;
        }
        else
        {
            RefreshSelectedName();
        }
    }

    private void RefreshCommandStates()
    {
        _setSelectedCommand.RaiseCanExecuteChanged();
        _clearSelectedCommand.RaiseCanExecuteChanged();
        _setAllCommand.RaiseCanExecuteChanged();
        _clearAllCommand.RaiseCanExecuteChanged();
        _copyDelimitedCommand.RaiseCanExecuteChanged();
        _updateFromBlockAttributesCommand.RaiseCanExecuteChanged();
        _autoFillCommand.RaiseCanExecuteChanged();
        _swapCommand.RaiseCanExecuteChanged();
        _checkCommand.RaiseCanExecuteChanged();
        _headingsAllCommand.RaiseCanExecuteChanged();
        _createXlsCommand.RaiseCanExecuteChanged();
        _completeCordsCommand.RaiseCanExecuteChanged();
        _completeCordsArchiveCommand.RaiseCanExecuteChanged();
        _getUtmsCommand.RaiseCanExecuteChanged();
        _addDrillPointsCommand.RaiseCanExecuteChanged();
        _addOffsetsCommand.RaiseCanExecuteChanged();
        _updateOffsetsCommand.RaiseCanExecuteChanged();
        _buildDrillCommand.RaiseCanExecuteChanged();
    }

    private static string NormalizeHeading(string? heading)
    {
        if (string.IsNullOrWhiteSpace(heading))
        {
            return HeadingChoices[0];
        }

        var candidate = heading.Trim().ToUpperInvariant();
        return HeadingChoices.Contains(candidate) ? candidate : HeadingChoices[0];
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
