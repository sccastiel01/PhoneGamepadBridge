using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace PhoneGamepadBridge;

/// <summary>
/// Owns the virtual Xbox 360 pad exposed through ViGEmBus and translates wire
/// frames into XInput reports. One instance lives for the whole process so the
/// pad does not churn in Device Manager across TCP reconnects.
/// </summary>
internal sealed class ViGEmSink : IDisposable
{
    private static readonly (PadButtons Wire, Xbox360Button Native)[] ButtonMap =
    [
        (PadButtons.A, Xbox360Button.A),
        (PadButtons.B, Xbox360Button.B),
        (PadButtons.X, Xbox360Button.X),
        (PadButtons.Y, Xbox360Button.Y),
        (PadButtons.LeftShoulder, Xbox360Button.LeftShoulder),
        (PadButtons.RightShoulder, Xbox360Button.RightShoulder),
        (PadButtons.Back, Xbox360Button.Back),
        (PadButtons.Start, Xbox360Button.Start),
        (PadButtons.LeftThumb, Xbox360Button.LeftThumb),
        (PadButtons.RightThumb, Xbox360Button.RightThumb),
        (PadButtons.DpadUp, Xbox360Button.Up),
        (PadButtons.DpadDown, Xbox360Button.Down),
        (PadButtons.DpadLeft, Xbox360Button.Left),
        (PadButtons.DpadRight, Xbox360Button.Right),
        (PadButtons.Guide, Xbox360Button.Guide),
    ];

    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _pad;
    private bool _neutral;

    public ViGEmSink()
    {
        // Throws VigemBusNotFoundException if the driver is not installed.
        _client = new ViGEmClient();
        _pad = _client.CreateXbox360Controller();
        _pad.AutoSubmitReport = false;
        _pad.Connect();
        Neutral();
    }

    public void Apply(in GamepadFrame frame)
    {
        // The phone reports "no controller attached" as a distinct state from
        // "controller attached, sticks centred". Treat it as full neutral so a
        // controller that powers off mid-game does not leave inputs latched.
        if (!frame.ControllerAttached)
        {
            Neutral();
            return;
        }

        foreach (var (wire, native) in ButtonMap)
            _pad.SetButtonState(native, (frame.Buttons & wire) != 0);

        _pad.SetAxisValue(Xbox360Axis.LeftThumbX, frame.LeftX);
        _pad.SetAxisValue(Xbox360Axis.LeftThumbY, frame.LeftY);
        _pad.SetAxisValue(Xbox360Axis.RightThumbX, frame.RightX);
        _pad.SetAxisValue(Xbox360Axis.RightThumbY, frame.RightY);

        _pad.SetSliderValue(Xbox360Slider.LeftTrigger, frame.LeftTrigger);
        _pad.SetSliderValue(Xbox360Slider.RightTrigger, frame.RightTrigger);

        _pad.SubmitReport();
        _neutral = false;
    }

    /// <summary>Releases every control. Idempotent — safe to call from the watchdog.</summary>
    public void Neutral()
    {
        if (_neutral) return;

        _pad.ResetReport();
        _pad.SubmitReport();
        _neutral = true;
    }

    public void Dispose()
    {
        try
        {
            Neutral();
            _pad.Disconnect();
        }
        catch
        {
            // Nothing useful to do while tearing down.
        }

        _client.Dispose();
    }
}
