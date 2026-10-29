using WiiHub.Core.Config;
using WiiHub.Core.Input;
using WiiHub.Core.Mapping;

namespace WiiHub.ViewModels;

/// <summary>One row in the mapping editor: source input → output target.</summary>
public sealed class BindingViewModel : ObservableObject
{
    private readonly ControllerViewModel _owner;
    private InputId _source;
    private int _target;
    private bool _invert;
    private bool _isActive;
    private bool _isListening;
    private bool _isCapturingKey;

    public BindingViewModel(ControllerViewModel owner, Binding binding, OutputKind output)
    {
        _owner = owner;
        Output = output;
        _source = binding.Source;
        _target = binding.Target;
        _invert = binding.Invert;
    }

    public OutputKind Output { get; }
    public bool IsKeyboard => Output == OutputKind.Keyboard;

    public InputId Source
    {
        get => _source;
        set
        {
            if (Set(ref _source, value))
            {
                Raise(nameof(SourceName));
                Raise(nameof(AccentColor));
                Raise(nameof(SoftColor));
                Raise(nameof(IsFret));
                _owner.CommitMapping();
            }
        }
    }

    public string SourceName => InputCatalog.Get(_source).Name;

    /// <summary>True when the source is one of the five frets (or touch-bar frets).</summary>
    public bool IsFret => InputColors.ForInput(_source).IsFret;

    /// <summary>Strong colour of the source ("#RRGGBB"): the fret colour for frets, accent blue otherwise.</summary>
    public string AccentColor => InputColors.ForInput(_source).Accent;

    /// <summary>Light tint of <see cref="AccentColor"/>, used as the row background while active.</summary>
    public string SoftColor => InputColors.ForInput(_source).Soft;

    public int Target
    {
        get => _target;
        set
        {
            if (Set(ref _target, value))
            {
                Raise(nameof(TargetName));
                _owner.CommitMapping();
            }
        }
    }

    public string TargetName => MappingEngine.DescribeTarget(Output, _target);

    public bool Invert
    {
        get => _invert;
        set
        {
            if (Set(ref _invert, value)) _owner.CommitMapping();
        }
    }

    /// <summary>Lit while the source input is triggering this binding (live input check).</summary>
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    /// <summary>Waiting for the user to press something on the controller.</summary>
    public bool IsListening
    {
        get => _isListening;
        set
        {
            if (Set(ref _isListening, value)) Raise(nameof(ListenText));
        }
    }

    public string ListenText => _isListening ? "Press a button…" : "Detect";

    /// <summary>Waiting for the user to press a keyboard key.</summary>
    public bool IsCapturingKey
    {
        get => _isCapturingKey;
        set
        {
            if (Set(ref _isCapturingKey, value)) Raise(nameof(CaptureText));
        }
    }

    public string CaptureText => _isCapturingKey ? "Press a key…" : "Press key";

    public Binding ToModel() => new(_source, _target, _invert);
}
