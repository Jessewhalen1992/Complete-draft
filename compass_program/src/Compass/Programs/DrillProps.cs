using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Runtime;
using Compass.Models;
using Compass.Modules;
using Compass.UI;
using Compass.ViewModels;

[assembly: CommandClass(typeof(Compass.Programs.DrillProps))]

namespace Compass.Programs;

public static class DrillProps
{
    private static DrillManagerModule Module => CompassApplication.GetDrillManagerModule();

    public static DrillManagerControl Control => Module.Control;

    public static DrillManagerViewModel ViewModel => Control.ViewModel;

    public static DrillPropsAccessor Accessor => ViewModel.DrillProps;

    public static int Minimum => Control.Minimum;

    public static int Maximum => Control.Maximum;

    public static int DrillCount
    {
        get => Control.DrillCount;
        set => Control.DrillCount = value;
    }

    public static IReadOnlyList<string> GetDrillProps()
    {
        return Control.GetDrillProps();
    }

    public static IReadOnlyDictionary<int, string> GetIndexedDrillProps()
    {
        return Control.GetIndexedDrillProps();
    }

    public static string GetDrillProp(int index)
    {
        return Control.GetDrillProp(index);
    }

    public static void SetDrillProp(int index, string? name)
    {
        Control.SetDrillProp(index, name);
    }

    public static void SetDrillProps(IEnumerable<string?>? names)
    {
        Control.SetDrillProps(names);
    }

    public static void ClearDrillProp(int index)
    {
        Control.ClearDrillProp(index);
    }

    public static void ClearAllDrillProps()
    {
        Control.ClearAllDrillProps();
    }

    public static void EnsureCapacity(int desiredCount)
    {
        Control.EnsureCapacity(desiredCount);
    }

    public static void LoadFromDelimitedList(string? delimitedNames, char separator = ',')
    {
        Control.LoadFromDelimitedList(delimitedNames, separator);
    }

    public static string ToDelimitedList(char separator = ',')
    {
        return Control.ToDelimitedList(separator);
    }

    public static void FillEmptyWith(Func<int, string> nameFactory)
    {
        Control.FillEmptyWith(nameFactory);
    }

    public static void Apply(Func<int, string, string?> mutator)
    {
        Control.Apply(mutator);
    }

    public static void ApplyState(DrillGridState state)
    {
        Control.ApplyState(state);
    }

    public static DrillGridState CaptureState()
    {
        return Control.CaptureState();
    }

    [CommandMethod("DRILLPROPS", CommandFlags.Modal | CommandFlags.Session)]
    public static void ShowPalette()
    {
        Module.Show();
    }

    public static string DrillProp1
    {
        get => Control.DrillProp1;
        set => Control.DrillProp1 = value;
    }

    public static string DrillProp2
    {
        get => Control.DrillProp2;
        set => Control.DrillProp2 = value;
    }

    public static string DrillProp3
    {
        get => Control.DrillProp3;
        set => Control.DrillProp3 = value;
    }

    public static string DrillProp4
    {
        get => Control.DrillProp4;
        set => Control.DrillProp4 = value;
    }

    public static string DrillProp5
    {
        get => Control.DrillProp5;
        set => Control.DrillProp5 = value;
    }

    public static string DrillProp6
    {
        get => Control.DrillProp6;
        set => Control.DrillProp6 = value;
    }

    public static string DrillProp7
    {
        get => Control.DrillProp7;
        set => Control.DrillProp7 = value;
    }

    public static string DrillProp8
    {
        get => Control.DrillProp8;
        set => Control.DrillProp8 = value;
    }

    public static string DrillProp9
    {
        get => Control.DrillProp9;
        set => Control.DrillProp9 = value;
    }

    public static string DrillProp10
    {
        get => Control.DrillProp10;
        set => Control.DrillProp10 = value;
    }

    public static string DrillProp11
    {
        get => Control.DrillProp11;
        set => Control.DrillProp11 = value;
    }

    public static string DrillProp12
    {
        get => Control.DrillProp12;
        set => Control.DrillProp12 = value;
    }

    public static string DrillProp13
    {
        get => Control.DrillProp13;
        set => Control.DrillProp13 = value;
    }

    public static string DrillProp14
    {
        get => Control.DrillProp14;
        set => Control.DrillProp14 = value;
    }

    public static string DrillProp15
    {
        get => Control.DrillProp15;
        set => Control.DrillProp15 = value;
    }

    public static string DrillProp16
    {
        get => Control.DrillProp16;
        set => Control.DrillProp16 = value;
    }

    public static string DrillProp17
    {
        get => Control.DrillProp17;
        set => Control.DrillProp17 = value;
    }

    public static string DrillProp18
    {
        get => Control.DrillProp18;
        set => Control.DrillProp18 = value;
    }

    public static string DrillProp19
    {
        get => Control.DrillProp19;
        set => Control.DrillProp19 = value;
    }

    public static string DrillProp20
    {
        get => Control.DrillProp20;
        set => Control.DrillProp20 = value;
    }

    public static string GetDrillProp1()
    {
        return Control.GetDrillProp1();
    }

    public static void SetDrillProp1(string? value)
    {
        Control.SetDrillProp1(value);
    }

    public static string GetDrillProp2()
    {
        return Control.GetDrillProp2();
    }

    public static void SetDrillProp2(string? value)
    {
        Control.SetDrillProp2(value);
    }

    public static string GetDrillProp3()
    {
        return Control.GetDrillProp3();
    }

    public static void SetDrillProp3(string? value)
    {
        Control.SetDrillProp3(value);
    }

    public static string GetDrillProp4()
    {
        return Control.GetDrillProp4();
    }

    public static void SetDrillProp4(string? value)
    {
        Control.SetDrillProp4(value);
    }

    public static string GetDrillProp5()
    {
        return Control.GetDrillProp5();
    }

    public static void SetDrillProp5(string? value)
    {
        Control.SetDrillProp5(value);
    }

    public static string GetDrillProp6()
    {
        return Control.GetDrillProp6();
    }

    public static void SetDrillProp6(string? value)
    {
        Control.SetDrillProp6(value);
    }

    public static string GetDrillProp7()
    {
        return Control.GetDrillProp7();
    }

    public static void SetDrillProp7(string? value)
    {
        Control.SetDrillProp7(value);
    }

    public static string GetDrillProp8()
    {
        return Control.GetDrillProp8();
    }

    public static void SetDrillProp8(string? value)
    {
        Control.SetDrillProp8(value);
    }

    public static string GetDrillProp9()
    {
        return Control.GetDrillProp9();
    }

    public static void SetDrillProp9(string? value)
    {
        Control.SetDrillProp9(value);
    }

    public static string GetDrillProp10()
    {
        return Control.GetDrillProp10();
    }

    public static void SetDrillProp10(string? value)
    {
        Control.SetDrillProp10(value);
    }

    public static string GetDrillProp11()
    {
        return Control.GetDrillProp11();
    }

    public static void SetDrillProp11(string? value)
    {
        Control.SetDrillProp11(value);
    }

    public static string GetDrillProp12()
    {
        return Control.GetDrillProp12();
    }

    public static void SetDrillProp12(string? value)
    {
        Control.SetDrillProp12(value);
    }

    public static string GetDrillProp13()
    {
        return Control.GetDrillProp13();
    }

    public static void SetDrillProp13(string? value)
    {
        Control.SetDrillProp13(value);
    }

    public static string GetDrillProp14()
    {
        return Control.GetDrillProp14();
    }

    public static void SetDrillProp14(string? value)
    {
        Control.SetDrillProp14(value);
    }

    public static string GetDrillProp15()
    {
        return Control.GetDrillProp15();
    }

    public static void SetDrillProp15(string? value)
    {
        Control.SetDrillProp15(value);
    }

    public static string GetDrillProp16()
    {
        return Control.GetDrillProp16();
    }

    public static void SetDrillProp16(string? value)
    {
        Control.SetDrillProp16(value);
    }

    public static string GetDrillProp17()
    {
        return Control.GetDrillProp17();
    }

    public static void SetDrillProp17(string? value)
    {
        Control.SetDrillProp17(value);
    }

    public static string GetDrillProp18()
    {
        return Control.GetDrillProp18();
    }

    public static void SetDrillProp18(string? value)
    {
        Control.SetDrillProp18(value);
    }

    public static string GetDrillProp19()
    {
        return Control.GetDrillProp19();
    }

    public static void SetDrillProp19(string? value)
    {
        Control.SetDrillProp19(value);
    }

    public static string GetDrillProp20()
    {
        return Control.GetDrillProp20();
    }

    public static void SetDrillProp20(string? value)
    {
        Control.SetDrillProp20(value);
    }

    public static void SaveState()
    {
        Module.SaveState();
    }
}
