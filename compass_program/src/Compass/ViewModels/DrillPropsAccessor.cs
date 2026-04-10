using System;
using System.Collections.Generic;
using System.Linq;

namespace Compass.ViewModels;

public class DrillPropsAccessor
{
    private readonly DrillManagerViewModel _viewModel;

    internal DrillPropsAccessor(DrillManagerViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public int Minimum => DrillManagerViewModel.MinimumDrills;

    public int Maximum => DrillManagerViewModel.MaximumDrills;

    public int Count
    {
        get => _viewModel.DrillCount;
        set => _viewModel.DrillCount = Math.Max(Minimum, Math.Min(Maximum, value));
    }

    public string this[int index]
    {
        get => GetDrillProp(index);
        set => SetDrillProp(index, value);
    }

    public IReadOnlyList<string> GetDrillProps()
    {
        return _viewModel.Drills.Select(slot => slot.Name).ToList();
    }

    public IReadOnlyDictionary<int, string> GetIndexedDrillProps()
    {
        return _viewModel.Drills
            .Select((slot, i) => new KeyValuePair<int, string>(i + 1, slot.Name))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    public string GetDrillProp(int index)
    {
        var slot = _viewModel.TryGetSlot(index);
        if (slot == null)
        {
            if (index < Minimum || index > Maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be between {Minimum} and {Maximum}.");
            }

            return string.Empty;
        }

        return slot.Name;
    }

    public void SetDrillProp(int index, string? name)
    {
        var slot = _viewModel.EnsureSlot(index);
        slot.Name = name?.Trim() ?? string.Empty;
    }

    public void SetDrillProps(IEnumerable<string?>? names)
    {
        if (names == null)
        {
            ClearAllDrillProps();
            return;
        }

        var normalized = names.Select(name => name?.Trim() ?? string.Empty).ToList();
        _viewModel.LoadExistingNames(normalized, commit: false);
    }

    public void ClearDrillProp(int index)
    {
        var slot = _viewModel.TryGetSlot(index);
        if (slot != null)
        {
            slot.Name = string.Empty;
        }
    }

    public void ClearAllDrillProps()
    {
        foreach (var slot in _viewModel.Drills)
        {
            slot.Name = string.Empty;
        }
    }

    public void EnsureCapacity(int desiredCount)
    {
        Count = desiredCount;
    }

    public void LoadFromDelimitedList(string? delimitedNames, char separator = ',')
    {
        if (string.IsNullOrWhiteSpace(delimitedNames))
        {
            ClearAllDrillProps();
            return;
        }

        var parsed = delimitedNames
            .Split(new[] { separator }, StringSplitOptions.None)
            .Select(name => name.Trim())
            .Where(name => !string.IsNullOrEmpty(name));

        SetDrillProps(parsed);
    }

    public string ToDelimitedList(char separator = ',')
    {
        return string.Join(separator.ToString(), _viewModel.Drills
            .Select(slot => slot.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    public void FillEmptyWith(Func<int, string> nameFactory)
    {
        if (nameFactory == null)
        {
            throw new ArgumentNullException(nameof(nameFactory));
        }

        for (var i = 0; i < _viewModel.Drills.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(_viewModel.Drills[i].Name))
            {
                _viewModel.Drills[i].Name = nameFactory(i + 1) ?? string.Empty;
            }
        }
    }

    public void Apply(Func<int, string, string?> mutator)
    {
        if (mutator == null)
        {
            throw new ArgumentNullException(nameof(mutator));
        }

        for (var i = 0; i < _viewModel.Drills.Count; i++)
        {
            var updated = mutator(i + 1, _viewModel.Drills[i].Name) ?? string.Empty;
            _viewModel.Drills[i].Name = updated;
        }
    }

    public string DrillProp1
    {
        get => GetDrillProp(1);
        set => SetDrillProp(1, value);
    }

    public string DrillProp2
    {
        get => GetDrillProp(2);
        set => SetDrillProp(2, value);
    }

    public string DrillProp3
    {
        get => GetDrillProp(3);
        set => SetDrillProp(3, value);
    }

    public string DrillProp4
    {
        get => GetDrillProp(4);
        set => SetDrillProp(4, value);
    }

    public string DrillProp5
    {
        get => GetDrillProp(5);
        set => SetDrillProp(5, value);
    }

    public string DrillProp6
    {
        get => GetDrillProp(6);
        set => SetDrillProp(6, value);
    }

    public string DrillProp7
    {
        get => GetDrillProp(7);
        set => SetDrillProp(7, value);
    }

    public string DrillProp8
    {
        get => GetDrillProp(8);
        set => SetDrillProp(8, value);
    }

    public string DrillProp9
    {
        get => GetDrillProp(9);
        set => SetDrillProp(9, value);
    }

    public string DrillProp10
    {
        get => GetDrillProp(10);
        set => SetDrillProp(10, value);
    }

    public string DrillProp11
    {
        get => GetDrillProp(11);
        set => SetDrillProp(11, value);
    }

    public string DrillProp12
    {
        get => GetDrillProp(12);
        set => SetDrillProp(12, value);
    }

    public string DrillProp13
    {
        get => GetDrillProp(13);
        set => SetDrillProp(13, value);
    }

    public string DrillProp14
    {
        get => GetDrillProp(14);
        set => SetDrillProp(14, value);
    }

    public string DrillProp15
    {
        get => GetDrillProp(15);
        set => SetDrillProp(15, value);
    }

    public string DrillProp16
    {
        get => GetDrillProp(16);
        set => SetDrillProp(16, value);
    }

    public string DrillProp17
    {
        get => GetDrillProp(17);
        set => SetDrillProp(17, value);
    }

    public string DrillProp18
    {
        get => GetDrillProp(18);
        set => SetDrillProp(18, value);
    }

    public string DrillProp19
    {
        get => GetDrillProp(19);
        set => SetDrillProp(19, value);
    }

    public string DrillProp20
    {
        get => GetDrillProp(20);
        set => SetDrillProp(20, value);
    }

    public string GetDrillProp1()
    {
        return GetDrillProp(1);
    }

    public void SetDrillProp1(string? value)
    {
        SetDrillProp(1, value);
    }

    public string GetDrillProp2()
    {
        return GetDrillProp(2);
    }

    public void SetDrillProp2(string? value)
    {
        SetDrillProp(2, value);
    }

    public string GetDrillProp3()
    {
        return GetDrillProp(3);
    }

    public void SetDrillProp3(string? value)
    {
        SetDrillProp(3, value);
    }

    public string GetDrillProp4()
    {
        return GetDrillProp(4);
    }

    public void SetDrillProp4(string? value)
    {
        SetDrillProp(4, value);
    }

    public string GetDrillProp5()
    {
        return GetDrillProp(5);
    }

    public void SetDrillProp5(string? value)
    {
        SetDrillProp(5, value);
    }

    public string GetDrillProp6()
    {
        return GetDrillProp(6);
    }

    public void SetDrillProp6(string? value)
    {
        SetDrillProp(6, value);
    }

    public string GetDrillProp7()
    {
        return GetDrillProp(7);
    }

    public void SetDrillProp7(string? value)
    {
        SetDrillProp(7, value);
    }

    public string GetDrillProp8()
    {
        return GetDrillProp(8);
    }

    public void SetDrillProp8(string? value)
    {
        SetDrillProp(8, value);
    }

    public string GetDrillProp9()
    {
        return GetDrillProp(9);
    }

    public void SetDrillProp9(string? value)
    {
        SetDrillProp(9, value);
    }

    public string GetDrillProp10()
    {
        return GetDrillProp(10);
    }

    public void SetDrillProp10(string? value)
    {
        SetDrillProp(10, value);
    }

    public string GetDrillProp11()
    {
        return GetDrillProp(11);
    }

    public void SetDrillProp11(string? value)
    {
        SetDrillProp(11, value);
    }

    public string GetDrillProp12()
    {
        return GetDrillProp(12);
    }

    public void SetDrillProp12(string? value)
    {
        SetDrillProp(12, value);
    }

    public string GetDrillProp13()
    {
        return GetDrillProp(13);
    }

    public void SetDrillProp13(string? value)
    {
        SetDrillProp(13, value);
    }

    public string GetDrillProp14()
    {
        return GetDrillProp(14);
    }

    public void SetDrillProp14(string? value)
    {
        SetDrillProp(14, value);
    }

    public string GetDrillProp15()
    {
        return GetDrillProp(15);
    }

    public void SetDrillProp15(string? value)
    {
        SetDrillProp(15, value);
    }

    public string GetDrillProp16()
    {
        return GetDrillProp(16);
    }

    public void SetDrillProp16(string? value)
    {
        SetDrillProp(16, value);
    }

    public string GetDrillProp17()
    {
        return GetDrillProp(17);
    }

    public void SetDrillProp17(string? value)
    {
        SetDrillProp(17, value);
    }

    public string GetDrillProp18()
    {
        return GetDrillProp(18);
    }

    public void SetDrillProp18(string? value)
    {
        SetDrillProp(18, value);
    }

    public string GetDrillProp19()
    {
        return GetDrillProp(19);
    }

    public void SetDrillProp19(string? value)
    {
        SetDrillProp(19, value);
    }

    public string GetDrillProp20()
    {
        return GetDrillProp(20);
    }

    public void SetDrillProp20(string? value)
    {
        SetDrillProp(20, value);
    }
}
