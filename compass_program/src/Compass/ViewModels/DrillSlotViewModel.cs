using System.ComponentModel;
using System.Runtime.CompilerServices;
using Compass.Models;

namespace Compass.ViewModels;

public class DrillSlotViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _committedName = string.Empty;
    private DrillCheckStatus _checkStatus = DrillCheckStatus.NotChecked;
    private string? _checkSummary;

    public DrillSlotViewModel(int index)
    {
        Index = index;
    }

    public int Index { get; }

    public string DisplayLabel
    {
        get
        {
            var name = Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                return $"Drill {Index}";
            }

            return $"Drill {Index} - {name}";
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
                ClearCheckStatus();
            }
        }
    }

    public string CommittedName
    {
        get => _committedName;
        private set
        {
            if (_committedName != value)
            {
                _committedName = value;
                OnPropertyChanged();
            }
        }
    }

    public DrillCheckStatus CheckStatus
    {
        get => _checkStatus;
        private set
        {
            if (_checkStatus != value)
            {
                _checkStatus = value;
                OnPropertyChanged();
            }
        }
    }

    public string? CheckSummary
    {
        get => _checkSummary;
        private set
        {
            if (_checkSummary != value)
            {
                _checkSummary = value;
                OnPropertyChanged();
            }
        }
    }

    public void Commit()
    {
        CommittedName = _name;
    }

    public void ApplyCheckResult(DrillCheckResult result)
    {
        if (result == null)
        {
            ClearCheckStatus();
            return;
        }

        CheckStatus = result.Status;
        CheckSummary = result.Summary;
    }

    public void ClearCheckStatus()
    {
        CheckStatus = DrillCheckStatus.NotChecked;
        CheckSummary = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(Name))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
        }
    }
}
