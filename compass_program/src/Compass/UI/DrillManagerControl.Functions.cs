using System;
using System.Collections.Generic;
using Compass.Models;

namespace Compass.UI;

public partial class DrillManagerControl
{
    public int Minimum => DrillProps.Minimum;

    public int Maximum => DrillProps.Maximum;

    public int DrillCount
    {
        get => ViewModel.DrillCount;
        set => ViewModel.DrillCount = value;
    }

    public string this[int index]
    {
        get => DrillProps[index];
        set => DrillProps[index] = value;
    }

    public IReadOnlyList<string> GetDrillProps()
    {
        return DrillProps.GetDrillProps();
    }

    public IReadOnlyDictionary<int, string> GetIndexedDrillProps()
    {
        return DrillProps.GetIndexedDrillProps();
    }

    public string GetDrillProp(int index)
    {
        return DrillProps.GetDrillProp(index);
    }

    public void SetDrillProp(int index, string? name)
    {
        DrillProps.SetDrillProp(index, name);
    }

    public void SetDrillProps(IEnumerable<string?>? names)
    {
        DrillProps.SetDrillProps(names);
    }

    public void ClearDrillProp(int index)
    {
        DrillProps.ClearDrillProp(index);
    }

    public void ClearAllDrillProps()
    {
        DrillProps.ClearAllDrillProps();
    }

    public void EnsureCapacity(int desiredCount)
    {
        DrillProps.EnsureCapacity(desiredCount);
    }

    public void LoadFromDelimitedList(string? delimitedNames, char separator = ',')
    {
        DrillProps.LoadFromDelimitedList(delimitedNames, separator);
    }

    public string ToDelimitedList(char separator = ',')
    {
        return DrillProps.ToDelimitedList(separator);
    }

    public void FillEmptyWith(Func<int, string> nameFactory)
    {
        DrillProps.FillEmptyWith(nameFactory);
    }

    public void Apply(Func<int, string, string?> mutator)
    {
        DrillProps.Apply(mutator);
    }

    public void ApplyState(DrillGridState state)
    {
        ViewModel.ApplyState(state);
    }

    public DrillGridState CaptureState()
    {
        return ViewModel.CaptureState();
    }

    public string DrillProp1
    {
        get => DrillProps.DrillProp1;
        set => DrillProps.DrillProp1 = value;
    }

    public string DrillProp2
    {
        get => DrillProps.DrillProp2;
        set => DrillProps.DrillProp2 = value;
    }

    public string DrillProp3
    {
        get => DrillProps.DrillProp3;
        set => DrillProps.DrillProp3 = value;
    }

    public string DrillProp4
    {
        get => DrillProps.DrillProp4;
        set => DrillProps.DrillProp4 = value;
    }

    public string DrillProp5
    {
        get => DrillProps.DrillProp5;
        set => DrillProps.DrillProp5 = value;
    }

    public string DrillProp6
    {
        get => DrillProps.DrillProp6;
        set => DrillProps.DrillProp6 = value;
    }

    public string DrillProp7
    {
        get => DrillProps.DrillProp7;
        set => DrillProps.DrillProp7 = value;
    }

    public string DrillProp8
    {
        get => DrillProps.DrillProp8;
        set => DrillProps.DrillProp8 = value;
    }

    public string DrillProp9
    {
        get => DrillProps.DrillProp9;
        set => DrillProps.DrillProp9 = value;
    }

    public string DrillProp10
    {
        get => DrillProps.DrillProp10;
        set => DrillProps.DrillProp10 = value;
    }

    public string DrillProp11
    {
        get => DrillProps.DrillProp11;
        set => DrillProps.DrillProp11 = value;
    }

    public string DrillProp12
    {
        get => DrillProps.DrillProp12;
        set => DrillProps.DrillProp12 = value;
    }

    public string DrillProp13
    {
        get => DrillProps.DrillProp13;
        set => DrillProps.DrillProp13 = value;
    }

    public string DrillProp14
    {
        get => DrillProps.DrillProp14;
        set => DrillProps.DrillProp14 = value;
    }

    public string DrillProp15
    {
        get => DrillProps.DrillProp15;
        set => DrillProps.DrillProp15 = value;
    }

    public string DrillProp16
    {
        get => DrillProps.DrillProp16;
        set => DrillProps.DrillProp16 = value;
    }

    public string DrillProp17
    {
        get => DrillProps.DrillProp17;
        set => DrillProps.DrillProp17 = value;
    }

    public string DrillProp18
    {
        get => DrillProps.DrillProp18;
        set => DrillProps.DrillProp18 = value;
    }

    public string DrillProp19
    {
        get => DrillProps.DrillProp19;
        set => DrillProps.DrillProp19 = value;
    }

    public string DrillProp20
    {
        get => DrillProps.DrillProp20;
        set => DrillProps.DrillProp20 = value;
    }

    public string GetDrillProp1()
    {
        return DrillProps.GetDrillProp1();
    }

    public void SetDrillProp1(string? value)
    {
        DrillProps.SetDrillProp1(value);
    }

    public string GetDrillProp2()
    {
        return DrillProps.GetDrillProp2();
    }

    public void SetDrillProp2(string? value)
    {
        DrillProps.SetDrillProp2(value);
    }

    public string GetDrillProp3()
    {
        return DrillProps.GetDrillProp3();
    }

    public void SetDrillProp3(string? value)
    {
        DrillProps.SetDrillProp3(value);
    }

    public string GetDrillProp4()
    {
        return DrillProps.GetDrillProp4();
    }

    public void SetDrillProp4(string? value)
    {
        DrillProps.SetDrillProp4(value);
    }

    public string GetDrillProp5()
    {
        return DrillProps.GetDrillProp5();
    }

    public void SetDrillProp5(string? value)
    {
        DrillProps.SetDrillProp5(value);
    }

    public string GetDrillProp6()
    {
        return DrillProps.GetDrillProp6();
    }

    public void SetDrillProp6(string? value)
    {
        DrillProps.SetDrillProp6(value);
    }

    public string GetDrillProp7()
    {
        return DrillProps.GetDrillProp7();
    }

    public void SetDrillProp7(string? value)
    {
        DrillProps.SetDrillProp7(value);
    }

    public string GetDrillProp8()
    {
        return DrillProps.GetDrillProp8();
    }

    public void SetDrillProp8(string? value)
    {
        DrillProps.SetDrillProp8(value);
    }

    public string GetDrillProp9()
    {
        return DrillProps.GetDrillProp9();
    }

    public void SetDrillProp9(string? value)
    {
        DrillProps.SetDrillProp9(value);
    }

    public string GetDrillProp10()
    {
        return DrillProps.GetDrillProp10();
    }

    public void SetDrillProp10(string? value)
    {
        DrillProps.SetDrillProp10(value);
    }

    public string GetDrillProp11()
    {
        return DrillProps.GetDrillProp11();
    }

    public void SetDrillProp11(string? value)
    {
        DrillProps.SetDrillProp11(value);
    }

    public string GetDrillProp12()
    {
        return DrillProps.GetDrillProp12();
    }

    public void SetDrillProp12(string? value)
    {
        DrillProps.SetDrillProp12(value);
    }

    public string GetDrillProp13()
    {
        return DrillProps.GetDrillProp13();
    }

    public void SetDrillProp13(string? value)
    {
        DrillProps.SetDrillProp13(value);
    }

    public string GetDrillProp14()
    {
        return DrillProps.GetDrillProp14();
    }

    public void SetDrillProp14(string? value)
    {
        DrillProps.SetDrillProp14(value);
    }

    public string GetDrillProp15()
    {
        return DrillProps.GetDrillProp15();
    }

    public void SetDrillProp15(string? value)
    {
        DrillProps.SetDrillProp15(value);
    }

    public string GetDrillProp16()
    {
        return DrillProps.GetDrillProp16();
    }

    public void SetDrillProp16(string? value)
    {
        DrillProps.SetDrillProp16(value);
    }

    public string GetDrillProp17()
    {
        return DrillProps.GetDrillProp17();
    }

    public void SetDrillProp17(string? value)
    {
        DrillProps.SetDrillProp17(value);
    }

    public string GetDrillProp18()
    {
        return DrillProps.GetDrillProp18();
    }

    public void SetDrillProp18(string? value)
    {
        DrillProps.SetDrillProp18(value);
    }

    public string GetDrillProp19()
    {
        return DrillProps.GetDrillProp19();
    }

    public void SetDrillProp19(string? value)
    {
        DrillProps.SetDrillProp19(value);
    }

    public string GetDrillProp20()
    {
        return DrillProps.GetDrillProp20();
    }

    public void SetDrillProp20(string? value)
    {
        DrillProps.SetDrillProp20(value);
    }
}
