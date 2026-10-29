using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using WiiHub.Core.Mapping;
using WiiHub.Core.Output;

namespace WiiHub.App.Output;

/// <summary>Creates virtual Xbox 360 controllers through the ViGEmBus driver.</summary>
public sealed class ViGEmPadFactory : IXboxPadFactory, IDisposable
{
    private readonly object _sync = new();
    private ViGEmClient? _client;

    public bool IsAvailable(out string message)
    {
        try
        {
            GetClient();
            message = "ViGEmBus driver found: virtual Xbox 360 controllers available.";
            return true;
        }
        catch (Exception ex)
        {
            message = "ViGEmBus driver not found. Install it to use Xbox 360 output " +
                      $"(keyboard output still works). Details: {ex.GetType().Name}";
            return false;
        }
    }

    public IXboxPad Create() => new ViGEmXboxPad(GetClient());

    private ViGEmClient GetClient()
    {
        lock (_sync) return _client ??= new ViGEmClient();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    private sealed class ViGEmXboxPad : IXboxPad
    {
        private readonly IXbox360Controller _pad;
        private readonly object _sync = new();
        private bool _disposed;

        public ViGEmXboxPad(ViGEmClient client)
        {
            _pad = client.CreateXbox360Controller();
            _pad.AutoSubmitReport = false;
            _pad.FeedbackReceived += OnFeedback;
            _pad.Connect();
        }

        public event Action<byte, byte>? RumbleReceived;

        private void OnFeedback(object sender, Xbox360FeedbackReceivedEventArgs e) =>
            RumbleReceived?.Invoke(e.LargeMotor, e.SmallMotor);

        public void Submit(in XboxReport r)
        {
            lock (_sync)
            {
                if (_disposed) return;
                _pad.SetButtonsFull(r.Buttons);
                _pad.SetSliderValue(Xbox360Slider.LeftTrigger, r.LeftTrigger);
                _pad.SetSliderValue(Xbox360Slider.RightTrigger, r.RightTrigger);
                _pad.SetAxisValue(Xbox360Axis.LeftThumbX, r.LeftStickX);
                _pad.SetAxisValue(Xbox360Axis.LeftThumbY, r.LeftStickY);
                _pad.SetAxisValue(Xbox360Axis.RightThumbX, r.RightStickX);
                _pad.SetAxisValue(Xbox360Axis.RightThumbY, r.RightStickY);
                _pad.SubmitReport();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _pad.FeedbackReceived -= OnFeedback;
                try { _pad.Disconnect(); } catch { /* already gone */ }
            }
        }
    }
}
